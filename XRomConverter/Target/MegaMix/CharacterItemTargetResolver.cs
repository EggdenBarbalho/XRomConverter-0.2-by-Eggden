using XRomConverter.Analysis;
using XRomConverter.Ids;
using System.Text.Json.Serialization;

namespace XRomConverter.Target.MegaMix;

internal sealed record TargetItemSet(string SourceName, string TargetName, uint SetId, bool Physical);
internal sealed record TargetItemObject(string SourceName, string TargetName, byte Rpk,
    string SourceSet, string TargetSet, uint? ObjectId, bool Physical);
internal sealed record TargetItemTextureChange(string OriginalName, string ReplacementName);
internal sealed record TargetItemBinding(int ItemNumber, string Role, IReadOnlyList<TargetItemSet> ObjectSets,
    IReadOnlyList<TargetItemObject> Objects, IReadOnlyList<TargetItemTextureChange> TextureChanges,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? SourceItemNumber = null);

internal static class CharacterItemTargetResolver
{
    public static int CostumeId(ModuleAnalysisResult result) => result.BuildPlan.TableSelection?.TargetCostumeId ?? result.NormalizedModule.SourceCosId;

    public static IReadOnlyList<AllocatedObjectSetId> AllocateSets(ModuleAnalysisResult result, string csv)
    {
        var plans = result.BuildPlan.ObjectSets;
        if (plans.GroupBy(x => x.SourceName, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() != 1) ||
            plans.GroupBy(x => x.TargetName, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() != 1))
            throw new InvalidDataException("Object-set source/target names in the build plan are ambiguous.");
        var bound = plans.Where(x => x.PhysicalSelection != null).ToArray();
        if (bound.Any(x => x.PhysicalSelection!.TargetSetId > ushort.MaxValue) ||
            bound.GroupBy(x => x.PhysicalSelection!.TargetSetId).Any(x => x.Count() != 1))
            throw new InvalidDataException("Physical object-set bindings collide or exceed the classic 16-bit range.");
        var physical = bound.Select(x => new AllocatedObjectSetId(x.TargetName, x.PhysicalSelection!.TargetSetId,
            "physical build plan:" + x.PhysicalSelection.RegistryProfile)).ToArray();
        var catalog = ObjectSetArchiveIdCatalog.Load(csv).Allocate(plans.Where(x => x.PhysicalSelection == null).Select(x => x.TargetName),
            additionalOccupied: physical.Select(x => x.TargetId));
        return physical.Concat(catalog.Select(x => new AllocatedObjectSetId(x.Key, x.Value,
            "Diva Mod Archive objset_spreadsheet.csv occupancy"))).OrderBy(x => x.TargetId).ToArray();
    }

