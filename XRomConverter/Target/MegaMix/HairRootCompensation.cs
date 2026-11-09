using System.Security.Cryptography;
using System.Text.Json;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Objects;
using MikuMikuLibrary.Objects.Extra.Blocks;

namespace XRomConverter.Target.MegaMix;

internal static class HairRootCompensation
{
    private static readonly string[] Roots = { "n_hair_twin_l_ex", "n_hair_twin_r_ex", "n_hair_long_c_ex", "n_hair_long_l_01_ex", "n_hair_long_r_01_ex" };
    private static readonly string[] Original = { "= 3 v 5.kl_kubi f neg", "= 4 v 3.cl_kao", "= 5 n 90 v 5.j_kao_wj g -" };
    private static readonly string[] Corrected = { Original[0], Original[1], "= 5 n 90 v 5.j_kao_wj g - v 5.cl_kao f cos n 0 g < n 180 n 0 h cond g +" };
    private static bool Selected(ExpressionBlock b) => Roots.Contains(b.Name, StringComparer.Ordinal);
    private static byte[] Encode(ObjectSet set) { using var s = new MemoryStream(); set.Save(s, leaveOpen: true); return s.ToArray(); }

    internal static void Write(string root)
    {
        var changes = new List<object>();
        foreach (string path in Directory.EnumerateFiles(Path.Combine(root, "rom", "objset"), "*.farc").Order(StringComparer.Ordinal))
        {
            string file = Path.GetFileName(path);
            using var archive = BinaryFile.Load<FarcArchive>(path);
            string? entry = archive.FileNames.SingleOrDefault(e => e.EndsWith("_obj.bin", StringComparison.OrdinalIgnoreCase));
            if (entry == null) continue;
            var set = archive.Open<ObjectSet>(entry);
            var blocks = set.Objects.SelectMany(o => o.Skin?.Blocks.OfType<ExpressionBlock>() ?? Enumerable.Empty<ExpressionBlock>())
                .Where(b => Selected(b) && b.Expressions.SequenceEqual(Original)).ToArray();
            if (blocks.Length == 0) continue; // Already patched, absent roots, or a different rig.
            byte[] baseline = Encode(set);
            using var baselineStream = new MemoryStream(baseline);
            byte[] roundTripBaseline = Encode(BinaryFile.Load<ObjectSet>(baselineStream));
            foreach (var block in blocks) { block.Expressions.Clear(); block.Expressions.AddRange(Corrected); }
            byte[] candidate = Encode(set);
            foreach (var block in blocks) { block.Expressions.Clear(); block.Expressions.AddRange(Original); }
            if (!baseline.SequenceEqual(Encode(set))) throw new InvalidDataException("Hair patch changed unrelated object data.");
            string temp = path + ".hair-patch-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var packed = new FarcArchive())
                {
                    foreach (string name in archive.FileNames)
                    {
                        using var input = archive.Open(name, EntryStreamMode.MemoryStream);
                        using var buffer = new MemoryStream(); input.CopyTo(buffer);
                        packed.Add(name, new MemoryStream(name == entry ? candidate : buffer.ToArray()), false);
                    }
                    packed.Save(temp);
                }
                using (var check = BinaryFile.Load<FarcArchive>(temp))
                {
                    if (!archive.FileNames.Order().SequenceEqual(check.FileNames.Order())) throw new InvalidDataException("Hair archive entries changed.");
                    foreach (string name in archive.FileNames.Where(n => n != entry))
                    {
                        using var a = archive.Open(name, EntryStreamMode.MemoryStream); using var b = check.Open(name, EntryStreamMode.MemoryStream);
                        if (!SHA256.HashData(a).SequenceEqual(SHA256.HashData(b))) throw new InvalidDataException("Hair non-object payload changed.");
                    }
                    var verified = check.Open<ObjectSet>(entry);
                    var patched = verified.Objects.SelectMany(o => o.Skin?.Blocks.OfType<ExpressionBlock>() ?? Enumerable.Empty<ExpressionBlock>())
                        .Where(b => Selected(b) && b.Expressions.SequenceEqual(Corrected)).ToArray();
                    int prior = set.Objects.SelectMany(o => o.Skin?.Blocks.OfType<ExpressionBlock>() ?? Enumerable.Empty<ExpressionBlock>())
                        .Count(b => Selected(b) && b.Expressions.SequenceEqual(Corrected));
                    if (patched.Length != blocks.Length + prior) throw new InvalidDataException("Hair patch round-trip failed.");
                    // Reverse only roots modified in this pass, preserving any already-corrected roots.
                    var originalRoots = set.Objects.SelectMany((o, oi) => (o.Skin?.Blocks ?? new()).OfType<ExpressionBlock>()
                        .Where(b => Selected(b) && b.Expressions.SequenceEqual(Original)).Select(b => (oi, b.Name))).ToHashSet();
                    foreach (var pair in verified.Objects.Select((o, oi) => (o, oi)))
                    foreach (var block in pair.o.Skin?.Blocks.OfType<ExpressionBlock>() ?? Enumerable.Empty<ExpressionBlock>())
                        if (originalRoots.Contains((pair.oi, block.Name))) { block.Expressions.Clear(); block.Expressions.AddRange(Original); }
                    if (!roundTripBaseline.SequenceEqual(Encode(verified))) throw new InvalidDataException("Hair round-trip changed unrelated data.");
                }
                archive.Dispose(); File.Move(temp, path, overwrite: true);
                changes.Add(new { File = file, Roots = blocks.Length, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) });
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        File.WriteAllText(ConversionReports.PathFor(root, "HAIR-ROOT-COMPENSATION.json"), JsonSerializer.Serialize(
            new { Scope = "All output object archives; exact original expression and supported hair root required", SupportedRoots = Roots, Changes = changes, PhysicsCoefficientsChanged = false, RuntimeVerifiedThisBuild = false }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"[HAIR_ROOT_COMPENSATION] Updated {changes.Count} compatible hair archives.");
    }
}
