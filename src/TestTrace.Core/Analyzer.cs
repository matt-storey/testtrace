namespace TestTrace.Core;

public sealed class AnalyzeResult
{
    public bool RunEverything { get; set; }
    public string? Reason { get; set; }
    public List<string> SelectedTests { get; set; } = [];
    public AssembliesReport? Assemblies { get; set; }
    public List<MethodChange> ChangedMethods { get; set; } = [];

    /// <summary>Selected tests with assembly/type detail, reason and chain
    /// (drives M4 filter grouping and --explain).</summary>
    public List<SelectedTest> Selection { get; set; } = [];

    /// <summary>Per-assembly vstest filters, built by the CLI from Selection.
    /// Assemblies with no selected tests are absent: an empty filter string passed to
    /// vstest fails the run with "No test matches", so they must be skipped entirely.</summary>
    public List<AssemblyFilter> Filters { get; set; } = [];

    /// <summary>Which change source produced the result: "manifest" or "pdb".</summary>
    public string? FrontEnd { get; set; }

    public List<string> Warnings { get; set; } = [];

    /// <summary>Framework this run targeted; every selected test belongs to it.</summary>
    public string? TestFramework { get; set; }

    /// <summary>Graph used for the selection, kept so post-processing (--always-run)
    /// need not rebuild or re-snapshot. Not serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public CallGraphIndex? Graph { get; set; }

    public static AnalyzeResult Everything(string reason) =>
        new() { RunEverything = true, Reason = reason };
}

public sealed class AssemblyFilter
{
    public string Assembly { get; set; } = "";
    public string? Dll { get; set; }
    public int TestCount { get; set; }

    /// <summary>Set when the clause threshold was exceeded: run the whole assembly
    /// (no filter argument) instead of an unwieldy filter string.</summary>
    public bool RunWholeAssembly { get; set; }

    /// <summary>Filter language this expression is written in — "VsTest" or
    /// "TreeNode". Decides which flag the caller passes it to, and to which runner.</summary>
    public string Dialect { get; set; } = "";

    public string Filter { get; set; } = "";
}

public sealed class MethodChange
{
    public string Assembly { get; set; } = "";
    public string Fqn { get; set; } = "";

    /// <summary>changed | added | removed | type</summary>
    public string Kind { get; set; } = "";
}

public sealed class AssembliesReport
{
    public List<string> Added { get; set; } = [];
    public List<string> Removed { get; set; } = [];
    public List<string> Changed { get; set; } = [];
    public int UnchangedCount { get; set; }
}

public static class Analyzer
{
    /// <summary>Default globs for the force-full-run escape hatch: files that map to no
    /// IL but change behavior, so a selection would silently miss them.</summary>
    public static readonly string[] DefaultForceFullRunGlobs =
    [
        "**/appsettings*.json",
        "**/Migrations/**",
        "**/*.razor",
        "**/*.cshtml",
    ];

    /// <summary>
    /// Front-end selection order, falling through on failure:
    /// manifest (preferred) -> PDB line map (degraded) -> RUN_EVERYTHING.
    /// The PDB path is never silently preferred: it only runs when no baseline
    /// manifest or directory was provided at all.
    /// </summary>
    public static AnalyzeResult Analyze(
        Manifest? baseline,
        string currentDirectory,
        ITestFrameworkDetector framework,
        IReadOnlyList<string>? changedFiles = null,
        IReadOnlyList<string>? forceFullRunGlobs = null,
        IReadOnlyList<string>? alwaysRunPatterns = null,
        AssemblyScope? scope = null) =>
        Analyze(baseline, [currentDirectory], framework, changedFiles, forceFullRunGlobs, alwaysRunPatterns, scope);

    /// <summary>
    /// A run targets ONE framework. Discovery uses only its detector, so the selection
    /// is exactly the tests the caller is about to run — no cross-framework results to
    /// filter back out afterwards.
    /// </summary>
    public static AnalyzeResult Analyze(
        Manifest? baseline,
        IReadOnlyList<string> currentDirectories,
        ITestFrameworkDetector framework,
        IReadOnlyList<string>? changedFiles = null,
        IReadOnlyList<string>? forceFullRunGlobs = null,
        IReadOnlyList<string>? alwaysRunPatterns = null,
        AssemblyScope? scope = null)
    {
        scope ??= AssemblyScope.Default;
        var result = AnalyzeCore(baseline, currentDirectories, changedFiles, forceFullRunGlobs, scope, framework);
        if (!result.RunEverything && alwaysRunPatterns is { Count: > 0 })
            ApplyAlwaysRun(result, currentDirectories, alwaysRunPatterns, scope, framework);
        result.TestFramework = framework.Name;
        return result;
    }

