using System.Collections.Concurrent;
using Mono.Cecil;

namespace TestTrace.Core;

public static class AssemblyScanner
{
    /// <summary>
    /// Snapshot every managed assembly under <paramref name="directory"/> (recursive):
    /// name, MVID, content hash and per-method hashes. Non-managed DLLs are skipped.
    /// Duplicate assembly names (the same dependency harvested twice) keep the first
    /// occurrence in sorted-path order.
    /// </summary>
    public static Manifest Snapshot(string directory, AssemblyScope? scope = null, Manifest? reuseFrom = null) =>
        Snapshot([directory], scope, reuseFrom);

    /// <summary>
    /// Snapshot several build output directories as one logical build — a solution's
    /// projects each have their own bin. Assemblies shared between them (project
    /// references copied into each output) are deduplicated by name.
    /// </summary>
    /// <param name="reuseFrom">
    /// An earlier manifest of the same build layout, normally the baseline. Method
    /// hashing is skipped for any assembly whose content hash still matches, and that
    /// assembly's method entries are carried over instead.
    ///
    /// This is sound rather than a heuristic: the content hash covers the whole PE with
    /// only the debug-only regions zeroed, so an unchanged content hash means the
    /// metadata and IL are byte-identical, and identical IL cannot produce different
    /// method hashes. The result is the same manifest either way — see
    /// AssemblyScannerTests, which asserts exactly that.
    ///
    /// It matters because a typical change touches one assembly out of hundreds, while
    /// method hashing is most of the cost of a snapshot. Baseline snapshots pass null
    /// and hash everything, which is what makes the reuse safe on the next run.
    /// </param>
    public static Manifest Snapshot(
        IReadOnlyList<string> directories, AssemblyScope? scope = null, Manifest? reuseFrom = null)
    {
        scope ??= AssemblyScope.Default;
        if (directories.Count == 0)
            throw new InvalidDataException("no build output directories to scan");
        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException($"assembly directory not found: {directory}");
        }

        var paths = EnumerateAssemblies(directories);

        var reusable = reuseFrom?.Assemblies
            .Where(a => a.MethodsAnalyzed && a.ContentHash.Length > 0)
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var scanned = new ConcurrentDictionary<string, (AssemblyEntry Entry, string? Tfm)>();
        Parallel.ForEach(paths, path =>
        {
            var result = TryScan(path, scope, reusable);
            if (result is not null)
                scanned[path] = result.Value;
        });

        var byName = new Dictionary<string, AssemblyEntry>(StringComparer.OrdinalIgnoreCase);
        var frameworks = new List<string>();
        foreach (var path in paths)
        {
            if (!scanned.TryGetValue(path, out var result))
                continue;
            if (byName.TryAdd(result.Entry.Name, result.Entry) && result.Tfm is not null)
                frameworks.Add(result.Tfm);
        }

        if (byName.Count == 0)
            throw new InvalidDataException($"no managed assemblies found under {string.Join(", ", directories)}");

