using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.CriMw;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Parameters;
using XRomConverter.Identity;

namespace XRomConverter.Ids;

internal sealed record OccupiedIdRegistry(
    IReadOnlyList<int> ModuleIds,
    IReadOnlyDictionary<string, IReadOnlyList<int>> CostumeIds,
    IReadOnlyDictionary<string, IReadOnlyList<int>> ItemNumbers,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<InstalledModuleObservation>? ModuleObservations = null,
    IReadOnlyList<InstalledModuleConflict>? ModuleConflicts = null);

internal sealed record InstalledIdentityCandidate(string CanonicalId, string DisplayName,
    string Confidence, IReadOnlyList<string> Evidence);

internal sealed record InstalledModuleObservation(int ModuleId, string SourceKind,
    string Source, string? ModName, IReadOnlyDictionary<string, string> Fields,
    string? StructuralSignature, IReadOnlyList<string> ArchiveNames,
    IReadOnlyList<InstalledIdentityCandidate> IdentityCandidates, string Resolution);

internal sealed record InstalledModuleConflict(int ModuleId, string Code, string Severity,
    string Message, IReadOnlyList<string> Sources);

internal static class OccupiedIdRegistrySnapshot
{
    public static OccupiedIdRegistry Load(string path) =>
        JsonSerializer.Deserialize<OccupiedIdRegistry>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidDataException("Empty installation ID registry snapshot.");
}

internal static class InstallationIdScannerCommand
{
    public static bool IsRequested(string[] args) => args.Length > 0 &&
        args[0].Equals("scan-installation-ids", StringComparison.OrdinalIgnoreCase);

    public static int Run(string[] args)
    {
        if (args.Length < 2 || !File.Exists(Path.Combine(args[1], "diva_main.cpk"))) return 64;
        string root = Path.GetFullPath(args[1]);
        string? moduleCsv = null;
        string? outputPath = null;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--module-csv" && i + 1 < args.Length) moduleCsv = args[++i];
            else if (args[i] == "--output" && i + 1 < args.Length) outputPath = args[++i];
            else return 64;
        }
        var archiveRows = moduleCsv == null ? Array.Empty<ModuleArchiveIdCatalog.Row>()
            : ModuleArchiveIdCatalog.LoadRows(moduleCsv).ToArray();
        var database = ModuleIdentifier.LoadDefault();
        var modules = new HashSet<int>();
        var costumes = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        var items = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        var sources = new List<string>();
        var diagnostics = new List<string>();
        var observations = new List<InstalledModuleObservation>();

