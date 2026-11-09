using System.Text.Json;

namespace XRomConverter.Ids;

internal sealed record MetadataIdAllocation(string Profile, string Namespace, string Scope, string Key, string Name, uint Id);
internal sealed record MetadataIdRegistryDocument(int SchemaVersion, IReadOnlyList<MetadataIdAllocation> Allocations);
internal sealed class MetadataIdRegistry
{
    private readonly List<MetadataIdAllocation> mAllocations;
    public MetadataIdRegistryDocument Document => new(1, mAllocations.OrderBy(x => x.Profile).ThenBy(x => x.Namespace)
        .ThenBy(x => x.Scope).ThenBy(x => x.Id).ToArray());
    public MetadataIdRegistry(string path)
    {
        var document = File.Exists(path) ? JsonSerializer.Deserialize<MetadataIdRegistryDocument>(File.ReadAllBytes(path), PhysicalOccupancy.Json)
            ?? throw new InvalidDataException("Empty metadata registry.") : new(1, Array.Empty<MetadataIdAllocation>());
        if (document.SchemaVersion != 1) throw new InvalidDataException("Unsupported metadata registry.");
        mAllocations = document.Allocations.ToList();
        if (mAllocations.Any(x => x.Namespace is not ("module" or "costume" or "item" or "sprite-set" or "sprite" or "sprite-texture") ||
            string.IsNullOrWhiteSpace(x.Scope) || x.Id == uint.MaxValue || x.Namespace is "module" or "costume" or "item" && x.Id > short.MaxValue) ||
            mAllocations.GroupBy(x => (x.Profile, x.Namespace, x.Scope, x.Key)).Any(x => x.Count() != 1) ||
            mAllocations.GroupBy(x => (x.Namespace, x.Scope, x.Id)).Any(x => x.Count() != 1) ||
            mAllocations.GroupBy(x => (x.Namespace, x.Scope, x.Name.ToUpperInvariant())).Any(x => x.Count() != 1))
            throw new InvalidDataException("Duplicate or invalid metadata registry binding.");
    }
    public uint Allocate(string profile, string ns, string scope, string key, string name, IReadOnlySet<uint> occupied, uint first, uint max)
    {
        var existing = mAllocations.SingleOrDefault(x => x.Profile == profile && x.Namespace == ns && x.Scope == scope && x.Key == key);
        if (existing != null)
        {
            if (existing.Name != name || existing.Id > max || occupied.Contains(existing.Id))
                throw new InvalidDataException("Persisted metadata binding conflicts with current occupancy or name: " + name);
            return existing.Id;
        }
        var used = occupied.Concat(mAllocations.Where(x => x.Namespace == ns && x.Scope == scope).Select(x => x.Id)).ToHashSet();
        uint id = first;
        while (used.Contains(id)) { if (id == max) throw new InvalidDataException("Metadata ID namespace is exhausted."); id++; }
        if (id > max || mAllocations.Any(x => x.Namespace == ns && x.Scope == scope && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Metadata allocation conflicts or is out of range.");
        mAllocations.Add(new(profile, ns, scope, key, name, id)); return id;
    }
}
