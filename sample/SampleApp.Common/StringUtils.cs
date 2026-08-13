namespace SampleApp.Common;

public static class StringUtils
{
    /// <summary>Trims and collapses runs of whitespace to a single space.</summary>
    public static string Normalise(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts);
    }
}
