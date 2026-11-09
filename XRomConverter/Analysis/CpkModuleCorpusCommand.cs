using System.Security.Cryptography;
using System.Text.Json;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.CriMw;
using MikuMikuLibrary.IO;

namespace XRomConverter.Analysis;

internal sealed record CpkModuleEntry(string Path, string Category);
internal sealed record CpkModuleInventory(string Source, long SourceLength,
    DateTime SourceLastWriteUtc, IReadOnlyDictionary<string, int> CategoryCounts,
    IReadOnlyList<CpkModuleEntry> Entries);
internal sealed record ExtractedCpkEntry(string SourcePath, string RelativePath,
    string Category, long Length, string Sha256);
internal sealed record CpkExtractionManifest(string Source, string Profile,
    IReadOnlyList<ExtractedCpkEntry> Files);

internal static class CpkModuleCorpusCommand
{
    public static bool IsRequested(string[] args) => args.Length > 0 &&
        (args[0].Equals("inventory-cpk-module-data", StringComparison.OrdinalIgnoreCase) ||
         args[0].Equals("extract-cpk-module-data", StringComparison.OrdinalIgnoreCase) ||
         args[0].Equals("extract-cpk-objectsets", StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("extract-cpk-objectsets", StringComparison.OrdinalIgnoreCase))
            return ExtractObjectSets(args);
        if (args.Length > 0 && args[0].Equals("extract-cpk-module-data", StringComparison.OrdinalIgnoreCase))
            return Extract(args);
        if (args.Length != 4 || args[2] != "--output" || !File.Exists(args[1]))
        {
            Console.Error.WriteLine("Usage: XRomConverter inventory-cpk-module-data CPK --output JSON");
            return 64;
        }

        string source = Path.GetFullPath(args[1]);
        string output = Path.GetFullPath(args[3]);
        var file = new FileInfo(source);
        using var cpk = BinaryFile.Load<CpkArchive>(source);
        var entries = cpk.FileNames.Select(path => new CpkModuleEntry(path, Classify(path)))
            .Where(entry => entry.Category != "unrelated")
            .OrderBy(entry => entry.Category, StringComparer.Ordinal)
            .ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var counts = entries.GroupBy(entry => entry.Category)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var inventory = new CpkModuleInventory(source, file.Length, file.LastWriteTimeUtc, counts, entries);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(inventory, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
        Console.WriteLine(output);
        return 0;
    }

    private static int ExtractObjectSets(string[] args)
    {
        if (args.Length < 6 || !File.Exists(args[1]))
        {
            Console.Error.WriteLine("Usage: XRomConverter extract-cpk-objectsets CPK --stem NAME [--stem NAME]... --output DIR");
            return 64;
        }
        var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? outputValue = null;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--stem" && i + 1 < args.Length) stems.Add(args[++i]);
            else if (args[i] == "--output" && i + 1 < args.Length) outputValue = args[++i];
            else
            {
                Console.Error.WriteLine("Usage: XRomConverter extract-cpk-objectsets CPK --stem NAME [--stem NAME]... --output DIR");
                return 64;
            }
        }
        if (stems.Count == 0 || outputValue == null)
        {
            Console.Error.WriteLine("Usage: XRomConverter extract-cpk-objectsets CPK --stem NAME [--stem NAME]... --output DIR");
            return 64;
        }
        string source = Path.GetFullPath(args[1]);
        string output = Path.GetFullPath(outputValue);
        Directory.CreateDirectory(output);
        using var cpk = BinaryFile.Load<CpkArchive>(source);
        var selected = cpk.FileNames.Select(path => new CpkModuleEntry(path, Classify(path)))
            .Where(entry => entry.Category == "object-set" && stems.Contains(
                Path.GetFileNameWithoutExtension(entry.Path)))
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        var missing = stems.Where(stem => selected.All(entry =>
            !Path.GetFileNameWithoutExtension(entry.Path).Equals(stem, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (missing.Length > 0)
        {
            Console.Error.WriteLine("Object set(s) absent from CPK: " + string.Join(", ", missing));
            return 2;
        }
        var extracted = ExtractEntries(cpk, selected, output);
        string manifest = Path.Combine(output, "extraction-manifest.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(
            new CpkExtractionManifest(source, "explicit-object-set-selection", extracted),
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Console.WriteLine(manifest);
        return 0;
    }

    private static int Extract(string[] args)
    {
        if (args.Length != 4 || args[2] != "--output" || !File.Exists(args[1]))
        {
            Console.Error.WriteLine("Usage: XRomConverter extract-cpk-module-data CPK --output DIR");
            return 64;
        }
        string source = Path.GetFullPath(args[1]);
        string output = Path.GetFullPath(args[3]);
        Directory.CreateDirectory(output);
        using var cpk = BinaryFile.Load<CpkArchive>(source);
        string[] sampleStems = { "mikitm001", "mikitm501", "rinitm001", "rinitm500" };
        var selected = cpk.FileNames.Select(path => new CpkModuleEntry(path, Classify(path)))
            .Where(entry => entry.Category is "character-item-table" or "module-table" or
                    "object-texture-database" or "osage-configuration" or "skin-parameter" ||
                entry.Path.Equals("rom_steam/rom/2d/spr_db.bin", StringComparison.OrdinalIgnoreCase) ||
                entry.Category == "object-set" && sampleStems.Any(stem =>
                    Path.GetFileNameWithoutExtension(entry.Path).Equals(stem, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        var extracted = ExtractEntries(cpk, selected, output);
        string manifest = Path.Combine(output, "extraction-manifest.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(
            new CpkExtractionManifest(source, "module-configuration-plus-four-object-set-samples", extracted),
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Console.WriteLine(manifest);
        return 0;
    }

    private static List<ExtractedCpkEntry> ExtractEntries(CpkArchive cpk,
        IEnumerable<CpkModuleEntry> selected, string output)
    {
        var extracted = new List<ExtractedCpkEntry>();
        foreach (var entry in selected)
        {
            string relative = entry.Path.Replace('/', Path.DirectorySeparatorChar);
            string destination = Path.GetFullPath(Path.Combine(output, relative));
            if (!destination.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsafe CPK entry path: " + entry.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = cpk.Open(entry.Path, EntryStreamMode.OriginalStream);
            using var target = File.Create(destination);
            using var hasher = SHA256.Create();
            var buffer = new byte[1024 * 1024];
            long length = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                target.Write(buffer, 0, read);
                hasher.TransformBlock(buffer, 0, read, buffer, 0);
                length += read;
            }
            hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            extracted.Add(new(entry.Path, relative.Replace('\\', '/'), entry.Category,
                length, Convert.ToHexString(hasher.Hash!)));
        }
        return extracted;
    }

    private static string Classify(string input)
    {
        string path = input.Replace('\\', '/').ToLowerInvariant();
        string file = Path.GetFileName(path);
        if (file.Contains("chritm_prop") && file.EndsWith(".farc")) return "character-item-table";
        if (file.Contains("gm_module_tbl") && file.EndsWith(".farc")) return "module-table";
        if (path.Contains("/skin_param/")) return "skin-parameter";
        if (path.Contains("osage_play_data") || path.Contains("osage_setting")) return "osage-configuration";
        if (path.Contains("/objset/") && (file.EndsWith("obj_db.bin") || file.EndsWith("tex_db.bin")))
            return "object-texture-database";
        if (path.Contains("/objset/") && file.EndsWith(".farc")) return "object-set";
        if (path.Contains("/2d/") && file == "spr_db.bin") return "sprite-database";
        return "unrelated";
    }
}
