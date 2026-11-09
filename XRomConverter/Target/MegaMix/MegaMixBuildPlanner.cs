using XRomConverter.Analysis;
using XRomConverter.Transformations.Skin;

namespace XRomConverter.Target.MegaMix;

internal sealed class MegaMixBuildPlanner
{
    private readonly MotionBlockAnalysisTransform mMotionTransform = new();
    private readonly SkinParameterValidator mValidator = new();

    public MegaMixBuildPlan Create(NormalizedModule module, TargetIdBinding binding)
    {
        var diagnostics = new List<Diagnostic>(module.Diagnostics);
        var plannedSets = new List<PlannedObjectSet>();
        foreach (var set in module.Components.SelectMany(x => x.ObjectSets)
                     .GroupBy(x => x.SourceName, StringComparer.OrdinalIgnoreCase)
                     .Select(x => x.First()))
        {
            bool baseSet = set.SourceName.EndsWith("ITM000", StringComparison.OrdinalIgnoreCase);
            string targetSet = baseSet ? set.SourceName : set.SourceName + "_X";
            var skinPlans = new List<PlannedSkinParameter>();
            foreach (var obj in set.Objects)
            {
                var transformed = mMotionTransform.Apply(obj);
                diagnostics.AddRange(transformed.Diagnostics);
                var validation = mValidator.Validate(transformed.Blocks, transformed.Parameters);
                string targetObject = baseSet ? obj.SourceName : obj.SourceName + "_x";
                string output = "ext_skp_" + targetObject + ".txt";
                if (obj.Expectation != SkinExpectation.NotRequired || transformed.Parameters.Count > 0)
                    skinPlans.Add(new PlannedSkinParameter(obj.SourceName, targetObject,
                        output, obj.Expectation, transformed.Blocks, transformed.Parameters,
                        validation, obj.Provenance, obj.SkinParameterProvenance));
                if (!obj.SourceValidation.IsValid)
                    diagnostics.AddRange(obj.SourceValidation.Issues.Select(x => new Diagnostic(
                        "SOURCE_" + x.Code, x.Severity, x.Message, obj.Provenance.LogicalPath)));
                if (!validation.IsValid)
                    diagnostics.AddRange(validation.Issues.Select(x => new Diagnostic(
                        "TARGET_" + x.Code, x.Severity, x.Message, obj.Provenance.LogicalPath)));
            }
            plannedSets.Add(new PlannedObjectSet(set.SourceName, targetSet,
                $"rom/objset/{targetSet.ToLowerInvariant()}.farc", set.Provenance, skinPlans));
        }

        var outputs = new List<PlannedOutput>
        {
            new("rom/gm_module_tbl.farc", "ModuleTable", "required", new[]{module.SourceName}),
            new("rom/mod_chritm_prop.farc", "CharacterItemTable", "required",
                module.Components.Select(x => $"item:{x.SourceItemNumber}").ToArray()),
            new("rom/lang2/mod_str_array.toml", "Strings", "required", new[]{module.SourceName}),
            new("rom/objset/mod_obj_db.bin", "ObjectDatabase", "required",
                plannedSets.Select(x => x.TargetName).ToArray()),
            new("rom/objset/mod_tex_db.bin", "TextureDatabase", "required",
                module.Components.SelectMany(x => x.ObjectSets).SelectMany(x => x.Textures)
                    .Select(x => x.Name ?? x.SourceId.ToString()).Distinct().ToArray())
        };
        outputs.AddRange(plannedSets.Select(x => new PlannedOutput(x.OutputArchive,
            "ObjectSetArchive", "required", new[]{x.Provenance.LogicalPath})));
        outputs.AddRange(plannedSets.SelectMany(x => x.SkinParameters).Select(x =>
            new PlannedOutput("rom/skin_param/" + x.OutputFile, "SkinParameter", "required",
                new[]{x.ObjectProvenance.LogicalPath, x.OspProvenance?.LogicalPath ?? "derived"})));
        if (module.Presentation.SpriteArchives.Count > 0 && binding.TargetId.HasValue)
        {
            outputs.Add(new PlannedOutput("rom/2d/mod_spr_db.bin", "SpriteDatabase", "required",
                module.Presentation.SpriteArchives));
            outputs.Add(new PlannedOutput($"rom/2d/spr_sel_md{binding.TargetId:D3}cmn.farc",
                "ModuleSprite", "required", module.Presentation.SpriteArchives));
        }
        bool hasErrors = diagnostics.Any(x => x.Severity == "error") ||
                         plannedSets.SelectMany(x => x.SkinParameters).Any(x => !x.Validation.IsValid) ||
                         binding.Status is "conflict" or "unresolved";
        return new MegaMixBuildPlan(binding, plannedSets, outputs,
            hasErrors ? "blocked" : "ready", diagnostics);
    }
}
