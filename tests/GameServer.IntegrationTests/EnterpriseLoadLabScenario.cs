using System.Diagnostics;
using GameServer.Observability;
using GameServer.Protocol;
using GameServer.Replication;
using GameServer.Routing;
using GameServer.Simulation;
using GameServer.Transport;
using Xunit.Abstractions;

namespace GameServer.IntegrationTests;

/// <summary>
/// The enterprise stress lab: many tenants, many rooms, tens of thousands of held-open clients,
/// one deliberately hot room, random reconnect churn, and a sprinkle of malformed input — driven
/// against the REAL in-process <see cref="RealtimeServer"/> kernel at a configured 30 Hz tick, then
/// asserted against four gates and written to an evidence artifact:
/// <list type="number">
///   <item>p95 input-to-fanout latency under a (generous, tick-derived) bound — recorded and reported;</item>
///   <item>no cross-tenant / cross-room messages — every client only ever receives its own;</item>
///   <item>no room-state divergence — authoritative <c>Project()</c> equals a replay of the room's
///         applied-command event log (server-sourced ground truth, independent of the client);</item>
///   <item>no unhandled errors — no faulted connection loops, malformed input is cleanly rejected,
///         and no unexpected error codes appear.</item>
/// </list>
/// Why in-process: a single box tops out at a few thousand REAL WebSocket connections (sockets,
/// TLS, Kestrel), so the faithful 50k shape is only reachable in-process (channels, no sockets).
/// This is the deterministic counterpart to the real-socket LoadHarness run.
///
/// Scale is env-overridable so CI runs a moderate-but-representative default in a few seconds, while
/// the full headline shape is one command away:
/// <code>
///   CITADEL_LAB_TENANTS=20 CITADEL_LAB_ROOMS=1000 CITADEL_LAB_CLIENTS=50000 \
///   CITADEL_LAB_HOTROOM=5000 dotnet test tests/GameServer.IntegrationTests \
///   --filter EnterpriseLoadLabScenario
/// </code>
/// </summary>
public sealed class EnterpriseLoadLabScenario
{
    private const string Game = "grid-walk";
    private static readonly string[] Directions = { GridWalkGame.Up, GridWalkGame.Down, GridWalkGame.Left, GridWalkGame.Right };
    private const string Malformed = "Teleport"; // not a GridWalk command → rejected as InvalidCommand

    private readonly ITestOutputHelper _output;