        return new Manifest
        {
            Tfm = DominantAppFramework(frameworks),
            Scope = scope.Describe(),
            Assemblies = byName.Values.OrderBy(a => a.Name, StringComparer.Ordinal).ToList(),
            ContentFiles = HashContentFiles(directories),
        };
    }

    /// <summary>
    /// Extensions of build outputs that change behaviour without changing any IL.
    /// Deliberately narrow. Two constraints shaped it:
    ///   - .xml is excluded: doc-comment files track source comments, so including
    ///     them would make a comment-only edit select everything — the exact
    ///     over-selection the IL-level design exists to avoid.
    ///   - anything a test run rewrites (nunit_random_seed.tmp, coverage mapping
    ///     files) is excluded, or every analysis after a test run would fail open.
    /// </summary>
    public static readonly string[] ContentFileExtensions = [".json", ".config"];

    private static List<ContentFileEntry> HashContentFiles(IReadOnlyList<string> directories)
    {
        // Deduplicated by file name, exactly as assemblies are: one appsettings.json is
        // copied into the output of every project that references it.
        var byFileName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                         .Where(p => ContentFileExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
                         .Order(StringComparer.Ordinal))
            {
                byFileName.TryAdd(Path.GetFileName(path), path);
            }
        }

        var entries = new ContentFileEntry[byFileName.Count];
        var items = byFileName.OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
        Parallel.For(0, items.Count, i =>
        {
            entries[i] = new ContentFileEntry
            {
                Name = items[i].Key,
                // Raw bytes: unlike an assembly there is no debug metadata to mask.
                Hash = HashFile(items[i].Value),
            };
        });

        return entries.ToList();
    }

    private static string HashFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        }
        catch (IOException)
        {
            // Unreadable (locked by another process): treat as ever-changing rather
            // than as unchanged, so it fails open instead of hiding a difference.
            return Guid.NewGuid().ToString("N");
        }
    }

    /// <summary>
    /// All assembly files under the directories, deduplicated by file name so a project
    /// reference copied into several outputs is read once.
    ///
    /// Of several copies the most recently written wins, not the first path
    /// alphabetically: a project that failed to rebuild leaves a stale copy of its
    /// dependencies in its own bin, and picking that one would hash the previous
    /// build's IL and miss the change entirely. Ties break on ordinal path so the
    /// result stays deterministic.
    /// </summary>
    public static List<string> EnumerateAssemblies(IReadOnlyList<string> directories)
    {
        var byFileName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(path);
                if (!byFileName.TryGetValue(name, out var existing) || IsNewer(path, existing))
                    byFileName[name] = path;
            }
        }

        return byFileName.Values.Order(StringComparer.Ordinal).ToList();
    }

    private static bool IsNewer(string candidate, string incumbent)
    {
        try
        {
            return File.GetLastWriteTimeUtc(candidate) > File.GetLastWriteTimeUtc(incumbent);
        }
        catch (IOException)
        {
            return false; // unreadable timestamp: keep the incumbent, which is deterministic
        }
    }

    /// <summary>Paths of the assemblies the call graph should be built from.</summary>
    public static List<string> InScopePaths(IReadOnlyList<string> directories, AssemblyScope scope) =>
        EnumerateAssemblies(directories)
            .Where(p => scope.IsInScope(Path.GetFileNameWithoutExtension(p), p))
            .ToList();

    private static (AssemblyEntry Entry, string? Tfm)? TryScan(
        string path, AssemblyScope scope, IReadOnlyDictionary<string, AssemblyEntry>? reusable)
    {
        ModuleDefinition module;
        try
        {
            // Custom-attribute parsing may need to resolve enum argument types, so the
            // resolver must at least search the assembly's own directory. References
            // into the shared framework still fail; MethodHasher falls back to raw
            // attribute blobs there.
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(path));
            module = ModuleDefinition.ReadModule(path, new ReaderParameters(ReadingMode.Deferred)
            {
                AssemblyResolver = resolver,
            });
        }
        catch (BadImageFormatException)
        {
            return null; // native or otherwise non-managed
        }

        using (module)
        {
            var name = module.Assembly?.Name.Name ?? Path.GetFileNameWithoutExtension(path);
            var inScope = scope.IsInScope(name, path);

            // Always: one file read + SHA-256. This is what detects package bumps.
            var contentHash = ContentHasher.HashIgnoringDebugInfo(path);

            // Byte-identical metadata and IL cannot hash to different methods, so an
            // unchanged content hash lets the previous entries stand.
            var carriedOver = inScope
                             && contentHash.Length > 0
                             && reusable is not null
                             && reusable.TryGetValue(name, out var previous)
                             && string.Equals(previous.ContentHash, contentHash, StringComparison.Ordinal)
                ? previous.Methods
                : null;

            var entry = new AssemblyEntry
            {
                Name = name,
                Mvid = module.Mvid.ToString("D"),
                ContentHash = contentHash,
                MethodsAnalyzed = inScope,
                // Only in scope: walking every method of every package assembly is
                // ~99% of the cost and buys nothing we can act on.
                Methods = inScope ? carriedOver ?? MethodHasher.HashModule(module) : [],
            };
            return (entry, TargetFrameworkOf(module));
        }
    }

    private static string? TargetFrameworkOf(ModuleDefinition module)
    {
        var attribute = module.Assembly?.CustomAttributes.FirstOrDefault(a =>
            a.AttributeType.FullName == "System.Runtime.Versioning.TargetFrameworkAttribute");
        return attribute?.ConstructorArguments.Count > 0
            ? attribute.ConstructorArguments[0].Value as string
            : null;
    }

    /// <summary>
    /// The app assemblies' framework is the highest .NETCoreApp version present:
    /// package assemblies may be older (netstandard, lower net versions), but nothing
    /// in the directory can be newer than the app itself.
    /// </summary>
    private static string DominantAppFramework(List<string> frameworks)
    {
        return frameworks
            .Where(f => f.StartsWith(".NETCoreApp", StringComparison.Ordinal))
            .OrderByDescending(FrameworkVersion)
            .FirstOrDefault() ?? "";

        static Version FrameworkVersion(string framework)
        {
            var marker = "Version=v";
            var index = framework.IndexOf(marker, StringComparison.Ordinal);
            return index >= 0 && Version.TryParse(framework[(index + marker.Length)..], out var v)
                ? v
                : new Version(0, 0);
        }
    }
}
