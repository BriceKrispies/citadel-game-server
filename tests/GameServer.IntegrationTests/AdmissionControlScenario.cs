using System.Text;
using GameServer.Protocol;
using GameServer.Simulation;
using GameServer.Transport;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// Gap #4 - global admission control. Originally the edge accepted every connection and
/// created every room with no ceiling, so a burst could drive the process past capacity.
/// This scenario shows connections accepted without bound (before) versus shed cleanly at
/// a configured ceiling (after), and also exercises the per-tenant connection quota and
/// the maximum-rooms cap. Rejections are explicit <c>ServerError(Overloaded)</c> responses
/// counted as <c>admission_rejected</c>.
/// </summary>
public sealed class AdmissionControlScenario
{
    private readonly ITestOutputHelper _output;

    public AdmissionControlScenario(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ConnectionsRoomsAndTenantsAreCapped()
    {
        const int attempts = 40;
        const int connectionCap = 16;

        // --- global connection cap: before (unlimited) vs after (capped) ---
        var before = await OpenManyAsync(AdmissionPolicy.Unlimited, attempts, "tenant-a");
        var after = await OpenManyAsync(new AdmissionPolicy(MaxConnections: connectionCap), attempts, "tenant-a");

        Assert.Equal(attempts, before.Admitted);
        Assert.Equal(0, before.Rejected);
        Assert.Equal(connectionCap, after.Admitted);
        Assert.Equal(attempts - connectionCap, after.Rejected);

        // --- per-tenant quota: one tenant cannot starve another ---
        var tenantQuota = await PerTenantAsync(quotaPerTenant: 5, perTenantAttempts: 8);
        Assert.Equal(5, tenantQuota.AdmittedTenantA);
        Assert.Equal(5, tenantQuota.AdmittedTenantB);
        Assert.Equal(3, tenantQuota.RejectedTenantA);

        // --- max rooms: a new room beyond the ceiling is shed ---
        var rooms = await MaxRoomsAsync(maxRooms: 3, distinctRooms: 5);
        Assert.Equal(3, rooms.Created);
        Assert.Equal(2, rooms.Rejected);

        var dir = ArtifactWriter.Write(
            "04-admission-control",
            new
            {
                scenario = "admission-control",
                fixable = true,
                globalConnections = new
                {
                    attempts,
                    before = new { policy = "unlimited", admitted = before.Admitted, rejected = before.Rejected },
                    after = new { cap = connectionCap, admitted = after.Admitted, rejected = after.Rejected },
                },
                perTenantQuota = new
                {
                    quota = 5,
                    attemptsPerTenant = 8,
                    admittedTenantA = tenantQuota.AdmittedTenantA,
                    admittedTenantB = tenantQuota.AdmittedTenantB,
                    rejectedTenantA = tenantQuota.RejectedTenantA,
                },
                maxRooms = new { cap = 3, attempted = 5, created = rooms.Created, rejected = rooms.Rejected },
            },
            BuildMarkdown(attempts, connectionCap, before, after, tenantQuota, rooms));

        _output.WriteLine($"connections: before admit {before.Admitted}/{attempts} (0 shed); after admit {after.Admitted}/{attempts} ({after.Rejected} shed)");
        _output.WriteLine($"artifact: {dir}");
    }

    private static async Task<ConnResult> OpenManyAsync(AdmissionPolicy policy, int attempts, string tenant)
    {
        var harness = new IntegrationHarness(_ => new MoveRightGame(), admission: policy);
        var conns = new List<IntegrationHarness.OpenConnection>(attempts);
        for (var i = 0; i < attempts; i++)
        {
            conns.Add(harness.OpenConnectionFor(tenant, $"p{i}"));
        }

        var admitted = harness.Server.ActiveConnectionCount;
        var rejected = conns.Count(c => c.WasRejected);
        foreach (var c in conns)
        {
            await c.CloseAsync();
        }

        return new ConnResult(admitted, rejected);
    }

    private static async Task<TenantResult> PerTenantAsync(int quotaPerTenant, int perTenantAttempts)
    {
        var harness = new IntegrationHarness(
            _ => new MoveRightGame(),
            tenants: new[] { "tenant-a", "tenant-b" },
            admission: new AdmissionPolicy(MaxConnectionsPerTenant: quotaPerTenant));

        var a = new List<IntegrationHarness.OpenConnection>();
        var b = new List<IntegrationHarness.OpenConnection>();
        for (var i = 0; i < perTenantAttempts; i++)
        {
            a.Add(harness.OpenConnectionFor("tenant-a", $"a{i}"));
            b.Add(harness.OpenConnectionFor("tenant-b", $"b{i}"));
        }

        var rejectedA = a.Count(c => c.WasRejected);
        var rejectedB = b.Count(c => c.WasRejected);
        foreach (var c in a.Concat(b))
        {
            await c.CloseAsync();
        }

        return new TenantResult(perTenantAttempts - rejectedA, perTenantAttempts - rejectedB, rejectedA);
    }

    private static async Task<RoomResult> MaxRoomsAsync(int maxRooms, int distinctRooms)
    {
        // Persist so each created room stays counted while we probe the ceiling.
        var harness = new IntegrationHarness(
            _ => new MoveRightGame(),
            lifecycle: RoomLifecycle.Persist,
            admission: new AdmissionPolicy(MaxRooms: maxRooms));

        var created = 0;
        var rejected = 0;
        for (var i = 0; i < distinctRooms; i++)
        {
            var transport = await harness.RunClientAsync("tenant-a", $"room-{i}", "p1", "demo", Array.Empty<string>());
            var shed = transport.DrainOutbound().Any(m => m.Payload is ServerError { Code: ServerErrorCode.Overloaded });
            if (shed)
            {
                rejected++;
            }
            else
            {
                created++;
            }
        }

        return new RoomResult(created, rejected);
    }

    private sealed record ConnResult(int Admitted, int Rejected);
    private sealed record TenantResult(int AdmittedTenantA, int AdmittedTenantB, int RejectedTenantA);
    private sealed record RoomResult(int Created, int Rejected);

    private static string BuildMarkdown(
        int attempts, int cap, ConnResult before, ConnResult after, TenantResult tenant, RoomResult rooms)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Gap #4 — Global admission control");
        sb.AppendLine();
        sb.AppendLine("Without admission control the edge accepts everything; under load that is how a");
        sb.AppendLine("server falls over instead of shedding. Rejections below are explicit");
        sb.AppendLine("`ServerError(Overloaded)` responses, counted as `admission_rejected`.");
        sb.AppendLine();
        sb.AppendLine($"## Global connection cap ({attempts} simultaneous attempts)");
        sb.AppendLine();
        sb.AppendLine("| policy | admitted | shed |");
        sb.AppendLine("|---|---:|---:|");
        sb.AppendLine($"| unlimited (before) | {before.Admitted} | {before.Rejected} |");
        sb.AppendLine($"| cap = {cap} (after) | {after.Admitted} | {after.Rejected} |");
        sb.AppendLine();
        sb.AppendLine("## Per-tenant quota (quota 5, 8 attempts each for two tenants)");
        sb.AppendLine();
        sb.AppendLine($"- tenant-a admitted **{tenant.AdmittedTenantA}**, shed **{tenant.RejectedTenantA}**");
        sb.AppendLine($"- tenant-b admitted **{tenant.AdmittedTenantB}**");
        sb.AppendLine();
        sb.AppendLine("Each tenant gets its own ceiling, so one tenant's burst cannot consume another's");
        sb.AppendLine("capacity (noisy-neighbour isolation).");
        sb.AppendLine();
        sb.AppendLine("## Max rooms (cap 3, 5 distinct rooms attempted)");
        sb.AppendLine();
        sb.AppendLine($"- rooms created **{rooms.Created}**, joins shed **{rooms.Rejected}**");
        sb.AppendLine();
        sb.AppendLine("A join that would create a NEW room beyond the ceiling is rejected; joins into");
        sb.AppendLine("already-running rooms are unaffected.");
        return sb.ToString();
    }
}
