using System.Text.Json;
using System.Text.RegularExpressions;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.Archives.CriMw;
using MikuMikuLibrary.Bones;
using MikuMikuLibrary.Databases;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Objects;
using MikuMikuLibrary.Objects.Extra.Blocks;
using MikuMikuLibrary.Textures;
using XRomConverter.Analysis;
using XRomConverter.Ids;
using XRomConverter.Transformations.Skin;

namespace XRomConverter.Target.MegaMix;

internal static class ModuleCandidateValidationCommand
{
    public static bool IsRequested(string[] args) => args.Length > 0 && args[0] == "validate-module-candidate";
    public static int Run(string[] args)
    {
        var required = new HashSet<string> { "--analysis", "--stage", "--physical-inventory", "--output" };
        var updateOptions = new HashSet<string> { "--update-mod", "--prior-stage", "--metadata-inventory", "--current-physical-inventory" };
        var allowed = required.Concat(updateOptions).ToHashSet();
        var options = new Dictionary<string, string>();
        for (int i = 1; i < args.Length; i += 2)
            if (i + 1 >= args.Length || !allowed.Contains(args[i]) || !options.TryAdd(args[i], args[i + 1])) return 64;
        if (!required.IsSubsetOf(options.Keys) || updateOptions.Count(x => options.ContainsKey(x)) is not (0 or 4)) return 64;
        bool updating = options.ContainsKey("--update-mod");
        string? verifiedUpdateRoot = null;
        var errors = new List<string>();
        var checks = new List<object>();
        int setsChecked = 0, textureSlots = 0, skinParameters = 0, skinFields = 0, coreBoneBindings = 0, collisionBones = 0;
        try
        {
            string root = Path.GetFullPath(options["--stage"]);
            byte[] bytes = File.ReadAllBytes(options["--analysis"]);
            var analysis = JsonSerializer.Deserialize<ModuleAnalysisResult>(bytes, PhysicalOccupancy.Json)!;
            var manifest = JsonSerializer.Deserialize<MegaMixStagingManifest>(File.ReadAllBytes(Path.Combine(root, "build-manifest.json")), PhysicalOccupancy.Json)!;
            if (manifest.AnalysisSha256 != PhysicalOccupancy.Hash(bytes) || manifest.CanonicalId != analysis.NormalizedModule.Identity?.CanonicalId ||
                manifest.TargetModuleId != analysis.BuildPlan.ModuleBinding.TargetId || manifest.Playable || manifest.PendingOutputs.Count != 0 ||
                analysis.BuildPlan.ObjectSets.Any(x => x.PhysicalSelection == null) || analysis.BuildPlan.TableSelection == null ||
                analysis.BuildPlan.PresentationSelection == null || analysis.BuildPlan.OsageSettingSelection == null)
                throw new InvalidDataException("A fully covered, disabled reference candidate with matching analysis and explicit bindings is required.");
            var emittedPaths = manifest.EmittedFiles.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (analysis.BuildPlan.RequiredOutputs.Any(x => !emittedPaths.Contains(x.Path)))
                throw new InvalidDataException("Candidate does not declare every required output in the analytical plan.");
            foreach (var file in manifest.EmittedFiles)
            {
                string path = Path.GetFullPath(Path.Combine(root, file.Path));
                if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(path) || PhysicalOccupancy.Hash(File.ReadAllBytes(path)) != file.Sha256)
                    throw new InvalidDataException("Candidate output hash/path differs: " + file.Path);
            }
            if (!Regex.IsMatch(File.ReadAllText(Path.Combine(root, "config.toml")), @"(?m)^enabled\s*=\s*false\s*$"))
                throw new InvalidDataException("The controlled candidate must remain disabled.");
            var bindings = CharacterItemTargetResolver.Resolve(analysis, manifest.ObjectSetIds);
            var module = MegaMixStagingWriterCommand.ValidateModuleTable(Path.Combine(root, "rom/gm_module_tbl.farc"), manifest.TargetModuleId,
                MegaMixStagingWriterCommand.NormalizeCharacter(analysis.NormalizedModule.Character),
                "COS_" + (CharacterItemTargetResolver.CostumeId(analysis) + 1), analysis.NormalizedModule.Identity!.DisplayName, analysis.Aggregate.Module.SortIndex);
            var table = MegaMixStagingWriterCommand.ValidateCharacterItemTable(Path.Combine(root, "rom/mod_chritm_prop.farc"),
                analysis.NormalizedModule.Character.ToLowerInvariant() + "itm_tbl.txt", CharacterItemTargetResolver.CostumeId(analysis),
                analysis.NormalizedModule.Components.OrderBy(x => x.SlotIndex).ToArray(), bindings);
            var links = CharacterItemLinkValidator.Validate(root, analysis, bindings,
                analysis.BuildPlan.ObjectSets.Select(x => x.TargetName).ToHashSet(StringComparer.OrdinalIgnoreCase));
            if (!module.Valid || !table.Valid || !links.Complete) throw new InvalidDataException("Candidate module/costume/item/model chain is incomplete or invalid.");
            if (manifest.RegistrationReference is { } registration)
                ModuleTableReference.Validate(Path.Combine(root, "rom/gm_module_tbl.farc"), registration, analysis);
            SupplementalAssetValidator.Validate(root, analysis);
            var meta = analysis.BuildPlan.TableSelection;
            if (PhysicalOccupancy.Hash(File.ReadAllBytes(meta.OccupancyPath)) != meta.OccupancySha256 ||
                PhysicalOccupancy.Hash(File.ReadAllBytes(meta.ReservationsPath)) != meta.ReservationsSha256)
                throw new InvalidDataException("Selected metadata occupancy/reservations changed.");
            // An installed previous candidate now occupies its own IDs. For an
            // explicit update, keep the historical allocation hashes as evidence
            // and check fresh inventories, exempting only a byte-verified copy.
            var occupied = MetadataOccupancy.LoadFresh(updating ? options["--metadata-inventory"] : meta.OccupancyPath);
            var physical = PhysicalOccupancy.LoadFresh(updating ? options["--current-physical-inventory"] : options["--physical-inventory"]);
            if (occupied.Installation != physical.Installation || analysis.BuildPlan.ObjectSets.Any(x =>
                x.PhysicalSelection!.OccupancySha256 != PhysicalOccupancy.Hash(File.ReadAllBytes(options["--physical-inventory"]))))
                throw new InvalidDataException("Physical and table bindings do not share the verified installation inventory.");
            if (updating)
            {
                var historicalMetadata = JsonSerializer.Deserialize<MetadataOccupancySnapshot>(File.ReadAllBytes(meta.OccupancyPath), PhysicalOccupancy.Json)!;
                var historicalPhysical = JsonSerializer.Deserialize<PhysicalOccupancySnapshot>(File.ReadAllBytes(options["--physical-inventory"]), PhysicalOccupancy.Json)!;
                if (historicalMetadata.Installation != occupied.Installation || historicalPhysical.Installation != physical.Installation)
                    throw new InvalidDataException("Update inventories belong to a different installation.");
                verifiedUpdateRoot = VerifyInstalledPrior(options["--update-mod"], options["--prior-stage"], occupied.Installation, root, manifest);
            }
            bool OwnSource(PhysicalDatabaseSource source) => verifiedUpdateRoot != null && source.Entry == null &&
                Path.GetFullPath(source.Path).StartsWith(verifiedUpdateRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            var selectedIds = new List<(string Namespace, string Scope, uint Id)>
            { ("module", "global", (uint)manifest.TargetModuleId), ("costume", meta.Character, (uint)meta.TargetCostumeId),
              ("sprite-set", "global", analysis.BuildPlan.PresentationSelection.SetId),
              ("sprite", "global", analysis.BuildPlan.PresentationSelection.SpriteId),
              ("sprite-texture", "global", analysis.BuildPlan.PresentationSelection.TextureId) };
            selectedIds.AddRange(meta.Items.Select(x => ("item", meta.Character, (uint)x.TargetItemNumber)));
            if (selectedIds.Any(a => occupied.Ids.Any(x => x.Namespace == a.Namespace && x.Scope == a.Scope && x.Id == a.Id && !OwnSource(occupied.Sources[x.Source]))) ||
                ModuleArchiveIdCatalog.LoadOccupiedModuleIds(meta.ReservationsPath).Contains(manifest.TargetModuleId))
                throw new InvalidDataException("Candidate table/sprite allocation is occupied or reserved.");
            var textureDb = BinaryFile.Load<TextureDatabase>(Path.Combine(root, "rom/objset/mod_tex_db.bin"));
            if (textureDb.Textures.Select(x => x.Id).Distinct().Count() != textureDb.Textures.Count ||
                textureDb.Textures.Select(x => x.Name.ToUpperInvariant()).Distinct().Count() != textureDb.Textures.Count)
                throw new InvalidDataException("Candidate texture database has duplicate IDs/names.");
            using var cpk = BinaryFile.Load<CpkArchive>(Path.Combine(occupied.Installation, "diva_main.cpk"));
            string boneEntry = cpk.FileNames.Single(x => x.EndsWith("rom/bone_data.bin", StringComparison.OrdinalIgnoreCase));
            var boneData = cpk.Open<BoneData>(boneEntry);
            var skeleton = boneData.Skeletons.Single(x => x.Name.Equals(meta.Character, StringComparison.OrdinalIgnoreCase));
            var knownBones = skeleton.Bones.Select(x => x.Name).Concat(skeleton.ObjectBoneNames).Concat(skeleton.MotionBoneNames).ToHashSet(StringComparer.Ordinal);
            foreach (var plan in analysis.BuildPlan.ObjectSets)
            {
                var selection = plan.PhysicalSelection!;
                if (physical.ObjectSets.Any(x => (x.Id == selection.TargetSetId || x.Name.Equals(plan.TargetName, StringComparison.OrdinalIgnoreCase)) && !OwnSource(physical.Sources[x.Source])) ||
                    selection.Textures.Any(t => physical.Textures.Any(x => (x.Id == t.TargetId || x.Name.Equals(t.TargetName, StringComparison.OrdinalIgnoreCase)) && !OwnSource(physical.Sources[x.Source]))))
                    throw new InvalidDataException("Candidate physical ID/name is occupied.");
                using var archive = BinaryFile.Load<FarcArchive>(Path.Combine(root, plan.OutputArchive));
                var model = archive.Open<ObjectSet>(archive.FileNames.Single(x => x.EndsWith("_obj.bin")));
                var textures = archive.Open<TextureSet>(archive.FileNames.Single(x => x.EndsWith("_tex.bin")));
                if (model.Objects.Count != selection.Objects.Count || !model.TextureIds.SequenceEqual(selection.Textures.OrderBy(x => x.Slot).Select(x => x.TargetId)) ||
                    textures.Textures.Count != model.TextureIds.Count || selection.Textures.Any(t => textureDb.Textures.Count(x => x.Id == t.TargetId && x.Name == t.TargetName) != 1) ||
                    model.Objects.SelectMany(x => x.Materials).SelectMany(x => x.MaterialTextures).Any(t => t != null && t.TextureId != uint.MaxValue && !model.TextureIds.Contains(t.TextureId)))
                    throw new InvalidDataException("Candidate model/material/positional texture linkage differs.");
                foreach (var obj in model.Objects)
                {
                    if (obj.Skin?.Blocks.OfType<MotionBlock>().Any() == true) throw new InvalidDataException("Candidate still contains MotionBlocks.");
                    foreach (var bone in obj.Skin?.Bones.Where(x => !x.IsEx) ?? Enumerable.Empty<MikuMikuLibrary.Objects.BoneInfo>())
                    {
                        int expected = skeleton.ObjectBoneNames.IndexOf(bone.Name);
                        if (expected < 0 || bone.Id != (uint)expected) throw new InvalidDataException("Base skeleton ID/name differs at " + bone.Name);
                        coreBoneBindings++;
                    }
                    var objectBones = new HashSet<string>(knownBones, StringComparer.Ordinal);
                    foreach (var name in obj.Skin?.Bones.Select(x => x.Name) ?? Enumerable.Empty<string>()) objectBones.Add(name);
                    var skin = plan.SkinParameters.SingleOrDefault(x => x.TargetObject == obj.Name);
                    var osgs = obj.Skin?.Blocks.OfType<OsageBlock>().ToArray() ?? Array.Empty<OsageBlock>();
                    if (osgs.Length == 0) continue;
                    if (skin?.ReferenceSelection == null) throw new InvalidDataException("Physical object has no selected parameter reference.");
                    byte[] skinBytes = File.ReadAllBytes(Path.Combine(root, "rom/skin_param/" + skin.OutputFile));
                    if (PhysicalOccupancy.Hash(skinBytes) != skin.ReferenceSelection.ReferenceSha256) throw new InvalidDataException("Selected skin parameter bytes changed.");
                    var parsed = SkinParameterTextCodec.Parse(skinBytes, "candidate-validation");
                    var normalized = parsed.Parameters.Select(x => new NormalizedSkinParameter(x.Name, x.BocTargets, x.CollisionTarget, x.NodeCount,
                        skin.ObjectProvenance, false, x.Physical)).ToArray();
                    var blocks = osgs.Select(x => new NormalizedSkinBlock("OSG", x.Name, x.ExternalName, x.ParentName, x.Nodes.Select(n => n.Name).ToArray(), false)).ToArray();
                    if (!new SkinParameterValidator().Validate(blocks, normalized).IsValid) throw new InvalidDataException("Candidate physical parameter topology is invalid.");
                    foreach (var field in parsed.Parameters.SelectMany(x => x.Physical!.Fields).Where(x => Regex.IsMatch(x.Key, @"^root\.coli\.\d+\.bone\.\d+\.name$") && x.Value != ""))
                    {
                        if (!objectBones.Contains(field.Value)) throw new InvalidDataException("Collision bone is absent from model/base skeleton: " + field.Value);
                        collisionBones++;
                    }
                    skinParameters += parsed.Parameters.Count; skinFields += parsed.Parameters.Sum(x => x.Physical!.Fields.Count);
                }
                setsChecked++; textureSlots += textures.Textures.Count;
            }
            if (textureDb.Textures.Count != analysis.BuildPlan.ObjectSets.Sum(x => x.PhysicalSelection!.Textures.Count))
                throw new InvalidDataException("Texture database contains missing or unrelated declarations.");
            checks.Add(new { module, table, links, allocatedIds = selectedIds.Select(x => new { x.Namespace, x.Scope, x.Id }),
                occupancyScope = "all-discovered-declarations-including-disabled-mods", verifiedUpdateOf = verifiedUpdateRoot, baseSkeleton = skeleton.Name });
        }
        catch (Exception ex) { errors.Add(ex.Message); }
        var report = new { valid = errors.Count == 0, playable = false, setsChecked, textureSlots, skinParameters, skinFields,
            coreBoneBindings, collisionBoneReferences = collisionBones, checks, errors,
            runtimePending = "Gameplay, loader resolution and visual physics/face verification have not been performed for this candidate." };
        string pathOutput = Path.GetFullPath(options["--output"]); Directory.CreateDirectory(Path.GetDirectoryName(pathOutput)!);
        File.WriteAllBytes(pathOutput, JsonSerializer.SerializeToUtf8Bytes(report, PhysicalOccupancy.Json)); Console.WriteLine(pathOutput);
        return errors.Count == 0 ? 0 : 2;
    }

