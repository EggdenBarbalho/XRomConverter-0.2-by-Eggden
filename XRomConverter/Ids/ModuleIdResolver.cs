using XRomConverter.Analysis;
using XRomConverter.Identity;

namespace XRomConverter.Ids;

internal sealed class ModuleIdResolver
{
    public TargetIdBinding Resolve(NormalizedModule module, ModuleIdentifier identifier,
        string targetProfile, int? explicitTargetId, IReadOnlySet<int> occupied,
        LocalIdRegistry? localRegistry = null, int? observedTargetId = null,
        string communityIdPolicy = "preserve")
    {
        if (explicitTargetId.HasValue)
            return occupied.Contains(explicitTargetId.Value)
                ? new TargetIdBinding(explicitTargetId, "conflict", "explicit")
                : new TargetIdBinding(explicitTargetId, "proposed", "explicit");
        if (module.Identity != null)
        {
            var entry = identifier.Find(module.Identity.CanonicalId);
            if (entry != null && entry.TargetBindings.TryGetValue(targetProfile, out var known))
            {
                if (known.Kind == "community" && observedTargetId.HasValue &&
                    communityIdPolicy == "preserve")
                    return new TargetIdBinding(observedTargetId, "observed-existing",
                        "installation", "local_observed", "installation-scan",
                        observedTargetId, "preserve");

                bool occupiedByUnknown = occupied.Contains(known.Id) &&
                    observedTargetId.HasValue && observedTargetId.Value != known.Id &&
                    known.Kind == "community";
                if (occupiedByUnknown)
                    return new TargetIdBinding(known.Id, "conflict", "database",
                        known.Kind, known.Authority, observedTargetId,
                        "blocked-target-occupied");

                // An occupied canonical binding is the expected destination identity,
                // not an allocation collision. Structural rebuilding may target it.
                return new TargetIdBinding(known.Id,
                    occupied.Contains(known.Id) ? "known-existing" : "known", "database",
                    known.Kind, known.Authority, observedTargetId,
                    observedTargetId.HasValue && observedTargetId.Value != known.Id
                        ? "remap-proposed" : "preserve");
            }
        }
        if (observedTargetId.HasValue)
            return new TargetIdBinding(observedTargetId, "observed-existing",
                "installation", "local_observed", "installation-scan",
                observedTargetId, "preserve");
        if (localRegistry != null && localRegistry.TryGet(module, targetProfile, out int persisted))
            return new TargetIdBinding(persisted,
                occupied.Contains(persisted) ? "registered-existing" : "registered",
                "local-registry");
        var unavailable = occupied.ToHashSet();
        unavailable.UnionWith(identifier.GetReservedTargetIds(targetProfile));
        if (localRegistry != null)
            unavailable.UnionWith(localRegistry.GetAllocatedIds(targetProfile));
        for (int candidate = 600; candidate <= 999; candidate++)
            if (!unavailable.Contains(candidate))
                return new TargetIdBinding(candidate, "proposed", "allocator-preview");
        return new TargetIdBinding(null, "unresolved", "no-free-id");
    }
}
