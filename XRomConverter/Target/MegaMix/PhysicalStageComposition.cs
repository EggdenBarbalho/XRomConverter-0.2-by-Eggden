using System.Text.Json;
using MikuMikuLibrary.Databases;
using MikuMikuLibrary.IO;
using XRomConverter.Analysis;
using XRomConverter.Ids;

namespace XRomConverter.Target.MegaMix;

internal sealed record ComposedPhysicalSource(string StagePath, string ManifestSha256, ReferenceObjectSetManifest Manifest);
internal sealed record PhysicalCompositionManifest(string Phase, bool Playable, string CanonicalId,
    IReadOnlyList<ComposedPhysicalSource> Sources, bool DatabasesMerged, IReadOnlyList<StagedFile> EmittedFiles);

internal static class PhysicalStageCompositionCommand
{
    public static bool IsRequested(string[] args) => args.Length > 0 && args[0] == "compose-physical-stages";

    public static int Run(string[] args)
    {
        var stages = new List<string>();
        string? output = null;
        for (int i = 1; i < args.Length; i++)
            if (args[i] == "--stage" && i + 1 < args.Length) stages.Add(Path.GetFullPath(args[++i]));
            else if (args[i] == "--output" && i + 1 < args.Length && output == null) output = Path.GetFullPath(args[++i]);
            else return 64;
        if (stages.Count == 0 || output == null || stages.Distinct(StringComparer.OrdinalIgnoreCase).Count() != stages.Count) return 64;
        if (Directory.Exists(output) || File.Exists(output))
        { Console.Error.WriteLine("Output already exists; refusing to overwrite: " + output); return 2; }
        string? staging = null;
        try
        {
            var inputs = stages.Select(path =>
            {
                byte[] bytes = File.ReadAllBytes(Path.Combine(path, "analysis-with-physical-bindings.json"));
                var analysis = JsonSerializer.Deserialize<ModuleAnalysisResult>(bytes, PhysicalOccupancy.Json)
                    ?? throw new InvalidDataException("Empty input analysis.");
                var imported = PhysicalStageImport.Prepare(path, analysis, bytes);
                if (imported.Sets.Count != 1) throw new InvalidDataException("Compose individual reference stages, not a composition.");
                return (Analysis: analysis, Import: imported);
            }).ToArray();
            var seed = inputs[0].Analysis;
            string identity = seed.NormalizedModule.Identity!.CanonicalId;
            string seedModule = JsonSerializer.Serialize(new { seed.Aggregate, seed.NormalizedModule, seed.BuildPlan.ModuleBinding }, PhysicalOccupancy.Json);
            if (inputs.Any(x => JsonSerializer.Serialize(new { x.Analysis.Aggregate, x.Analysis.NormalizedModule,
                x.Analysis.BuildPlan.ModuleBinding }, PhysicalOccupancy.Json) != seedModule))
                throw new InvalidDataException("Reference stages do not share the same source module and binding.");
            var selected = inputs.Select(x => x.Analysis.BuildPlan.ObjectSets.Single(p => p.SourceName == x.Import.Sets.Single().SourceSet)).ToArray();
            if (selected.GroupBy(x => x.SourceName, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() != 1) ||
                selected.GroupBy(x => x.TargetName, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() != 1) ||
                selected.GroupBy(x => x.PhysicalSelection!.TargetSetId).Any(x => x.Count() != 1))
                throw new InvalidDataException("Composed object sets collide.");
            var replacements = selected.ToDictionary(x => x.SourceName, StringComparer.OrdinalIgnoreCase);
            if (selected.Any(x => !seed.BuildPlan.ObjectSets.Any(p => p.SourceName == x.SourceName)))
                throw new InvalidDataException("Composed set is outside the source module plan.");
            var rewritten = seed.BuildPlan.RequiredOutputs.Select(required =>
            {
                if (required.Kind == "ObjectDatabase") return required with { Inputs = required.Inputs.Select(name =>
                    seed.BuildPlan.ObjectSets.FirstOrDefault(p => p.TargetName == name) is { } old && replacements.TryGetValue(old.SourceName, out var newer)
                        ? newer.TargetName : name).ToArray() };
                foreach (var old in seed.BuildPlan.ObjectSets)
                {
                    if (!replacements.TryGetValue(old.SourceName, out var newer)) continue;
                    if (required.Path == old.OutputArchive) return required with { Path = newer.OutputArchive };
                    foreach (var oldSkin in old.SkinParameters)
                        if (required.Path == "rom/skin_param/" + oldSkin.OutputFile)
                        {
                            var newerSkin = newer.SkinParameters.Single(x => x.SourceObject == oldSkin.SourceObject);
                            return required with { Path = "rom/skin_param/" + newerSkin.OutputFile };
                        }
                }
                return required;
            }).ToArray();
            var mergedAnalysis = seed with { BuildPlan = seed.BuildPlan with
            { ObjectSets = seed.BuildPlan.ObjectSets.Select(x => replacements.GetValueOrDefault(x.SourceName) ?? x).ToArray(), RequiredOutputs = rewritten } };
            string parent = Path.GetDirectoryName(output)!;
            Directory.CreateDirectory(parent);
            staging = Path.Combine(parent, "." + Path.GetFileName(output) + ".staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(staging, "rom/objset"));
            var emitted = new List<StagedFile>();
            void Write(string relative, byte[] bytes)
            {
                string path = Path.Combine(staging, relative);
                string full = Path.GetFullPath(path);
                if (Path.IsPathRooted(relative) || !full.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || File.Exists(full))
                    throw new InvalidDataException("Duplicate or unsafe composition output: " + relative);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, bytes);
                emitted.Add(new(relative, "ComposedPhysicalReference", PhysicalOccupancy.Hash(bytes)));
            }
            var objects = new ObjectDatabase { Format = BinaryFormat.DT };
            var textures = new TextureDatabase { Format = BinaryFormat.DT };
            foreach (var input in inputs)
            {
                foreach (var file in input.Import.Assets.Where(x => x.Path is not ("rom/objset/mod_obj_db.bin" or "rom/objset/mod_tex_db.bin")))
                {
                    byte[] bytes = File.ReadAllBytes(Path.Combine(input.Import.Root, file.Path));
                    if (PhysicalOccupancy.Hash(bytes) != file.Sha256) throw new InvalidDataException("Reference changed during composition.");
                    Write(file.Path, bytes);
                }
                objects.ObjectSets.AddRange(BinaryFile.Load<ObjectDatabase>(Path.Combine(input.Import.Root, "rom/objset/mod_obj_db.bin")).ObjectSets);
                textures.Textures.AddRange(BinaryFile.Load<TextureDatabase>(Path.Combine(input.Import.Root, "rom/objset/mod_tex_db.bin")).Textures);
            }
            if (objects.ObjectSets.Count != selected.Length || objects.ObjectSets.Select(x => x.Id).Distinct().Count() != selected.Length ||
                objects.ObjectSets.Select(x => x.Name.ToUpperInvariant()).Distinct().Count() != selected.Length ||
                textures.Textures.Select(x => x.Id).Distinct().Count() != textures.Textures.Count ||
                textures.Textures.Select(x => x.Name.ToUpperInvariant()).Distinct().Count() != textures.Textures.Count)
                throw new InvalidDataException("Merged object/texture declarations collide or are incomplete.");
            foreach (var plan in selected)
            {
                var set = objects.ObjectSets.Single(x => x.Name == plan.TargetName);
                var selection = plan.PhysicalSelection!;
                if (set.Id != selection.TargetSetId || set.Objects.Count != selection.Objects.Count ||
                    selection.Objects.Any(x => !set.Objects.Any(o => o.Id == x.TargetId && o.Name.Equals(x.TargetName, StringComparison.OrdinalIgnoreCase))) ||
                    selection.Textures.Any(x => !textures.Textures.Any(t => t.Id == x.TargetId && t.Name == x.TargetName)))
                    throw new InvalidDataException("Merged database differs from the explicit physical selections.");
            }
            using (var stream = new MemoryStream()) { objects.Save(stream, leaveOpen: true); Write("rom/objset/mod_obj_db.bin", stream.ToArray()); }
            using (var stream = new MemoryStream()) { textures.Save(stream, leaveOpen: true); Write("rom/objset/mod_tex_db.bin", stream.ToArray()); }
            Write("analysis-with-physical-bindings.json", JsonSerializer.SerializeToUtf8Bytes(mergedAnalysis, PhysicalOccupancy.Json));
            var allocations = inputs.SelectMany(x => JsonSerializer.Deserialize<PhysicalIdRegistryDocument>(
                File.ReadAllBytes(Path.Combine(x.Import.Root, "physical-id-registry.json")), PhysicalOccupancy.Json)!.Allocations)
                .Distinct().OrderBy(x => x.Profile).ThenBy(x => x.Namespace).ThenBy(x => x.Scope).ThenBy(x => x.TargetId).ToArray();
            Write("physical-id-registry.json", JsonSerializer.SerializeToUtf8Bytes(new PhysicalIdRegistryDocument(1, allocations), PhysicalOccupancy.Json));
            _ = new PhysicalIdRegistry(Path.Combine(staging, "physical-id-registry.json"));
            var sources = inputs.Select(x => new ComposedPhysicalSource(x.Import.Root,
                PhysicalOccupancy.Hash(File.ReadAllBytes(Path.Combine(x.Import.Root, "physical-stage-manifest.json"))), x.Import.Sets.Single())).ToArray();
            var manifest = new PhysicalCompositionManifest("composed-reference-objectsets-staging", false, identity, sources, true, emitted);
            File.WriteAllBytes(Path.Combine(staging, "physical-composition-manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, PhysicalOccupancy.Json));
            var reopenedObjects = BinaryFile.Load<ObjectDatabase>(Path.Combine(staging, "rom/objset/mod_obj_db.bin"));
            var reopenedTextures = BinaryFile.Load<TextureDatabase>(Path.Combine(staging, "rom/objset/mod_tex_db.bin"));
            if (!reopenedObjects.ObjectSets.Select(x => (x.Id, x.Name)).SequenceEqual(objects.ObjectSets.Select(x => (x.Id, x.Name))) ||
                !reopenedTextures.Textures.Select(x => (x.Id, x.Name)).SequenceEqual(textures.Textures.Select(x => (x.Id, x.Name))))
                throw new InvalidDataException("Merged databases changed after reopening.");
            PhysicalStageImport.Prepare(staging, mergedAnalysis, File.ReadAllBytes(Path.Combine(staging, "analysis-with-physical-bindings.json")));
            Directory.Move(staging, output); staging = null;
            Console.WriteLine(output); return 0;
        }
        catch (Exception ex)
        {
            if (staging != null)
            {
                string parent = Path.GetDirectoryName(output)!;
                string full = Path.GetFullPath(staging);
                if (full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(full).StartsWith("." + Path.GetFileName(output) + ".staging-", StringComparison.Ordinal) && Directory.Exists(full))
                    Directory.Delete(full, recursive: true);
            }
            Console.Error.WriteLine("Physical composition failed without publishing output: " + ex.Message); return 2;
        }
    }
}