        using (var cpk = BinaryFile.Load<CpkArchive>(Path.Combine(root, "diva_main.cpk")))
        {
            foreach (string entry in cpk.FileNames.Where(x => x.EndsWith("chritm_prop.farc", StringComparison.OrdinalIgnoreCase)
                                                           || x.EndsWith("gm_module_tbl.farc", StringComparison.OrdinalIgnoreCase)))
            {
                try { using var stream = cpk.Open(entry, EntryStreamMode.MemoryStream); ScanFarc(stream, "base:" + entry, "base", null, modules, costumes, items, observations, database, archiveRows); sources.Add("base:" + entry); }
                catch (Exception ex) { diagnostics.Add(entry + ": " + ex.Message); }
            }
        }
        string mods = Path.Combine(root, "mods");
        if (Directory.Exists(mods))
        {
            foreach (string path in Directory.EnumerateFiles(mods, "*.farc", SearchOption.AllDirectories)
                         .Where(x => Path.GetFileName(x).Equals("mod_chritm_prop.farc", StringComparison.OrdinalIgnoreCase)
                                  || Path.GetFileName(x).Equals("gm_module_tbl.farc", StringComparison.OrdinalIgnoreCase)))
            {
                try { using var stream = File.OpenRead(path); ScanFarc(stream, path, "mod", GetModName(mods, path), modules, costumes, items, observations, database, archiveRows); sources.Add(path); }
                catch (Exception ex) { diagnostics.Add(path + ": " + ex.Message); }
            }
        }
        observations = ResolveStructuralMatches(observations);
        var conflicts = FindConflicts(observations, archiveRows, database);
        var result = new OccupiedIdRegistry(modules.Order().ToArray(),
            costumes.ToDictionary(x => x.Key, x => (IReadOnlyList<int>)x.Value.Order().ToArray()),
            items.ToDictionary(x => x.Key, x => (IReadOnlyList<int>)x.Value.Order().ToArray()),
            sources, diagnostics, observations.OrderBy(x => x.ModuleId).ThenBy(x => x.Source).ToArray(),
            conflicts);
        string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        if (outputPath == null) Console.WriteLine(json);
        else
        {
            string fullPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, json);
            Console.WriteLine(fullPath);
        }
        return diagnostics.Count == 0 && conflicts.All(x => x.Severity != "error") ? 0 : 2;
    }

    private static void ScanFarc(Stream stream, string source, string sourceKind,
        string? modName, HashSet<int> modules,
        Dictionary<string, HashSet<int>> costumes, Dictionary<string, HashSet<int>> items,
        List<InstalledModuleObservation> observations, ModuleDatabaseDocument database,
        IReadOnlyList<ModuleArchiveIdCatalog.Row> archiveRows)
    {
        using var farc = BinaryFile.Load<FarcArchive>(stream);
        foreach (string entry in farc.FileNames)
        {
            using var data = farc.Open(entry, EntryStreamMode.MemoryStream);
            var tree = ParameterTree.Load(data);
            if (entry.Equals("gm_module_id.bin", StringComparison.OrdinalIgnoreCase))
            {
                if (!tree.OpenScope("module")) continue;
                foreach (string key in tree.Keys.ToArray())
                    if (tree.OpenScope(key))
                    {
                        int id = tree.Get("id", -1);
                        if (id >= 0)
                        {
                            modules.Add(id);
                            var fields = ReadLeafFields(tree);
                            observations.Add(CreateObservation(id, sourceKind, source,
                                modName, fields, database, archiveRows));
                        }
                        tree.CloseScope();
                    }
                tree.CloseScope();
            }
            else if (entry.EndsWith("itm_tbl.txt", StringComparison.OrdinalIgnoreCase))
            {
                string character = entry[..^"itm_tbl.txt".Length].ToUpperInvariant();
                var charCostumes = costumes.TryGetValue(character, out var c) ? c : costumes[character] = new();
                var charItems = items.TryGetValue(character, out var i) ? i : items[character] = new();
                if (tree.OpenScope("cos"))
                {
                    foreach (string key in tree.Keys.ToArray())
                        if (tree.OpenScope(key)) { int id = tree.Get("id", -1); if (id >= 0) charCostumes.Add(id); tree.CloseScope(); }
                    tree.CloseScope();
                }
                if (tree.OpenScope("item"))
                {
                    foreach (string key in tree.Keys.ToArray())
                        if (tree.OpenScope(key)) { int no = tree.Get("no", -1); if (no >= 0) charItems.Add(no); tree.CloseScope(); }
                    tree.CloseScope();
                }
            }
        }
    }

    private static IReadOnlyDictionary<string, string> ReadLeafFields(ParameterTree tree)
    {
        var fields = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string field in tree.Keys.ToArray())
        {
            try { fields[field] = tree.Get(field, ""); }
            catch { }
        }
        return fields;
    }

    private static InstalledModuleObservation CreateObservation(int id, string sourceKind,
        string source, string? modName, IReadOnlyDictionary<string, string> fields,
        ModuleDatabaseDocument database, IReadOnlyList<ModuleArchiveIdCatalog.Row> archiveRows)
    {
        var candidates = new Dictionary<string, InstalledIdentityCandidate>();
        var names = archiveRows.Where(x => x.Id == id &&
            !x.SourceOrReservation.Equals("Unused", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.NameEn).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
        var observedNames = fields.Where(x => x.Key.Contains("name", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Value).Append(modName ?? "").Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        foreach (var entry in database.Modules)
        {
            var binding = entry.TargetBindings.GetValueOrDefault("megamix_plus");
            bool nameConfirms = observedNames.Any(x => NormalizeName(x) == NormalizeName(entry.DisplayName));
            if (binding?.Id == id && (sourceKind == "base" || nameConfirms))
                candidates[entry.CanonicalId] = new(entry.CanonicalId, entry.DisplayName,
                    "exact", sourceKind == "base"
                        ? new[] { $"database-target:{binding.Kind}", binding.SourceReference, "base-source" }
                        : new[] { $"database-target:{binding.Kind}", binding.SourceReference, "installed-name" });
        }

        foreach (var entry in database.Modules)
        {
            if (observedNames.Any(x => NormalizeName(x) == NormalizeName(entry.DisplayName)))
                candidates.TryAdd(entry.CanonicalId, new(entry.CanonicalId, entry.DisplayName,
                    "exact-name", new[] { "installed-name" }));
            else if (modName != null && NameContainsIdentity(modName, entry))
                candidates.TryAdd(entry.CanonicalId, new(entry.CanonicalId, entry.DisplayName,
                    "hint", new[] { "mod-directory-name" }));
        }

        string signatureInput = string.Join("\n", fields
            .Where(x => !x.Key.Equals("id", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Key.ToLowerInvariant() + "=" + x.Value));
        string? signature = signatureInput.Length == 0 ? null : Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(signatureInput))).ToLowerInvariant();
        var ordered = candidates.Values.OrderBy(x => x.Confidence == "exact" ? 0 :
            x.Confidence == "exact-name" ? 1 : 2).ThenBy(x => x.CanonicalId).ToArray();
        string resolution = ordered.Length == 0 ? "unidentified" :
            ordered.Count(x => x.Confidence != "hint") == 1 ? "identified" :
            ordered.Count(x => x.Confidence != "hint") > 1 ? "ambiguous" : "hint-only";
        return new(id, sourceKind, source, modName, fields, signature, names, ordered, resolution);
    }

    private static List<InstalledModuleObservation> ResolveStructuralMatches(
        IReadOnlyList<InstalledModuleObservation> observations)
    {
        var knownBySignature = observations.Where(x => x.StructuralSignature != null)
            .GroupBy(x => x.StructuralSignature!)
            .ToDictionary(x => x.Key, x => x.SelectMany(y => y.IdentityCandidates)
                .Where(y => y.Confidence == "exact" || y.Confidence == "exact-name")
                .GroupBy(y => y.CanonicalId).Select(y => y.First()).ToArray());
        var result = new List<InstalledModuleObservation>(observations.Count);
        foreach (var observation in observations)
        {
            if (observation.StructuralSignature == null || observation.IdentityCandidates.Any(x => x.Confidence != "hint") ||
                !knownBySignature.TryGetValue(observation.StructuralSignature, out var known) || known.Length != 1)
            {
                result.Add(observation);
                continue;
            }
            var sourceIds = observations.Where(x => x.StructuralSignature == observation.StructuralSignature &&
                    x.IdentityCandidates.Any(c => c.CanonicalId == known[0].CanonicalId && c.Confidence != "hint"))
                .Select(x => x.ModuleId).Distinct().Order().ToArray();
            var structural = new InstalledIdentityCandidate(known[0].CanonicalId, known[0].DisplayName,
                "structural", new[] { "same-module-fields-as-known-id:" + string.Join(',', sourceIds) });
            var candidates = observation.IdentityCandidates.Append(structural).ToArray();
            result.Add(observation with { IdentityCandidates = candidates, Resolution = "identified" });
        }
        return result;
    }

    private static IReadOnlyList<InstalledModuleConflict> FindConflicts(
        IReadOnlyList<InstalledModuleObservation> observations,
        IReadOnlyList<ModuleArchiveIdCatalog.Row> archiveRows,
        ModuleDatabaseDocument database)
    {
        var conflicts = new List<InstalledModuleConflict>();
        foreach (var group in observations.GroupBy(x => x.ModuleId))
        {
            var canonicalIds = group.SelectMany(x => x.IdentityCandidates)
                .Where(x => x.Confidence != "hint").Select(x => x.CanonicalId).Distinct().ToArray();
            if (canonicalIds.Length > 1)
                conflicts.Add(new(group.Key, "MULTIPLE_CANONICAL_IDENTITIES", "error",
                    $"ID {group.Key} is associated with multiple canonical identities: {string.Join(", ", canonicalIds)}.",
                    group.Select(x => x.Source).Distinct().ToArray()));

            var archiveNames = archiveRows.Where(x => x.Id == group.Key &&
                    !x.SourceOrReservation.Equals("Unused", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.NameEn).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
            foreach (var modObservation in group.Where(x => x.SourceKind == "mod"))
            {
                string? installedName = modObservation.Fields
                    .FirstOrDefault(x => x.Key.Contains("name", StringComparison.OrdinalIgnoreCase)).Value;
                var officialEntry = database.Modules.FirstOrDefault(x =>
                    x.TargetBindings.GetValueOrDefault("megamix_plus")?.Id == group.Key);
                if (officialEntry != null && !string.IsNullOrWhiteSpace(installedName) &&
                    NormalizeName(installedName) != NormalizeName(officialEntry.DisplayName))
                    conflicts.Add(new(group.Key, "PHYSICAL_ID_CONTENT_MISMATCH", "warning",
                        $"Mod '{modObservation.ModName}' uses official ID {group.Key} for '{installedName}', not '{officialEntry.DisplayName}'.",
                        new[] { modObservation.Source }));

            foreach (var candidate in modObservation.IdentityCandidates.Where(x => x.Confidence != "hint"))
                if (archiveNames.Length > 0 && !archiveNames.Any(x => NormalizeName(x) == NormalizeName(candidate.DisplayName)))
                    conflicts.Add(new(group.Key, "MOD_OVERWRITES_ARCHIVE_ID", "warning",
                        $"Mod '{modObservation.ModName}' associates ID {group.Key} with '{candidate.DisplayName}', while the archive lists: {string.Join(" | ", archiveNames)}.",
                        new[] { modObservation.Source }));
            }
        }
        return conflicts.GroupBy(x => (x.ModuleId, x.Code, x.Message)).Select(x => x.First())
            .OrderBy(x => x.ModuleId).ThenBy(x => x.Code).ToArray();
    }

    private static string GetModName(string modsRoot, string path)
    {
        string relative = Path.GetRelativePath(modsRoot, path);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
    }

    private static bool NameContainsIdentity(string value, ModuleIdentityEntry entry)
    {
        string normalized = NormalizeName(value);
        return normalized.Contains(NormalizeName(entry.DisplayName), StringComparison.Ordinal) ||
            normalized.Contains(NormalizeName(entry.CanonicalId), StringComparison.Ordinal);
    }

    private static string NormalizeName(string value) => new(value.ToLowerInvariant()
        .Where(char.IsLetterOrDigit).ToArray());
}
