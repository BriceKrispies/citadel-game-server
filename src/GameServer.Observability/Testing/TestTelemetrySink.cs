using System.Collections.Concurrent;

namespace GameServer.Observability.Testing;

/// <summary>
/// Recording telemetry sink. Captures everything emitted so tests can assert that
/// meaningful operations are observable, without any real metrics backend.
/// Cross-feature test support, so it lives in <c>Testing/</c>.
/// </summary>
public sealed class TestTelemetrySink : ITelemetrySink
{
    public ConcurrentBag<(string Metric, IReadOnlyDictionary<string, string>? Tags)> Increments { get; } = new();
    public ConcurrentBag<(string Metric, double Value, IReadOnlyDictionary<string, string>? Tags)> Measures { get; } = new();
    public ConcurrentBag<(string Name, IReadOnlyDictionary<string, string>? Fields)> Events { get; } = new();

    public void Increment(string metric, IReadOnlyDictionary<string, string>? tags = null) =>
        Increments.Add((metric, tags));

    public void Measure(string metric, double value, IReadOnlyDictionary<string, string>? tags = null) =>
        Measures.Add((metric, value, tags));

    public void Event(string name, IReadOnlyDictionary<string, string>? fields = null) =>
        Events.Add((name, fields));

    public int CountIncrements(string metric) => Increments.Count(i => i.Metric == metric);

    public bool HasEvent(string name) => Events.Any(e => e.Name == name);
}
