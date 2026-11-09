using System.Security.Cryptography;
using System.Text.Json;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.Databases;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Objects;
using MikuMikuLibrary.Objects.Extra.Parameters;
using MikuMikuLibrary.Parameters;
using MikuMikuLibrary.Textures;

namespace XRomConverter.Analysis;

internal sealed record ReferenceObject(string Name, uint Id, int Meshes, int Materials,
    int Bones, IReadOnlyList<string> Blocks);
internal sealed record ReferenceObjectSet(string Name, string Path, string Sha256,
    int TextureCount, IReadOnlyList<uint> TextureIds, IReadOnlyList<ReferenceObject> Objects);
internal sealed record ReferenceSkinParameter(string File, string Sha256,
    IReadOnlyList<string> Names, IReadOnlyDictionary<string, int> NodeCounts,
    string? ParseError, IReadOnlyDictionary<string, string>? RawValues);
internal sealed record ReferenceSkinValueDiff(string File, string Key,
    string? CompleteEdition, string? WorkingTest, string Kind);
internal sealed record ReferenceTableEntry(string Scope, IReadOnlyDictionary<string, string> Fields);
internal sealed record P4ReferenceSide(string Name, IReadOnlyList<ReferenceObjectSet> ObjectSets,
    IReadOnlyList<ReferenceSkinParameter> SkinParameters,
    ReferenceTableEntry? Module, ReferenceTableEntry? Costume,
    IReadOnlyList<ReferenceTableEntry> Items);
internal sealed record P4ReferenceComparison(P4ReferenceSide DivaX,
    P4ReferenceSide CompleteEdition, P4ReferenceSide WorkingTest,
    IReadOnlyList<string> Findings, IReadOnlyList<ReferenceSkinValueDiff> SkinValueDiffs);

internal static class P4ReferenceComparisonCommand
{
    private static readonly string[] sSets = { "mikitm153", "mikitm553", "mikitm753" };

    public static bool IsRequested(string[] args) => args.Length > 0 &&
        args[0].Equals("compare-p4-references", StringComparison.OrdinalIgnoreCase);

    public static int Run(string[] args)
    {
        string? x = null, ce = null, test = null, output = null;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--x" && i + 1 < args.Length) x = args[++i];
            else if (args[i] == "--complete" && i + 1 < args.Length) ce = args[++i];
            else if (args[i] == "--test" && i + 1 < args.Length) test = args[++i];
            else if (args[i] == "--output" && i + 1 < args.Length) output = args[++i];
            else return 64;
        }
        if (x == null || ce == null || test == null || output == null ||
            !Directory.Exists(x) || !Directory.Exists(ce) || !Directory.Exists(test)) return 64;

