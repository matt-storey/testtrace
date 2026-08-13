using NUnit.Framework;
using TestTrace.Core;

namespace TestTrace.Scenarios.Tests;

/// <summary>
/// The whole approach rests on builds being reproducible: if two clean builds of
/// unchanged source produce different assemblies, every diff is noise. Usual
/// culprits when this fails are AssemblyVersion wildcards or a timestamp baked
/// into generated code.
/// </summary>
[TestFixture("net8.0")]
[TestFixture("net10.0")]
[NonParallelizable]
public sealed class DeterminismTests
{
    private readonly string _tfm;

    public DeterminismTests(string tfm) => _tfm = tfm;

    [Test]
    public void TwoCleanBuilds_ProduceIdenticalAssemblies()
    {
        using var workspace = SampleWorkspace.Create(_tfm);

        workspace.Build();
        var first = AssemblyScanner.Snapshot(workspace.Harvest("det-1"));
        workspace.Build();
        var second = AssemblyScanner.Snapshot(workspace.Harvest("det-2"));

        var firstByName = first.Assemblies.ToDictionary(a => a.Name, StringComparer.Ordinal);
        var differing = second.Assemblies
            .Where(a => !firstByName.TryGetValue(a.Name, out var other)
                        || other.Mvid != a.Mvid
                        || other.ContentHash != a.ContentHash)
            .Select(a => a.Name)
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(first.Assemblies, Is.Not.Empty);
            Assert.That(differing, Is.Empty, "these assemblies did not build deterministically");
            Assert.That(second.Assemblies, Has.Count.EqualTo(first.Assemblies.Count));
        });
    }

    [Test]
    public void SnapshotOfCurrentBuild_SelectsNothing()
    {
        // Idempotence: analyzing a build against its own manifest must produce an
        // empty selection, not RUN_EVERYTHING.
        using var workspace = SampleWorkspace.Create(_tfm);
        workspace.Build();
        var output = workspace.Harvest("idempotent");
        var manifest = AssemblyScanner.Snapshot(output);

        var result = Analyzer.Analyze(manifest, [output], TestFrameworks.ByName("nunit")!);

        Assert.Multiple(() =>
        {
            Assert.That(result.RunEverything, Is.False, result.Reason);
            Assert.That(result.SelectedTests, Is.Empty);
        });
    }
}
