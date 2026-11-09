using System.Text.Json;

namespace XRomConverter.Ids;

internal sealed record PhysicalIdAllocation(string Profile, string Namespace, string Scope,
    string IdentityKey, string Name, uint TargetId);
internal sealed record PhysicalIdRegistryDocument(int SchemaVersion, IReadOnlyList<PhysicalIdAllocation> Allocations);

// Allocations are planned in memory and committed only after output validation.
// Object IDs are scoped to the target object set. Sets and textures are global.
internal sealed class PhysicalIdRegistry
{
    private readonly List<PhysicalIdAllocation> mAllocations;
    public PhysicalIdRegistryDocument Document => new(1, mAllocations.OrderBy(x => x.Profile)
        .ThenBy(x => x.Namespace).ThenBy(x => x.Scope).ThenBy(x => x.TargetId).ToArray());

    public PhysicalIdRegistry(string path)
    {
        var document = File.Exists(path) ? JsonSerializer.Deserialize<PhysicalIdRegistryDocument>(File.ReadAllBytes(path), PhysicalOccupancy.Json)
            ?? throw new InvalidDataException("Empty physical registry.") : new(1, Array.Empty<PhysicalIdAllocation>());
        if (document.SchemaVersion != 1) throw new InvalidDataException("Unsupported physical registry schema.");
        mAllocations = document.Allocations.ToList();
        if (mAllocations.GroupBy(x => (x.Profile, x.Namespace, x.Scope, x.IdentityKey)).Any(x => x.Count() != 1) ||
            mAllocations.GroupBy(x => (x.Profile, x.Namespace, x.Scope, x.TargetId)).Any(x => x.Count() != 1) ||
            mAllocations.GroupBy(x => (x.Profile, x.Namespace, x.Name.ToUpperInvariant())).Any(x => x.Count() != 1))
            throw new InvalidDataException("Duplicate physical registry binding.");
        foreach (var item in mAllocations)
            if (item.Namespace is not ("object-set" or "object" or "texture") ||
                (item.Namespace == "object" ? string.IsNullOrWhiteSpace(item.Scope) : item.Scope != "global") ||
                item.TargetId == uint.MaxValue || item.Namespace != "texture" && item.TargetId > ushort.MaxValue)
                throw new InvalidDataException("Invalid physical registry namespace, scope or ID.");
    }

    public uint Allocate(string profile, string ns, string scope, string key, string name,
        IReadOnlySet<uint> occupied, uint first, uint maximum)
    {
        if (first > maximum) throw new InvalidDataException("Physical allocation range is invalid.");
        var existing = mAllocations.SingleOrDefault(x => x.Profile == profile && x.Namespace == ns && x.Scope == scope && x.IdentityKey == key);
        if (existing != null)
        {
            if (!existing.Name.Equals(name, StringComparison.Ordinal) || existing.TargetId > maximum || occupied.Contains(existing.TargetId))
                throw new InvalidDataException("Persisted physical binding conflicts with the requested name or current occupancy: " + name);
            return existing.TargetId;
        }
        if (mAllocations.Any(x => x.Profile == profile && x.Namespace == ns && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Physical name is already allocated to another identity: " + name);
        var used = occupied.Concat(mAllocations.Where(x => x.Profile == profile && x.Namespace == ns && x.Scope == scope)
            .Select(x => x.TargetId)).ToHashSet();
        uint candidate = first;
        while (used.Contains(candidate))
        {
            if (candidate == maximum) throw new InvalidDataException("Physical ID namespace is exhausted: " + ns);
            candidate++;
        }
        if (candidate > maximum) throw new InvalidDataException("Physical ID is outside its binary range.");
        mAllocations.Add(new(profile, ns, scope, key, name, candidate));
        return candidate;
    }
}
