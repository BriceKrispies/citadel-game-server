using System.Text.Json;

namespace GameServer.IntegrationTests;

/// <summary>
/// Writes a scenario's evidence to <c>artifacts/&lt;scenario&gt;/</c> at the repo root:
/// a machine-readable <c>report.json</c> and a human-readable <c>report.md</c>. The
/// artifacts directory is git-ignored — these are regenerated demonstration outputs,
/// not source. Returns the directory so a test can surface it via test output.
/// </summary>
public static class ArtifactWriter
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string Write(string scenario, object json, string markdown)
    {
        var dir = Path.Combine(RepoRoot(), "artifacts", scenario);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "report.json"), JsonSerializer.Serialize(json, JsonOptions));
        File.WriteAllText(Path.Combine(dir, "report.md"), markdown);
        return dir;
    }

    private static string RepoRoot()
    {
        // Walk up from the test bin directory to the repo (marked by the solution file).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Citadel.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