    private static void ApplyAlwaysRun(
        AnalyzeResult result, IReadOnlyList<string> currentDirectories, IReadOnlyList<string> patterns,
        AssemblyScope scope, ITestFrameworkDetector framework)
    {
        // Reuses the graph the selection already built (cached by MVID set), rather
        // than re-snapshotting the whole directory just to recompute the cache key.
        var graph = result.Graph
                    ?? GetGraph(AssemblyScanner.Snapshot(currentDirectories, scope), currentDirectories, scope, framework);
        var already = new HashSet<string>(result.Selection.Select(t => t.DisplayName), StringComparer.Ordinal);
        foreach (var pinned in GraphWalker.SelectAlwaysRun(graph, patterns))
        {
            if (already.Add(pinned.DisplayName))
                result.Selection.Add(pinned);
        }

        result.Selection.Sort((a, b) => string.CompareOrdinal(a.DisplayName, b.DisplayName));
        result.SelectedTests = result.Selection.Select(t => t.DisplayName).ToList();
    }

    private static AnalyzeResult AnalyzeCore(
        Manifest? baseline,
        IReadOnlyList<string> currentDirectories,
        IReadOnlyList<string>? changedFiles,
        IReadOnlyList<string>? forceFullRunGlobs,
        AssemblyScope scope,
        ITestFrameworkDetector framework)
    {
        var ranges = changedFiles is { Count: > 0 } ? ChangedFiles.Parse(changedFiles) : [];

        // Escape hatch runs before any analysis: these changes produce no IL diff
        // and would otherwise silently select nothing.
        if (ranges.Count > 0)
        {
            var globs = forceFullRunGlobs is { Count: > 0 } ? forceFullRunGlobs : DefaultForceFullRunGlobs;
            if (GlobMatcher.AnyMatch(ranges.Select(r => r.Path), globs, out var matchedPath, out var matchedGlob))
                return AnalyzeResult.Everything(
                    $"changed file '{matchedPath}' matches force-full-run path '{matchedGlob}'");
        }

        if (baseline is null)
        {
            return ranges.Count > 0
                ? AnalyzePdb(currentDirectories, ranges, scope, framework)
                : AnalyzeResult.Everything("no baseline manifest or directory, and no --changed-files for the PDB front-end");
        }

        // The baseline doubles as a cache: assemblies whose content is unchanged keep
        // their method hashes instead of being re-hashed, which is most of the cost
        // of a snapshot and is wasted on the hundreds of assemblies a change did not
        // touch. DiffMethods only ever reads the changed and added ones anyway.
        var current = AssemblyScanner.Snapshot(currentDirectories, scope, baseline);

        // Comparability guards first. Both mean "this baseline cannot be diffed against
        // this build at all", so they outrank anything the diff itself would report.
        if (baseline.Tfm != current.Tfm)
            return AnalyzeResult.Everything(
                $"manifest TFM '{baseline.Tfm}' does not match current build TFM '{current.Tfm}'");

        if (baseline.Scope != current.Scope)
            return AnalyzeResult.Everything(
                $"manifest scope '{baseline.Scope}' does not match current scope '{current.Scope}'");

        var report = Diff(baseline, current);

        // Before the "nothing changed" verdict below: a config-only edit produces no
        // assembly diff whatsoever, so without this it would read as "nothing
        // affected" and skip the whole run. These files map to no IL, so there is
        // nothing to trace — running everything is the only honest answer.
        var contentWarnings = new List<string>();
        var changedContent = DiffContentFiles(baseline, current, contentWarnings);
        if (changedContent.Count > 0)
        {
            var content = AnalyzeResult.Everything(
                $"non-assembly build outputs changed ({string.Join(", ", changedContent.Take(5))}" +
                (changedContent.Count > 5 ? $" +{changedContent.Count - 5} more" : "") +
                "); these compile to no IL, so their impact cannot be traced");
            content.Assemblies = report;
            content.FrontEnd = "manifest";
            content.Warnings.AddRange(contentWarnings);
            return content;
        }

        if (report.Added.Count == 0 && report.Changed.Count == 0 && report.Removed.Count == 0)
            return new AnalyzeResult
            {
                Reason = "no assembly-level changes",
                Assemblies = report,
                FrontEnd = "manifest",
                Warnings = contentWarnings,
            };

        if (report.Removed.Count > 0)
        {
            var removedResult = AnalyzeResult.Everything(
                $"assemblies removed from the build: {string.Join(", ", report.Removed)}");
            removedResult.Assemblies = report;
            removedResult.Warnings.AddRange(contentWarnings);
            return removedResult;
        }

        // An out-of-scope assembly (package/framework) changed. We deliberately did
        // not method-hash it, so its impact cannot be traced: fail open. This is the
        // transitive-package-bump case, and running everything is the honest answer.
        var untraceable = UntraceableAssemblies(current, report.Changed.Concat(report.Added));
        if (untraceable.Count > 0)
        {
            var opaque = AnalyzeResult.Everything(UntraceableReason(untraceable));
            opaque.Assemblies = report;
            opaque.Warnings.AddRange(contentWarnings);
            return opaque;
        }

        var changes = DiffMethods(baseline, current, report);
        AnalyzeResult result;
        if (changes.Count == 0)
        {
            // Content changed but nothing we model did (resources, assembly-level
            // attributes, ...). We cannot reason about it, so run everything.
            result = AnalyzeResult.Everything(
                "assembly content changed but no method-level difference was detected (unmodeled change)");
        }
        else
        {
            var graph = GetGraph(current, currentDirectories, scope, framework);
            if (NoTestsDiscovered(graph, framework) is { } noTests)
            {
                noTests.Warnings.AddRange(contentWarnings);
                return Attach(noTests, report, changes);
            }

            var selected = GraphWalker.SelectTests(graph, changes.Select(c => c.Fqn));
            result = new AnalyzeResult
            {
                Reason = $"{changes.Count} changed method(s) -> {selected.Count} impacted test(s)",
                SelectedTests = selected.Select(t => t.DisplayName).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList(),
                Selection = selected,
                Graph = graph,
            };
            WarnAboutOtherFrameworks(result, graph, framework);
        }

        // Carried onto every outcome, not just the early returns: a baseline too old to
        // record content files means this selection may be missing a config change, and
        // that caveat matters most when a narrow selection is what gets acted on.
        result.Warnings.AddRange(contentWarnings);
        result.Assemblies = report;
        result.ChangedMethods = changes;
        result.FrontEnd = "manifest";
        return result;
    }

