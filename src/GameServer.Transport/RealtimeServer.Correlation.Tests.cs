using GameServer.Observability;
using GameServer.Protocol;
using GameServer.Simulation;
using GameServer.Transport.Testing;
using Xunit;

namespace GameServer.Transport;

/// <summary>
/// Proves the end-to-end correlation requirement (Wave 8): a single, stable correlation id
/// threads one connection's whole lifecycle through the structured-telemetry trail —
/// connect → session → join → command (accepted) → command (rejected/error) — so an operator
/// can follow the entire session with one id. The per-message <c>traceId</c> a client mints is
/// echoed on the matching response and recorded alongside, but the correlation id is the single
/// connection-level thread that ties the legs together.
/// </summary>
public sealed class RealtimeServerCorrelationTests
{
    private static readonly RoomId Arena = new("arena");

    private static string CorrelationOf((string Name, IReadOnlyDictionary<string, string>? Fields) ev)
    {
        Assert.NotNull(ev.Fields);
        Assert.True(ev.Fields!.TryGetValue("correlationId", out var id),
            $"event '{ev.Name}' carries no correlationId; fields: {string.Join(",", ev.Fields!.Keys)}");
        Assert.False(string.IsNullOrWhiteSpace(id));
        return id!;
    }

    [Fact]
    public async Task OneCorrelationId_Flows_Connect_Through_Error()
    {
        var harness = new SliceHarness("tenant-a");
        var (transport, client) = harness.NewClient("c1", "tenant-a", "p1");

        // connect → hello → join → accepted command → a stale command that produces an error.
        client.Hello();
        client.Join(Arena);
        client.Command(MoveRightGame.MoveRight, Arena, sequence: 5); // accepted
        client.Command(MoveRightGame.MoveRight, Arena, sequence: 5); // stale -> ServerError (the error leg)
        client.Close();
        await harness.Server.HandleConnectionAsync(transport, client.Principal);

        var events = harness.Telemetry.Events.ToList();

        // Every leg of the chain must be observable AND carry one shared correlation id.
        var connect = events.Single(e => e.Name == TelemetryEvents.ConnectionOpened);
        var session = events.Single(e => e.Name == TelemetryEvents.SessionCreated);
        var join = events.Single(e => e.Name == TelemetryEvents.RoomJoined);
        var accepted = events.Single(e => e.Name == TelemetryEvents.CommandAccepted);
        var rejected = events.Single(e => e.Name == TelemetryEvents.CommandRejected);

        var id = CorrelationOf(connect);
        Assert.Equal(id, CorrelationOf(session));
        Assert.Equal(id, CorrelationOf(join));
        Assert.Equal(id, CorrelationOf(accepted));
        Assert.Equal(id, CorrelationOf(rejected));

        // The error leg also records the per-request traceId so a single request is pinpointable
        // within the correlated session.
        Assert.True(rejected.Fields!.ContainsKey("traceId"));

        // The wire response to the failing request echoes that request's traceId (per-message id).
        var error = client.Received().Select(m => m).Single(m => m.Payload is ServerError);
        Assert.False(string.IsNullOrWhiteSpace(error.TraceId));
    }

    [Fact]
    public async Task CorrelationId_IsStable_AcrossDifferentPerMessageTraceIds()
    {
        // The client mints a different traceId per message (see FakeClient). The correlation id
        // must NOT change between messages — it is fixed at hello for the connection's lifetime.
        var harness = new SliceHarness("tenant-a");
        var (transport, client) = harness.NewClient("c1", "tenant-a", "p1");

        client.Hello();
        client.Join(Arena);
        client.Close();
        await harness.Server.HandleConnectionAsync(transport, client.Principal);

        var events = harness.Telemetry.Events.ToList();
        var connectId = CorrelationOf(events.Single(e => e.Name == TelemetryEvents.ConnectionOpened));
        var joinId = CorrelationOf(events.Single(e => e.Name == TelemetryEvents.RoomJoined));

        Assert.Equal(connectId, joinId);
    }

    [Fact]
    public async Task RejectedConnect_StillCarriesCorrelationId()
    {
        // An unknown-tenant rejection (the failure leg with no session) must still be correlated:
        // connect and the rejection share one id even when the handshake never completes.
        var harness = new SliceHarness("tenant-a");
        var (transport, client) = harness.NewClient("c1", "ghost-tenant", "p1");

        client.Hello();
        client.Close();
        await harness.Server.HandleConnectionAsync(transport, client.Principal);

        var events = harness.Telemetry.Events.ToList();
        var connectId = CorrelationOf(events.Single(e => e.Name == TelemetryEvents.ConnectionOpened));
        var rejectId = CorrelationOf(events.Single(e => e.Name == TelemetryEvents.TenantRejected));

        Assert.Equal(connectId, rejectId);
    }
}
