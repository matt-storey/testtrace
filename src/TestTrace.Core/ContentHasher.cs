using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace TestTrace.Core;

/// <summary>
/// Deterministic MVIDs hash the whole PE image, and the PE embeds the PDB's
/// identity (debug directory), which in turn hashes source document checksums and
/// sequence points — so ANY source edit changes the MVID, comments included.
/// For change detection we instead hash the PE with the debug-only regions zeroed:
/// COFF timestamp, optional-header checksum, debug directory (table + blobs) and
/// the MVID itself. What remains is metadata + IL: the parts that can affect tests.
/// </summary>
public static class ContentHasher
{
    public static string HashIgnoringDebugInfo(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var pe = new PEReader(new MemoryStream(bytes, writable: false));
        var headers = pe.PEHeaders;

        // COFF header: Machine(2) NumberOfSections(2) TimeDateStamp(4) ...
        Zero(bytes, headers.CoffHeaderStartOffset + 4, 4);

        if (headers.PEHeader is not null)
        {
            // Optional header: CheckSum sits at offset 64 in both PE32 and PE32+.
            Zero(bytes, headers.PEHeaderStartOffset + 64, 4);

            foreach (var entry in pe.ReadDebugDirectory())
                Zero(bytes, entry.DataPointer, entry.DataSize);

            var debugTable = headers.PEHeader.DebugTableDirectory;
            if (debugTable.Size > 0 && headers.TryGetDirectoryOffset(debugTable, out var tableOffset))
                Zero(bytes, tableOffset, debugTable.Size);
        }

        if (pe.HasMetadata)
        {
            var reader = pe.GetMetadataReader();
            var mvid = reader.GetGuid(reader.GetModuleDefinition().Mvid).ToByteArray();
            ZeroFirstOccurrence(bytes, mvid);
        }

        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static void Zero(byte[] bytes, int offset, int length)
    {
        if (offset >= 0 && length > 0 && offset + length <= bytes.Length)
            Array.Clear(bytes, offset, length);
    }

    private static void ZeroFirstOccurrence(byte[] bytes, byte[] needle)
    {
        var index = bytes.AsSpan().IndexOf(needle);
        if (index >= 0)
            Array.Clear(bytes, index, needle.Length);
    }
}
