namespace TestTrace.Core;

/// <summary>A changed source region. EndLine is int.MaxValue when the whole file counts.</summary>
public sealed record ChangedFileRange(string Path, int StartLine, int EndLine)
{
    public bool IsWholeFile => EndLine == int.MaxValue;
}

public static class ChangedFiles
{
    /// <summary>
    /// Parse --changed-files content. Two input shapes are accepted, detected
    /// automatically:
    ///
    /// A plain list, one entry per line:
    ///   path/to/File.cs            (whole file)
    ///   path/to/File.cs:12         (single line)
    ///   path/to/File.cs:12-34      (range)
    ///
    /// Or a unified diff (`diff -u`, or any tool that emits the same format) piped in
    /// directly, so callers need no conversion step. Hunk headers give the line
    /// ranges, which is exactly what the PDB front-end wants.
    ///
    /// A colon suffix that is not a line spec (e.g. "C:\x.cs") reads as a plain path.
    /// </summary>
    public static List<ChangedFileRange> Parse(IEnumerable<string> lines)
    {
        var materialized = lines as IList<string> ?? lines.ToList();
        return LooksLikeUnifiedDiff(materialized)
            ? ParseUnifiedDiff(materialized)
            : ParsePlainList(materialized);
    }

    /// <summary>
    /// Detected from unified-diff markers only (POSIX `diff -u`): the file headers
    /// and the hunk header. No tool-specific vocabulary — output from any diff
    /// producer is recognised by the same markers.
    /// </summary>
    public static bool LooksLikeUnifiedDiff(IEnumerable<string> lines) =>
        lines.Any(l => l.StartsWith("+++ ", StringComparison.Ordinal)
                       || l.StartsWith("--- ", StringComparison.Ordinal)
                       || l.StartsWith("@@ ", StringComparison.Ordinal));

    private static List<ChangedFileRange> ParsePlainList(IEnumerable<string> lines)
    {
        var ranges = new List<ChangedFileRange>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var colon = line.LastIndexOf(':');
            if (colon > 0 && TryParseLineSpec(line[(colon + 1)..], out var start, out var end))
                ranges.Add(new ChangedFileRange(line[..colon], start, end));
            else
                ranges.Add(new ChangedFileRange(line, 1, int.MaxValue));
        }

        return ranges;
    }

    private static List<ChangedFileRange> ParseUnifiedDiff(IEnumerable<string> lines)
    {
        var ranges = new List<ChangedFileRange>();
        string? path = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var target = TrimTimestamp(line[4..]);
                // /dev/null means the file was deleted; nothing to attribute.
                path = target == "/dev/null" ? null : target;
                continue;
            }

            if (path is null || !line.StartsWith("@@", StringComparison.Ordinal))
                continue;

            // @@ -oldStart,oldCount +newStart,newCount @@ optional context
            var plus = line.IndexOf('+');
            var end = line.IndexOf("@@", 2, StringComparison.Ordinal);
            if (plus < 0 || end < 0 || plus > end)
                continue;

            var span = line[(plus + 1)..end].Trim();
            var comma = span.IndexOf(',');
            var startText = comma < 0 ? span : span[..comma];
            var countText = comma < 0 ? "1" : span[(comma + 1)..];
            if (!int.TryParse(startText, out var start) || !int.TryParse(countText, out var count))
                continue;

            // A pure deletion (count 0) still affects the surrounding method: record
            // the adjacent line rather than an empty range.
            if (count <= 0)
                ranges.Add(new ChangedFileRange(path, Math.Max(start, 1), Math.Max(start, 1)));
            else
                ranges.Add(new ChangedFileRange(path, start, start + count - 1));
        }

        return ranges;
    }

    /// <summary>
    /// Unified diff file headers may carry a trailing timestamp column, tab-separated.
    /// The path itself is left exactly as written — callers match it by suffix, which
    /// absorbs whatever prefix segment the producer used.
    /// </summary>
    private static string TrimTimestamp(string target)
    {
        var tab = target.IndexOf('\t');
        return (tab >= 0 ? target[..tab] : target).Trim();
    }

    private static bool TryParseLineSpec(string spec, out int start, out int end)
    {
        start = end = 0;
        var dash = spec.IndexOf('-');
        if (dash < 0)
            return int.TryParse(spec, out start) && start > 0 && (end = start) > 0;
        return int.TryParse(spec[..dash], out start) && start > 0
            && int.TryParse(spec[(dash + 1)..], out end) && end >= start;
    }
}