    public EnterpriseLoadLabScenario(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task EnterpriseScenario_MeetsAllGates_AndWritesArtifact()
    {
        var cfg = LabConfig.FromEnvironment();
        var tickIntervalMs = 1000.0 / cfg.TickHz;
        // Record-and-report with a generous, tick-derived gate (4 tick intervals, floored at 100ms)
        // so the assertion is meaningful without being flaky on a loaded CI box. The MEASURED p95 is
        // what matters and is surfaced in the artifact.
        var latencyGateMs = Math.Max(100.0, 4 * tickIntervalMs);

        var tenants = Enumerable.Range(0, cfg.Tenants).Select(i => $"tenant-{i:D2}").ToArray();
        var policy = new ReplicationPolicy(
            SnapshotMode.Full, InterestKind.Everyone, Radius: 0, CellSize: 0, NeighborRings: 0,
            // A hard per-client byte budget bounds the hot room's fan-out (its O(N²) shape would
            // otherwise blow up memory); normal small rooms send everything under it.
            PerClientBudgetBytes: 256, SendRateHz: (int)cfg.TickHz);

        var harness = new IntegrationHarness(
            _ => new GridWalkGame(),
            tenants: tenants,
            policy: _ => policy,
            // Reap so a reconnect (close → reopen) actually frees the old subscriber; event-log
            // retention 0 (unbounded) so the full applied-command history is available for the
            // divergence replay; generous timeouts so nothing is reaped mid-measurement.
            lifecycle: RoomLifecycle.Reap,
            handshakeTimeout: TimeSpan.FromMinutes(2),
            idlePolicy: new HeartbeatIdlePolicy(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)));

        var scheduler = ParallelRoomTickScheduler.ForProcessorCount();
        var rng = new Random(20260525); // seeded → deterministic malformed/churn selection
        var sw = Stopwatch.StartNew();

        // ---- Topology -------------------------------------------------------
        // room-0000 is the hot room; rooms are assigned round-robin to tenants. The hot room's
        // clients all belong to its tenant (a realistic "one tenant has a flagship room").
        var roomTenant = new string[cfg.Rooms];
        for (var r = 0; r < cfg.Rooms; r++)
        {
            roomTenant[r] = tenants[r % cfg.Tenants];
        }

        string RoomName(int r) => $"room-{r:D4}";
        const int HotRoomIndex = 0;

        // Assign each client to (roomIndex, player). The first HotRoomClients land in the hot room;
        // the rest spread round-robin over the remaining rooms.
        var clientRoomIndex = new int[cfg.Clients];
        for (var i = 0; i < cfg.Clients; i++)
        {
            clientRoomIndex[i] = i < cfg.HotRoomClients
                ? HotRoomIndex
                : 1 + ((i - cfg.HotRoomClients) % Math.Max(1, cfg.Rooms - 1));
        }

        var counters = new LabCounters();
        var allLoops = new List<Task>(cfg.Clients);

        // ---- Phase 1: open all clients (hello + join), then wait for joins to settle ----
        var clients = new IntegrationHarness.LabClient[cfg.Clients];
        for (var i = 0; i < cfg.Clients; i++)
        {
            var r = clientRoomIndex[i];
            var c = harness.OpenLabClient(roomTenant[r], RoomName(r), $"p{i}", Game);
            clients[i] = c;
            allLoops.Add(c.Loop);
            counters.ClientsOpened++;
        }

        var expectedRooms = DistinctRoomCount(clientRoomIndex);
        await WaitUntilAsync(
            () => harness.Server.ActiveRooms.Count >= expectedRooms && TotalWalkers(harness) >= cfg.Clients,
            TimeSpan.FromSeconds(90));

        var settledRooms = harness.Server.ActiveRooms.Count;
        var settledWalkers = TotalWalkers(harness);
        _output.WriteLine($"settled: rooms={settledRooms}/{expectedRooms} walkers={settledWalkers}/{cfg.Clients} in {sw.ElapsedMilliseconds}ms");

        // Probe cohort: a sample of NON-hot-room clients (their rooms are uncontended, so their
        // input is reliably accepted and reflected) used to measure input-to-fanout latency. Tracked
        // by index and never churned, so the probe connection stays the same object for the run.
        var probeIndices = SelectProbeIndices(clientRoomIndex, HotRoomIndex, sampleSize: 200);
        var isProbe = new bool[cfg.Clients];
        foreach (var idx in probeIndices)
        {
            isProbe[idx] = true;
        }

        // Reconnect churners: a random 5% (excluding probes) that will close + reopen mid-run.
        var churnTargets = SelectChurners(cfg, isProbe, rng);

        // The hot room is special: with "everyone sees everyone" replication, a single 5,000-subscriber
        // room is inherently O(N²) per tick — so it is ticked sparingly (it still proves fan-out,
        // backpressure shedding, and divergence) and its per-tick cost is reported on its OWN line.
        // Normal rooms tick every round and are what the input-to-fanout latency is measured against —
        // production ticks rooms CONCURRENTLY, so a normal client's input never waits on the hot room.
        var hotKey = harness.Key(roomTenant[HotRoomIndex], RoomName(HotRoomIndex));
        var hotRoomTickMsMax = 0.0;
        var hotRoomTicks = 0;

        List<RoomKey> NormalRooms() => harness.Server.ActiveRooms.Where(k => !k.Equals(hotKey)).ToList();

        async Task TickHotRoomAsync()
        {
            var t0 = Stopwatch.GetTimestamp();
            await harness.Server.TickRoom(hotKey);
            hotRoomTickMsMax = Math.Max(hotRoomTickMsMax, Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            hotRoomTicks++;
        }

        // ---- Phase 2: steady load — every client sends each round; tick; drain; churn ----
        var cycleMs = new List<double>(cfg.SteadyRounds + cfg.ProbeRounds);
        var churnSchedule = SpreadOverRounds(churnTargets, cfg.SteadyRounds);
        var hotTickEvery = Math.Max(1, cfg.SteadyRounds / 2); // ~2 hot-room ticks during steady load

        for (var round = 0; round < cfg.SteadyRounds; round++)
        {
            foreach (var c in clients)
            {
                var malformed = rng.NextDouble() * 100.0 < cfg.MalformedPercent;
                if (malformed)
                {
                    c.Send(Malformed);
                    counters.MalformedSent++;
                }
                else
                {
                    c.Send(Directions[rng.Next(Directions.Length)]);
                }

                counters.CommandsSent++;
            }

            await SettleAsync(); // let the connection loops move commands into room queues
            var report = await scheduler.TickCycleAsync(NormalRooms(), harness.TickFn);
            cycleMs.Add(report.TotalElapsedMs);
            if (round % hotTickEvery == 0)
            {
                await TickHotRoomAsync();
            }

            DrainAll(clients, counters);

            // Reconnect this round's churners: open the replacement BEFORE closing the old one, so
            // the room never drops to zero subscribers (its authoritative state is preserved).
            if (churnSchedule.TryGetValue(round, out var toChurn))
            {
                foreach (var idx in toChurn)
                {
                    var old = clients[idx];
                    var r = clientRoomIndex[idx];
                    // Resume the per-player command sequence across the reconnect (the room's gate
                    // persists for the player), otherwise the replacement's commands are stale.
                    var replacement = harness.OpenLabClient(roomTenant[r], RoomName(r), $"p{idx}", Game, old.NextSequence);
                    clients[idx] = replacement;
                    allLoops.Add(replacement.Loop);
                    counters.Reconnects++;
                    counters.ClientsOpened++;
                    await old.CloseAsync();
                }
            }
        }

        // ---- Phase 3: latency probe rounds — only probes send; measure send→fanout per client ----
        // Tick ONLY the probe rooms in the measured window. This is the input-to-fanout a real client
        // experiences: production ticks rooms concurrently, so a normal client's snapshot is produced
        // when ITS room ticks, never gated behind the expensive hot room.
        var latencies = new List<double>(probeIndices.Count * cfg.ProbeRounds);
        var probeRoomKeys = probeIndices
            .Select(i => harness.Key(roomTenant[clientRoomIndex[i]], RoomName(clientRoomIndex[i])))
            .Distinct().ToList();

        for (var round = 0; round < cfg.ProbeRounds; round++)
        {
            foreach (var idx in probeIndices)
            {
                clients[idx].DrainReceived(); // clear stale fan-out so we only see this round's snapshot
            }

            var sentAt = new long[probeIndices.Count];
            for (var i = 0; i < probeIndices.Count; i++)
            {
                sentAt[i] = Stopwatch.GetTimestamp();
                clients[probeIndices[i]].Send(GridWalkGame.Right);
            }

            // The command must reach the room queue before we tick; spin (bounded) on the real
            // queue depth so the measured window is the genuine enqueue-wait + tick + fan-out.
            await WaitUntilAsync(
                () => TotalQueueDepth(harness, probeRoomKeys) >= probeIndices.Count,
                TimeSpan.FromMilliseconds(500));

            await scheduler.TickCycleAsync(probeRoomKeys, harness.TickFn);
            var tickEnd = Stopwatch.GetTimestamp();

            for (var i = 0; i < probeIndices.Count; i++)
            {
                var probe = clients[probeIndices[i]];
                var received = probe.DrainReceived();
                if (received.Any(m => m.Payload is ServerSnapshot))
                {
                    latencies.Add(Stopwatch.GetElapsedTime(sentAt[i], tickEnd).TotalMilliseconds);
                }

                counters.Observe(received, probe);
            }

            // Drain the non-probe clients too, so their unbounded outbound channels do not grow.
            for (var i = 0; i < clients.Length; i++)
            {
                if (!isProbe[i])
                {
                    counters.Observe(clients[i].DrainReceived(), clients[i]);
                }
            }
        }

        // ---- Phase 4: drain ticks — flush queued commands (incl. the hot room) into state ----
        for (var i = 0; i < cfg.DrainTicks; i++)
        {
            await SettleAsync();
            var report = await scheduler.TickCycleAsync(NormalRooms(), harness.TickFn);
            cycleMs.Add(report.TotalElapsedMs);
            await TickHotRoomAsync();
            DrainAll(clients, counters);
        }

        // ---- Phase 5: divergence check — authoritative state vs event-log replay ----
        var divergence = CheckDivergence(harness);

        // ---- Phase 6: assemble, assert gates, write artifact ----
        var tele = harness.Telemetry.Snapshot();
        var latencyP95 = Percentile(latencies, 95);
        var report6 = new LabReport(
            Config: cfg,
            DurationMs: sw.ElapsedMilliseconds,
            SettledRooms: settledRooms,
            SettledWalkers: settledWalkers,
            ProbeCount: probeIndices.Count,
            Counters: counters,
            CycleMsP50: Percentile(cycleMs, 50),
            CycleMsP95: Percentile(cycleMs, 95),
            CycleMsMax: cycleMs.Count > 0 ? cycleMs.Max() : 0,
            HotRoomTicks: hotRoomTicks,
            HotRoomTickMsMax: hotRoomTickMsMax,
            LatencyP50: Percentile(latencies, 50),
            LatencyP95: latencyP95,
            LatencyP99: Percentile(latencies, 99),
            LatencyMax: latencies.Count > 0 ? latencies.Max() : 0,
            LatencyGateMs: latencyGateMs,
            Divergence: divergence,
            FaultedLoops: allLoops.Count(t => t.IsFaulted),
            Telemetry: tele);

        var dir = ArtifactWriter.Write("enterprise-load-lab", ToJson(report6), ToMarkdown(report6));
        _output.WriteLine(ToMarkdown(report6));
        _output.WriteLine($"artifact: {dir}");

        // Close every still-open client (after the divergence check has read authoritative state).
        foreach (var c in clients)
        {
            await c.CloseAsync();
        }

        // ---- Gates --------------------------------------------------------------
        // Gate 2: no cross-tenant / cross-room delivery.
        Assert.Equal(0, counters.CrossTenantEnvelopes);
        Assert.Equal(0, counters.CrossRoomEnvelopes);

        // Gate 3: no room-state divergence (authoritative == event-log replay, every room).
        Assert.Equal(0, divergence.MismatchedRooms);
        Assert.True(divergence.CheckedRooms > 0, "divergence check examined no rooms");

        // Gate 4: no unhandled errors.
        Assert.Equal(0, report6.FaultedLoops);
        Assert.True(counters.InvalidCommandErrors >= counters.MalformedSent,
            $"malformed input not fully rejected: invalidCommandErrors={counters.InvalidCommandErrors} < malformedSent={counters.MalformedSent}");
        Assert.Equal(0, counters.UnexpectedErrors); // only InvalidCommand (malformed) + Overloaded (backpressure) are allowed

        // Gate 1: p95 input-to-fanout latency under the generous, tick-derived bound (recorded above).
        Assert.True(latencies.Count > 0, "no latency samples were collected");
        Assert.True(latencyP95 <= latencyGateMs,
            $"p95 input-to-fanout {latencyP95:0.##}ms exceeded gate {latencyGateMs:0.##}ms");
    }

    // ---- Helpers ------------------------------------------------------------

    private static int DistinctRoomCount(int[] clientRoomIndex)
    {
        var seen = new HashSet<int>();
        foreach (var r in clientRoomIndex)
        {
            seen.Add(r);
        }

        return seen.Count;
    }

    private static int TotalWalkers(IntegrationHarness harness)
    {
        var total = 0;
        foreach (var key in harness.Server.ActiveRooms)
        {
            if (harness.Router.TryGetRoom(key, out var room))
            {
                lock (room)
                {
                    total += room.Project().Count;
                }
            }
        }

        return total;
    }

    private static int TotalQueueDepth(IntegrationHarness harness, IReadOnlyList<RoomKey> rooms)
    {
        var total = 0;
        foreach (var key in rooms)
        {
            if (harness.Router.TryGetRoom(key, out var room))
            {
                total += room.QueueDepth;
            }
        }

        return total;
    }

    private static List<int> SelectProbeIndices(int[] roomIndex, int hotRoomIndex, int sampleSize)
    {
        var normal = new List<int>();
        for (var i = 0; i < roomIndex.Length; i++)
        {
            if (roomIndex[i] != hotRoomIndex)
            {
                normal.Add(i);
            }
        }

        var probes = new List<int>(Math.Min(sampleSize, normal.Count));
        if (normal.Count == 0)
        {
            return probes;
        }

        var step = Math.Max(1, normal.Count / Math.Max(1, sampleSize));
        for (var k = 0; k < normal.Count && probes.Count < sampleSize; k += step)
        {
            probes.Add(normal[k]);
        }

        return probes;
    }

    private static HashSet<int> SelectChurners(LabConfig cfg, bool[] isProbe, Random rng)
    {
        var target = (int)(cfg.Clients * cfg.ReconnectPercent / 100.0);
        var churners = new HashSet<int>();
        var guard = 0;
        while (churners.Count < target && guard++ < Math.Max(1, target) * 40)
        {
            var idx = rng.Next(cfg.Clients);
            if (!isProbe[idx])
            {
                churners.Add(idx);
            }
        }

        return churners;
    }

    private static Dictionary<int, List<int>> SpreadOverRounds(HashSet<int> churners, int rounds)
    {
        var schedule = new Dictionary<int, List<int>>();
        if (rounds <= 0)
        {
            return schedule;
        }

        var round = 0;
        foreach (var idx in churners)
        {
            var r = round++ % rounds;
            if (!schedule.TryGetValue(r, out var list))
            {
                schedule[r] = list = new List<int>();
            }

            list.Add(idx);
        }

        return schedule;
    }

    private static void DrainAll(IEnumerable<IntegrationHarness.LabClient> clients, LabCounters counters)
    {
        foreach (var c in clients)
        {
            counters.Observe(c.DrainReceived(), c);
        }
    }

    private static DivergenceResult CheckDivergence(IntegrationHarness harness)
    {
        var checkedRooms = 0;
        var mismatched = 0;
        var firstMismatch = string.Empty;

        foreach (var key in harness.Server.ActiveRooms)
        {
            if (!harness.Router.TryGetRoom(key, out var room))
            {
                continue;
            }

            IReadOnlyList<EntitySnapshot> authoritative;
            lock (room)
            {
                authoritative = room.Project();
            }

            // Server-sourced ground truth: replay the room's applied-command log into a fresh game
            // and compare positions. If the authoritative state ever diverged from the events that
            // produced it, these would differ.
            var replay = new GridWalkGame();
            foreach (var e in authoritative)
            {
                replay.Join(new PlayerId(e.Id.Value));
            }

            foreach (var ev in harness.Events.Read(key))
            {
                replay.Join(ev.Player);
                replay.Apply(ev.Player, ev.Command);
            }

            var expected = replay.Project().ToDictionary(e => e.Id.Value, e => GridWalkGame.Decode(e.Payload));
            checkedRooms++;

            foreach (var e in authoritative)
            {
                var actual = GridWalkGame.Decode(e.Payload);
                if (!expected.TryGetValue(e.Id.Value, out var exp) || exp != actual)
                {
                    mismatched++;
                    if (firstMismatch.Length == 0)
                    {
                        firstMismatch =
                            $"{key.TenantId.Value}/{key.RoomId.Value} {e.Id.Value}: authoritative={actual} " +
                            $"replay={(expected.TryGetValue(e.Id.Value, out var x) ? x.ToString() : "<none>")}";
                    }

                    break;
                }
            }
        }

        return new DivergenceResult(checkedRooms, mismatched, firstMismatch);
    }

    private static Task SettleAsync() => Task.Delay(2);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout)
            {
                return;
            }

