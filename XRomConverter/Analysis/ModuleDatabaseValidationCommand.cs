using System.Text.Json;
using XRomConverter.Identity;
using XRomConverter.Ids;

namespace XRomConverter.Analysis;

internal sealed record DatabaseValidationIssue(string Code, string Severity,
    string Message, string? CanonicalId = null, int? TargetId = null);

internal sealed record DatabaseBindingCheck(string CanonicalId, string DisplayName,
    string TargetProfile, int TargetId, string? ArchiveName, string? ArchiveSource,
    string BindingKind, string Authority, string SourceReference, string Status);

internal sealed record ModuleDatabaseValidationReport(int DatabaseEntries, int ArchiveRows,
    int BoundEntries, int UnboundEntries, IReadOnlyList<DatabaseBindingCheck> Bindings,
    IReadOnlyList<DatabaseValidationIssue> Issues, string Status);

internal static class ModuleDatabaseValidationCommand
{
    public static bool IsRequested(string[] args) => args.Length > 0 &&
        args[0].Equals("validate-module-database", StringComparison.OrdinalIgnoreCase);

    public static int Run(string[] args)
    {
        string? databasePath = null;
        string? csvPath = null;
        string? outputPath = null;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--database" && i + 1 < args.Length) databasePath = args[++i];
            else if (args[i] == "--module-csv" && i + 1 < args.Length) csvPath = args[++i];
            else if (args[i] == "--output" && i + 1 < args.Length) outputPath = args[++i];
        }
        if (csvPath == null)
        {
            Console.Error.WriteLine("Usage: XRomConverter validate-module-database --module-csv PATH [--database PATH] [--output PATH]");
            return 64;
        }

        var database = databasePath == null ? ModuleIdentifier.LoadDefault() : ModuleIdentifier.Load(databasePath);
        var rows = ModuleArchiveIdCatalog.LoadRows(csvPath);
        var issues = new List<DatabaseValidationIssue>();

        foreach (var group in database.Modules.GroupBy(x => x.CanonicalId).Where(x => x.Count() > 1))
            issues.Add(new("DUPLICATE_CANONICAL_ID", "error",
                $"Canonical ID '{group.Key}' occurs {group.Count()} times.", group.Key));
        foreach (var group in database.Modules.SelectMany(x => x.Representations.Select(r => (Entry:x, Rep:r)))
                     .GroupBy(x => (x.Rep.System, x.Rep.Character, x.Rep.ModuleId, x.Rep.CosId, x.Rep.PackageId))
                     .Where(x => x.Count() > 1))
            issues.Add(new("DUPLICATE_SOURCE_REPRESENTATION", "error",
                $"Source representation {group.Key} belongs to {group.Count()} canonical entries."));
        foreach (var group in database.Modules.SelectMany(x => x.TargetBindings.Select(b => (Entry:x, Profile:b.Key, Id:b.Value.Id)))
                     .GroupBy(x => (x.Profile, x.Id)).Where(x => x.Count() > 1))
            issues.Add(new("DUPLICATE_DATABASE_TARGET", "error",
                $"Target {group.Key.Id} in '{group.Key.Profile}' belongs to {group.Count()} canonical entries.",
                TargetId: group.Key.Id));
        foreach (var entry in database.Modules)
        foreach (var binding in entry.TargetBindings)
        {
            if (binding.Value.Kind != "official" && binding.Value.Kind != "community")
                issues.Add(new("INVALID_BINDING_KIND", "error",
                    $"Binding '{entry.CanonicalId}/{binding.Key}' has unsupported kind '{binding.Value.Kind}'.",
                    entry.CanonicalId, binding.Value.Id));
            if (string.IsNullOrWhiteSpace(binding.Value.Authority) ||
                string.IsNullOrWhiteSpace(binding.Value.SourceReference))
                issues.Add(new("BINDING_PROVENANCE_MISSING", "error",
                    $"Binding '{entry.CanonicalId}/{binding.Key}' has no explicit authority/source.",
                    entry.CanonicalId, binding.Value.Id));
        }
        foreach (var group in rows.GroupBy(x => x.Id).Where(x => x.Count() > 1))
            issues.Add(new("DUPLICATE_ARCHIVE_TARGET", "warning",
                $"Archive ID {group.Key} has {group.Count()} rows: {string.Join(" | ", group.Select(x => x.NameEn + " [" + x.SourceOrReservation + "]"))}.",
                TargetId: group.Key));

        var checks = new List<DatabaseBindingCheck>();
        foreach (var entry in database.Modules)
        foreach (var binding in entry.TargetBindings)
        {
            var candidates = rows.Where(x => x.Id == binding.Value.Id).ToArray();
            string status;
            if (candidates.Length == 0)
            {
                status = "missing-from-archive";
                issues.Add(new("DATABASE_TARGET_NOT_IN_ARCHIVE", "error",
                    $"Target {binding.Value.Id} for '{entry.CanonicalId}' is absent from the archive sheet.",
                    entry.CanonicalId, binding.Value.Id));
            }
            else if (candidates.Length > 1) status = "archive-id-ambiguous";
            else if (candidates[0].SourceOrReservation.Equals("Unused", StringComparison.OrdinalIgnoreCase))
            {
                status = "marked-unused";
                issues.Add(new("DATABASE_TARGET_MARKED_UNUSED", "error",
                    $"Target {binding.Value.Id} for '{entry.CanonicalId}' is marked Unused.",
                    entry.CanonicalId, binding.Value.Id));
            }
            else status = NamesEqual(entry.DisplayName, candidates[0].NameEn) ? "confirmed" : "name-mismatch";
            if (status == "name-mismatch")
                issues.Add(new("DATABASE_ARCHIVE_NAME_MISMATCH", "warning",
                    $"Database name '{entry.DisplayName}' differs from archive name '{candidates[0].NameEn}'.",
                    entry.CanonicalId, binding.Value.Id));
            checks.Add(new(entry.CanonicalId, entry.DisplayName, binding.Key, binding.Value.Id,
                candidates.FirstOrDefault()?.NameEn, candidates.FirstOrDefault()?.SourceOrReservation,
                binding.Value.Kind, binding.Value.Authority, binding.Value.SourceReference, status));
        }

        int bound = database.Modules.Count(x => x.TargetBindings.Count > 0);
        string statusResult = issues.Any(x => x.Severity == "error") ? "invalid" :
            issues.Count > 0 ? "valid-with-warnings" : "valid";
        var report = new ModuleDatabaseValidationReport(database.Modules.Count, rows.Count,
            bound, database.Modules.Count - bound, checks, issues, statusResult);
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
        return statusResult == "invalid" ? 2 : 0;
    }

    private static bool NamesEqual(string left, string right) => string.Equals(
        NormalizeName(left), NormalizeName(right), StringComparison.Ordinal);

    private static string NormalizeName(string value) => new(value.ToLowerInvariant()
        .Where(char.IsLetterOrDigit).ToArray());
}
