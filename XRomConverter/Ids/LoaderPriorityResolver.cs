using System.Text.Json;
using System.Text.RegularExpressions;

namespace XRomConverter.Ids;

internal sealed record ModLoadState(string Name, bool? Enabled, int? PriorityRank,
    string EnabledSource, string PrioritySource);

internal sealed record EffectiveModuleDefinition(int ModuleId, string Owner,
    string OwnerKind, bool? Enabled, int? PriorityRank, string? Name,
    string? StructuralSignature, IReadOnlyList<string> Sources,
    IReadOnlyList<string> CanonicalCandidates);

internal sealed record EffectiveModuleResolution(int ModuleId, string Status,
    string Confidence, EffectiveModuleDefinition? Winner,
    IReadOnlyList<EffectiveModuleDefinition> Contenders, string Reason);

internal sealed record LoaderResolutionReport(string InstallationRoot,
    string PriorityConfig, string PriorityDirection,
    IReadOnlyList<string> ConfiguredPriority, IReadOnlyList<ModLoadState> Mods,
    IReadOnlyList<EffectiveModuleResolution> Modules,
    IReadOnlyList<string> Diagnostics);

internal static class LoaderPriorityResolverCommand
{
    public static bool IsRequested(string[] args) => args.Length > 0 &&
        args[0].Equals("resolve-installation-load-order", StringComparison.OrdinalIgnoreCase);

    public static int Run(string[] args)
    {
        if (args.Length < 2) return Usage();
        string root = Path.GetFullPath(args[1]);
        string? inventoryPath = null;
        string? outputPath = null;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--inventory" && i + 1 < args.Length) inventoryPath = args[++i];
            else if (args[i] == "--output" && i + 1 < args.Length) outputPath = args[++i];
            else return Usage();
        }
        if (inventoryPath == null || !File.Exists(inventoryPath)) return Usage();

        var inventory = OccupiedIdRegistrySnapshot.Load(inventoryPath);
        var observations = inventory.ModuleObservations ?? Array.Empty<InstalledModuleObservation>();
        string modsRoot = Path.Combine(root, "mods");
        string priorityPath = Path.Combine(root, "config.toml");
        var diagnostics = new List<string>();
        var priority = File.Exists(priorityPath) ? ReadPriority(priorityPath) : Array.Empty<string>();
        if (!File.Exists(priorityPath)) diagnostics.Add("Missing root config.toml priority source.");
        var ranks = priority.Select((name, index) => (name, index))
            .GroupBy(x => x.name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().index, StringComparer.OrdinalIgnoreCase);

        var modNames = observations.Where(x => x.SourceKind == "mod" && x.ModName != null)
            .Select(x => x.ModName!).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
        var states = modNames.Select(name => ReadModState(modsRoot, name, ranks, priorityPath)).ToArray();
        var stateMap = states.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        foreach (string configured in priority.Where(x => !Directory.Exists(Path.Combine(modsRoot, x))))
            diagnostics.Add($"Priority entry has no matching mod directory: {configured}");

        var resolutions = observations.GroupBy(x => x.ModuleId).OrderBy(x => x.Key)
            .Select(x => Resolve(x.Key, x.ToArray(), stateMap)).ToArray();
        var report = new LoaderResolutionReport(root, priorityPath, "top-is-highest",
            priority, states, resolutions, diagnostics);
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        if (outputPath == null) Console.WriteLine(json);
        else
        {
            string fullPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, json);
            Console.WriteLine(fullPath);
        }
        return resolutions.Any(x => x.Status == "ambiguous") ? 2 : 0;
    }

    private static EffectiveModuleResolution Resolve(int moduleId,
        IReadOnlyList<InstalledModuleObservation> observations,
        IReadOnlyDictionary<string, ModLoadState> states)
    {
        var definitions = observations.GroupBy(x => x.SourceKind == "base" ? "base" : x.ModName ?? x.Source)
            .Select(group =>
            {
                var first = group.First();
                ModLoadState? state = first.SourceKind == "mod" && states.TryGetValue(group.Key, out var found) ? found : null;
                var signatures = group.Select(x => x.StructuralSignature).Distinct().ToArray();
                return new EffectiveModuleDefinition(moduleId, group.Key, first.SourceKind,
                    first.SourceKind == "base" ? true : state?.Enabled,
                    state?.PriorityRank, group.Select(x => x.Fields.GetValueOrDefault("name"))
                        .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
                    signatures.Length == 1 ? signatures[0] : null,
                    group.Select(x => x.Source).Distinct().ToArray(),
                    group.SelectMany(x => x.IdentityCandidates).Select(x => x.CanonicalId).Distinct().ToArray());
            }).ToArray();
        var enabledMods = definitions.Where(x => x.OwnerKind == "mod" && x.Enabled == true).ToArray();
        var unknownMods = definitions.Where(x => x.OwnerKind == "mod" && x.Enabled == null).ToArray();
        var ranked = enabledMods.Where(x => x.PriorityRank.HasValue).OrderBy(x => x.PriorityRank).ToArray();

        if (ranked.Length > 0)
        {
            var winner = ranked[0];
            return new(moduleId, "resolved", "configured-priority", winner, definitions,
                $"'{winner.Owner}' is the highest enabled contender in root config.toml.");
        }
        if (enabledMods.Length == 1 && unknownMods.Length == 0)
            return new(moduleId, "resolved", "single-enabled-mod", enabledMods[0], definitions,
                "Only one enabled mod defines this ID.");
        if (enabledMods.Length == 0 && unknownMods.Length == 0)
        {
            var baseDefinition = definitions.FirstOrDefault(x => x.OwnerKind == "base");
            return new(moduleId, baseDefinition == null ? "absent" : "resolved", "base-only",
                baseDefinition, definitions, "No enabled mod contender was found.");
        }
        return new(moduleId, "ambiguous", "insufficient-loader-evidence", null, definitions,
            "Multiple unranked enabled definitions or unknown enabled states remain.");
    }

    private static ModLoadState ReadModState(string modsRoot, string name,
        IReadOnlyDictionary<string, int> ranks, string priorityPath)
    {
        string config = Path.Combine(modsRoot, name, "config.toml");
        bool? enabled = null;
        if (File.Exists(config))
        {
            var match = Regex.Match(File.ReadAllText(config), @"(?im)^\s*enabled\s*=\s*(true|false)\s*$");
            if (match.Success) enabled = bool.Parse(match.Groups[1].Value);
        }
        return new(name, enabled, ranks.TryGetValue(name, out int rank) ? rank : null,
            File.Exists(config) ? config : "missing-config", priorityPath);
    }

    private static string[] ReadPriority(string path)
    {
        var match = Regex.Match(File.ReadAllText(path), @"(?is)\bpriority\s*=\s*\[(.*?)\]");
        if (!match.Success) return Array.Empty<string>();
        return Regex.Matches(match.Groups[1].Value, "\"([^\"]*)\"")
            .Select(x => JsonSerializer.Deserialize<string>("\"" + x.Groups[1].Value + "\"")!)
            .ToArray();
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: XRomConverter resolve-installation-load-order <MegaMix+ root> --inventory PATH [--output PATH]");
        return 64;
    }
}