            await Task.Delay(5);
        }
    }

    private static double Percentile(List<double> values, int percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.ToArray();
        Array.Sort(sorted);
        var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1;
        rank = Math.Clamp(rank, 0, sorted.Length - 1);
        return sorted[rank];
    }

    private static object ToJson(LabReport r) => new
    {
        topology = new
        {
            tenants = r.Config.Tenants,
            rooms = r.Config.Rooms,
            clients = r.Config.Clients,
            hotRoomClients = r.Config.HotRoomClients,
            settledRooms = r.SettledRooms,
            settledWalkers = r.SettledWalkers,
            tickHz = r.Config.TickHz,
            malformedPercent = r.Config.MalformedPercent,
            reconnectPercent = r.Config.ReconnectPercent,
        },
        durationMs = r.DurationMs,
        traffic = new
        {
            clientsOpened = r.Counters.ClientsOpened,
            reconnects = r.Counters.Reconnects,
            commandsSent = r.Counters.CommandsSent,
            malformedSent = r.Counters.MalformedSent,
            snapshotsReceived = r.Counters.SnapshotsReceived,
            serverErrors = r.Counters.ServerErrors,
            invalidCommandErrors = r.Counters.InvalidCommandErrors,
            overloadedErrors = r.Counters.OverloadedErrors,
            unexpectedErrors = r.Counters.UnexpectedErrors,
        },
        gates = new
        {
            latency = new
            {
                p50Ms = r.LatencyP50,
                p95Ms = r.LatencyP95,
                p99Ms = r.LatencyP99,
                maxMs = r.LatencyMax,
                gateMs = r.LatencyGateMs,
                pass = r.LatencyP95 <= r.LatencyGateMs,
            },
            crossTenant = new
            {
                crossTenantEnvelopes = r.Counters.CrossTenantEnvelopes,
                crossRoomEnvelopes = r.Counters.CrossRoomEnvelopes,
                pass = r.Counters.CrossTenantEnvelopes == 0 && r.Counters.CrossRoomEnvelopes == 0,
            },
            divergence = new
            {
                checkedRooms = r.Divergence.CheckedRooms,
                mismatchedRooms = r.Divergence.MismatchedRooms,
                firstMismatch = r.Divergence.FirstMismatch,
                pass = r.Divergence.MismatchedRooms == 0,
            },
            unhandledErrors = new
            {
                faultedLoops = r.FaultedLoops,
                malformedRejected = r.Counters.InvalidCommandErrors >= r.Counters.MalformedSent,
                unexpectedErrors = r.Counters.UnexpectedErrors,
                pass = r.FaultedLoops == 0 && r.Counters.InvalidCommandErrors >= r.Counters.MalformedSent && r.Counters.UnexpectedErrors == 0,
            },
        },
        fanoutCycleMs = new { p50 = r.CycleMsP50, p95 = r.CycleMsP95, max = r.CycleMsMax },
        hotRoom = new { clients = r.Config.HotRoomClients, ticks = r.HotRoomTicks, tickMsMax = r.HotRoomTickMsMax },
        telemetry = new
        {
            connections_opened = r.Telemetry.Counter(TelemetryMetrics.ConnectionsOpened),
            connections_closed = r.Telemetry.Counter(TelemetryMetrics.ConnectionsClosed),
            messages_in = r.Telemetry.Counter(TelemetryMetrics.MessagesIn),
            messages_out = r.Telemetry.Counter(TelemetryMetrics.MessagesOut),
            commands_accepted = r.Telemetry.Counter(TelemetryMetrics.CommandsAccepted),
            commands_rejected = r.Telemetry.Counter(TelemetryMetrics.CommandsRejected),
            backpressure_rejections = r.Telemetry.Counter(TelemetryMetrics.BackpressureRejections),
            invalid_messages = r.Telemetry.Counter(TelemetryMetrics.InvalidMessages),
            snapshots_emitted = r.Telemetry.Counter(TelemetryMetrics.SnapshotsEmitted),
            missed_ticks = r.Telemetry.Counter(TelemetryMetrics.MissedTicks),
            tick_duration_ms_mean = r.Telemetry.Measure(TelemetryMetrics.TickDurationMs).Mean,
            tick_duration_ms_max = r.Telemetry.Measure(TelemetryMetrics.TickDurationMs).Max,
        },
    };

    private static string ToMarkdown(LabReport r)
    {
        bool latPass = r.LatencyP95 <= r.LatencyGateMs;
        bool xtPass = r.Counters.CrossTenantEnvelopes == 0 && r.Counters.CrossRoomEnvelopes == 0;
        bool divPass = r.Divergence.MismatchedRooms == 0;
        bool errPass = r.FaultedLoops == 0 && r.Counters.InvalidCommandErrors >= r.Counters.MalformedSent && r.Counters.UnexpectedErrors == 0;
        string Mark(bool b) => b ? "PASS" : "FAIL";

        return $"""
        # Enterprise Load Lab

        In-process run against the real `RealtimeServer` kernel at {r.Config.TickHz:0} Hz.

        ## Topology
        | knob | value |
        |---|---|
        | tenants | {r.Config.Tenants} |
        | rooms | {r.Config.Rooms} (settled live: {r.SettledRooms}) |
        | clients | {r.Config.Clients} (settled walkers: {r.SettledWalkers}) |
        | hot room | room-0000 with {r.Config.HotRoomClients} clients |
        | malformed input | {r.Config.MalformedPercent:0.###}% of commands |
        | reconnect churn | {r.Config.ReconnectPercent:0.#}% of clients |
        | run duration | {r.DurationMs} ms |

        ## Gates
        | gate | result | detail |
        |---|---|---|
        | p95 input->fanout latency | {Mark(latPass)} | p50={r.LatencyP50:0.##}ms p95={r.LatencyP95:0.##}ms p99={r.LatencyP99:0.##}ms max={r.LatencyMax:0.##}ms (gate <= {r.LatencyGateMs:0.##}ms) |
        | no cross-tenant messages | {Mark(xtPass)} | cross-tenant={r.Counters.CrossTenantEnvelopes} cross-room={r.Counters.CrossRoomEnvelopes} |
        | no room-state divergence | {Mark(divPass)} | checked {r.Divergence.CheckedRooms} rooms, {r.Divergence.MismatchedRooms} mismatched{(r.Divergence.FirstMismatch.Length > 0 ? $" - {r.Divergence.FirstMismatch}" : string.Empty)} |
        | no unhandled errors | {Mark(errPass)} | faulted loops={r.FaultedLoops}, malformed rejected={r.Counters.InvalidCommandErrors}/{r.Counters.MalformedSent}, unexpected errors={r.Counters.UnexpectedErrors} |

        ## Fan-out cycle cost (normal rooms — ticked every round, concurrently)
        p50={r.CycleMsP50:0.##}ms  p95={r.CycleMsP95:0.##}ms  max={r.CycleMsMax:0.##}ms (one cycle = tick every normal room + fan out to its subscribers)

        ## Hot room ({r.Config.HotRoomClients} subscribers, "everyone" replication)
        A single everyone-sees-everyone room is inherently O(N²) per tick; ticked {r.HotRoomTicks}x (sparingly), worst single-tick fan-out cost = {r.HotRoomTickMsMax:0.##}ms. This is the cost a real deployment avoids with interest management (radius/grid) or by sharding the room — surfaced here as the hotspot, separate from normal-client latency.

        ## Traffic
        | metric | value |
        |---|---|
        | clients opened (incl. reconnects) | {r.Counters.ClientsOpened} |
        | reconnects | {r.Counters.Reconnects} |
        | commands sent | {r.Counters.CommandsSent} |
        | malformed sent | {r.Counters.MalformedSent} |
        | snapshots received | {r.Counters.SnapshotsReceived} |
        | server errors (invalid / overloaded) | {r.Counters.InvalidCommandErrors} / {r.Counters.OverloadedErrors} |

        ## Server telemetry (aggregated)
        | metric | value |
        |---|---|
        | connections_opened | {r.Telemetry.Counter(TelemetryMetrics.ConnectionsOpened)} |
        | messages_in | {r.Telemetry.Counter(TelemetryMetrics.MessagesIn)} |
        | messages_out | {r.Telemetry.Counter(TelemetryMetrics.MessagesOut)} |
        | commands_accepted | {r.Telemetry.Counter(TelemetryMetrics.CommandsAccepted)} |
        | commands_rejected | {r.Telemetry.Counter(TelemetryMetrics.CommandsRejected)} |
        | backpressure_rejections | {r.Telemetry.Counter(TelemetryMetrics.BackpressureRejections)} |
        | snapshots_emitted | {r.Telemetry.Counter(TelemetryMetrics.SnapshotsEmitted)} |
        | tick_duration_ms (mean/max) | {r.Telemetry.Measure(TelemetryMetrics.TickDurationMs).Mean:0.##} / {r.Telemetry.Measure(TelemetryMetrics.TickDurationMs).Max:0.##} |
        | missed_ticks | {r.Telemetry.Counter(TelemetryMetrics.MissedTicks)} |
        """;
    }

    // ---- Config / records ---------------------------------------------------

    private sealed record LabConfig(
        int Tenants, int Rooms, int Clients, int HotRoomClients,
        double MalformedPercent, double ReconnectPercent, double TickHz,
        int SteadyRounds, int ProbeRounds, int DrainTicks)
    {
        public static LabConfig FromEnvironment()
        {
            var clients = EnvInt("CITADEL_LAB_CLIENTS", 5000);
            var rooms = Math.Max(2, EnvInt("CITADEL_LAB_ROOMS", 200));
            var tenants = Math.Clamp(EnvInt("CITADEL_LAB_TENANTS", 20), 1, rooms);
            // Clamp so the assignment is always coherent (hot room ⊆ clients, ≥1 normal room).
            var hot = Math.Clamp(EnvInt("CITADEL_LAB_HOTROOM", Math.Min(1000, clients / 2)), 0, clients);
            return new LabConfig(
                tenants, rooms, clients, hot,
                MalformedPercent: EnvDouble("CITADEL_LAB_MALFORMED_PCT", 0.1),
                ReconnectPercent: EnvDouble("CITADEL_LAB_RECONNECT_PCT", 5.0),
                TickHz: EnvDouble("CITADEL_LAB_TICK_HZ", 30.0),
                SteadyRounds: EnvInt("CITADEL_LAB_STEADY_ROUNDS", 20),
                ProbeRounds: EnvInt("CITADEL_LAB_PROBE_ROUNDS", 20),
                DrainTicks: EnvInt("CITADEL_LAB_DRAIN_TICKS", 5));
        }

        private static int EnvInt(string name, int fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;

        private static double EnvDouble(string name, double fallback) =>
            double.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;
    }

    private sealed class LabCounters
    {
        public long ClientsOpened;
        public long Reconnects;
        public long CommandsSent;
        public long MalformedSent;
        public long SnapshotsReceived;
        public long ServerErrors;
        public long InvalidCommandErrors;
        public long OverloadedErrors;
        public long UnexpectedErrors;
        public long CrossTenantEnvelopes;
        public long CrossRoomEnvelopes;

        public void Observe(IReadOnlyList<MessageEnvelope> received, IntegrationHarness.LabClient owner)
        {
            foreach (var m in received)
            {
                if (m.TenantId.Value != owner.Tenant)
                {
                    CrossTenantEnvelopes++;
                }

                if (m.RoomId is { } rid && rid.Value != owner.Room)
                {
                    CrossRoomEnvelopes++;
                }

                switch (m.Payload)
                {
                    case ServerSnapshot:
                        SnapshotsReceived++;
                        break;
                    case ServerError error:
                        ServerErrors++;
                        switch (error.Code)
                        {
                            case ServerErrorCode.InvalidCommand:
                                InvalidCommandErrors++;
                                break;
                            case ServerErrorCode.Overloaded:
                                OverloadedErrors++;
                                break;
                            default:
                                // StaleSequence / Unauthorized / MalformedMessage / NotJoined / etc.
                                // would each signal a real bug under this scenario.
                                UnexpectedErrors++;
                                break;
                        }

                        break;
                }
            }
        }
    }

    private sealed record DivergenceResult(int CheckedRooms, int MismatchedRooms, string FirstMismatch);

    private sealed record LabReport(
        LabConfig Config,
        long DurationMs,
        int SettledRooms,
        int SettledWalkers,
        int ProbeCount,
        LabCounters Counters,
        double CycleMsP50,
        double CycleMsP95,
        double CycleMsMax,
        int HotRoomTicks,
        double HotRoomTickMsMax,
        double LatencyP50,
        double LatencyP95,
        double LatencyP99,
        double LatencyMax,
        double LatencyGateMs,
        DivergenceResult Divergence,
        int FaultedLoops,
        TelemetrySnapshot Telemetry);
}
