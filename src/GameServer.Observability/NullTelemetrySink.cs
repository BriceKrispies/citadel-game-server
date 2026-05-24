namespace GameServer.Observability;

/// <summary>No-op sink for contexts that do not care about telemetry. Never null-checks at call sites.</summary>
public sealed class NullTelemetrySink : ITelemetrySink
{
    public static readonly NullTelemetrySink Instance = new();

    private NullTelemetrySink() { }

    public void Increment(string metric, IReadOnlyDictionary<string, string>? tags = null) { }

    public void Measure(string metric, double value, IReadOnlyDictionary<string, string>? tags = null) { }

    public void Event(string name, IReadOnlyDictionary<string, string>? fields = null) { }
}
