using System.Text.Json;

namespace TestTrace.Core;

public static class ManifestIo
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Save(Manifest manifest, string path)
    {
        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, manifest, Options);
    }

    public static Manifest Load(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<Manifest>(stream, Options)
               ?? throw new InvalidDataException($"manifest {path} deserialised to null");
    }
}
