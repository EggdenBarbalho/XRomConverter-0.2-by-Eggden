using System.Text.Json;
using XRomConverter.Identity;

namespace XRomConverter.Ids;

internal sealed record CanonicalBindingPlan(string CanonicalId, string DisplayName,
    string TargetProfile, int? PreferredTargetId, string? PreferredBindingKind,
    IReadOnlyList<int> EffectiveObservedIds, int? SelectedTargetId,
    IReadOnlyList<int> AliasesToRetire, string Action, string Status,
    IReadOnlyList<string> Evidence);

internal sealed record InstallationBindingPlan(string TargetProfile,
    IReadOnlyList<CanonicalBindingPlan> Modules, IReadOnlyList<string> Errors,
    string Status);

internal static class InstallationBindingPlannerCommand
{
    public static bool IsRequested(string[] args) => args.Length > 0 &&
        args[0].Equals("plan-installation-bindings", StringComparison.OrdinalIgnoreCase);

    public static int Run(string[] args)
    {
        string? effectivePath = null;
        string? databasePath = null;
        string? outputPath = null;
        string profile = "megamix_plus";
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--effective-installation" && i + 1 < args.Length) effectivePath = args[++i];
            else if (args[i] == "--database" && i + 1 < args.Length) databasePath = args[++i];
            else if (args[i] == "--target-profile" && i + 1 < args.Length) profile = args[++i];
            else if (args[i] == "--output" && i + 1 < args.Length) outputPath = args[++i];
            else return Usage();
        }
        if (effectivePath == null || !File.Exists(effectivePath)) return Usage();

        var effective = JsonSerializer.Deserialize<LoaderResolutionReport>(
            File.ReadAllText(effectivePath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Empty effective installation report.");
        var database = databasePath == null ? ModuleIdentifier.LoadDefault() : ModuleIdentifier.Load(databasePath);
        var plans = database.Modules.Select(entry => Plan(entry, profile, effective)).ToArray();
        var errors = new List<string>();
        foreach (var duplicate in plans.Where(x => x.SelectedTargetId.HasValue && x.Status != "blocked")
                     .GroupBy(x => x.SelectedTargetId!.Value).Where(x => x.Count() > 1))
            errors.Add($"Target {duplicate.Key} selected by multiple identities: {string.Join(", ", duplicate.Select(x => x.CanonicalId))}.");
        errors.AddRange(plans.Where(x => x.Status == "blocked")
            .Select(x => $"{x.CanonicalId}: {x.Action}"));
        string status = errors.Count == 0 ? "ready" : "blocked";
        var report = new InstallationBindingPlan(profile, plans, errors, status);
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
        return status == "ready" ? 0 : 2;
    }

    private static CanonicalBindingPlan Plan(ModuleIdentityEntry entry, string profile,
        LoaderResolutionReport effective)
    {
        var preferred = entry.TargetBindings.GetValueOrDefault(profile);
        var observed = effective.Modules.Where(x => x.Status == "resolved" &&
                x.Winner?.CanonicalCandidates.Contains(entry.CanonicalId) == true)
            .Select(x => x.ModuleId).Distinct().Order().ToArray();
        var evidence = effective.Modules.Where(x => observed.Contains(x.ModuleId))
            .Select(x => $"effective:{x.ModuleId}:{x.Winner!.Owner}").ToArray();

        int? selected;
        int[] retire;
        string action;
        string status;
        if (preferred != null)
        {
            selected = preferred.Id;
            retire = observed.Where(x => x != preferred.Id).ToArray();
            action = retire.Length > 0
                ? observed.Contains(preferred.Id) ? "preserve-preferred-and-retire-aliases" : "remap-to-preferred"
                : observed.Contains(preferred.Id) ? "preserve-preferred" : "rebuild-at-preferred";
            status = "ready";
            evidence = evidence.Append($"database:{preferred.Kind}:{preferred.SourceReference}").ToArray();
        }
        else if (observed.Length == 1)
        {
            selected = observed[0];
            retire = Array.Empty<int>();
            action = "adopt-local-observed";
            status = "ready";
        }
        else if (observed.Length > 1)
        {
            selected = null;
            retire = Array.Empty<int>();
            action = "multiple-effective-ids-require-explicit-choice";
            status = "blocked";
        }
        else
        {
            selected = null;
            retire = Array.Empty<int>();
            action = "allocate-free-id";
            status = "needs-allocation";
        }
        return new(entry.CanonicalId, entry.DisplayName, profile, preferred?.Id,
            preferred?.Kind, observed, selected, retire, action, status, evidence);
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: XRomConverter plan-installation-bindings --effective-installation PATH [--database PATH] [--target-profile NAME] [--output PATH]");
        return 64;
    }
}
