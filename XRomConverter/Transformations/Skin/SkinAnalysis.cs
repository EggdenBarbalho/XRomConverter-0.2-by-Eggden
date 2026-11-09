using XRomConverter.Analysis;

namespace XRomConverter.Transformations.Skin;

internal sealed class SkinParameterValidator
{
    public SkinValidationResult Validate(IReadOnlyList<NormalizedSkinBlock> blocks,
        IReadOnlyList<NormalizedSkinParameter> parameters)
    {
        var issues = new List<SkinValidationIssue>();
        foreach (var block in blocks.Where(x => x.Kind == "OSG" && !string.IsNullOrEmpty(x.ExternalName)))
        {
            int count = parameters.Count(x => x.Name == block.ExternalName);
            if (count != 1)
                issues.Add(new SkinValidationIssue(count == 0 ? "MISSING_EXTERNAL_PARAMETER" : "DUPLICATE_EXTERNAL_PARAMETER",
                    "error", $"OSG '{block.Name}' expects exactly one parameter '{block.ExternalName}', found {count}.", block.ExternalName));
        }
        foreach (var parameter in parameters)
        foreach (string target in parameter.BocTargets.Concat(string.IsNullOrEmpty(parameter.CollisionTarget)
                     ? Array.Empty<string>() : new[] { parameter.CollisionTarget! }))
        {
            int count = parameters.Count(x => x.Name == target);
            if (count != 1)
                issues.Add(new SkinValidationIssue(count == 0 ? "MISSING_INTERNAL_TARGET" : "DUPLICATE_INTERNAL_TARGET",
                    "error", $"Parameter '{parameter.Name}' references '{target}', found {count} targets.", target));
        }
        return new SkinValidationResult(issues.Count == 0, issues);
    }
}

internal sealed record SkinTemplate(string RootBone, string ParameterName, int NodeCount,
    IReadOnlyList<string> BocTargets)
{
    public NormalizedSkinParameter Instantiate(AssetProvenance provenance) =>
        new(ParameterName, BocTargets.ToArray(), null, NodeCount, provenance, true);
}

internal sealed class MotionBlockAnalysisTransform
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<SkinTemplate>> sTemplates =
        new Dictionary<string, IReadOnlyList<SkinTemplate>>(StringComparer.Ordinal)
        {
            ["n_hair_twin_mot"] = new[] {
                new SkinTemplate("j_hair_twin_l_000_wj", "c_hair_twin_l_osg", 6, Array.Empty<string>()),
                new SkinTemplate("j_hair_twin_r_000_wj", "c_hair_twin_r_osg", 6, Array.Empty<string>())
            },
            ["n_hair_long_mot"] = new[] {
                new SkinTemplate("j_hair_long_c_000_wj", "c_hair_long_c_osg", 5, Array.Empty<string>()),
                new SkinTemplate("j_hair_long_l_01_000_wj", "c_hair_long_l_01_osg", 3, Array.Empty<string>()),
                new SkinTemplate("j_hair_long_r_01_000_wj", "c_hair_long_r_01_osg", 3, Array.Empty<string>())
            },
            ["n_cloth_skirt_mot"] = new[] {
                new SkinTemplate("j_cloth_skirt_b_000_wj", "c_cloth_skirt_b_osg", 1, Array.Empty<string>()),
                new SkinTemplate("j_cloth_skirt_f_000_wj", "c_cloth_skirt_f_osg", 2, new[]{"c_cloth_skirt_l_02_osg","c_cloth_skirt_r_02_osg"}),
                new SkinTemplate("j_cloth_skirt_l_02_000_wj", "c_cloth_skirt_l_02_osg", 2, new[]{"c_cloth_skirt_l_04_osg"}),
                new SkinTemplate("j_cloth_skirt_l_04_000_wj", "c_cloth_skirt_l_04_osg", 2, new[]{"c_cloth_skirt_l_06_osg"}),
                new SkinTemplate("j_cloth_skirt_l_06_000_wj", "c_cloth_skirt_l_06_osg", 1, new[]{"c_cloth_skirt_b_osg"}),
                new SkinTemplate("j_cloth_skirt_r_02_000_wj", "c_cloth_skirt_r_02_osg", 2, new[]{"c_cloth_skirt_r_04_osg"}),
                new SkinTemplate("j_cloth_skirt_r_04_000_wj", "c_cloth_skirt_r_04_osg", 2, new[]{"c_cloth_skirt_r_06_osg"}),
                new SkinTemplate("j_cloth_skirt_r_06_000_wj", "c_cloth_skirt_r_06_osg", 1, new[]{"c_cloth_skirt_b_osg"})
            },
            ["n_cloth_long_mot"] = new[] {
                new SkinTemplate("j_cloth_long_f_000_wj", "c_cloth_long_f_osg", 3, Array.Empty<string>()),
                new SkinTemplate("j_cloth_long_b_000_wj", "c_cloth_long_b_osg", 3, Array.Empty<string>()),
                new SkinTemplate("j_cloth_long_l_01_000_wj", "c_cloth_long_l_01_osg", 3, Array.Empty<string>()),
                new SkinTemplate("j_cloth_long_l_02_000_wj", "c_cloth_long_l_02_osg", 3, Array.Empty<string>()),
                new SkinTemplate("j_cloth_long_l_03_000_wj", "c_cloth_long_l_03_osg", 3, Array.Empty<string>()),
                new SkinTemplate("j_cloth_long_r_01_000_wj", "c_cloth_long_r_01_osg", 3, Array.Empty<string>()),
                new SkinTemplate("j_cloth_long_r_02_000_wj", "c_cloth_long_r_02_osg", 3, Array.Empty<string>()),
                new SkinTemplate("j_cloth_long_r_03_000_wj", "c_cloth_long_r_03_osg", 3, Array.Empty<string>())
            }
        };

    public (IReadOnlyList<NormalizedSkinBlock> Blocks,
        IReadOnlyList<NormalizedSkinParameter> Parameters, IReadOnlyList<Diagnostic> Diagnostics)
        Apply(NormalizedObject obj)
    {
        var blocks = obj.OriginalBlocks.Where(x => x.Kind != "MOT").ToList();
        var parameters = obj.OriginalParameters.ToList();
        var diagnostics = new List<Diagnostic>();
        foreach (var motion in obj.OriginalBlocks.Where(x => x.Kind == "MOT"))
        {
            if (!sTemplates.TryGetValue(motion.Name, out var templates))
            {
                diagnostics.Add(new Diagnostic("UNSUPPORTED_MOTION_BLOCK", "error",
                    $"No analytical skin template exists for '{motion.Name}'.", obj.Provenance.LogicalPath));
                continue;
            }
            foreach (var template in templates)
            {
                // Instantiate returns a fresh immutable value. No shared template is mutated.
                var derivedProv = new AssetProvenance(obj.Provenance.SourceId,
                    obj.Provenance.RootPath, obj.Provenance.PackageId,
                    obj.Provenance.ContainerIndex, $"derived/{motion.Name}/{template.RootBone}",
                    "Derived", obj.Provenance.LogicalPath);
                parameters.Add(template.Instantiate(derivedProv));
                blocks.Add(new NormalizedSkinBlock("OSG",
                    "e_" + template.ParameterName[2..^4], template.ParameterName,
                    motion.ParentName, new[] { template.RootBone }, true));
            }
        }
        return (blocks, parameters, diagnostics);
    }
}
