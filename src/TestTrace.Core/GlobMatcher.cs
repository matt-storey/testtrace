using System.Text;
using System.Text.RegularExpressions;

namespace TestTrace.Core;

/// <summary>
/// Minimal glob matching for the force-full-run escape hatch. Supports
/// "**/" (zero or more path segments), "**", "*" (within a segment) and "?".
/// Paths are compared with forward slashes, case-insensitively.
/// </summary>
public static class GlobMatcher
{
    public static bool Matches(string path, string glob) =>
        ToRegex(glob).IsMatch(path.Replace('\\', '/'));

    public static bool AnyMatch(IEnumerable<string> paths, IReadOnlyList<string> globs, out string? matchedPath, out string? matchedGlob)
    {
        var regexes = globs.Select(g => (Glob: g, Regex: ToRegex(g))).ToList();
        foreach (var path in paths)
        {
            var normalized = path.Replace('\\', '/');
            foreach (var (glob, regex) in regexes)
            {
                if (regex.IsMatch(normalized))
                {
                    matchedPath = path;
                    matchedGlob = glob;
                    return true;
                }
            }
        }

        matchedPath = null;
        matchedGlob = null;
        return false;
    }

    private static Regex ToRegex(string glob)
    {
        var pattern = new StringBuilder("^");
        var i = 0;
        var normalized = glob.Replace('\\', '/');
        while (i < normalized.Length)
        {
            if (normalized.AsSpan(i).StartsWith("**/"))
            {
                pattern.Append("(?:.*/)?");
                i += 3;
            }
            else if (normalized.AsSpan(i).StartsWith("**"))
            {
                pattern.Append(".*");
                i += 2;
            }
            else
            {
                var c = normalized[i++];
                pattern.Append(c switch
                {
                    '*' => "[^/]*",
                    '?' => "[^/]",
                    _ => Regex.Escape(c.ToString()),
                });
            }
        }

        pattern.Append('$');
        return new Regex(pattern.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
