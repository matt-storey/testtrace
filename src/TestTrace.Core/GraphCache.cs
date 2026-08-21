using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TestTrace.Core;

/// <summary>
/// Disk cache for the call graph, keyed by the exact set of input MVIDs and the
/// analysis scope (plus a format version). First analyze on a build is slow;
/// repeats are near-instant. Best-effort on both ends: any cache failure just
/// means a rebuild.
/// </summary>
/// <summary>What the per-assembly cache needs beyond the assemblies themselves: the
/// scope description, since scope decides which edges are kept.</summary>
public sealed record GraphCacheContext(string Scope);

public static class GraphCache
{
    // 7: per-assembly partials are cached alongside merged graphs.
    private const int FormatVersion = 7;

    /// <summary>
    /// The scope and the framework MUST both be part of the key. The graph is built from in-scope assemblies
    /// only, but the MVID set is identical for every scope over the same build — so
    /// keying on MVIDs alone lets a narrowly-scoped run serve its graph to a wider one,
    /// silently dropping every test that lives outside the narrower scope. That is an
    /// under-selection, the one failure mode this tool exists to prevent.
    ///
    /// The framework is in the key for exactly the same reason: discovery only finds
    /// the chosen framework's tests, so an nunit-built graph handed to an xunit run
    /// would report none of its tests.
    /// </summary>
    public static string KeyFor(Manifest current, string framework)
    {
        var text = new StringBuilder()
            .Append("v").Append(FormatVersion).Append('\n')
            .Append("scope:").Append(current.Scope).Append('\n')
            .Append("framework:").Append(framework).Append('\n');
        foreach (var assembly in current.Assemblies.OrderBy(a => a.Name, StringComparer.Ordinal))
            text.Append(assembly.Name).Append(':').Append(assembly.Mvid).Append('\n');
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return Convert.ToHexString(hash.AsSpan(0, 16));
    }

    /// <summary>
    /// Key for ONE assembly's partial graph.
    ///
    /// The closure matters, and keying on the assembly's own MVID alone would be
    /// unsound. Scanning an assembly resolves types out of the ones it references —
    /// interfaces and base types for the polymorphism edges, the base chain for MVC
    /// controller detection, injected fixture types. Deterministic builds make the
    /// hazard reachable rather than theoretical: if a referenced assembly changes but
    /// this one's IL does not, its MVID is unchanged, so a self-keyed entry would
    /// serve a partial that is missing edges — a dropped edge is a dropped test.
    ///
    /// When everything transitively references everything, this degenerates to the
    /// whole-build key, so it can never be worse than caching the merged graph alone.
    /// </summary>
    public static string KeyForAssembly(
        string assemblyName,
        IReadOnlyDictionary<string, string> mvidsByName,
        IReadOnlyCollection<string> closure,
        string scope,
        string framework)
    {
        var text = new StringBuilder()
            .Append("v").Append(FormatVersion).Append("\npartial\n")
            .Append("scope:").Append(scope).Append('\n')
            .Append("framework:").Append(framework).Append('\n')
            .Append("self:").Append(assemblyName).Append(':')
            .Append(mvidsByName.GetValueOrDefault(assemblyName, "?")).Append('\n');

        foreach (var name in closure.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!string.Equals(name, assemblyName, StringComparison.OrdinalIgnoreCase))
                text.Append("ref:").Append(name).Append(':')
                    .Append(mvidsByName.GetValueOrDefault(name, "?")).Append('\n');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return Convert.ToHexString(hash.AsSpan(0, 16));
    }

    public static string DefaultDirectory =>
        Path.Combine(Path.GetTempPath(), "testtrace-graph-cache");

    public static CallGraphIndex? TryLoad(string key)
    {
        try
        {
            var path = Path.Combine(DefaultDirectory, key + ".json");
            if (!File.Exists(path))
                return null;
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<CallGraphIndex>(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static CallGraphIndex? TryLoadPartial(string key) => TryLoad("p-" + key);

    public static void TrySavePartial(string key, CallGraphIndex partial) => TrySave("p-" + key, partial);

    /// <summary>
    /// Entries are per build state, and per-assembly caching multiplies that by the
    /// number of assemblies, so the directory would grow without bound. Prune once per
    /// process rather than per save — it is a cache, so over-keeping briefly is fine
    /// and stat-ing thousands of files on every write is not.
    /// </summary>
    private static int _pruned;

    private static void PruneOnce()
    {
        if (Interlocked.Exchange(ref _pruned, 1) != 0)
            return;

        try
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(7);
            foreach (var file in Directory.EnumerateFiles(DefaultDirectory, "*.json"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch (Exception)
        {
            // Pruning is housekeeping; never let it break an analysis.
        }
    }

    public static void TrySave(string key, CallGraphIndex graph)
    {
        try
        {
            Directory.CreateDirectory(DefaultDirectory);
            PruneOnce();
            var path = Path.Combine(DefaultDirectory, key + ".json");
            var temp = path + "." + Environment.ProcessId + ".tmp";
            using (var stream = File.Create(temp))
                JsonSerializer.Serialize(stream, graph);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception)
        {
            // cache is an optimization, never a failure
        }
    }
}
