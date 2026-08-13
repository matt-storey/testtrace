using NUnit.Framework;
using TestTrace.Core;

namespace TestTrace.Core.Tests;

[TestFixture]
public class GraphCacheTests
{
    private static Manifest ManifestOf(string scope, params (string Name, string Mvid)[] assemblies) => new()
    {
        Tfm = ".NETCoreApp,Version=v8.0",
        Scope = scope,
        Assemblies = assemblies.Select(a => new AssemblyEntry { Name = a.Name, Mvid = a.Mvid }).ToList(),
    };

    [Test]
    public void KeyFor_SameBuildAndScope_IsStable()
    {
        var key = GraphCache.KeyFor(ManifestOf("pdb-adjacent", ("A", "m1"), ("B", "m2")), "nunit");
        var same = GraphCache.KeyFor(ManifestOf("pdb-adjacent", ("B", "m2"), ("A", "m1")), "nunit");

        Assert.That(same, Is.EqualTo(key), "assembly ordering must not affect the key");
    }

    [Test]
    public void KeyFor_DifferentScope_DiffersEvenWhenMvidsAreIdentical()
    {
        // The regression this guards: the graph is built from IN-SCOPE assemblies only,
        // but the MVID set is identical for every scope over the same build. Keying on
        // MVIDs alone let a narrowly-scoped run serve its graph to a wider one, silently
        // dropping every test outside the narrower scope.
        var wide = ManifestOf("pdb-adjacent", ("App", "m1"), ("App.Tests", "m2"));
        var narrow = ManifestOf("include=[App] exclude=[]", ("App", "m1"), ("App.Tests", "m2"));

        Assert.That(GraphCache.KeyFor(narrow, "nunit"), Is.Not.EqualTo(GraphCache.KeyFor(wide, "nunit")));
    }

    [Test]
    public void KeyFor_DifferentFramework_Differs()
    {
        // Same guard as the scope case: discovery only finds the chosen framework's
        // tests, so an nunit-built graph served to an xunit run would report none of
        // xunit's — a silent under-selection.
        var build = ManifestOf("pdb-adjacent", ("App", "m1"), ("App.Tests", "m2"));

        Assert.That(GraphCache.KeyFor(build, "xunit"), Is.Not.EqualTo(GraphCache.KeyFor(build, "nunit")));
    }

    [Test]
    public void KeyFor_DifferentMvid_Differs()
    {
        var before = ManifestOf("pdb-adjacent", ("A", "m1"));
        var after = ManifestOf("pdb-adjacent", ("A", "m2"));

        Assert.That(GraphCache.KeyFor(after, "nunit"), Is.Not.EqualTo(GraphCache.KeyFor(before, "nunit")));
    }

    [Test]
    public void SaveThenLoad_RoundTripsTheGraph()
    {
        var key = "test-" + Guid.NewGuid().ToString("N");
        var graph = new CallGraphIndex
        {
            Reverse = { ["Callee::M/0"] = ["Caller::M/0"] },
            Tests = { new TestNode { Key = "T::M/0", DisplayName = "T.M", Assembly = "A", DeclaringType = "T" } },
        };

        try
        {
            GraphCache.TrySave(key, graph);
            var loaded = GraphCache.TryLoad(key);

            Assert.That(loaded, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(loaded!.Reverse["Callee::M/0"], Is.EqualTo(new[] { "Caller::M/0" }));
                Assert.That(loaded.Tests.Single().DisplayName, Is.EqualTo("T.M"));
            });
        }
        finally
        {
            try { File.Delete(Path.Combine(GraphCache.DefaultDirectory, key + ".json")); }
            catch (IOException) { /* best effort */ }
        }
    }

    [Test]
    public void TryLoad_UnknownKey_ReturnsNull() =>
        Assert.That(GraphCache.TryLoad("no-such-key-" + Guid.NewGuid().ToString("N")), Is.Null);
}
