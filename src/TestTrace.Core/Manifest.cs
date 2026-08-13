namespace TestTrace.Core;

/// <summary>
/// The baseline snapshot of a build output directory. Nothing downstream ever needs
/// the baseline DLLs, only this. Method entries are populated from M2 onward.
/// </summary>
public sealed class Manifest
{
    /// <summary>Bumped to 4 when ContentFiles was added.</summary>
    public const int CurrentVersion = 4;

    /// <summary>First version carrying <see cref="ContentFiles"/>. An older baseline
    /// has no content-file data, so comparing against it would read every file as
    /// added; the diff is skipped (with a warning) instead.</summary>
    public const int FirstVersionWithContentFiles = 4;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Framework name of the app assemblies, e.g. ".NETCoreApp,Version=v8.0".</summary>
    public string Tfm { get; set; } = "";

    /// <summary>Which assemblies were method-hashed (see AssemblyScope). Recorded so a
    /// baseline taken under a different scope is detected, not silently mis-diffed.</summary>
    public string Scope { get; set; } = "";

    public List<AssemblyEntry> Assemblies { get; set; } = [];

    /// <summary>
    /// Non-assembly build outputs that carry behaviour but compile to no IL —
    /// appsettings.json and friends. Without these a config-only change produces no
    /// assembly diff at all and reads as "nothing affected", which is a silent skip
    /// of a real behavioural change. See AssemblyScanner.ContentFileExtensions.
    /// </summary>
    public List<ContentFileEntry> ContentFiles { get; set; } = [];
}

public sealed class ContentFileEntry
{
    /// <summary>File name only: output directories differ between machines, and the
    /// same file is copied into every referencing project's output.</summary>
    public string Name { get; set; } = "";

    public string Hash { get; set; } = "";
}

public sealed class AssemblyEntry
{
    public string Name { get; set; } = "";
    public string Mvid { get; set; } = "";

    /// <summary>
    /// SHA-256 of the PE image with debug-only regions zeroed (see ContentHasher).
    /// This, not the MVID, is what change detection compares: deterministic MVIDs
    /// shift on any source edit because they hash the PDB identity too.
    /// </summary>
    public string ContentHash { get; set; } = "";

    /// <summary>False for out-of-scope assemblies (packages/framework): content-hashed
    /// only, so a change to one cannot be traced and must fail open.</summary>
    public bool MethodsAnalyzed { get; set; }
    public List<MethodEntry> Methods { get; set; } = [];
}

public sealed class MethodEntry
{
    public int Token { get; set; }
    public string Fqn { get; set; } = "";
    public string Hash { get; set; } = "";
}
