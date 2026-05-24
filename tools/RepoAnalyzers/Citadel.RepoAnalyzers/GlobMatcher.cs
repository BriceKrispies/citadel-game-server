using System.Text.RegularExpressions;

namespace Citadel.RepoAnalyzers;

/// <summary>
/// Minimal glob matcher for path-ignore globs. Supports <c>**</c> (any number of
/// path segments), <c>*</c> (within a segment), and <c>?</c>. Matching is
/// case-insensitive and operates on forward-slash-normalized paths.
/// </summary>
internal static class GlobMatcher
{
    public static bool IsMatch(string path, string glob)
    {
        var normalizedPath = Normalize(path);
        var regex = "^" + Translate(Normalize(glob)) + "$";
        return Regex.IsMatch(normalizedPath, regex, RegexOptions.IgnoreCase);
    }

    public static string Normalize(string path) => path.Replace('\\', '/');

    private static string Translate(string glob)
    {
        // Escape everything, then re-expand the wildcard tokens. After Regex.Escape,
        // '*' becomes "\*" and '?' becomes "\?".
        var escaped = Regex.Escape(glob);
        return escaped
            .Replace("\\*\\*/", "(?:.*/)?") // leading "**/" matches zero or more segments
            .Replace("\\*\\*", ".*")          // bare "**" matches anything
            .Replace("\\*", "[^/]*")          // "*" matches within a segment
            .Replace("\\?", "[^/]");          // "?" matches one non-separator char
    }
}
