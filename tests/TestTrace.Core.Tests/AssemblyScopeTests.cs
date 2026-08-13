using NUnit.Framework;
using TestTrace.Core;

namespace TestTrace.Core.Tests;

[TestFixture]
public class AssemblyScopeTests
{
    /// <summary>These suites are NUnit projects, so that is the framework under test.</summary>
    private static readonly ITestFrameworkDetector NUnit = TestFrameworks.ByName("nunit")!;

    [Test]
    public void Default_UsesPdbAdjacency()
    {
        var scope = AssemblyScope.Default;
        var self = typeof(AssemblyScopeTests).Assembly.Location;
        var package = Path.Combine(Path.GetDirectoryName(self)!, "nunit.framework.dll");

        Assert.Multiple(() =>
        {
            Assert.That(scope.IsInScope("TestTrace.Core.Tests", self), Is.True, "our own build has a pdb");
            if (File.Exists(package) && !File.Exists(Path.ChangeExtension(package, ".pdb")))
                Assert.That(scope.IsInScope("nunit.framework", package), Is.False, "package ships no pdb");
            Assert.That(scope.Describe(), Is.EqualTo("pdb-adjacent"));
        });
    }

    [Test]
    public void Include_ReplacesDefaultHeuristic()
    {
        var scope = new AssemblyScope { Include = ["MyApp.*"] };
        var anyPath = typeof(AssemblyScopeTests).Assembly.Location;

        Assert.Multiple(() =>
        {
            Assert.That(scope.IsInScope("MyApp.Core", anyPath), Is.True);
            // In scope by the pdb heuristic, but excluded because Include is set.
            Assert.That(scope.IsInScope("TestTrace.Core.Tests", anyPath), Is.False);
        });
    }

    [Test]
    public void Exclude_WinsOverInclude()
    {
        var scope = new AssemblyScope { Include = ["MyApp.*"], Exclude = ["MyApp.Generated*"] };
        var anyPath = typeof(AssemblyScopeTests).Assembly.Location;

        Assert.Multiple(() =>
        {
            Assert.That(scope.IsInScope("MyApp.Core", anyPath), Is.True);
            Assert.That(scope.IsInScope("MyApp.Generated.Client", anyPath), Is.False);
            Assert.That(scope.Describe(), Does.Contain("MyApp.Generated*"));
        });
    }

    [Test]
    public void Snapshot_ContentHashesEverything_ButMethodHashesOnlyInScope()
    {
        var manifest = AssemblyScanner.Snapshot(
            AppContext.BaseDirectory,
            new AssemblyScope { Include = ["TestTrace.Core"] });

        var inScope = manifest.Assemblies.Single(a => a.Name == "TestTrace.Core");
        var outOfScope = manifest.Assemblies.Where(a => a.Name != "TestTrace.Core").ToList();

        Assert.Multiple(() =>
        {
            Assert.That(inScope.MethodsAnalyzed, Is.True);
            Assert.That(inScope.Methods, Is.Not.Empty);
            Assert.That(outOfScope, Is.Not.Empty);
            Assert.That(outOfScope, Has.All.Matches<AssemblyEntry>(a => !a.MethodsAnalyzed && a.Methods.Count == 0));
            Assert.That(outOfScope, Has.All.Matches<AssemblyEntry>(a => a.ContentHash.Length == 64),
                "every assembly is still content-hashed, so package bumps are detected");
        });
    }

    [Test]
    public void Analyze_OutOfScopeAssemblyChanged_FailsOpen()
    {
        // Baseline claims a package assembly with a different content hash and no
        // method entries: exactly the transitive-package-bump shape.
        var current = AssemblyScanner.Snapshot(AppContext.BaseDirectory, AssemblyScope.Default);
        var baseline = new Manifest
        {
            Tfm = current.Tfm,
            Scope = current.Scope,
            // Identical apart from the perturbed assembly hash below, so the content
            // files must match too or the content-file check fires first.
            ContentFiles = current.ContentFiles,
            Assemblies = current.Assemblies
                .Select(a => new AssemblyEntry
                {
                    Name = a.Name,
                    Mvid = a.Mvid,
                    ContentHash = a.MethodsAnalyzed ? a.ContentHash : "0",
                    MethodsAnalyzed = a.MethodsAnalyzed,
                    Methods = a.Methods,
                })
                .ToList(),
        };

        var result = Analyzer.Analyze(baseline, AppContext.BaseDirectory, NUnit);

        Assert.Multiple(() =>
        {
            Assert.That(result.RunEverything, Is.True);
            Assert.That(result.Reason, Does.Contain("out-of-scope"));
        });
    }