    public static IReadOnlyList<TargetItemBinding> Resolve(ModuleAnalysisResult result, IReadOnlyList<AllocatedObjectSetId> ids)
    {
        var plans = result.BuildPlan.ObjectSets;
        if (plans.GroupBy(x => x.SourceName, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() != 1) ||
            plans.GroupBy(x => x.TargetName, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() != 1) ||
            ids.GroupBy(x => x.TargetName, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() != 1) ||
            ids.GroupBy(x => x.TargetId).Any(x => x.Count() != 1))
            throw new InvalidDataException("Ambiguous target object-set binding.");
        PlannedObjectSet Plan(string source) => plans.SingleOrDefault(x => x.SourceName.Equals(source, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("Object set has no target plan: " + source);
        uint Id(PlannedObjectSet plan)
        {
            var id = ids.SingleOrDefault(x => x.TargetName.Equals(plan.TargetName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("Object set has no target ID: " + plan.TargetName);
            if (plan.PhysicalSelection != null && id.TargetId != plan.PhysicalSelection.TargetSetId)
                throw new InvalidDataException("Metadata disagrees with the explicit physical set ID: " + plan.TargetName);
            return id.TargetId;
        }

        var components = result.NormalizedModule.Components.OrderBy(x => x.SlotIndex).ToArray();
        if (components.Select(x => x.SourceItemNumber).Distinct().Count() != components.Length)
            throw new InvalidDataException("A costume contains duplicate item numbers.");
        var table = result.BuildPlan.TableSelection;
        if (table != null && (table.Character != result.NormalizedModule.Character || table.TargetCostumeId < 0 || table.TargetCostumeId > short.MaxValue ||
            table.Items.Count != components.Length || table.Items.Select(x => x.SourceItemNumber).Distinct().Count() != table.Items.Count ||
            !table.Items.Select(x => x.SourceItemNumber).ToHashSet().SetEquals(components.Select(x => (int)x.SourceItemNumber)) ||
            table.Items.Select(x => x.TargetItemNumber).Distinct().Count() != table.Items.Count || table.Items.Any(x => x.TargetItemNumber < 0 || x.TargetItemNumber > short.MaxValue)))
            throw new InvalidDataException("Target costume/item bindings are incomplete, ambiguous or out of range.");
        return components.Select(component =>
        {
            var sets = component.ObjectSets.Select(source =>
            {
                var plan = Plan(source.SourceName);
                return new TargetItemSet(source.SourceName, plan.TargetName, Id(plan), plan.PhysicalSelection != null);
            }).ToArray();
            var objects = component.Objects.Select(reference =>
            {
                // The original converter and Complete Modules emit this explicit
                // replacement sentinel. It does not name a physical head object.
                if (reference.SourceId == MikuMikuLibrary.Hashes.MurmurHash.Calculate("NULL") && reference.SourceName == "NULL")
                    return new TargetItemObject("NULL", "NULL_X", reference.Rpk, "", "", null, false);
                if (string.IsNullOrWhiteSpace(reference.SourceName))
                    throw new InvalidDataException($"Item {component.SourceItemNumber} has an unresolved object name.");
                var owners = component.ObjectSets.Where(set => set.Objects.Any(obj => obj.SourceId == reference.SourceId &&
                    obj.SourceName.Equals(reference.SourceName, StringComparison.OrdinalIgnoreCase))).ToArray();
                if (owners.Length != 1) throw new InvalidDataException("Item object has no unique source-set owner: " + reference.SourceName);
                var plan = Plan(owners[0].SourceName);
                var skinPlans = plan.SkinParameters.Where(x => x.SourceObject.Equals(reference.SourceName, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (skinPlans.Length > 1) throw new InvalidDataException("Duplicate object target plan: " + reference.SourceName);
                if (plan.PhysicalSelection != null)
                {
                    // Existing reference stages record the converted reference
                    // name. The skin plan connects the original X name to its
                    // final physical object without inferring a suffix.
                    string target = skinPlans.Length == 1 ? skinPlans[0].TargetObject : reference.SourceName;
                    var matches = plan.PhysicalSelection.Objects.Where(x =>
                        x.AnalyticalSourceName != null ? x.AnalyticalSourceName.Equals(reference.SourceName, StringComparison.OrdinalIgnoreCase) :
                        x.TargetName.Equals(target, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matches.Length != 1 || matches[0].TargetId > ushort.MaxValue)
                        throw new InvalidDataException("Item object has no unique physical target binding: " + reference.SourceName);
                    return new TargetItemObject(reference.SourceName, matches[0].TargetName, reference.Rpk,
                        plan.SourceName, plan.TargetName, matches[0].TargetId, true);
                }
                string projected = skinPlans.Length == 1 ? skinPlans[0].TargetObject :
                    reference.SourceName.Contains("itm000", StringComparison.OrdinalIgnoreCase) ? reference.SourceName : reference.SourceName + "_x";
                return new TargetItemObject(reference.SourceName, projected, reference.Rpk, plan.SourceName, plan.TargetName, null, false);
            }).ToArray();
            string TextureName(string name)
            {
                if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("Unresolved texture change in item " + component.SourceItemNumber);
                var targets = component.ObjectSets.Select(x => Plan(x.SourceName)).Where(x => x.PhysicalSelection != null)
                    .SelectMany(x => x.PhysicalSelection!.Textures).Where(x => x.SourceName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.TargetName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (targets.Length > 1) throw new InvalidDataException("Texture change has ambiguous physical targets: " + name);
                return targets.Length == 1 ? targets[0] : name;
            }
            var changes = component.TextureChanges.Select(x => new TargetItemTextureChange(TextureName(x.OriginalName!), TextureName(x.ReplacementName!))).ToArray();
            int itemNumber = table?.Items.Single(x => x.SourceItemNumber == component.SourceItemNumber).TargetItemNumber ?? component.SourceItemNumber;
            return new TargetItemBinding(itemNumber, component.Role, sets, objects, changes, table == null ? null : component.SourceItemNumber);
        }).ToArray();
    }
}
