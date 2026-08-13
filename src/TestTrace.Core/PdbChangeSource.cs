using Mono.Cecil;

namespace TestTrace.Core;

public sealed class PdbChangeResult
{
    public List<MethodChange> Changes { get; } = [];
    public int PdbCount { get; set; }
    public List<string> Warnings { get; } = [];

    /// <summary>A changed file the PDB view cannot reason about (no matching source
    /// document and not a .cs file): csproj, props, resources. Forces RUN_EVERYTHING.</summary>
    public string? UnanalyzableFile { get; set; }
}

/// <summary>
/// Fallback front-end needing no baseline: intersect changed source line ranges with
/// portable-PDB sequence points of the CURRENT build.
///
/// This is a degraded mode, not an equivalent one: change detection drops from
/// compiled IL to source lines, so source-generator output, package bumps and
/// analyzer-induced changes no longer register, and code moved between files is
/// mis-attributed. It must never be silently preferred over the manifest.
/// </summary>
public static class PdbChangeSource
{
    public static PdbChangeResult GetChangedMethods(string currentDirectory, List<ChangedFileRange> ranges) =>
        GetChangedMethods([currentDirectory], ranges);

    public static PdbChangeResult GetChangedMethods(IReadOnlyList<string> currentDirectories, List<ChangedFileRange> ranges)
    {
        var result = new PdbChangeResult();
        var matchedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = new HashSet<(string Assembly, string Fqn)>();

        var seenAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dllPath in AssemblyScanner.EnumerateAssemblies(currentDirectories))
        {
            if (!File.Exists(Path.ChangeExtension(dllPath, ".pdb")))
                continue;

            ModuleDefinition module;
            try
            {
                module = ModuleDefinition.ReadModule(dllPath, new ReaderParameters(ReadingMode.Deferred)
                {
                    ReadSymbols = true,
                });
            }
            catch (Exception)
            {
                continue; // native, non-managed, or unreadable symbols
            }

            using (module)
            {
                var assemblyName = module.Assembly?.Name.Name ?? Path.GetFileNameWithoutExtension(dllPath);
                if (!seenAssemblies.Add(assemblyName))
                    continue;
                result.PdbCount++;

                foreach (var type in AllTypes(module))
                foreach (var method in type.Methods)
                {
                    var debug = method.DebugInformation;
                    if (debug is null || !debug.HasSequencePoints)
                        continue;

                    // Batch form: the per-instruction lookup is O(n) each and would be
                    // pathological across a whole solution.
                    foreach (var group in debug.SequencePoints
                                 .Where(sp => !sp.IsHidden && sp.Document?.Url is not null)
                                 .GroupBy(sp => sp.Document.Url))
                    {
                        var min = group.Min(sp => sp.StartLine);
                        var max = group.Max(sp => sp.EndLine);
                        foreach (var range in ranges)
                        {
                            if (!PathMatches(group.Key, range.Path))
                                continue;
                            matchedFiles.Add(range.Path);
                            if (range.StartLine <= max && range.EndLine >= min)
                                changed.Add((assemblyName, OwnerFqn(method, type)));
                        }
                    }
                }
            }
        }

        foreach (var range in ranges)
        {
            if (matchedFiles.Contains(range.Path))
                continue;
            if (Path.GetExtension(range.Path).Equals(".cs", StringComparison.OrdinalIgnoreCase))
                result.Warnings.Add($"changed file '{range.Path}' matches no PDB document (deleted or non-executable code?)");
            else
                result.UnanalyzableFile ??= range.Path;
        }

        foreach (var (assembly, fqn) in changed.OrderBy(c => c.Fqn, StringComparer.Ordinal))
            result.Changes.Add(new MethodChange { Assembly = assembly, Fqn = fqn, Kind = "changed" });
        return result;
    }

    /// <summary>Generated members' sequence points already map to the owner's source
    /// lines; report the owner method fqn so downstream matches the manifest path.</summary>
    private static string OwnerFqn(MethodDefinition method, TypeDefinition type)
    {
        var owner = GeneratedNames.OwnerFromMemberName(method.Name)
                    ?? (type.DeclaringType is not null && GeneratedNames.IsGeneratedTypeName(type.Name)
                        ? GeneratedNames.OwnerFromTypeName(type.Name)
                        : null);
        if (owner is not null)
        {
            var userType = type;
            while (userType.DeclaringType is not null && GeneratedNames.IsGeneratedTypeName(userType.Name))
                userType = userType.DeclaringType;
            var ownerMethod = userType.Methods.FirstOrDefault(m => m.Name == owner);
            if (ownerMethod is not null)
                return GeneratedNames.NormalizeOrdinals(ownerMethod.FullName);
        }

        return GeneratedNames.NormalizeOrdinals(method.FullName);
    }

    /// <summary>
    /// A source document matches a changed path when the document path ends with it
    /// on a segment boundary.
    ///
    /// Unified diff headers commonly carry a leading prefix segment that is not part
    /// of the real path — `diff -u old/A.cs new/A.cs` writes "new/A.cs", and version
    /// control tools use their own conventions. Rather than encode any one of those,
    /// the first segment is dropped and the match retried. One retry only: dropping
    /// further would match any file with the same name.
    /// </summary>
    private static bool PathMatches(string documentUrl, string changedPath)
    {
        var doc = documentUrl.Replace('\\', '/');
        var path = changedPath.Replace('\\', '/');
        if (EndsWithSegment(doc, path))
            return true;

        var slash = path.IndexOf('/');
        return slash >= 0 && slash < path.Length - 1 && EndsWithSegment(doc, path[(slash + 1)..]);
    }

    private static bool EndsWithSegment(string documentPath, string candidate)
    {
        if (candidate.Length == 0)
            return false;
        if (documentPath.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            return true;
        return documentPath.EndsWith(candidate, StringComparison.OrdinalIgnoreCase)
               && documentPath.Length > candidate.Length
               && documentPath[documentPath.Length - candidate.Length - 1] == '/';
    }

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        var stack = new Stack<TypeDefinition>(module.Types);
        while (stack.Count > 0)
        {
            var type = stack.Pop();
            yield return type;
            foreach (var nested in type.NestedTypes)
                stack.Push(nested);
        }
    }
}
