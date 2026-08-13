using NUnit.Framework;
using TestTrace.Core;

namespace TestTrace.Core.Tests;

[TestFixture]
public class AssemblyScannerTests
{
    [Test]
    public void Snapshot_OwnOutputDirectory_FindsThisAssembly()
    {
        var manifest = AssemblyScanner.Snapshot(AppContext.BaseDirectory);

        var self = manifest.Assemblies.SingleOrDefault(a => a.Name == "TestTrace.Core.Tests");
        Assert.Multiple(() =>
        {
            Assert.That(self, Is.Not.Null);
            Assert.That(Guid.TryParse(self!.Mvid, out var mvid) && mvid != Guid.Empty, Is.True);
            Assert.That(self.ContentHash, Has.Length.EqualTo(64), "SHA-256 hex");
            Assert.That(manifest.Tfm, Does.StartWith(".NETCoreApp,Version=v"));
        });
    }

    [Test]
    public void Snapshot_IsStableAcrossCalls()
    {
        var first = AssemblyScanner.Snapshot(AppContext.BaseDirectory);
        var second = AssemblyScanner.Snapshot(AppContext.BaseDirectory);

        Assert.That(
            second.Assemblies.Select(a => (a.Name, a.Mvid)),
            Is.EqualTo(first.Assemblies.Select(a => (a.Name, a.Mvid))));
    }

    [Test]
    public void Snapshot_MissingDirectory_Throws()
    {
        Assert.Throws<DirectoryNotFoundException>(() =>
            AssemblyScanner.Snapshot(Path.Combine(AppContext.BaseDirectory, "does-not-exist")));
    }

    [Test]
    public void ManifestRoundTrip_PreservesEntries()
    {
        var manifest = AssemblyScanner.Snapshot(AppContext.BaseDirectory);
        var path = Path.Combine(Path.GetTempPath(), $"testtrace-{Guid.NewGuid():N}.json");
        try
        {
            ManifestIo.Save(manifest, path);
            var loaded = ManifestIo.Load(path);

            Assert.Multiple(() =>
            {
                Assert.That(loaded.Tfm, Is.EqualTo(manifest.Tfm));
                Assert.That(
                    loaded.Assemblies.Select(a => (a.Name, a.Mvid)),
                    Is.EqualTo(manifest.Assemblies.Select(a => (a.Name, a.Mvid))));
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    // -- reusing method hashes from a baseline ---------------------------------

    private static string Fingerprint(Manifest m) =>
        string.Join("\n", m.Assemblies.Select(a =>
            $"{a.Name}|{a.Mvid}|{a.ContentHash}|{a.MethodsAnalyzed}|" +
            string.Join(",", a.Methods.Select(x => x.Fqn + "=" + x.Hash))));

    [Test]
    public void Snapshot_ReusingAnUnchangedBaseline_ProducesTheSameManifest()
    {
        // The property the optimisation rests on: skipping method hashing where the
        // content hash is unchanged must be invisible in the result. If this ever
        // diverges, analyze would diff against hashes that do not match reality.
        var scope = AssemblyScope.Default;
        var full = AssemblyScanner.Snapshot(AppContext.BaseDirectory, scope);
        var reusing = AssemblyScanner.Snapshot(AppContext.BaseDirectory, scope, full);

        Assert.That(Fingerprint(reusing), Is.EqualTo(Fingerprint(full)));
    }

    [Test]
    public void Snapshot_WhenContentHashDiffers_RehashesRatherThanTrustingTheBaseline()
    {
        // A stale baseline entry must never be carried over: that would hide the very
        // change the run exists to find.
        var scope = AssemblyScope.Default;
        var real = AssemblyScanner.Snapshot(AppContext.BaseDirectory, scope);
        var target = real.Assemblies.First(a => a.MethodsAnalyzed && a.Methods.Count > 0);

        var stale = new Manifest
        {
            Tfm = real.Tfm,
            Scope = real.Scope,
            Assemblies = real.Assemblies.Select(a => new AssemblyEntry
            {
                Name = a.Name,
                Mvid = a.Mvid,
                // Same assembly, different content hash: the entry cannot be trusted.
                ContentHash = a.Name == target.Name ? "not-the-real-hash" : a.ContentHash,
                MethodsAnalyzed = a.MethodsAnalyzed,
                Methods = a.Name == target.Name
                    ? [new MethodEntry { Fqn = "Ghost::Method()", Hash = "DEADBEEF" }]
                    : a.Methods,
            }).ToList(),
        };

        var rescanned = AssemblyScanner.Snapshot(AppContext.BaseDirectory, scope, stale);
        var entry = rescanned.Assemblies.Single(a => a.Name == target.Name);

        Assert.Multiple(() =>
        {
            Assert.That(entry.Methods.Select(m => m.Fqn), Has.None.EqualTo("Ghost::Method()"));
            Assert.That(entry.Methods.Select(m => m.Fqn), Is.EquivalentTo(target.Methods.Select(m => m.Fqn)));
        });
    }

    [Test]
    public void Snapshot_DoesNotReuseFromAnOutOfScopeBaselineEntry()
    {
        // The baseline recorded no methods for it, so there is nothing to carry over
        // even though the content matches — it must be hashed for real.
        var scope = AssemblyScope.Default;
        var real = AssemblyScanner.Snapshot(AppContext.BaseDirectory, scope);
        var target = real.Assemblies.First(a => a.MethodsAnalyzed && a.Methods.Count > 0);

        var narrowed = new Manifest
        {
            Tfm = real.Tfm,
            Scope = real.Scope,
            Assemblies = real.Assemblies.Select(a => new AssemblyEntry
            {
                Name = a.Name,
                Mvid = a.Mvid,
                ContentHash = a.ContentHash,
                MethodsAnalyzed = a.Name != target.Name && a.MethodsAnalyzed,
                Methods = a.Name == target.Name ? [] : a.Methods,
            }).ToList(),
        };

        var rescanned = AssemblyScanner.Snapshot(AppContext.BaseDirectory, scope, narrowed);

        Assert.That(rescanned.Assemblies.Single(a => a.Name == target.Name).Methods, Is.Not.Empty);
    }
}
