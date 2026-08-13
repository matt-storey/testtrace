using NUnit.Framework;
using TestTrace.Core;

namespace TestTrace.Core.Tests;

[TestFixture]
public class AnalyzerTests
{
    /// <summary>These suites are NUnit projects, so that is the framework under test.</summary>
    private static readonly ITestFrameworkDetector NUnit = TestFrameworks.ByName("nunit")!;

    private static Manifest ManifestOf(string tfm, params (string Name, string Mvid)[] assemblies) => new()
    {
        Tfm = tfm,
        Assemblies = assemblies
            .Select(a => new AssemblyEntry { Name = a.Name, Mvid = a.Mvid })
            .ToList(),
    };

    [Test]
    public void Diff_ClassifiesAddedRemovedChangedUnchanged()
    {
        var baseline = ManifestOf("net8", ("A", "m1"), ("B", "m2"), ("C", "m3"));
        var current = ManifestOf("net8", ("A", "m1"), ("B", "m2-changed"), ("D", "m4"));

        var report = Analyzer.Diff(baseline, current);

        Assert.Multiple(() =>
        {
            Assert.That(report.Added, Is.EqualTo(new[] { "D" }));
            Assert.That(report.Removed, Is.EqualTo(new[] { "C" }));
            Assert.That(report.Changed, Is.EqualTo(new[] { "B" }));
            Assert.That(report.UnchangedCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void Diff_AssemblyNamesAreCaseInsensitive()
    {
        var baseline = ManifestOf("net8", ("SampleApp.Api", "m1"));
        var current = ManifestOf("net8", ("sampleapp.api", "m1"));

        var report = Analyzer.Diff(baseline, current);

        Assert.Multiple(() =>
        {
            Assert.That(report.Added, Is.Empty);
            Assert.That(report.Removed, Is.Empty);
            Assert.That(report.UnchangedCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void Analyze_TfmMismatch_FailsOpen()
    {
        var baseline = ManifestOf(".NETCoreApp,Version=v99.0", ("A", "m1"));
        var result = Analyzer.Analyze(baseline, AppContext.BaseDirectory, NUnit);

        Assert.Multiple(() =>
        {
            Assert.That(result.RunEverything, Is.True);
            Assert.That(result.Reason, Does.Contain("TFM"));
        });
    }
}
