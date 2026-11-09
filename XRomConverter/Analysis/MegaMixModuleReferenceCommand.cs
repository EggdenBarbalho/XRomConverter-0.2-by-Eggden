using System.Text.Json;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Parameters;

namespace XRomConverter.Analysis;

internal sealed record MegaMixModuleOccurrence(string Layer, string File,
    string Scope, IReadOnlyDictionary<string, string> Fields);
internal sealed record MegaMixItemOccurrence(string Scope,
    IReadOnlyDictionary<string, string> Fields);
internal sealed record MegaMixCostumeOccurrence(string Layer, string File,
    string Entry, string Scope, int CostumeId, IReadOnlyList<int> ItemNumbers,
    IReadOnlyList<MegaMixItemOccurrence> Items);
internal sealed record MegaMixObjectSetCandidate(string Stem,
    IReadOnlyList<string> CpkPaths);
internal sealed record MegaMixModuleReferenceReport(int RequestedModuleId,
    IReadOnlyList<MegaMixModuleOccurrence> Modules,
    IReadOnlyList<MegaMixCostumeOccurrence> Costumes,
    IReadOnlyList<MegaMixObjectSetCandidate> ObjectSets,
    IReadOnlyList<string> Diagnostics);

internal static class MegaMixModuleReferenceCommand
{
    public static bool IsRequested(string[] args) => args.Length > 0 &&
        args[0].Equals("inspect-megamix-module-reference", StringComparison.OrdinalIgnoreCase);

