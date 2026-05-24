using Xunit;

namespace GameServer.LoadHarness;

public sealed class ResultExporterTests
{
    [Fact]
    public void ResultExporter_WritesJsonAndCsv()
    {
        var exporter = new ResultExporter();
        var dir = Path.Combine(Path.GetTempPath(), "citadel-load-" + Guid.NewGuid().ToString("n"));

        try
        {
            var metrics = new MetricsRecorder();
            metrics.IncrementSuccessfulConnections();
            metrics.RecordMessageSent(10);
            metrics.RecordMessageReceived(20);

            var result = new LoadResult(
                "smoke", "sustained-input", 100, metrics.Snapshot(),
                new Dictionary<string, long> { ["ServerError"] = 2 });

            var jsonPath = Path.Combine(dir, "smoke.result.json");
            var csvPath = Path.Combine(dir, "smoke.timeseries.csv");

            exporter.WriteJson(result, jsonPath);
            exporter.WriteCsv(
                new[]
                {
                    new TimeSeriesSample(1, 5, 10, 20, 100, 200, 0, 0),
                    new TimeSeriesSample(2, 5, 12, 24, 110, 210, 1, 0),
                },
                csvPath);

            Assert.True(File.Exists(jsonPath));
            Assert.True(File.Exists(csvPath));

            var json = File.ReadAllText(jsonPath);
            Assert.Contains("smoke", json);
            Assert.Contains("sustained-input", json);

            var lines = File.ReadAllLines(csvPath);
            Assert.StartsWith("elapsedSeconds,activeConnections", lines[0]);
            Assert.Equal(3, lines.Length); // header + 2 samples
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
