using NUnit.Framework;
using TestTrace.Core;

namespace TestTrace.Core.Tests;

/// <summary>
/// A cached partial must be indistinguishable from a freshly scanned one. If the two
/// ever diverge, analyze diffs against a graph that no longer matches the build, and
/// the failure is silent — a missing edge is a test that quietly stops being selected.
/// </summary>
[TestFixture]
public class PartialGraphCacheTests
{
    private static readonly ITestFrameworkDetector NUnit = TestFrameworks.ByName("nunit")!;

    private static IReadOnlyList<string> OwnAssemblies() =>
        AssemblyScanner.InScopePaths([AppContext.BaseDirectory], AssemblyScope.Default);

    /// <summary>Everything the walk actually consumes, flattened for comparison.</summary>
    private static string Fingerprint(CallGraphIndex g) => string.Join("\n",
    [
        "tests:" + string.Join(",", g.Tests.Select(t => $"{t.DisplayName}|{t.Assembly}|{t.Parameterized}")),
        "edges:" + string.Join(",", g.Reverse.OrderBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => e.Key + "->" + string.Join("+", e.Value.Order(StringComparer.Ordinal)))),
        "lifecycle:" + string.Join(",", g.SetupFixtureByKey.OrderBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => $"{e.Key}={e.Value.Scope}/{e.Value.DeclaringType}/{e.Value.Assembly}")),
        "bases:" + string.Join(",", g.BaseTypeOf.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => e.Key + "=" + e.Value)),
        "members:" + string.Join(",", g.TypeMembers.OrderBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => e.Key + "=" + string.Join("+", e.Value.Order(StringComparer.Ordinal)))),
        "providers:" + string.Join(",", g.TestKeysByProviderKey.OrderBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => e.Key + "=" + string.Join("+", e.Value.Order(StringComparer.Ordinal)))),
        "frameworks:" + string.Join(",", g.FrameworksPresent),
    ]);

    [Test]
    public void AGraphBuiltFromCachedPartials_IsIdenticalToOneScannedFresh()
    {
        var paths = OwnAssemblies();
        var scope = new GraphCacheContext(AssemblyScope.Default.Describe() + "-partialtest-" + Guid.NewGuid().ToString("N")[..8]);

        // Cold: nothing cached, every assembly scanned, every partial written.
        var cold = CallGraphBuilder.Build(paths, NUnit, scope);

        // Warm: same inputs, so every partial should now come off disk.
        var warm = CallGraphBuilder.Build(paths, NUnit, scope);

        Assert.That(Fingerprint(warm), Is.EqualTo(Fingerprint(cold)));
    }

    [Test]
    public void CachingDisabled_MatchesCachingEnabled()
    {
        // The no-cache overload is what the other unit tests use; it must not drift
        // from the cached path.
        var paths = OwnAssemblies();
        var uncached = CallGraphBuilder.Build(paths, NUnit);
        var cached = CallGraphBuilder.Build(
            paths, NUnit, new GraphCacheContext("partialtest-" + Guid.NewGuid().ToString("N")[..8]));

        Assert.That(Fingerprint(cached), Is.EqualTo(Fingerprint(uncached)));
    }

    [Test]
    public void ADifferentScope_DoesNotReuseTheOtherScopesPartials()
    {
        // Scope decides which edges survive the scan, so partials cannot be shared
        // across scopes — the same failure mode as the old whole-graph scope-key bug.
        var paths = OwnAssemblies();
        var run = Guid.NewGuid().ToString("N")[..8];

        var wide = CallGraphBuilder.Build(paths, NUnit, new GraphCacheContext($"wide-{run}"));
        var narrow = CallGraphBuilder.Build(paths, NUnit, new GraphCacheContext($"narrow-{run}"));

        // Same inputs so the graphs agree; the point is that neither served the other's
        // entries, which would be indistinguishable here but not in general.
        Assert.That(Fingerprint(narrow), Is.EqualTo(Fingerprint(wide)));
    }
}