    private static AnalyzeResult AnalyzePdb(
        IReadOnlyList<string> currentDirectories, List<ChangedFileRange> ranges, AssemblyScope scope,
        ITestFrameworkDetector framework)
    {
        var pdb = PdbChangeSource.GetChangedMethods(currentDirectories, ranges);

        if (pdb.UnanalyzableFile is not null)
        {
            var blocked = AnalyzeResult.Everything(
                $"PDB front-end cannot analyze change to '{pdb.UnanalyzableFile}' (no source document); " +
                "provide a manifest or add the path to --force-full-run-paths");
            blocked.Warnings.AddRange(pdb.Warnings);
            return blocked;
        }

        if (pdb.PdbCount == 0)
            return AnalyzeResult.Everything(
                "no portable PDBs found under the current build (need DebugType=portable); cannot fall back further");

        const string degraded =
            "PDB line-map front-end (degraded: source-generator output, package bumps and " +
            "analyzer changes are invisible; moved code is mis-attributed)";

        AnalyzeResult result;
        if (pdb.Changes.Count == 0)
        {
            result = new AnalyzeResult { Reason = $"{degraded}: changed lines intersect no methods" };
        }
        else
        {
            var current = AssemblyScanner.Snapshot(currentDirectories, scope);

            // Same fail-open rule the manifest front-end applies: PdbChangeSource reads
            // every assembly with a co-located .pdb, but the graph is built from
            // in-scope assemblies only. A change in an out-of-scope assembly therefore
            // resolves to no graph node and would contribute nothing to the walk —
            // reporting "0 impacted tests" for a change we simply could not follow.
            var untraceable = UntraceableAssemblies(current, pdb.Changes.Select(c => c.Assembly));
            if (untraceable.Count > 0)
            {
                var opaque = AnalyzeResult.Everything(UntraceableReason(untraceable));
                opaque.ChangedMethods = pdb.Changes;
                opaque.FrontEnd = "pdb";
                opaque.Warnings.AddRange(pdb.Warnings);
                return opaque;
            }

            var graph = GetGraph(current, currentDirectories, scope, framework);
            if (NoTestsDiscovered(graph, framework) is { } noTests)
            {
                noTests.ChangedMethods = pdb.Changes;
                noTests.FrontEnd = "pdb";
                noTests.Warnings.AddRange(pdb.Warnings);
                return noTests;
            }

            var selected = GraphWalker.SelectTests(graph, pdb.Changes.Select(c => c.Fqn));
            result = new AnalyzeResult
            {
                Reason = $"{degraded}: {pdb.Changes.Count} changed method(s) -> {selected.Count} impacted test(s)",
                SelectedTests = selected.Select(t => t.DisplayName).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList(),
                Selection = selected,
                Graph = graph,
            };
            WarnAboutOtherFrameworks(result, graph, framework);
        }

        result.ChangedMethods = pdb.Changes;
        result.FrontEnd = "pdb";
        result.Warnings.AddRange(pdb.Warnings);
        return result;
    }