    [Test]
    public void Analyze_ChangesButNoTestsDiscovered_FailsOpen()
    {
        // Scope limited to an assembly containing no test methods. A change is
        // detected, but an empty selection would mean "skip everything" rather than
        // "nothing affected" — the exact silent miss the design forbids. Also covers
        // pointing at a non-NUnit project, where no tests are detected either.
        var scope = new AssemblyScope { Include = ["TestTrace.Core"] };
        var current = AssemblyScanner.Snapshot(AppContext.BaseDirectory, scope);
        var baseline = new Manifest
        {
            Tfm = current.Tfm,
            Scope = current.Scope,
            ContentFiles = current.ContentFiles,
            Assemblies = current.Assemblies
                .Select(a => new AssemblyEntry
                {
                    Name = a.Name,
                    Mvid = a.Mvid,
                    // Perturb the in-scope assembly so a method-level change is seen.
                    ContentHash = a.MethodsAnalyzed ? "0" : a.ContentHash,
                    MethodsAnalyzed = a.MethodsAnalyzed,
                    Methods = a.MethodsAnalyzed ? [] : a.Methods,
                })
                .ToList(),
        };

        var result = Analyzer.Analyze(baseline, [AppContext.BaseDirectory], NUnit, scope: scope);

        Assert.Multiple(() =>
        {
            Assert.That(result.RunEverything, Is.True);
            Assert.That(result.Reason, Does.Contain("no 'nunit' tests were found"));
        });
    }

    [Test]
    public void Analyze_WrongFrameworkChosen_SaysWhichOneTheBuildActuallyHas()
    {
        // This assembly is an NUnit test project, so asking for xunit finds nothing.
        // The presence data — read from assembly references, not by running every
        // detector — turns a puzzling empty selection into an actionable message.
        var scope = AssemblyScope.Default;
        var current = AssemblyScanner.Snapshot(AppContext.BaseDirectory, scope);
        var baseline = new Manifest
        {
            Tfm = current.Tfm,
            Scope = current.Scope,
            ContentFiles = current.ContentFiles,
            Assemblies = current.Assemblies
                .Select(a => new AssemblyEntry
                {
                    Name = a.Name,
                    Mvid = a.Mvid,
                    ContentHash = a.MethodsAnalyzed ? "0" : a.ContentHash,
                    MethodsAnalyzed = a.MethodsAnalyzed,
                    Methods = a.MethodsAnalyzed ? [] : a.Methods,
                })
                .ToList(),
        };

        var result = Analyzer.Analyze(
            baseline, [AppContext.BaseDirectory], TestFrameworks.ByName("xunit")!, scope: scope);

        Assert.Multiple(() =>
        {
            Assert.That(result.RunEverything, Is.True);
            Assert.That(result.Reason, Does.Contain("no 'xunit' tests were found"));
            Assert.That(result.Reason, Does.Contain("--test-framework nunit"));
        });
    }

    [Test]
    public void AnalyzePdb_ChangeInOutOfScopeAssembly_FailsOpen()
    {
        // The PDB front-end reads every assembly with a co-located .pdb, but the graph
        // is built from in-scope assemblies only — so a change outside the scope
        // resolves to no graph node and contributes nothing to the walk. It used to
        // report "0 impacted tests" (exit 3: skip the run) for a change it had just
        // printed as changed. It must fail open, like the manifest front-end does.
        var scope = new AssemblyScope { Include = ["TestTrace.Core.Tests"] };

        var result = Analyzer.Analyze(
            baseline: null,
            currentDirectories: [AppContext.BaseDirectory],
            framework: NUnit,
            changedFiles: ["TestTrace.Core/Analyzer.cs:1-4000"],
            scope: scope);

        Assert.Multiple(() =>
        {
            Assert.That(result.RunEverything, Is.True, result.Reason);
            Assert.That(result.Reason, Does.Contain("out-of-scope"));
        });
    }

    [Test]
    public void AnalyzePdb_SameChangeInScope_SelectsTestsInsteadOfFailingOpen()
    {
        // Control for the test above: the fail-open must be caused by the scope, not by
        // the change being untraceable in general.
        var result = Analyzer.Analyze(
            baseline: null,
            currentDirectories: [AppContext.BaseDirectory],
            framework: NUnit,
            changedFiles: ["TestTrace.Core/Analyzer.cs:1-4000"],
            scope: AssemblyScope.Default);

        Assert.Multiple(() =>
        {
            Assert.That(result.RunEverything, Is.False, result.Reason);
            Assert.That(result.SelectedTests, Is.Not.Empty);
        });
    }

    [Test]
    public void Analyze_ScopeMismatch_FailsOpen()
    {
        var current = AssemblyScanner.Snapshot(AppContext.BaseDirectory, AssemblyScope.Default);
        var baseline = new Manifest { Tfm = current.Tfm, Scope = "include=[Something.Else]", Assemblies = [] };

        var result = Analyzer.Analyze(baseline, AppContext.BaseDirectory, NUnit);

        Assert.Multiple(() =>
        {
            Assert.That(result.RunEverything, Is.True);
            Assert.That(result.Reason, Does.Contain("scope"));
        });
    }
}
