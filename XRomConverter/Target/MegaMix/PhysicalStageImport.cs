using System.Text.Json;
using XRomConverter.Analysis;
using XRomConverter.Ids;

namespace XRomConverter.Target.MegaMix;

internal sealed record PhysicalStageImport(string Root, IReadOnlyList<ReferenceObjectSetManifest> Sets,
    IReadOnlyList<StagedFile> Assets)
{
    public static PhysicalStageImport Prepare(string path, ModuleAnalysisResult analysis, byte[] analysisBytes)
    {
        string root = Path.GetFullPath(path);
        IReadOnlyList<ReferenceObjectSetManifest> sets;
        IReadOnlyList<StagedFile> files;
        string compositionPath = Path.Combine(root, "physical-composition-manifest.json");
        if (File.Exists(compositionPath))
        {
            var composition = JsonSerializer.Deserialize<PhysicalCompositionManifest>(File.ReadAllBytes(compositionPath), PhysicalOccupancy.Json)
                ?? throw new InvalidDataException("Empty physical composition manifest.");
            if (composition.Phase != "composed-reference-objectsets-staging" || composition.Playable || !composition.DatabasesMerged ||
                composition.CanonicalId != analysis.NormalizedModule.Identity?.CanonicalId || composition.Sources.Count == 0)
                throw new InvalidDataException("Physical composition identity or validation does not match the analytical input.");
            sets = composition.Sources.Select(x => x.Manifest).ToArray();
            files = composition.EmittedFiles;
        }
        else
        {
            var manifest = JsonSerializer.Deserialize<ReferenceObjectSetManifest>(File.ReadAllBytes(Path.Combine(root, "physical-stage-manifest.json")), PhysicalOccupancy.Json)
                ?? throw new InvalidDataException("Empty physical stage manifest.");
            sets = new[] { manifest };
            files = manifest.EmittedFiles;
        }
        if (sets.GroupBy(x => x.SourceSet, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() != 1) ||
            sets.GroupBy(x => x.TargetSet, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() != 1) ||
            sets.GroupBy(x => x.TargetSetId).Any(x => x.Count() != 1))
            throw new InvalidDataException("Physical composition set bindings are ambiguous.");
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "rom/objset/mod_obj_db.bin", "rom/objset/mod_tex_db.bin" };
        foreach (var manifest in sets)
        {
            if (manifest.Phase != "explicit-reference-objectset-staging" || manifest.Playable ||
                manifest.CanonicalId != analysis.NormalizedModule.Identity?.CanonicalId || !manifest.ModelRoundTripValid ||
                !manifest.TexturePayloadByteIdentical || !manifest.SkinParameterByteIdentical)
                throw new InvalidDataException("Physical stage identity or validation does not match the analytical input.");
            var plan = analysis.BuildPlan.ObjectSets.SingleOrDefault(x => x.SourceName == manifest.SourceSet)
                ?? throw new InvalidDataException("Physical stage has no source-set plan.");
            if (plan.TargetName != manifest.TargetSet || plan.PhysicalSelection == null ||
                JsonSerializer.Serialize(plan.PhysicalSelection, PhysicalOccupancy.Json) != JsonSerializer.Serialize(manifest.Selection, PhysicalOccupancy.Json))
                throw new InvalidDataException("Physical stage bindings differ from the build plan.");
            expected.Add(plan.OutputArchive);
            foreach (var skin in plan.SkinParameters) expected.Add("rom/skin_param/" + skin.OutputFile);
        }
        if (analysis.BuildPlan.PresentationSelection is { } presentation)
        { expected.Add(presentation.OutputArchive); expected.Add("rom/2d/mod_spr_db.bin"); }
        if (analysis.BuildPlan.OsageSettingSelection is { } osage) expected.Add(osage.OutputPath);
        if (files.GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() != 1))
            throw new InvalidDataException("Duplicate physical stage output path.");
        foreach (var file in files)
        {
            string source = SafePath(root, file.Path);
            if (!File.Exists(source) || PhysicalOccupancy.Hash(File.ReadAllBytes(source)) != file.Sha256)
                throw new InvalidDataException("Physical stage file changed or is missing: " + file.Path);
        }
        var planFile = files.SingleOrDefault(x => x.Path == "analysis-with-physical-bindings.json")
            ?? throw new InvalidDataException("Physical stage has no transported analysis.");
        if (PhysicalOccupancy.Hash(analysisBytes) != planFile.Sha256)
            throw new InvalidDataException("The analysis is not the one transported by the physical stage.");
        var assets = files.Where(x => x.Path.StartsWith("rom/", StringComparison.Ordinal)).ToArray();
        if (!expected.SetEquals(assets.Select(x => x.Path)))
            throw new InvalidDataException("Physical stage asset paths differ from the selected plan.");
        return new(root, sets, assets);
    }

    public void CopyTo(string output, List<StagedFile> emitted)
    {
        foreach (var file in Assets)
        {
            byte[] bytes = File.ReadAllBytes(SafePath(Root, file.Path));
            if (PhysicalOccupancy.Hash(bytes) != file.Sha256) throw new InvalidDataException("Physical asset changed during import: " + file.Path);
            string destination = SafePath(output, file.Path);
            if (File.Exists(destination)) throw new InvalidDataException("Physical asset conflicts with another output: " + file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, bytes);
            emitted.Add(new(file.Path, "VerifiedPhysicalReference", file.Sha256));
        }
    }

    public IReadOnlyList<PlannedOutput> Pending(ModuleAnalysisResult result, IReadOnlyList<StagedFile> emitted)
    {
        var paths = emitted.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var coveredSets = Sets.Select(x => x.TargetSet).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var coveredTextures = Sets.SelectMany(x => x.Selection.Textures).Select(x => x.SourceName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = new List<PlannedOutput>();
        foreach (var required in result.BuildPlan.RequiredOutputs)
        {
            if (required.Kind is "ObjectDatabase" or "TextureDatabase")
            {
                var covered = required.Kind == "ObjectDatabase" ? coveredSets : coveredTextures;
                string[] remaining = required.Inputs.Where(x => !covered.Contains(x)).ToArray();
                if (remaining.Length > 0)
                    pending.Add(required with { Status = paths.Contains(required.Path) ? "partial" : required.Status, Inputs = remaining });
            }
            else if (!paths.Contains(required.Path.Replace('\\', '/'))) pending.Add(required);
        }
        return pending;
    }

    private static string SafePath(string root, string relative)
    {
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (Path.IsPathRooted(relative) || !full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsafe physical stage path: " + relative);
        return full;
    }
}
