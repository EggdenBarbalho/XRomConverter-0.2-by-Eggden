using System.Security.Cryptography;
using System.Text.Json;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Objects;
using MikuMikuLibrary.Objects.Extra.Blocks;

namespace XRomConverter.Analysis;

// Explicit diagnostic only. Never called by normal conversion.
internal static class HairRootIsolationTest
{
    private static readonly string[] Original = { "= 3 v 5.kl_kubi f neg", "= 4 v 3.cl_kao", "= 5 n 90 v 5.j_kao_wj g -" };
    private static readonly string[] Frozen = { "= 3 n 0", "= 4 n 0", "= 5 n 90" };
    private static byte[] Encode(ObjectSet set) { using var s = new MemoryStream(); set.Save(s, leaveOpen: true); return s.ToArray(); }
    public static int Run(string[] args)
    {
        bool allTwintails = args.Length == 6 && args[5] == "--all-twintails";
        bool allLongHair = args.Length == 6 && args[5] == "--all-long-hair";
        bool Selected(string name) => allLongHair ? name.StartsWith("n_hair_long_", StringComparison.Ordinal) && name.EndsWith("_ex", StringComparison.Ordinal) : name == "n_hair_twin_l_ex" || name == "n_hair_twin_r_ex";
        if ((!allTwintails && !allLongHair && args.Length != 5) || args[3] != "--axis" || Directory.Exists(args[2])) return 64;
        int axis = args[4].ToUpperInvariant() switch { "X" => 0, "Y" => 1, "Z" => 2, "Z-BRANCH" => 2, _ => -1 };
        if (axis < 0) return 64;
        var replacement = Original.ToArray();
        replacement[axis] = Frozen[axis];
        if (args[4].Equals("Z-BRANCH", StringComparison.OrdinalIgnoreCase))
            replacement[2] = "= 5 n 90 v 5.j_kao_wj g - v 5.cl_kao f cos n 0 g < n 180 n 0 h cond g +";
        string input = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(output);
        var changes = new List<object>();
        var skipped = new List<object>();
        IEnumerable<string> names = (allTwintails || allLongHair)
            ? Directory.EnumerateFiles(Path.Combine(input, "rom", "objset"), "*.farc").Select(p => Path.GetFileName(p)).Order()
            : new[] { "mikitm601_x.farc", "mikitm709_x.farc", "mikitm501.farc", "mikitm609.farc" };
        foreach (string name in names)
        {
            string path = Path.Combine(input, "rom", "objset", name);
            if (!File.Exists(path)) continue;
            using var archive = BinaryFile.Load<FarcArchive>(path);
            string? entry = archive.FileNames.SingleOrDefault(e => e.EndsWith("_obj.bin", StringComparison.OrdinalIgnoreCase) || e.EndsWith(".osd", StringComparison.OrdinalIgnoreCase));
            if (entry == null) continue;
            var set = archive.Open<ObjectSet>(entry);
            byte[] baseline = Encode(set);
            using var baselineStream = new MemoryStream(baseline);
            byte[] roundTripBaseline = Encode(BinaryFile.Load<ObjectSet>(baselineStream));
            var blocks = set.Objects.SelectMany(o => o.Skin?.Blocks.OfType<ExpressionBlock>() ?? Enumerable.Empty<ExpressionBlock>())
                .Where(b => Selected(b.Name) && b.Expressions.SequenceEqual(Original)).ToArray();
            foreach (var obj in set.Objects)
            foreach (var block in obj.Skin?.Blocks.OfType<ExpressionBlock>() ?? Enumerable.Empty<ExpressionBlock>())
                if (Selected(block.Name) && !block.Expressions.SequenceEqual(Original))
                    skipped.Add(new { Archive = path, Object = obj.Name, Block = block.Name,
                        Reason = block.Expressions.SequenceEqual(replacement) ? "Already patched" : "Different expressions", Expressions = block.Expressions.ToArray() });
            if (blocks.Length == 0) continue;
            foreach (var b in blocks) { b.Expressions.Clear(); b.Expressions.AddRange(replacement); }
            byte[] candidate = Encode(set);
            // Restore and compare the entire encoded object set: geometry, rig,
            // transforms, materials and unrelated expressions must be unchanged.
            foreach (var b in blocks) { b.Expressions.Clear(); b.Expressions.AddRange(Original); }
            if (!baseline.SequenceEqual(Encode(set))) throw new InvalidDataException("Unrelated object data changed.");
            using var packed = new FarcArchive();
            foreach (string e in archive.FileNames)
            {
                using var source = archive.Open(e, EntryStreamMode.MemoryStream);
                using var copy = new MemoryStream(); source.CopyTo(copy);
                packed.Add(e, new MemoryStream(e == entry ? candidate : copy.ToArray()), false);
            }
            string target = Path.Combine(output, name); packed.Save(target); packed.Dispose();
            using var check = BinaryFile.Load<FarcArchive>(target);
            if (!archive.FileNames.Order().SequenceEqual(check.FileNames.Order())) throw new InvalidDataException("Archive entries changed.");
            foreach (string e in archive.FileNames.Where(e => e != entry))
            {
                using var a = archive.Open(e, EntryStreamMode.MemoryStream); using var b = check.Open(e, EntryStreamMode.MemoryStream);
                if (!SHA256.HashData(a).SequenceEqual(SHA256.HashData(b))) throw new InvalidDataException("Non-object payload changed.");
            }
            var verified = check.Open<ObjectSet>(entry);
            int actual = verified.Objects.SelectMany(o => o.Skin?.Blocks.OfType<ExpressionBlock>() ?? Enumerable.Empty<ExpressionBlock>())
                .Count(b => Selected(b.Name) && b.Expressions.SequenceEqual(replacement));
            if (actual != blocks.Length) throw new InvalidDataException("Expression round-trip failed.");
            var verifiedBlocks = verified.Objects.SelectMany(o => o.Skin?.Blocks.OfType<ExpressionBlock>() ?? Enumerable.Empty<ExpressionBlock>())
                .Where(b => Selected(b.Name) && b.Expressions.SequenceEqual(replacement)).ToArray();
            foreach (var b in verifiedBlocks) { b.Expressions.Clear(); b.Expressions.AddRange(Original); }
            if (!roundTripBaseline.SequenceEqual(Encode(verified))) throw new InvalidDataException("Round-trip changed unrelated object data.");
            changes.Add(new { Source = path, Candidate = target, Blocks = actual, Axis = args[4].ToUpperInvariant(),
                Before = Original, After = replacement,
                SourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                CandidateSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))) });
        }
        if (changes.Count == 0) throw new InvalidDataException("No matching X hair roots found.");
        File.WriteAllText(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(changes, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(output, "skipped.json"), JsonSerializer.Serialize(skipped, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Prepared {changes.Count} diagnostic archives; no installed files changed.");
        return 0;
    }
}