    private static string VerifyInstalledPrior(string installed, string priorStage, string installation,
        string currentRoot, MegaMixStagingManifest current)
    {
        string mod = Path.GetFullPath(installed).TrimEnd(Path.DirectorySeparatorChar);
        string mods = Path.GetFullPath(Path.Combine(installation, "mods"));
        if (!string.Equals(Path.GetDirectoryName(mod), mods, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(mod))
            throw new InvalidDataException("Update target must be one installed mod directory.");
        string prior = Path.GetFullPath(priorStage);
        string Safe(string root, string relative)
        {
            string full = Path.GetFullPath(Path.Combine(root, relative));
            if (Path.IsPathRooted(relative) || !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsafe prior candidate path.");
            return full;
        }
        byte[] manifestBytes = File.ReadAllBytes(Path.Combine(prior, "build-manifest.json"));
        var previous = JsonSerializer.Deserialize<MegaMixStagingManifest>(manifestBytes, PhysicalOccupancy.Json)!;
        if (previous.CanonicalId != current.CanonicalId || previous.TargetModuleId != current.TargetModuleId ||
            previous.AnalysisSha256 != current.AnalysisSha256 || previous.Playable || previous.PendingOutputs.Count != 0 ||
            previous.EmittedFiles.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != previous.EmittedFiles.Count)
            throw new InvalidDataException("Prior candidate identity/analysis differs from the controlled update.");
        var expectedFiles = previous.EmittedFiles.Select(x => x.Path).Append("build-manifest.json").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualFiles = Directory.EnumerateFiles(mod, "*", SearchOption.AllDirectories)
            .Select(x => Path.GetRelativePath(mod, x).Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!expectedFiles.SetEquals(actualFiles) || PhysicalOccupancy.Hash(File.ReadAllBytes(Path.Combine(mod, "build-manifest.json"))) != PhysicalOccupancy.Hash(manifestBytes))
            throw new InvalidDataException("Installed mod file list/manifest differs from the prior candidate.");
        string Config(byte[] bytes) => Regex.Replace(System.Text.Encoding.UTF8.GetString(bytes),
            @"(?m)^enabled[ \t]*=[ \t]*(true|false)[ \t]*\r?$", "enabled = false");
        foreach (var file in previous.EmittedFiles)
        {
            byte[] historical = File.ReadAllBytes(Safe(prior, file.Path));
            byte[] existing = File.ReadAllBytes(Safe(mod, file.Path));
            if (PhysicalOccupancy.Hash(historical) != file.Sha256 ||
                (file.Path == "config.toml" ? Config(existing) != Config(historical) : PhysicalOccupancy.Hash(existing) != file.Sha256))
                throw new InvalidDataException("Installed prior candidate file differs: " + file.Path);
            if (file.Path.StartsWith("rom/", StringComparison.Ordinal) && file.Path != "rom/gm_module_tbl.farc" &&
                PhysicalOccupancy.Hash(File.ReadAllBytes(Safe(currentRoot, file.Path))) != file.Sha256)
                throw new InvalidDataException("Controlled registration update changed a prior ROM asset: " + file.Path);
        }
        return mod;
    }
}
