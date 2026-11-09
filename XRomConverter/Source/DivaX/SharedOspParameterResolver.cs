using XRomConverter.Analysis;

namespace XRomConverter.Source.DivaX;

internal sealed record ResolvedSourceSkinParameter(XSkinParameter Parameter, AssetProvenance Provenance, string SourceObject);

// Selected components can repeat the same OSG while storing its OSP in one
// component. Resolve only an exact block from a unique donor in the same source
// package/container. Original local parameters always remain authoritative.
internal static class SharedOspParameterResolver
{
    public static IReadOnlyList<ResolvedSourceSkinParameter> Resolve(XObjectRecord owner, XModuleAggregate module, List<Diagnostic> diagnostics)
    {
        var result = owner.SkinParameter.Parameters.Select(x => new ResolvedSourceSkinParameter(x,
            owner.SkinParameter.Provenance!, owner.SourceName)).ToList();
        if (!owner.SkinParameter.Found || owner.SkinParameter.Provenance == null) return result;
        var scope = owner.SkinParameter.Provenance;
        var donors = module.Components.SelectMany(x => x.ObjectSets).SelectMany(x => x.Objects)
            .Where(x => x.SourceName != owner.SourceName && x.SkinParameter.Found && x.SkinParameter.Provenance is { } p &&
                p.SourceId == scope.SourceId && p.RootPath == scope.RootPath && p.PackageId == scope.PackageId && p.ContainerIndex == scope.ContainerIndex)
            .DistinctBy(x => (x.SourceName, x.SkinParameter.Provenance!.LogicalPath)).ToArray();
        foreach (var block in owner.SkinBlocks.Where(x => x.Kind == "OSG" && !string.IsNullOrWhiteSpace(x.ExternalName)))
        {
            if (result.Any(x => x.Parameter.Name == block.ExternalName)) continue;
            var candidates = donors.Where(x => x.SkinBlocks.Any(b => SameBlock(block, b)))
                .SelectMany(x => x.SkinParameter.Parameters.Where(p => p.Name == block.ExternalName && p.NodeCount == block.NodeNames.Count)
                    .Select(p => new ResolvedSourceSkinParameter(p, x.SkinParameter.Provenance!, x.SourceName))).ToArray();
            if (candidates.Length == 1)
            {
                result.Add(candidates[0]);
                diagnostics.Add(new("SHARED_OSP_PARAMETER", "info",
                    $"{owner.SourceName}: {block.ExternalName} resolves from {candidates[0].Provenance.LogicalPath} ({candidates[0].SourceObject}) with an identical source OSG.",
                    owner.SourceName));
            }
            else if (candidates.Length > 1)
                diagnostics.Add(new("AMBIGUOUS_SHARED_OSP_PARAMETER", "warning",
                    $"{owner.SourceName}: {block.ExternalName} has multiple matching OSP donors; no parameter was selected.", owner.SourceName));
        }
        return result;
    }

    private static bool SameBlock(XSkinBlock a, XSkinBlock b) => a.Kind == "OSG" && b.Kind == "OSG" &&
        a.Name == b.Name && a.ExternalName == b.ExternalName && a.ParentName == b.ParentName && a.NodeNames.SequenceEqual(b.NodeNames);
}
