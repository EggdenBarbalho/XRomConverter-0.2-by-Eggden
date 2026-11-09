using System.Text.Json;
using System.Text.RegularExpressions;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.IO;
using XRomConverter.Ids;

namespace XRomConverter.Target.MegaMix;

// Import already converted facial motions without reserializing their curves.
// /rob names address songs/characters globally, not a module ID or costume.
internal static class RobReferenceImportCommand
{
    internal static int Run(string[] args)
    {
        string? temporary = null;
        try
        {
            if (args.Length != 5)
                throw new ArgumentException("Usage: import-rob-reference BASE_PACKAGE SOURCE_MOD MIK|ALL NEW_OUTPUT");
            string baseline = Path.GetFullPath(args[1]), source = Path.GetFullPath(args[2]);
            string character = args[3].ToUpperInvariant(), output = Path.GetFullPath(args[4]);
            string[] supported = { "MIK", "RIN", "LEN", "LUK", "KAI", "MEI", "HAK", "NER", "SAK", "TET", "ALL" };
            if (!supported.Contains(character)) throw new ArgumentException("Unknown character selector.");
            if (Directory.Exists(output) || File.Exists(output)) throw new InvalidDataException("Output must not already exist.");
            if (Inside(output, baseline) || Inside(output, source))
                throw new InvalidDataException("Output must be outside its source directories.");
            string rob = Path.GetFullPath(Path.Combine(source, "rom/rob"));
            string[] all = Directory.GetFiles(rob, "*", SearchOption.AllDirectories).Order().ToArray();
            if (all.Length == 0) throw new InvalidDataException("Source /rob is empty.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in all)
                if (!string.Equals(Path.GetDirectoryName(path), rob, StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(Path.GetFileName(path),
                    @"^mot_(?:PV\d+_FACE_[A-Z]{3}|[A-Z]{3}|common)\.farc$", RegexOptions.IgnoreCase) ||
                    !names.Add(Path.GetFileName(path)))
                    throw new InvalidDataException("Unsupported or ambiguous /rob entry: " + path);
            string[] selected = character == "ALL" ? all : all.Where(p => Regex.IsMatch(Path.GetFileName(p),
                @"^mot_(?:PV\d+_FACE_)?" + character + @"\.farc$", RegexOptions.IgnoreCase)).ToArray();
            if (selected.Length == 0) throw new InvalidDataException("No facial archives match the character.");
            string baseManifestPath = Path.Combine(baseline, "build-manifest.json");
            using var baseManifest = JsonDocument.Parse(File.ReadAllText(baseManifestPath));
            int moduleId = baseManifest.RootElement.GetProperty("moduleId").GetInt32();
            var declared = baseManifest.RootElement.GetProperty("emittedFiles").EnumerateArray()
                .ToDictionary(x => x.GetProperty("path").GetString()!, x => x.GetProperty("sha256").GetString()!);
            string[] baseFiles = Directory.GetFiles(baseline, "*", SearchOption.AllDirectories).Order().ToArray();
            if (baseFiles.Length != declared.Count + 1 || baseFiles.Any(p =>
                Path.GetRelativePath(baseline, p).Replace('\\', '/') != "build-manifest.json" &&
                (!declared.TryGetValue(Path.GetRelativePath(baseline, p).Replace('\\', '/'), out var hash) || hash != Hash(p))))
                throw new InvalidDataException("Base package differs from its manifest.");
            foreach (string path in selected)
                if (File.Exists(Path.Combine(baseline, "rom/rob", Path.GetFileName(path))))
                    throw new InvalidDataException("Base already contains a selected /rob archive; replacement requires a separate explicit build.");
            var baseHashes = baseFiles.ToDictionary(p => Path.GetRelativePath(baseline, p).Replace('\\', '/'), Hash);
            var provenance = new List<object>();
            var sourceHashes = new Dictionary<string, string>();
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            temporary = Path.Combine(Path.GetDirectoryName(output)!, ".rob-import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            foreach (string path in baseFiles)
            {
                string target = Path.Combine(temporary, Path.GetRelativePath(baseline, path));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(path, target);
            }
            Directory.CreateDirectory(Path.Combine(temporary, "rom/rob"));
            foreach (string path in selected)
            {
                string hash = Hash(path); sourceHashes.Add(path, hash);
                var entries = new List<object>();
                using (var archive = BinaryFile.Load<FarcArchive>(path))
                {
                    if (!archive.FileNames.Any() || !archive.FileNames.Any(x => x.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("Archive has no motion payload: " + path);
                    foreach (string name in archive.FileNames)
                    {
                        using var stream = archive.Open(name, EntryStreamMode.MemoryStream);
                        using var memory = new MemoryStream(); stream.CopyTo(memory);
                        if (memory.Length == 0) throw new InvalidDataException("Empty motion payload: " + name);
                        entries.Add(new { name, bytes = memory.Length, sha256 = PhysicalOccupancy.Hash(memory.ToArray()) });
                    }
                }
                string target = Path.Combine(temporary, "rom/rob", Path.GetFileName(path));
                File.Copy(path, target);
                if (Hash(target) != hash) throw new InvalidDataException("Archive bytes changed during import.");
                provenance.Add(new { sourcePath = path, path = "rom/rob/" + Path.GetFileName(path), sha256 = hash, entries });
            }
            foreach (var pair in baseHashes)
                if (Hash(Path.Combine(baseline, pair.Key)) != pair.Value || Hash(Path.Combine(temporary, pair.Key)) != pair.Value)
                    throw new InvalidDataException("Base files changed during import: " + pair.Key);
            foreach (var pair in sourceHashes)
                if (Hash(pair.Key) != pair.Value) throw new InvalidDataException("Source motion changed during import.");
            string config = Path.Combine(temporary, "config.toml");
            string text = File.ReadAllText(config);
            string description = "description = \"Runtime with preserved " + character + " facial /rob reference; global song/character scope.\"";
            text = Regex.IsMatch(text, @"^description\s*=.*$", RegexOptions.Multiline)
                ? Regex.Replace(text, @"^description\s*=.*$", description, RegexOptions.Multiline) : text + "\n" + description + "\n";
            File.WriteAllText(config, text);
            var manifest = new {
                schemaVersion = 1, phase = "rob-reference-import", moduleId,
                character, source, baseline, globalSongCharacterScope = true,
                scopeLimitedToModule662 = false, retargetComputed = false, curvesReserialized = false,
                runtimeVerified = false, baseManifestSha256 = baseHashes["build-manifest.json"],
                baselineManifest = baseManifest.RootElement.Clone(), baselinePreservedFileHashes = baseHashes,
                importedArchiveCount = selected.Length, sourceArchiveCount = all.Length,
                genericCharacterArchivePresent = names.Contains("mot_" + character + ".farc"),
                commonArchivePresent = names.Contains("mot_common.farc"), importedArchives = provenance,
                emittedFiles = Directory.GetFiles(temporary, "*", SearchOption.AllDirectories)
                    .Where(p => Path.GetFileName(p) != "build-manifest.json").Order()
                    .Select(p => new { path = Path.GetRelativePath(temporary, p).Replace('\\', '/'), sha256 = Hash(p) }).ToArray()
            };
            File.WriteAllText(Path.Combine(temporary, "build-manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            Directory.Move(temporary, output); temporary = null;
            Console.WriteLine(output);
            Console.WriteLine($"Imported {selected.Length} facial archives unchanged; baseline models and skin parameters preserved. Scope: song/character, gameplay test pending.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
        finally { if (temporary != null && Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }
    }

    private static string Hash(string path) => PhysicalOccupancy.Hash(File.ReadAllBytes(path));
    private static bool Inside(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