    /// <summary>
    /// Code changed, but the analysed assemblies contain no tests of the chosen
    /// framework — so an empty selection would mean "skip every test" rather than
    /// "nothing is affected". Always a mistake, and always fail-open.
    ///
    /// The presence data makes the diagnosis specific: if the build references another
    /// framework's assemblies, the likely cause is the wrong --test-framework, and
    /// saying so beats a generic "no tests found".
    /// </summary>
    private static AnalyzeResult? NoTestsDiscovered(CallGraphIndex graph, ITestFrameworkDetector framework)
    {
        if (graph.Tests.Count > 0)
            return null;

        var others = graph.FrameworksPresent
            .Where(f => !string.Equals(f, framework.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return AnalyzeResult.Everything(others.Count > 0
            ? $"code changed, but no '{framework.Name}' tests were found; the build references " +
              $"{string.Join(" and ", others)} — did you mean --test-framework {others[0]}?"
            : $"code changed, but no '{framework.Name}' tests were found in the analysed " +
              "assemblies; check the scope includes your test projects");
    }

    /// <summary>
    /// Other frameworks are present alongside the one being analysed. Not an error:
    /// the selection is complete for the runner the caller is about to use. But their
    /// tests are invisible to this run, so the re-run that would cover them is named.
    /// </summary>
    private static void WarnAboutOtherFrameworks(
        AnalyzeResult result, CallGraphIndex graph, ITestFrameworkDetector framework)
    {
        foreach (var other in graph.FrameworksPresent)
        {
            if (!string.Equals(other, framework.Name, StringComparison.OrdinalIgnoreCase))
                result.Warnings.Add(
                    $"the build also contains {other} tests, which this run does not cover; " +
                    $"re-run with --test-framework {other} to select those");
        }
    }

    /// <summary>
    /// Of the named assemblies, those that were not method-hashed — so nothing
    /// downstream can trace a change inside them. Used by both front-ends: whatever
    /// noticed the change, an untraceable one must fail open rather than contribute
    /// nothing and read as "not affected".
    /// </summary>
    private static List<string> UntraceableAssemblies(Manifest current, IEnumerable<string> names)
    {
        var byName = current.Assemblies.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
        return names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            // Absent from the snapshot entirely is also untraceable, not "fine".
            .Where(name => !byName.TryGetValue(name, out var entry) || !entry.MethodsAnalyzed)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    private static string UntraceableReason(List<string> untraceable) =>
        $"out-of-scope assemblies changed and cannot be traced ({string.Join(", ", untraceable.Take(5))}" +
        (untraceable.Count > 5 ? $" +{untraceable.Count - 5} more" : "") +
        "); a package or framework bump can affect anything";

    private static AnalyzeResult Attach(AnalyzeResult result, AssembliesReport report, List<MethodChange> changes)
    {
        result.Assemblies = report;
        result.ChangedMethods = changes;
        result.FrontEnd = "manifest";
        return result;
    }

    private static CallGraphIndex GetGraph(
        Manifest current, IReadOnlyList<string> currentDirectories, AssemblyScope scope,
        ITestFrameworkDetector framework)
    {
        // The framework belongs in the key: discovery now depends on it, so an
        // nunit-built graph served to an xunit run would silently return the wrong
        // tests — the same shape as the scope-key defect fixed earlier.
        var cacheKey = GraphCache.KeyFor(current, framework.Name);
        var graph = GraphCache.TryLoad(cacheKey);
        if (graph is null)
        {
            // In-scope assemblies only: the caller of one of your changed methods is
            // your code, and framework callbacks are covered by type-node edges.
            graph = CallGraphBuilder.Build(
                AssemblyScanner.InScopePaths(currentDirectories, scope), framework);
            GraphCache.TrySave(cacheKey, graph);
        }

        return graph;
    }

    public static List<MethodChange> DiffMethods(Manifest baseline, Manifest current, AssembliesReport report)
    {
        var baseByName = baseline.Assemblies.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
        var currentByName = current.Assemblies.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
        var changes = new List<MethodChange>();

        foreach (var name in report.Changed)
        {
            var baseMethods = baseByName[name].Methods.ToDictionary(m => m.Fqn, m => m.Hash, StringComparer.Ordinal);
            var currentMethods = currentByName[name].Methods;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var method in currentMethods)
            {
                seen.Add(method.Fqn);
                if (!baseMethods.TryGetValue(method.Fqn, out var baseHash))
                    changes.Add(Change(name, method.Fqn, added: true));
                else if (!string.Equals(baseHash, method.Hash, StringComparison.Ordinal))
                    changes.Add(Change(name, method.Fqn, added: false));
            }

            foreach (var fqn in baseMethods.Keys)
            {
                if (!seen.Contains(fqn))
                    changes.Add(new MethodChange { Assembly = name, Fqn = fqn, Kind = "removed" });
            }
        }

        foreach (var name in report.Added)
        {
            foreach (var method in currentByName[name].Methods)
                changes.Add(Change(name, method.Fqn, added: true));
        }

        changes.Sort((a, b) => string.CompareOrdinal(a.Fqn, b.Fqn));
        return changes;

        static MethodChange Change(string assembly, string fqn, bool added) => new()
        {
            Assembly = assembly,
            Fqn = fqn,
            Kind = fqn.EndsWith(MethodHasher.TypeEntrySuffix, StringComparison.Ordinal)
                ? "type"
                : added ? "added" : "changed",
        };
    }

    /// <summary>
    /// Names of content files that were added, removed or edited. A pre-v4 baseline
    /// carries none, which is indistinguishable from "there were none": that is
    /// reported as a warning and the comparison skipped, rather than reading every
    /// current file as added and failing open on every run until the baseline is
    /// retaken.
    /// </summary>
    public static List<string> DiffContentFiles(Manifest baseline, Manifest current, List<string> warnings)
    {
        if (baseline.Version < Manifest.FirstVersionWithContentFiles)
        {
            if (current.ContentFiles.Count > 0)
                warnings.Add(
                    $"baseline manifest is version {baseline.Version} and records no content files; " +
                    "changes to appsettings.json and similar non-IL outputs cannot be detected — " +
                    "retake the baseline to close that gap");
            return [];
        }

        var baseByName = baseline.ContentFiles
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Hash, StringComparer.OrdinalIgnoreCase);
        var changed = new List<string>();

        foreach (var file in current.ContentFiles)
        {
            if (!baseByName.TryGetValue(file.Name, out var baseHash))
                changed.Add(file.Name);
            else if (!string.Equals(baseHash, file.Hash, StringComparison.Ordinal))
                changed.Add(file.Name);
        }

        var currentNames = current.ContentFiles.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        changed.AddRange(baseByName.Keys.Where(name => !currentNames.Contains(name)));

        changed.Sort(StringComparer.Ordinal);
        return changed;
    }

    public static AssembliesReport Diff(Manifest baseline, Manifest current)
    {
        var baseByName = baseline.Assemblies.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
        var currentByName = current.Assemblies.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
        var report = new AssembliesReport();

        foreach (var (name, entry) in currentByName.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!baseByName.TryGetValue(name, out var baseEntry))
                report.Added.Add(name);
            else if (!string.Equals(IdentityOf(baseEntry), IdentityOf(entry), StringComparison.OrdinalIgnoreCase))
                report.Changed.Add(name);
            else
                report.UnchangedCount++;
        }

        report.Removed.AddRange(
            baseByName.Keys.Except(currentByName.Keys, StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.Ordinal));
        return report;
    }

    /// <summary>Content hash when both sides have one (see ContentHasher); MVID otherwise.</summary>
    private static string IdentityOf(AssemblyEntry entry) =>
        entry.ContentHash.Length > 0 ? entry.ContentHash : entry.Mvid;
}
