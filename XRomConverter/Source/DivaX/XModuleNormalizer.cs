using XRomConverter.Analysis;
using XRomConverter.Identity;
using XRomConverter.Transformations.Skin;

namespace XRomConverter.Source.DivaX;

internal sealed class XModuleNormalizer
{
    private readonly SkinParameterValidator mValidator = new();

    public NormalizedModule Normalize(XModuleAggregate aggregate, ModuleIdentifier identifier)
    {
        var diagnostics = aggregate.Diagnostics.ToList();
        var components = aggregate.Components.Select(component => new NormalizedComponent(
            component.SlotIndex, ToRole(component.SubId), component.ItemNumber,
            component.SubId, component.Attribute, component.SortIndex, component.Type,
            component.DestinationId, component.Objects,
            component.ObjectSets.Select(set => new NormalizedObjectSet(
                set.SourceId, set.SourceName, set.Objects.Select(obj => NormalizeObject(obj, aggregate, diagnostics)).ToArray(),
                set.Textures, set.Provenance)).ToArray(), component.TextureChanges,
            component.Provenance)).ToArray();
        var identity = identifier.Identify(aggregate);
        return new NormalizedModule(aggregate.Module.ModuleId, aggregate.Module.CosId,
            aggregate.Module.Character, aggregate.Module.Name, components,
            aggregate.Presentation, aggregate.Module.Provenance, identity,
            diagnostics);
    }

    private NormalizedObject NormalizeObject(XObjectRecord obj, XModuleAggregate module, List<Diagnostic> diagnostics)
    {
        var blocks = obj.SkinBlocks.Where(x => x.Kind is "OSG" or "MOT")
            .Select(x => new NormalizedSkinBlock(x.Kind, x.Name ?? "", x.ExternalName,
                x.ParentName, x.NodeNames.ToArray(), false)).ToArray();
        var parameters = SharedOspParameterResolver.Resolve(obj, module, diagnostics).Select(x => new NormalizedSkinParameter(
            x.Parameter.Name, x.Parameter.BocTargets, x.Parameter.CollisionTarget, x.Parameter.NodeCount,
            x.Provenance, false, SkinParameterTextCodec.Clone(x.Parameter.Physical))).ToArray();
        bool hasOsg = blocks.Any(x => x.Kind == "OSG");
        bool hasMot = blocks.Any(x => x.Kind == "MOT");
        SkinExpectation expectation = (hasOsg, hasMot, obj.SkinParameter.Found) switch
        {
            (false, false, false) => SkinExpectation.NotRequired,
            (true, _, false) => SkinExpectation.RequiredButMissing,
            (_, true, false) => SkinExpectation.DerivedOnly,
            (_, true, true) => SkinExpectation.PartiallyDerived,
            (true, false, true) => SkinExpectation.RequiredAndFound,
            _ => SkinExpectation.Indeterminate
        };
        return new NormalizedObject(obj.SourceId, obj.SourceName, blocks, parameters,
            expectation, obj.Provenance, obj.SkinParameter.Provenance,
            mValidator.Validate(blocks.Where(x => x.Kind == "OSG").ToArray(), parameters));
    }

    private static string ToRole(string subId) => subId switch
    {
        "ATAM_ZUJO" => "HeadTop",
        "ATAM_ATAMA" => "Hair",
        "FACE_FACE" => "Face",
        "ATAM_HEAD" => "Head",
        "JOHA_NECK" => "Neck",
        "JOHA_OUTER" => "Outer",
        "JOHA_BACK" => "Back",
        "UDE_HAND" => "Hand",
        _ => "Unknown"
    };
}
