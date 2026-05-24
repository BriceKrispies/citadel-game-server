using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GameServer.LoadHarness;

/// <summary>One periodic sample of counters during a run, for the CSV time-series.</summary>
public sealed record TimeSeriesSample(
    double ElapsedSeconds,
    long ActiveConnections,
    long MessagesSent,
    long MessagesReceived,
    long BytesSent,
    long BytesReceived,
    long ServerErrors,
    long UnexpectedCloses);

/// <summary>
/// The server's authoritative reading of one room at two points near end of steady state.
/// <see cref="Advancing"/> proves the room's authoritative tick moved between the two polls
/// — i.e. the room is live and fanning out updates, not stalled.
/// </summary>
public sealed record ServerRoomCheck(string RoomId, int SubscriberCount, long TickStart, long TickEnd)
{
    public bool Advancing => TickEnd > TickStart;
}

/// <summary>
/// Aggregate verdict from the server admin API: how many rooms the server reports for this
/// tenant, whether every one of them has the expected subscriber count, and whether every
/// one advanced its tick. This is the server-side half of "each room is receiving updates".
/// </summary>
public sealed record ServerVerification(
    int ExpectedRooms,
    int ExpectedSubscribersPerRoom,
    IReadOnlyList<ServerRoomCheck> Rooms)
{
    public int ObservedRooms => Rooms.Count;
    public int RoomsAtExpectedSubscribers => Rooms.Count(r => r.SubscriberCount == ExpectedSubscribersPerRoom);
    public int RoomsAdvancing => Rooms.Count(r => r.Advancing);
    public bool Passed =>
        ObservedRooms == ExpectedRooms &&
        RoomsAtExpectedSubscribers == ExpectedRooms &&
        RoomsAdvancing == ExpectedRooms;

    public static readonly ServerVerification NotRun = new(0, 0, Array.Empty<ServerRoomCheck>());
}

/// <summary>The final result of a scenario run: config identity + metrics + failure counts.</summary>
public sealed record LoadResult(
    string ScenarioName,
    string ScenarioType,
    int TotalClients,
    MetricsSnapshot Metrics,
    IReadOnlyDictionary<string, long> FailureCounts)
{
    /// <summary>Server-side per-room verification (empty when not run, e.g. static-token mode).</summary>
    public ServerVerification ServerVerification { get; init; } = ServerVerification.NotRun;
}

/// <summary>
/// Writes run output in three forms: a console progress/summary, a JSON result file,
/// and a CSV time-series. Pure I/O — no clock, no sockets — so it is unit-testable
/// against a temp directory.
/// </summary>
public sealed class ResultExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public void WriteJson(LoadResult result, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(result, JsonOptions));
    }

    public void WriteCsv(IEnumerable<TimeSeriesSample> samples, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        var sb = new StringBuilder();
        sb.AppendLine("elapsedSeconds,activeConnections,messagesSent,messagesReceived,bytesSent,bytesReceived,serverErrors,unexpectedCloses");
        foreach (var s in samples)
        {
            sb.Append(s.ElapsedSeconds.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
              .Append(s.ActiveConnections).Append(',')
              .Append(s.MessagesSent).Append(',')
              .Append(s.MessagesReceived).Append(',')
              .Append(s.BytesSent).Append(',')
              .Append(s.BytesReceived).Append(',')
              .Append(s.ServerErrors).Append(',')
              .Append(s.UnexpectedCloses).Append('\n');
        }

        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>Per-room client-side receive tallies as CSV (one row per room).</summary>
    public void WritePerRoomCsv(IReadOnlyList<RoomMetric> rooms, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        var sb = new StringBuilder();
        sb.AppendLine("room,clientsReceiving,snapshotsReceived,maxServerTick,maxSnapshotLagTicks");
        foreach (var r in rooms)
        {
            sb.Append(r.Room).Append(',')
              .Append(r.ClientsReceiving).Append(',')
              .Append(r.SnapshotsReceived).Append(',')
              .Append(r.MaxServerTick).Append(',')
              .Append(r.MaxSnapshotLagTicks).Append('\n');
        }

        File.WriteAllText(path, sb.ToString());
    }

    public void WriteConsoleSummary(LoadResult result, TextWriter writer)
    {
        var m = result.Metrics;
        writer.WriteLine($"=== Load result: {result.ScenarioName} ({result.ScenarioType}) ===");
        writer.WriteLine($"clients: {result.TotalClients}  connections ok/fail: {m.SuccessfulConnections}/{m.FailedConnections}");
        writer.WriteLine($"messages sent/recv: {m.MessagesSent}/{m.MessagesReceived}  bytes sent/recv: {m.BytesSent}/{m.BytesReceived}");
        writer.WriteLine($"server errors: {m.ServerErrors}  unexpected closes: {m.UnexpectedCloses}  corrections: {m.Corrections}  malformed rejected: {m.MalformedFrameRejections}");
        writer.WriteLine($"connect p95: {m.ConnectLatency.P95Ms:0.##}ms  handshake p95: {m.HandshakeLatency.P95Ms:0.##}ms  join p95: {m.JoinLatency.P95Ms:0.##}ms");
        writer.WriteLine($"input→snapshot p95: {m.InputToSnapshotLatency.P95Ms:0.##}ms  max snapshot lag: {m.MaxSnapshotLagTicks} ticks");

        WritePerRoomSummary(result.Metrics.PerRoom, writer);
        WriteServerVerificationSummary(result.ServerVerification, writer);
    }

    // Client-side: how many rooms received data, and the weakest room by client-receive count.
    private static void WritePerRoomSummary(IReadOnlyList<RoomMetric> rooms, TextWriter writer)
    {
        if (rooms.Count == 0)
        {
            return;
        }

        var totalSnapshots = rooms.Sum(r => r.SnapshotsReceived);
        var minClients = rooms.Min(r => r.ClientsReceiving);
        var maxLag = rooms.Max(r => r.MaxSnapshotLagTicks);
        var weakest = rooms.OrderBy(r => r.ClientsReceiving).First();
        writer.WriteLine(
            $"per-room (client view): {rooms.Count} rooms received data  " +
            $"snapshots={totalSnapshots}  min clients-receiving/room={minClients}  worst lag={maxLag} ticks  " +
            $"(weakest: {weakest.Room} {weakest.ClientsReceiving} clients)");
    }

    // Server-side: the authoritative cross-check that every room is live and full.
    private static void WriteServerVerificationSummary(ServerVerification v, TextWriter writer)
    {
        if (v.ExpectedRooms == 0 && v.ObservedRooms == 0)
        {
            return;
        }

        writer.WriteLine(
            $"server check: {(v.Passed ? "PASS" : "FAIL")}  " +
            $"rooms {v.ObservedRooms}/{v.ExpectedRooms}  " +
            $"at {v.ExpectedSubscribersPerRoom} subscribers: {v.RoomsAtExpectedSubscribers}/{v.ExpectedRooms}  " +
            $"tick advancing: {v.RoomsAdvancing}/{v.ExpectedRooms}");
    }
}