    public static int Run(string[] args)
    {
        if (args.Length < 4 || !Directory.Exists(args[1]) ||
            !int.TryParse(args[2], out int moduleId))
        {
            PrintUsage();
            return 64;
        }

        string corpusRoot = Path.GetFullPath(args[1]);
        string? inventoryPath = null;
        string? outputPath = null;
        for (int i = 3; i < args.Length; i++)
        {
            if (args[i] == "--inventory" && i + 1 < args.Length) inventoryPath = args[++i];
            else if (args[i] == "--output" && i + 1 < args.Length) outputPath = args[++i];
            else
            {
                PrintUsage();
                return 64;
            }
        }
        if (outputPath == null || inventoryPath != null && !File.Exists(inventoryPath))
        {
            PrintUsage();
            return 64;
        }

        var diagnostics = new List<string>();
        var modules = new List<MegaMixModuleOccurrence>();
        foreach (string path in Directory.EnumerateFiles(corpusRoot, "*gm_module_tbl*.farc",
                     SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            try { modules.AddRange(ReadModules(corpusRoot, path, moduleId)); }
            catch (Exception ex) { diagnostics.Add($"module-table-parse: {Relative(corpusRoot, path)}: {ex.GetType().Name}: {ex.Message}"); }
        }

        var costumes = new List<MegaMixCostumeOccurrence>();
        foreach (var module in modules.GroupBy(module => (
                     Character: module.Fields.GetValueOrDefault("chara"),
                     Costume: module.Fields.GetValueOrDefault("cos"))).Select(group => group.First()))
        {
            if (!module.Fields.TryGetValue("cos", out string? cos) ||
                !TryCostumeId(cos, out int costumeId))
            {
                diagnostics.Add($"{module.Scope}: invalid or absent cos field.");
                continue;
            }
            string entryPrefix = GetItemPrefix(module.Fields.GetValueOrDefault("chara"));
            if (entryPrefix.Length == 0)
            {
                diagnostics.Add($"{module.Scope}: unsupported character {module.Fields.GetValueOrDefault("chara")}.");
                continue;
            }
            foreach (string path in Directory.EnumerateFiles(corpusRoot, "*chritm_prop*.farc",
                         SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var costume = ReadCostume(corpusRoot, path, entryPrefix, costumeId);
                    if (costume != null) costumes.Add(costume);
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"character-item-parse: {Relative(corpusRoot, path)}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        string prefix = modules.Select(module => GetItemPrefix(module.Fields.GetValueOrDefault("chara")))
            .FirstOrDefault(value => value.Length > 0) ?? "";
        string[] cpkObjectSetPaths = ReadObjectSetPaths(inventoryPath, diagnostics);
        var objectSets = costumes.SelectMany(costume => costume.ItemNumbers).Distinct().Order()
            .Select(number => prefix + number.ToString("000"))
            .Select(stem => new MegaMixObjectSetCandidate(stem, cpkObjectSetPaths.Where(path =>
                    Path.GetFileNameWithoutExtension(path).Equals(stem, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase).ToArray()))
            .ToArray();

        if (modules.Count == 0) diagnostics.Add($"Module ID {moduleId} was not found in the extracted module tables.");
        if (modules.Count > 0 && costumes.Count == 0) diagnostics.Add("No matching costume was found in the extracted character-item tables.");
        foreach (var candidate in objectSets.Where(candidate => candidate.CpkPaths.Count == 0))
            diagnostics.Add($"No CPK object-set entry was found for {candidate.Stem}.");

        var report = new MegaMixModuleReferenceReport(moduleId, modules, costumes, objectSets, diagnostics);
        string fullOutput = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        File.WriteAllText(fullOutput, JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
        Console.WriteLine(fullOutput);
        return modules.Count > 0 && costumes.Count > 0 ? 0 : 2;
    }

    private static IEnumerable<MegaMixModuleOccurrence> ReadModules(string root, string path, int moduleId)
    {
        using var farc = BinaryFile.Load<FarcArchive>(path);
        string? entry = farc.FileNames.FirstOrDefault(name =>
            name.Equals("gm_module_id.bin", StringComparison.OrdinalIgnoreCase));
        if (entry == null) yield break;
        using var stream = farc.Open(entry, EntryStreamMode.MemoryStream);
        var tree = ParameterTree.Load(stream);
        if (!tree.OpenScope("module")) yield break;
        foreach (string key in tree.Keys.ToArray())
        {
            if (!tree.OpenScope(key)) continue;
            var fields = ReadFields(tree);
            tree.CloseScope();
            if (fields.TryGetValue("id", out string? value) && int.TryParse(value, out int id) && id == moduleId)
                yield return new MegaMixModuleOccurrence(Layer(root, path), Relative(root, path), "module." + key, fields);
        }
        tree.CloseScope();
    }

    private static MegaMixCostumeOccurrence? ReadCostume(string root, string path,
        string entryPrefix, int costumeId)
    {
        using var farc = BinaryFile.Load<FarcArchive>(path);
        string? entry = farc.FileNames.FirstOrDefault(name =>
            name.Equals(entryPrefix + "_tbl.txt", StringComparison.OrdinalIgnoreCase));
        if (entry == null) return null;
        using var stream = farc.Open(entry, EntryStreamMode.MemoryStream);
        var tree = ParameterTree.Load(stream);
        var itemNumbers = new List<int>();
        string? costumeScope = null;
        if (tree.OpenScope("cos"))
        {
            foreach (string key in tree.Keys.ToArray())
            {
                if (!tree.OpenScope(key)) continue;
                int id = tree.Get("id", -1);
                if (id == costumeId && tree.OpenScope("item"))
                {
                    int length = tree.Get("length", 0);
                    for (int i = 0; i < length; i++) itemNumbers.Add(tree.Get(i.ToString(), -1));
                    tree.CloseScope();
                    costumeScope = "cos." + key;
                }
                tree.CloseScope();
                if (costumeScope != null) break;
            }
            tree.CloseScope();
        }
        if (costumeScope == null) return null;

        var selected = itemNumbers.ToHashSet();
        var items = new List<MegaMixItemOccurrence>();
        if (tree.OpenScope("item"))
        {
            foreach (string key in tree.Keys.ToArray())
            {
                if (!tree.OpenScope(key)) continue;
                var fields = ReadFields(tree);
                tree.CloseScope();
                if (fields.TryGetValue("no", out string? noValue) && int.TryParse(noValue, out int no) && selected.Contains(no))
                    items.Add(new MegaMixItemOccurrence("item." + key, fields));
            }
            tree.CloseScope();
        }
        return new MegaMixCostumeOccurrence(Layer(root, path), Relative(root, path), entry,
            costumeScope, costumeId, itemNumbers, items);
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

    private static string[] ReadObjectSetPaths(string? inventoryPath, List<string> diagnostics)
    {
        if (inventoryPath == null) return Array.Empty<string>();
        try
        {
            var inventory = JsonSerializer.Deserialize<CpkModuleInventory>(File.ReadAllText(inventoryPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return inventory?.Entries.Where(entry => entry.Category == "object-set")
                .Select(entry => entry.Path).ToArray() ?? Array.Empty<string>();
        }
        catch (Exception ex)
        {
            diagnostics.Add($"inventory-parse: {ex.GetType().Name}: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    private static bool TryCostumeId(string value, out int id)
    {
        id = -1;
        return value.StartsWith("COS_", StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(value[4..], out int physical) && (id = physical - 1) >= 0;
    }

    private static string GetItemPrefix(string? character) => character?.ToUpperInvariant() switch
    {
        "MIKU" or "MIK" => "mikitm",
        "RIN" => "rinitm",
        "LEN" => "lenitm",
        "LUKA" or "LUK" => "lukitm",
        "KAITO" or "KAI" => "kaiitm",
        "MEIKO" or "MEI" => "meiitm",
        "HAKU" or "HAK" => "hakitm",
        "NERU" or "NER" => "neritm",
        "SAKINE" or "SAK" => "sakitm",
        "TETO" or "TET" => "tetitm",
        _ => ""
    };

    private static string Layer(string root, string path) =>
        Relative(root, path).Split('/')[0];
    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static void PrintUsage() => Console.Error.WriteLine(
        "Usage: XRomConverter inspect-megamix-module-reference CORPUS_ROOT MODULE_ID " +
        "[--inventory CPK_INVENTORY_JSON] --output JSON");
}
