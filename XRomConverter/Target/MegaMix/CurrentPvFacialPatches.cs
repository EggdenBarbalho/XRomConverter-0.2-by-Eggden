using System.Security.Cryptography;
using System.Text.Json;

namespace XRomConverter.Target.MegaMix;

// Reproduce the audited x 3 PV patches, including the final PV236 calibration.
// Generic facial banks and other characters are deliberately outside this catalog.
internal static class CurrentPvFacialPatches
{
    private sealed record Entry(string File, string Sha256, long Bytes);
    private static byte[] Resource(string name)
    {
        using var input = typeof(CurrentPvFacialPatches).Assembly.GetManifestResourceStream(
            "XRomConverter.PvFacialPatches." + name)
            ?? throw new InvalidDataException("Missing PV facial patch resource: " + name);
        using var buffer = new MemoryStream(); input.CopyTo(buffer); return buffer.ToArray();
    }

    internal static void Write(string output)
    {
        var entries = JsonSerializer.Deserialize<Entry[]>(Resource("manifest.json"))
            ?? throw new InvalidDataException("Invalid PV facial patch manifest.");
        if (entries.Length != 30 || entries.Select(e => e.File).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Length)
            throw new InvalidDataException("Unexpected PV facial patch catalog.");
        var pending = new List<(Entry Entry, byte[] Data)>();
        foreach (var entry in entries)
        {
            if (Path.GetFileName(entry.File) != entry.File || !entry.File.EndsWith("_FACE_MIK.farc", StringComparison.Ordinal))
                throw new InvalidDataException("Invalid PV facial patch filename.");
            byte[] data = Resource(entry.File);
            if (data.LongLength != entry.Bytes || !Convert.ToHexString(SHA256.HashData(data)).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("PV facial patch integrity failure: " + entry.File);
            pending.Add((entry, data));
        }
        string folder = Path.Combine(output, "rom", "rob"); Directory.CreateDirectory(folder);
        foreach (var patch in pending) File.WriteAllBytes(Path.Combine(folder, patch.Entry.File), patch.Data);
        File.WriteAllText(ConversionReports.PathFor(output, "PV-FACIAL-PATCHES.json"),
            JsonSerializer.Serialize(new { Source = "Audited x 3 snapshot 2026-10-26", Archives = entries,
                GenericFacialBanksChanged = false, AllPvsVisuallyVerified = false }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"[PV_FACIAL_PATCHES] Installed {entries.Length} audited Miku PV archives.");
    }
}
