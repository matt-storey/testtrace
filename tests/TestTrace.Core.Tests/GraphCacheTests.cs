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

    // -- per-assembly partial keys --------------------------------------------

    private static readonly Dictionary<string, string> Mvids = new(StringComparer.OrdinalIgnoreCase)
    {
        ["App"] = "m-app",
        ["Lib"] = "m-lib",
        ["Other"] = "m-other",
    };

    private static string PartialKey(
        string assembly, string[] closure, string scope = "pdb-adjacent",
        string framework = "nunit", Dictionary<string, string>? mvids = null) =>
        GraphCache.KeyForAssembly(assembly, mvids ?? Mvids, closure, scope, framework);

    [Test]
    public void KeyForAssembly_ChangesWhenAReferencedAssemblyChanges()
    {
        // THE property the whole per-assembly cache rests on. Scanning App resolves
        // types out of Lib — interfaces and base types for polymorphism edges, the base
        // chain for controller detection. Deterministic builds mean App's own MVID does
        // NOT move when only Lib changes, so keying on App alone would serve a partial
        // that is missing edges. A dropped edge is a dropped test.
        var before = PartialKey("App", ["Lib"]);

        var afterLibChanged = new Dictionary<string, string>(Mvids, StringComparer.OrdinalIgnoreCase)
        {
            ["Lib"] = "m-lib-CHANGED",
        };

        Assert.That(PartialKey("App", ["Lib"], mvids: afterLibChanged), Is.Not.EqualTo(before),
            "a change in a referenced assembly must invalidate this assembly's partial");
    }

    [Test]
    public void KeyForAssembly_IgnoresAssembliesOutsideItsClosure()
    {
        // The point of closure keying over whole-build keying: an unrelated assembly
        // moving must NOT invalidate this one, or nothing is ever reused.
        var before = PartialKey("App", ["Lib"]);

        var afterUnrelatedChanged = new Dictionary<string, string>(Mvids, StringComparer.OrdinalIgnoreCase)
        {
            ["Other"] = "m-other-CHANGED",
        };

        Assert.That(PartialKey("App", ["Lib"], mvids: afterUnrelatedChanged), Is.EqualTo(before));
    }

    [Test]
    public void KeyForAssembly_ChangesWhenTheAssemblyItselfChanges()
    {
        var before = PartialKey("App", ["Lib"]);
        var moved = new Dictionary<string, string>(Mvids, StringComparer.OrdinalIgnoreCase)
        {
            ["App"] = "m-app-CHANGED",
        };

        Assert.That(PartialKey("App", ["Lib"], mvids: moved), Is.Not.EqualTo(before));
    }

    [Test]
    public void KeyForAssembly_IsStableRegardlessOfClosureOrdering() =>
        Assert.That(PartialKey("App", ["Other", "Lib"]), Is.EqualTo(PartialKey("App", ["Lib", "Other"])));

    [Test]
    public void KeyForAssembly_SeparatesScopeAndFramework()
    {
        // Same reasons as the merged-graph key: scope decides which edges survive, and
        // discovery only finds the chosen framework's tests.
        var baseline = PartialKey("App", ["Lib"]);

        Assert.Multiple(() =>
        {
            Assert.That(PartialKey("App", ["Lib"], scope: "include=[App]"), Is.Not.EqualTo(baseline));
            Assert.That(PartialKey("App", ["Lib"], framework: "xunit"), Is.Not.EqualTo(baseline));
        });
    }

    [Test]
    public void PartialKeys_DoNotCollideWithMergedGraphKeys()
    {
        // Both live in one directory; a collision would hand a partial to a caller
        // expecting a whole graph.
        var partial = "p-" + PartialKey("App", []);
        var merged = GraphCache.KeyFor(ManifestOf("pdb-adjacent", ("App", "m-app")), "nunit");

        Assert.That(partial, Is.Not.EqualTo(merged));
    }
}
