using TestTrace.Core;

namespace TestTrace.Cli;

public static class Explain
{
    public static void Print(AnalyzeResult result, TextWriter output)
    {
        if (result.FrontEnd is not null)
            output.WriteLine($"front-end: {result.FrontEnd}");
        if (result.Reason is not null)
            output.WriteLine($"summary:   {result.Reason}");
        foreach (var warning in result.Warnings)
            output.WriteLine($"warning:   {warning}");

        if (result.RunEverything)
        {
            output.WriteLine();
            output.WriteLine("RUN EVERYTHING — no selection was attempted.");
            return;
        }

        if (result.ChangedMethods.Count > 0)
        {
            output.WriteLine();
            output.WriteLine($"changed methods ({result.ChangedMethods.Count}):");
            foreach (var change in result.ChangedMethods)
                output.WriteLine($"  [{change.Kind}] {change.Fqn}");
        }

        output.WriteLine();
        output.WriteLine($"selected tests ({result.Selection.Count}):");
        foreach (var test in result.Selection)
        {
            output.WriteLine($"  {test.DisplayName}");
            output.WriteLine($"      why: {test.Reason}");
            if (test.Chain.Count > 1)
                output.WriteLine($"      via: {string.Join("\n        -> ", test.Chain.Select(Pretty))}");
        }
    }

    /// <summary>Node keys read awkwardly ("Type::Name/2", "TN:Type"); soften them.</summary>
    private static string Pretty(string key)
    {
        if (key.StartsWith(MethodKeys.TypeNodePrefix, StringComparison.Ordinal))
            return $"[type {key[MethodKeys.TypeNodePrefix.Length..]}]";
        var slash = key.LastIndexOf('/');
        return slash > 0 ? key[..slash].Replace("::", ".") : key.Replace("::", ".");
    }
}
