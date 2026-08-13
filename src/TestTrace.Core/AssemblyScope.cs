namespace TestTrace.Core;

/// <summary>
/// Decides which assemblies get expensive treatment (method-level hashing and call
/// graph construction). Every assembly is still content-hashed — that is one file
/// read and a SHA-256, and it is what detects package bumps.
///
/// Default heuristic: an assembly is in scope when a portable PDB sits next to it.
/// Projects you build emit one; NuGet packages restored into the output directory
/// almost never do. On the sample solution that separates 8 application assemblies
/// from 407 package assemblies, which is 99.8% of the method-hashing work.
///
/// Why this is safe rather than merely cheap: the caller of one of YOUR changed
/// methods is your code. Framework code reaches back into yours only through
/// interfaces, virtuals, delegates and reflection — and those are covered by the
/// type-node edges (anything that constructs, stores or generically references the
/// type), not by walking framework method bodies.
/// </summary>
public sealed class AssemblyScope
{
    /// <summary>Globs matched against the assembly simple name. When non-empty these
    /// replace the default PDB heuristic entirely.</summary>
    public IReadOnlyList<string> Include { get; init; } = [];

    /// <summary>Globs matched against the assembly simple name; applied after Include.</summary>
    public IReadOnlyList<string> Exclude { get; init; } = [];

    public static AssemblyScope Default { get; } = new();

    public bool IsInScope(string assemblyName, string dllPath)
    {
        if (Exclude.Count > 0 && Exclude.Any(g => GlobMatcher.Matches(assemblyName, g)))
            return false;
        if (Include.Count > 0)
            return Include.Any(g => GlobMatcher.Matches(assemblyName, g));
        return File.Exists(Path.ChangeExtension(dllPath, ".pdb"));
    }

    /// <summary>Stable description recorded in the manifest, so a baseline taken with
    /// a different scope can be detected instead of silently mis-diffed.</summary>
    public string Describe() =>
        Include.Count == 0 && Exclude.Count == 0
            ? "pdb-adjacent"
            : $"include=[{string.Join(",", Include)}] exclude=[{string.Join(",", Exclude)}]";
}
