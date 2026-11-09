using System.Text.Json;
using XRomConverter.Analysis;

namespace XRomConverter.Identity;

internal sealed record ModuleIdentityRepresentation(string System, string Character,
    short ModuleId, short CosId, string PackageId);

internal sealed record ModuleTargetBinding(int Id, string Kind, string Authority,
    string SourceReference);

internal sealed record ModuleIdentityEntry(string CanonicalId, string DisplayName,
    IReadOnlyList<ModuleIdentityRepresentation> Representations,
    IReadOnlyDictionary<string, ModuleTargetBinding> TargetBindings);

internal sealed record ModuleDatabaseDocument(int SchemaVersion,
    IReadOnlyList<ModuleIdentityEntry> Modules);

internal sealed class ModuleIdentifier
{
    private readonly ModuleDatabaseDocument mDatabase;

    public ModuleIdentifier(ModuleDatabaseDocument database) => mDatabase = database;

    public CanonicalIdentity? Identify(XModuleAggregate aggregate)
    {
        string package = aggregate.Module.Provenance.PackageId;
        var matches = mDatabase.Modules.Where(entry => entry.Representations.Any(rep =>
            rep.System == "diva_x" && rep.Character == aggregate.Module.Character &&
            rep.ModuleId == aggregate.Module.ModuleId && rep.CosId == aggregate.Module.CosId &&
            (string.IsNullOrEmpty(rep.PackageId) || rep.PackageId == package))).ToArray();
        if (matches.Length != 1) return null;
        var match = matches[0];
        return new CanonicalIdentity(match.CanonicalId, match.DisplayName, "exact",
            new[] { "system", "character", "moduleId", "cosId", "packageId" });
    }

    public ModuleIdentityEntry? Find(string canonicalId) =>
        mDatabase.Modules.SingleOrDefault(x => x.CanonicalId == canonicalId);

    public IReadOnlySet<int> GetReservedTargetIds(string targetProfile) =>
        mDatabase.Modules.Where(x => x.TargetBindings.ContainsKey(targetProfile))
            .Select(x => x.TargetBindings[targetProfile].Id).ToHashSet();

    public static ModuleDatabaseDocument LoadDefault()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Resources", "modules", "module-database.json");
        return Load(path);
    }

    public static ModuleDatabaseDocument Load(string path)
    {
        var database = JsonSerializer.Deserialize<ModuleDatabaseDocument>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Empty module database.");
        if (database.SchemaVersion != 2)
            throw new InvalidDataException($"Unsupported module database schema {database.SchemaVersion}; expected 2.");
        return database;
    }
}
