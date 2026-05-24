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

/// <summary>The final result of a scenario run: config identity + metrics + failure counts.</summary>
public sealed record LoadResult(
    string ScenarioName,
    string ScenarioType,
    int TotalClients,
    MetricsSnapshot Metrics,
    IReadOnlyDictionary<string, long> FailureCounts);

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

    public void WriteConsoleSummary(LoadResult result, TextWriter writer)
    {
        var m = result.Metrics;
        writer.WriteLine($"=== Load result: {result.ScenarioName} ({result.ScenarioType}) ===");
        writer.WriteLine($"clients: {result.TotalClients}  connections ok/fail: {m.SuccessfulConnections}/{m.FailedConnections}");
        writer.WriteLine($"messages sent/recv: {m.MessagesSent}/{m.MessagesReceived}  bytes sent/recv: {m.BytesSent}/{m.BytesReceived}");
        writer.WriteLine($"server errors: {m.ServerErrors}  unexpected closes: {m.UnexpectedCloses}  corrections: {m.Corrections}  malformed rejected: {m.MalformedFrameRejections}");
        writer.WriteLine($"connect p95: {m.ConnectLatency.P95Ms:0.##}ms  handshake p95: {m.HandshakeLatency.P95Ms:0.##}ms  join p95: {m.JoinLatency.P95Ms:0.##}ms");
        writer.WriteLine($"input→snapshot p95: {m.InputToSnapshotLatency.P95Ms:0.##}ms  max snapshot lag: {m.MaxSnapshotLagTicks} ticks");
    }
}
