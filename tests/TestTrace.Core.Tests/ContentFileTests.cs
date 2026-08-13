using NUnit.Framework;
using TestTrace.Core;

namespace TestTrace.Core.Tests;

/// <summary>
/// Files that carry behaviour but compile to no IL. Without them a config-only edit
/// produces no assembly diff at all and reads as "nothing affected" — a silent skip
/// of the whole test run, which is the one failure mode the design forbids.
/// </summary>
[TestFixture]
public class ContentFileTests
{
    /// <summary>These suites are NUnit projects, so that is the framework under test.</summary>
    private static readonly ITestFrameworkDetector NUnit = TestFrameworks.ByName("nunit")!;

    private static Manifest ManifestWith(params (string Name, string Hash)[] files) => new()
    {
        Version = Manifest.CurrentVersion,
        Tfm = ".NETCoreApp,Version=v8.0",
        ContentFiles = files.Select(f => new ContentFileEntry { Name = f.Name, Hash = f.Hash }).ToList(),
    };

    [Test]
    public void DiffContentFiles_EditedFile_IsReported()
    {
        var changed = Analyzer.DiffContentFiles(
            ManifestWith(("appsettings.json", "h1")),
            ManifestWith(("appsettings.json", "h2")),
            []);

        Assert.That(changed, Is.EqualTo(new[] { "appsettings.json" }));
    }

    [Test]
    public void DiffContentFiles_AddedAndRemoved_AreReported()
    {
        var changed = Analyzer.DiffContentFiles(
            ManifestWith(("gone.json", "h1")),
            ManifestWith(("new.json", "h2")),
            []);

        Assert.That(changed, Is.EquivalentTo(new[] { "gone.json", "new.json" }));
    }

    [Test]
    public void DiffContentFiles_Unchanged_ReportsNothing()
    {
        var changed = Analyzer.DiffContentFiles(
            ManifestWith(("appsettings.json", "h1"), ("a.deps.json", "h2")),
            ManifestWith(("a.deps.json", "h2"), ("appsettings.json", "h1")),
            []);

        Assert.That(changed, Is.Empty);
    }

    [Test]
    public void DiffContentFiles_OlderBaseline_WarnsAndSkipsRatherThanFlaggingEverything()
    {
        var warnings = new List<string>();
        var baseline = ManifestWith(("appsettings.json", "h1"));
        baseline.Version = 3;
        baseline.ContentFiles = []; // v3 recorded none

        var changed = Analyzer.DiffContentFiles(baseline, ManifestWith(("appsettings.json", "h1")), warnings);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.Empty, "an old baseline must not read as 'every file added'");
            Assert.That(warnings, Has.Count.EqualTo(1));
            Assert.That(warnings[0], Does.Contain("retake the baseline"));
        });
    }

    [Test]
    public void Snapshot_RecordsContentFiles_ButNotDocumentationXml()
    {
        var manifest = AssemblyScanner.Snapshot(AppContext.BaseDirectory, AssemblyScope.Default);
        var names = manifest.ContentFiles.Select(f => f.Name).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(names, Is.Not.Empty, "a test output directory always has .deps.json / .runtimeconfig.json");
            Assert.That(names, Has.All.Matches<string>(
                n => AssemblyScanner.ContentFileExtensions.Contains(Path.GetExtension(n), StringComparer.OrdinalIgnoreCase)));
            // .xml would track doc comments, making a comment-only edit select everything.
            Assert.That(names, Has.None.EndsWith(".xml"));
            Assert.That(manifest.ContentFiles, Has.All.Matches<ContentFileEntry>(f => f.Hash.Length == 64));
        });
    }

    [Test]
    public void Analyze_ContentFileChangedButNoIlChange_FailsOpen()
    {
        // Exactly the appsettings.json shape: every assembly is byte-identical, so the
        // assembly diff is empty. Before content files were tracked this returned
        // "no assembly-level changes" and exit 3 — skip the entire suite.
        var current = AssemblyScanner.Snapshot(AppContext.BaseDirectory, AssemblyScope.Default);
        var baseline = new Manifest
        {
            Version = Manifest.CurrentVersion,
            Tfm = current.Tfm,
            Scope = current.Scope,
            Assemblies = current.Assemblies,
            ContentFiles = current.ContentFiles
                .Select(f => new ContentFileEntry { Name = f.Name, Hash = f.Name == current.ContentFiles[0].Name ? "0" : f.Hash })
                .ToList(),
        };

        var result = Analyzer.Analyze(baseline, AppContext.BaseDirectory, NUnit);

        Assert.Multiple(() =>
        {
            Assert.That(result.RunEverything, Is.True);
            Assert.That(result.Reason, Does.Contain("non-assembly build outputs changed"));
            Assert.That(result.Reason, Does.Contain(current.ContentFiles[0].Name));
        });
    }

    [Test]
    public void Analyze_NothingChangedAtAll_StillReportsNothingAffected()
    {
        // The counterweight: content-file tracking must not turn the idempotent case
        // into a permanent RUN_EVERYTHING.
        var current = AssemblyScanner.Snapshot(AppContext.BaseDirectory, AssemblyScope.Default);

        var result = Analyzer.Analyze(current, AppContext.BaseDirectory, NUnit);

        Assert.Multiple(() =>
        {
            Assert.That(result.RunEverything, Is.False, result.Reason);
            Assert.That(result.SelectedTests, Is.Empty);
        });
    }
}