        var xSide = ReadSide("diva-x-original", x, source: true);
        var ceSide = ReadSide("complete-edition", ce, source: false);
        var testSide = ReadSide("working-test", test, source: false);
        var findings = Compare(xSide, ceSide, testSide);
        var report = new P4ReferenceComparison(xSide, ceSide, testSide, findings,
            DiffSkinValues(ceSide, testSide));
        string fullPath = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
        Console.WriteLine(fullPath);
        return 0;
    }

    private static P4ReferenceSide ReadSide(string name, string root, bool source)
    {
        var sets = new List<ReferenceObjectSet>();
        foreach (string stem in sSets)
        {
            string path = Path.Combine(root, source ? stem + ".farc" : "objset/" + stem + "_x.farc");
            using var farc = BinaryFile.Load<FarcArchive>(path);
            string objectEntry = source ? stem + ".osd" : stem + "_x_obj.bin";
            string textureEntry = source ? stem + ".txd" : stem + "_x_tex.bin";
            var objectSet = farc.Open<ObjectSet>(objectEntry);
            var textureSet = farc.Open<TextureSet>(textureEntry);
            sets.Add(new ReferenceObjectSet(stem, path, Hash(path), textureSet.Textures.Count,
                objectSet.TextureIds.ToArray(), objectSet.Objects.Select(o => new ReferenceObject(
                    o.Name, o.Id, o.Meshes.Count, o.Materials.Count, o.Skin?.Bones.Count ?? 0,
                    o.Skin?.Blocks.Select(b => b.Signature).ToArray() ?? Array.Empty<string>())).ToArray()));
        }
        string skinRoot = source ? root : Path.Combine(root, "skin_param");
        string pattern = source ? "ext_skp_*.osp" : "ext_skp_*.txt";
        var skins = Directory.EnumerateFiles(skinRoot, pattern).Where(p =>
                Path.GetFileName(p).Contains("mikitm153", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(p).Contains("mikitm753", StringComparison.OrdinalIgnoreCase))
            .Select(ReadSkin).OrderBy(s => s.File).ToArray();
        ReferenceTableEntry? module = null, costume = null;
        IReadOnlyList<ReferenceTableEntry> items = Array.Empty<ReferenceTableEntry>();
        if (!source)
        {
            module = ReadP4Module(Path.Combine(root, "gm_module_tbl.farc"));
            int costumeId = -1;
            if (module?.Fields.TryGetValue("cos", out string? cosName) == true &&
                cosName.StartsWith("COS_", StringComparison.OrdinalIgnoreCase))
                int.TryParse(cosName[4..], out costumeId);
            costumeId--;
            (costume, items) = ReadP4Items(Path.Combine(root, "mod_chritm_prop.farc"), costumeId);
        }
        return new P4ReferenceSide(name, sets, skins, module, costume, items);
    }

    private static ReferenceTableEntry? ReadP4Module(string path)
    {
        using var farc = BinaryFile.Load<FarcArchive>(path);
        using var stream = farc.Open("gm_module_id.bin", EntryStreamMode.MemoryStream);
        var tree = ParameterTree.Load(stream);
        if (!tree.OpenScope("module")) return null;
        ReferenceTableEntry? result = null;
        foreach (string key in tree.Keys.ToArray())
        {
            if (!tree.OpenScope(key)) continue;
            var fields = ReadFields(tree);
            if (fields.TryGetValue("name", out string? value) &&
                value.Contains("Dancing All Night", StringComparison.OrdinalIgnoreCase))
                result = new("module." + key, fields);
            tree.CloseScope();
            if (result != null) break;
        }
        tree.CloseScope();
        return result;
    }

    private static (ReferenceTableEntry? Costume, IReadOnlyList<ReferenceTableEntry> Items)
        ReadP4Items(string path, int costumeId)
    {
        using var farc = BinaryFile.Load<FarcArchive>(path);
        using var stream = farc.Open("mikitm_tbl.txt", EntryStreamMode.MemoryStream);
        var tree = ParameterTree.Load(stream);
        ReferenceTableEntry? costume = null;
        if (tree.OpenScope("cos"))
        {
            foreach (string key in tree.Keys.ToArray())
            {
                if (!tree.OpenScope(key)) continue;
                var fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var field in ReadFields(tree)) fields[field.Key] = field.Value;
                if (fields.TryGetValue("id", out string? id) && id == costumeId.ToString())
                {
                    if (tree.OpenScope("item"))
                    {
                        int length = tree.Get("length", 0);
                        fields["items"] = string.Join(",", Enumerable.Range(0, length)
                            .Select(i => tree.Get(i.ToString(), -1)));
                        tree.CloseScope();
                    }
                    costume = new("cos." + key, fields);
                }
                tree.CloseScope();
                if (costume != null) break;
            }
            tree.CloseScope();
        }
        var selectedNumbers = costume?.Fields.TryGetValue("items", out string? itemList) == true
            ? itemList.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var items = new List<ReferenceTableEntry>();
        if (tree.OpenScope("item"))
        {
            foreach (string key in tree.Keys.ToArray())
            {
                if (!tree.OpenScope(key)) continue;
                var fields = ReadFields(tree);
                if (fields.TryGetValue("no", out string? no) && selectedNumbers.Contains(no))
                    items.Add(new("item." + key, fields));
                tree.CloseScope();
            }
            tree.CloseScope();
        }
        return (costume, items);
    }

    private static IReadOnlyDictionary<string, string> ReadFields(ParameterTree tree)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string key in tree.Keys.ToArray())
        {
            try { result[key] = tree.Get(key, ""); }
            catch { }
        }
        return result;
    }

    private static ReferenceSkinParameter ReadSkin(string path)
    {
        try
        {
            var set = new OsageSkinParameterSet();
            set.Load(path);
            return new(Path.GetFileName(path), Hash(path), set.Parameters.Select(p => p.Name).ToArray(),
                set.Parameters.ToDictionary(p => p.Name, p => p.Nodes.Count, StringComparer.Ordinal), null,
                Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase) ? ReadRaw(path) : null);
        }
        catch (Exception exception) when (Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            // Preserve and inspect malformed target text as-is. Never rewrite a reference file merely
            // to make the legacy typed parser accept it.
            var nodeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string line in File.ReadLines(path))
            {
                int separator = line.IndexOf('=');
                if (separator <= 0) continue;
                string key = line[..separator];
                const string suffix = ".node.length";
                if (!key.EndsWith(suffix, StringComparison.Ordinal)) continue;
                string name = key[..^suffix.Length];
                if (int.TryParse(line[(separator + 1)..], out int count)) nodeCounts[name] = count;
            }
            return new(Path.GetFileName(path), Hash(path), nodeCounts.Keys.ToArray(), nodeCounts,
                exception.GetType().Name + ": " + exception.Message, ReadRaw(path));
        }
    }

    private static IReadOnlyDictionary<string, string> ReadRaw(string path)
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(path))
        {
            int separator = line.IndexOf('=');
            if (separator > 0) values[line[..separator]] = line[(separator + 1)..];
        }
        return values;
    }

    private static IReadOnlyList<ReferenceSkinValueDiff> DiffSkinValues(P4ReferenceSide complete,
        P4ReferenceSide test)
    {
        var result = new List<ReferenceSkinValueDiff>();
        foreach (var ce in complete.SkinParameters.Where(skin => skin.RawValues != null))
        {
            var other = test.SkinParameters.SingleOrDefault(skin =>
                skin.File.Equals(ce.File, StringComparison.OrdinalIgnoreCase));
            if (other?.RawValues == null) continue;
            foreach (string key in ce.RawValues.Keys.Union(other.RawValues.Keys).Order(StringComparer.Ordinal))
            {
                ce.RawValues.TryGetValue(key, out string? left);
                other.RawValues.TryGetValue(key, out string? right);
                if (left == right) continue;
                string kind = left == null ? "working-test-only" : right == null ? "complete-edition-only" : "value-diff";
                result.Add(new(ce.File, key, left, right, kind));
            }
        }
        return result;
    }

    private static IReadOnlyList<string> Compare(P4ReferenceSide x, P4ReferenceSide ce,
        P4ReferenceSide test)
    {
        var findings = new List<string>();
        foreach (string stem in sSets)
        {
            var xs = x.ObjectSets.Single(s => s.Name == stem);
            var cs = ce.ObjectSets.Single(s => s.Name == stem);
            var ts = test.ObjectSets.Single(s => s.Name == stem);
            if (xs.Objects.Count != cs.Objects.Count || xs.Objects.Count != ts.Objects.Count)
                findings.Add($"{stem}: object count differs X={xs.Objects.Count}, CE={cs.Objects.Count}, test={ts.Objects.Count}.");
            if (cs.TextureCount != ts.TextureCount)
                findings.Add($"{stem}: target texture count differs CE={cs.TextureCount}, test={ts.TextureCount}.");
            if (cs.Objects.Select(o => (o.Meshes, o.Materials, o.Bones)).SequenceEqual(
                    ts.Objects.Select(o => (o.Meshes, o.Materials, o.Bones))))
                findings.Add($"{stem}: CE and working test have equal mesh/material/bone topology.");
        }
        foreach (var sourceSkin in x.SkinParameters)
        {
            string targetName = Path.GetFileNameWithoutExtension(sourceSkin.File) + "_x.txt";
            var ceSkin = ce.SkinParameters.SingleOrDefault(s => s.File.Equals(targetName, StringComparison.OrdinalIgnoreCase));
            var testSkin = test.SkinParameters.SingleOrDefault(s => s.File.Equals(targetName, StringComparison.OrdinalIgnoreCase));
            if (ceSkin == null || testSkin == null)
                findings.Add(sourceSkin.File + ": corresponding target skin parameter is absent.");
            else
            {
                findings.Add($"{sourceSkin.File}: X={sourceSkin.Names.Count} parameters, CE={ceSkin.Names.Count}, test={testSkin.Names.Count}.");
                if (ceSkin.ParseError != null)
                    findings.Add(targetName + ": CE typed parse failed; raw text inventory retained. " + ceSkin.ParseError);
                if (testSkin.ParseError != null)
                    findings.Add(targetName + ": working-test typed parse failed; raw text inventory retained. " + testSkin.ParseError);
                if (ceSkin.Sha256 != testSkin.Sha256)
                    findings.Add(targetName + ": CE and working-test contents differ.");
            }
        }
        if (ce.Module != null && test.Module != null)
            findings.Add($"module table: CE id={ce.Module.Fields.GetValueOrDefault("id")}, test id={test.Module.Fields.GetValueOrDefault("id")}; both bind COS_153.");
        findings.Add($"character-item table: CE has {ce.Items.Count} selected P4 items; test has {test.Items.Count}.");
        return findings;
    }

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
