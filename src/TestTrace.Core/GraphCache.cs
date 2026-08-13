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
public static class GraphCache
{
    // 6: SetupFixtureByKey carries a LifecycleTarget rather than a type name.
    private const int FormatVersion = 6;

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

    public static void TrySave(string key, CallGraphIndex graph)
    {
        try
        {
            Directory.CreateDirectory(DefaultDirectory);
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
