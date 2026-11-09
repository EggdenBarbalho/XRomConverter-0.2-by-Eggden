using System.Text.Json;
using System.Text.RegularExpressions;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.Databases;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Objects;
using MikuMikuLibrary.Objects.Extra.Blocks;
using MikuMikuLibrary.Textures;
using XRomConverter.Analysis;
using XRomConverter.Ids;
using XRomConverter.Transformations.Skin;

namespace XRomConverter.Target.MegaMix;

internal sealed record ReferenceObjectSetManifest(string Phase, bool Playable, string CanonicalId,
    string SourceSet, string TargetSet, uint TargetSetId, string RegistryProfile, string AnalysisSha256,
    string OccupancySha256, string ArchiveReservationsSha256, PhysicalObjectSetSelection Selection,
    string ReferenceModelCanonicalSha256, string RestoredOutputCanonicalSha256,
    string ReferenceModelRawSha256, string ReversedIdentifierPatchSha256,
    IReadOnlyList<ClassicIdentifierPatch> IdentifierPatches,
    bool ModelRoundTripValid, bool TexturePayloadByteIdentical, bool SkinParameterByteIdentical,
    IReadOnlyList<StagedFile> EmittedFiles);

internal static class ReferenceObjectSetStagingWriterCommand
{
    public static bool IsRequested(string[] args) => args.Length > 0 && args[0] == "write-objectset-stage";

    public static int Run(string[] args)
    {
        var required = new HashSet<string> { "--analysis", "--source-set", "--texture-db", "--inventory",
            "--objset-csv", "--registry", "--profile", "--target-set", "--target-object", "--output" };
        var allowed = required.Concat(new[] { "--model-reference", "--reference-object" }).ToHashSet();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i += 2)
            if (!allowed.Contains(args[i]) || i + 1 >= args.Length || !options.TryAdd(args[i], args[i + 1])) return Usage();
        if (!required.IsSubsetOf(options.Keys) || options.ContainsKey("--model-reference") != options.ContainsKey("--reference-object") ||
            new[] { "--analysis", "--texture-db", "--inventory", "--objset-csv" }
                .Any(x => !File.Exists(options[x]))) return Usage();
        string output = Path.GetFullPath(options["--output"]);
        if (Directory.Exists(output) || File.Exists(output))
        { Console.Error.WriteLine("Output already exists; refusing to overwrite: " + output); return 2; }
        string? staging = null;
        try
        {
            string targetSet = options["--target-set"];
            string targetObject = options["--target-object"];
            string profile = options["--profile"];
            if (new[] { targetSet, targetObject, profile }.Any(x => !Regex.IsMatch(x, @"^[A-Za-z][A-Za-z0-9_-]{0,95}$")))
                throw new InvalidDataException("Target names and profile must be plain identifiers.");
            string analysisPath = Path.GetFullPath(options["--analysis"]);
            string inventoryPath = Path.GetFullPath(options["--inventory"]);
            string textureDbPath = Path.GetFullPath(options["--texture-db"]);
            string registryPath = Path.GetFullPath(options["--registry"]);
            string csvPath = Path.GetFullPath(options["--objset-csv"]);
            if (registryPath.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                new[] { analysisPath, inventoryPath, textureDbPath, csvPath }.Contains(registryPath, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("The registry must be separate from the output and input files.");
            var occupancy = PhysicalOccupancy.LoadFresh(inventoryPath);
            string installationPrefix = Path.GetFullPath(occupancy.Installation).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (output.StartsWith(installationPrefix, StringComparison.OrdinalIgnoreCase) ||
                registryPath.StartsWith(installationPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("This controlled stage requires output and registry outside the game installation.");
            var reservations = ObjectSetArchiveIdCatalog.Load(csvPath).ReservedRows;
            if (occupancy.ObjectSets.Any(x => x.Name.Equals(targetSet, StringComparison.OrdinalIgnoreCase)) ||
                reservations.Any(x => x.Name.Equals(targetSet, StringComparison.OrdinalIgnoreCase)) ||
                occupancy.ObjectSets.SelectMany(x => x.Objects).Any(x => x.Name.Equals(targetObject, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Requested physical target name is already declared: " + targetSet + "/" + targetObject);

            byte[] analysisBytes = File.ReadAllBytes(analysisPath);
            var analysis = JsonSerializer.Deserialize<ModuleAnalysisResult>(analysisBytes, PhysicalOccupancy.Json)
                ?? throw new InvalidDataException("Empty analysis.");
            if (analysis.BuildPlan.Status != "ready" || analysis.NormalizedModule.Identity == null)
                throw new InvalidDataException("A ready analysis with canonical identity is required.");
            var plannedSet = analysis.BuildPlan.ObjectSets.Single(x => x.SourceName == options["--source-set"]);
            var normalizedSet = analysis.NormalizedModule.Components.SelectMany(x => x.ObjectSets)
                .Where(x => x.SourceName == plannedSet.SourceName).First();
            if (normalizedSet.Objects.Count != 1)
                throw new InvalidDataException("This controlled writer requires one analytical source object.");
            string analyticalObject = normalizedSet.Objects.Single().SourceName;
            PlannedSkinParameter? skin = plannedSet.SkinParameters.SingleOrDefault();
            bool modelOnly = options.ContainsKey("--model-reference");
            if (modelOnly ? skin != null : skin?.ReferenceSelection == null)
                throw new InvalidDataException("Use an explicit skin/model reference, or a model-only reference for an object without planned physical blocks.");
            var reference = skin?.ReferenceSelection;
            string referenceArchive = modelOnly ? Path.GetFullPath(options["--model-reference"]) : reference!.TargetArchivePath;
            string expectedReferenceObject = modelOnly ? options["--reference-object"] : skin!.TargetObject;
            string archiveHash = modelOnly ? PhysicalOccupancy.Hash(File.ReadAllBytes(referenceArchive)) : reference!.TargetArchiveSha256;
            if (PhysicalOccupancy.Hash(File.ReadAllBytes(referenceArchive)) != archiveHash)
                throw new InvalidDataException("The selected model reference changed.");
            if (reference != null && PhysicalOccupancy.Hash(File.ReadAllBytes(reference.ReferencePath)) != reference.ReferenceSha256)
                throw new InvalidDataException("The selected skin reference changed.");
            byte[]? skinBytes = reference == null ? null : SkinParameterTextCodec.Serialize(skin!.FinalParameters, reference.Layout);
            if (skinBytes != null && PhysicalOccupancy.Hash(skinBytes) != reference!.ReferenceSha256)
                throw new InvalidDataException("The analysis no longer reproduces the selected skin reference.");

            using var archive = BinaryFile.Load<FarcArchive>(referenceArchive);
            string objectEntry = archive.FileNames.Single(x => x.EndsWith("_obj.bin", StringComparison.OrdinalIgnoreCase));
            string textureEntry = archive.FileNames.Single(x => x.EndsWith("_tex.bin", StringComparison.OrdinalIgnoreCase));
            var model = archive.Open<ObjectSet>(objectEntry);
            using var objectInput = archive.Open(objectEntry, EntryStreamMode.MemoryStream);
            using var objectBuffer = new MemoryStream(); objectInput.CopyTo(objectBuffer);
            byte[] objectBytes = objectBuffer.ToArray();
            var textureSet = archive.Open<TextureSet>(textureEntry);
            using var textureInput = archive.Open(textureEntry, EntryStreamMode.MemoryStream);
            using var textureBuffer = new MemoryStream();
            textureInput.CopyTo(textureBuffer);
            byte[] textureBytes = textureBuffer.ToArray();
            if (!model.Format.IsClassic() || model.Objects.Count != 1 || model.Objects[0].Name != expectedReferenceObject)
                throw new InvalidDataException("Reference object set is not the expected single classic object.");
            if (skin != null) ValidateTopology(model.Objects[0], skin);
            else ValidateModelOnly(model.Objects[0], normalizedSet.Objects.Single());
            if (model.TextureIds.Count != textureSet.Textures.Count || model.TextureIds.Distinct().Count() != model.TextureIds.Count)
                throw new InvalidDataException("The positional texture binding is incomplete or duplicated.");
            var textureDatabase = BinaryFile.Load<TextureDatabase>(textureDbPath);
            var textureNames = model.TextureIds.Select(id =>
            {
                var matches = textureDatabase.Textures.Where(x => x.Id == id).ToArray();
                if (matches.Length != 1 || string.IsNullOrWhiteSpace(matches[0].Name))
                    throw new InvalidDataException("Texture ID has no unique named reference: " + id);
                return matches[0].Name;
            }).ToArray();
            if (textureNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != textureNames.Length)
                throw new InvalidDataException("Reference texture names are duplicated.");
            foreach (uint id in MaterialIds(model))
                if (!model.TextureIds.Contains(id)) throw new InvalidDataException("Material texture is outside the positional binding: " + id);

            string originalName = model.Objects[0].Name;
            uint originalId = model.Objects[0].Id;
            uint[] originalTextureIds = model.TextureIds.ToArray();
            string referenceCanonical = PhysicalOccupancy.Hash(SaveModel(model));
            Directory.CreateDirectory(Path.GetDirectoryName(registryPath)!);
            // Shared lock covers read, planning, validation and atomic registry commit.
            using var registryLock = new FileStream(registryPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var registry = new PhysicalIdRegistry(registryPath);
            string identityKey = "canonical:" + analysis.NormalizedModule.Identity.CanonicalId + "/set:" + plannedSet.SourceName;
            uint setId = registry.Allocate(profile, "object-set", "global", identityKey, targetSet,
                occupancy.ObjectSets.Select(x => x.Id).Concat(reservations.Select(x => x.Id)).ToHashSet(), 40000, ushort.MaxValue);
            uint objectId = registry.Allocate(profile, "object", identityKey, identityKey + "/object:" + analyticalObject,
                targetObject, occupancy.ObjectSets.Where(x => x.Id == setId).SelectMany(x => x.Objects).Select(x => x.Id).ToHashSet(), 0, ushort.MaxValue);
            var textureBindings = new List<PhysicalTextureBinding>();
            for (int slot = 0; slot < originalTextureIds.Length; slot++)
            {
                string name = targetSet.ToUpperInvariant() + "__" + textureNames[slot];
                if (occupancy.Textures.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Target texture name is already declared: " + name);
                uint id = registry.Allocate(profile, "texture", "global", identityKey + "/texture:" + textureNames[slot], name,
                    occupancy.Textures.Select(x => x.Id).ToHashSet(), 90000, uint.MaxValue - 1);
                textureBindings.Add(new(slot, textureNames[slot], name, originalTextureIds[slot], id));
            }
            var forwardTextures = textureBindings.ToDictionary(x => x.SourceId, x => x.TargetId);
            var patched = ClassicObjectSetIdPatcher.Apply(objectBytes, model, targetObject, objectId, forwardTextures);
            var selection = new PhysicalObjectSetSelection("explicit-converted-model-reference", referenceArchive,
                archiveHash, textureDbPath, PhysicalOccupancy.Hash(File.ReadAllBytes(textureDbPath)),
                PhysicalOccupancy.Hash(File.ReadAllBytes(inventoryPath)), profile, setId,
                new[] { new PhysicalObjectBinding(originalName, targetObject, originalId, objectId, modelOnly ? analyticalObject : null) }, textureBindings);
            string stem = targetSet.ToLowerInvariant();
            string archiveOutput = "rom/objset/" + stem + ".farc";
            string skinOutput = "rom/skin_param/ext_skp_" + targetObject + ".txt";
            var updatedSkin = skin == null ? null : skin with { TargetObject = targetObject, OutputFile = Path.GetFileName(skinOutput) };
            var updatedSet = plannedSet with { TargetName = targetSet, OutputArchive = archiveOutput,
                SkinParameters = updatedSkin == null ? Array.Empty<PlannedSkinParameter>() : new[] { updatedSkin }, PhysicalSelection = selection };
            var updated = analysis with { BuildPlan = analysis.BuildPlan with
            {
                ObjectSets = analysis.BuildPlan.ObjectSets.Select(x => x.SourceName == plannedSet.SourceName ? updatedSet : x).ToArray(),
                RequiredOutputs = analysis.BuildPlan.RequiredOutputs.Select(x => x with
                {
                    Path = x.Path == plannedSet.OutputArchive ? archiveOutput : skin != null && x.Path == "rom/skin_param/" + skin.OutputFile ? skinOutput : x.Path,
                    Inputs = x.Kind == "ObjectDatabase" ? x.Inputs.Select(input => input == plannedSet.TargetName ? targetSet : input).ToArray() : x.Inputs
                }).ToArray()
            } };

            string parent = Path.GetDirectoryName(output)!;
            Directory.CreateDirectory(parent);
            staging = Path.Combine(parent, "." + Path.GetFileName(output) + ".staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(staging, "rom", "objset"));
            if (skinBytes != null) Directory.CreateDirectory(Path.Combine(staging, "rom", "skin_param"));
            string archiveDestination = Path.Combine(staging, archiveOutput);
            using (var emittedArchive = new FarcArchive())
            {
                emittedArchive.Add(stem + "_obj.bin", new MemoryStream(patched.Bytes, writable: false), leaveOpen: false);
                emittedArchive.Add(stem + "_tex.bin", new MemoryStream(textureBytes, writable: false), leaveOpen: false);
                emittedArchive.Save(archiveDestination);
            }
            var objectDatabase = new ObjectDatabase { Format = BinaryFormat.DT };
            var setInfo = new ObjectSetInfo { Name = targetSet, Id = setId, ArchiveFileName = stem + ".farc",
                FileName = stem + "_obj.bin", TextureFileName = stem + "_tex.bin" };
            setInfo.Objects.Add(new ObjectInfo { Name = targetObject.ToUpperInvariant(), Id = objectId });
            objectDatabase.ObjectSets.Add(setInfo);
            objectDatabase.Save(Path.Combine(staging, "rom", "objset", "mod_obj_db.bin"));
            var emittedTextureDb = new TextureDatabase { Format = BinaryFormat.DT };
            emittedTextureDb.Textures.AddRange(textureBindings.Select(x => new TextureInfo { Name = x.TargetName, Id = x.TargetId }));
            emittedTextureDb.Save(Path.Combine(staging, "rom", "objset", "mod_tex_db.bin"));
            if (skinBytes != null) File.WriteAllBytes(Path.Combine(staging, skinOutput), skinBytes);
            byte[] transported = JsonSerializer.SerializeToUtf8Bytes(updated, PhysicalOccupancy.Json);
            var reopenedAnalysis = JsonSerializer.Deserialize<ModuleAnalysisResult>(transported, PhysicalOccupancy.Json)!;
            var reopenedSelection = reopenedAnalysis.BuildPlan.ObjectSets.Single(x => x.SourceName == plannedSet.SourceName).PhysicalSelection!;
            if (JsonSerializer.Serialize(reopenedSelection, PhysicalOccupancy.Json) != JsonSerializer.Serialize(selection, PhysicalOccupancy.Json))
                throw new InvalidDataException("Physical bindings changed after JSON transport.");
            File.WriteAllBytes(Path.Combine(staging, "analysis-with-physical-bindings.json"), transported);
            byte[] registryBytes = JsonSerializer.SerializeToUtf8Bytes(registry.Document, PhysicalOccupancy.Json);
            File.WriteAllBytes(Path.Combine(staging, "physical-id-registry.json"), registryBytes);

            using var checkArchive = BinaryFile.Load<FarcArchive>(archiveDestination);
            var checkModel = checkArchive.Open<ObjectSet>(stem + "_obj.bin");
            using var checkObjectInput = checkArchive.Open(stem + "_obj.bin", EntryStreamMode.MemoryStream);
            using var checkObjectBuffer = new MemoryStream(); checkObjectInput.CopyTo(checkObjectBuffer);
            byte[] reversedObject = ClassicObjectSetIdPatcher.Verify(objectBytes, checkObjectBuffer.ToArray(), patched.Patches, targetObject);
            var checkTextures = checkArchive.Open<TextureSet>(stem + "_tex.bin");
            var checkObjectDb = BinaryFile.Load<ObjectDatabase>(Path.Combine(staging, "rom", "objset", "mod_obj_db.bin"));
            var checkTextureDb = BinaryFile.Load<TextureDatabase>(Path.Combine(staging, "rom", "objset", "mod_tex_db.bin"));
            if (checkModel.Objects.Count != 1 || checkModel.Objects[0].Name != targetObject || checkModel.Objects[0].Id != objectId ||
                !checkModel.TextureIds.SequenceEqual(textureBindings.Select(x => x.TargetId)) || checkTextures.Textures.Count != textureBindings.Count ||
                checkObjectDb.ObjectSets.Count != 1 || checkObjectDb.ObjectSets[0].Id != setId ||
                checkObjectDb.ObjectSets[0].Name != targetSet || checkObjectDb.ObjectSets[0].Objects.Single().Id != objectId ||
                checkObjectDb.ObjectSets[0].Objects.Single().Name != targetObject.ToUpperInvariant() ||
                checkObjectDb.ObjectSets[0].ArchiveFileName != stem + ".farc" ||
                checkObjectDb.ObjectSets[0].FileName != stem + "_obj.bin" || checkObjectDb.ObjectSets[0].TextureFileName != stem + "_tex.bin" ||
                !checkTextureDb.Textures.Select(x => (x.Id, x.Name)).SequenceEqual(textureBindings.Select(x => (x.TargetId, x.TargetName))))
                throw new InvalidDataException("Physical ID/database/archive round-trip failed.");
            if (updatedSkin != null) ValidateTopology(checkModel.Objects[0], updatedSkin);
            else ValidateModelOnly(checkModel.Objects[0], normalizedSet.Objects.Single());
            foreach (uint id in MaterialIds(checkModel))
                if (!checkModel.TextureIds.Contains(id)) throw new InvalidDataException("Output material has an unresolved texture.");
            using var checkTextureInput = checkArchive.Open(stem + "_tex.bin", EntryStreamMode.MemoryStream);
            using var checkBuffer = new MemoryStream(); checkTextureInput.CopyTo(checkBuffer);
            if (!checkBuffer.ToArray().AsSpan().SequenceEqual(textureBytes)) throw new InvalidDataException("Texture payload changed.");
            if (reference != null && !File.ReadAllBytes(Path.Combine(staging, skinOutput)).AsSpan().SequenceEqual(File.ReadAllBytes(reference.ReferencePath)))
                throw new InvalidDataException("Skin parameter output changed.");
            // Undo only the declared identifiers, then compare the complete
            // library serialization to catch mesh, material or skin changes.
            checkModel.Objects[0].Name = originalName;
            checkModel.Objects[0].Id = originalId;
            RemapTextures(checkModel, textureBindings.ToDictionary(x => x.TargetId, x => x.SourceId));
            string restoredCanonical = PhysicalOccupancy.Hash(SaveModel(checkModel));
            if (restoredCanonical != referenceCanonical)
                throw new InvalidDataException("The model round-trip changed data beyond the declared identifier remapping.");
            var emittedFiles = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal).Select(path => new StagedFile(Path.GetRelativePath(staging, path).Replace('\\', '/'),
                    "PhysicalReferenceStage", PhysicalOccupancy.Hash(File.ReadAllBytes(path)))).ToArray();
            var manifest = new ReferenceObjectSetManifest("explicit-reference-objectset-staging", false,
                analysis.NormalizedModule.Identity.CanonicalId, plannedSet.SourceName, targetSet, setId, profile,
                PhysicalOccupancy.Hash(analysisBytes), selection.OccupancySha256, PhysicalOccupancy.Hash(File.ReadAllBytes(csvPath)),
                selection, referenceCanonical, restoredCanonical, PhysicalOccupancy.Hash(objectBytes),
                PhysicalOccupancy.Hash(reversedObject), patched.Patches, true, true, true, emittedFiles);
            File.WriteAllBytes(Path.Combine(staging, "physical-stage-manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, PhysicalOccupancy.Json));
            // The archive owns a Windows file handle. Release it before the
            // atomic directory rename that publishes the validated stage.
            checkArchive.Dispose();
            // Recheck occupancy at the end, before publishing or committing IDs.
            PhysicalOccupancy.LoadFresh(inventoryPath);
            string pendingRegistry = registryPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllBytes(pendingRegistry, registryBytes);
                Directory.Move(staging, output);
                try { File.Move(pendingRegistry, registryPath, overwrite: true); }
                catch { Directory.Move(output, staging); throw; }
                staging = null;
            }
            finally { if (File.Exists(pendingRegistry)) File.Delete(pendingRegistry); }
            Console.WriteLine(output);
            return 0;
        }
        catch (Exception ex)
        {
            if (staging != null)
            {
                string parent = Path.GetDirectoryName(output)!;
                string resolved = Path.GetFullPath(staging);
                if (resolved.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(resolved).StartsWith("." + Path.GetFileName(output) + ".staging-", StringComparison.Ordinal) &&
                    Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
            }
            Console.Error.WriteLine("Physical staging failed without publishing output: " + ex.Message);
            return 2;
        }
    }

    private static byte[] SaveModel(ObjectSet model)
    { using var stream = new MemoryStream(); model.Save(stream, leaveOpen: true); return stream.ToArray(); }

    private static IEnumerable<uint> MaterialIds(ObjectSet model) => model.Objects.SelectMany(x => x.Materials)
        .SelectMany(x => x.MaterialTextures).Where(x => x != null && x.TextureId != uint.MaxValue).Select(x => x.TextureId);

    private static void RemapTextures(ObjectSet model, IReadOnlyDictionary<uint, uint> map)
    {
        for (int i = 0; i < model.TextureIds.Count; i++) model.TextureIds[i] = map[model.TextureIds[i]];
        foreach (var texture in model.Objects.SelectMany(x => x.Materials).SelectMany(x => x.MaterialTextures))
            if (texture != null && texture.TextureId != uint.MaxValue) texture.TextureId = map[texture.TextureId];
    }

    private static void ValidateTopology(MikuMikuLibrary.Objects.Object obj, PlannedSkinParameter skin)
    {
        if (obj.Skin == null || obj.Skin.Blocks.OfType<MotionBlock>().Any())
            throw new InvalidDataException("Reference/output has no skin or still has MotionBlocks.");
        var blocks = obj.Skin.Blocks.OfType<OsageBlock>().Select(x => new NormalizedSkinBlock("OSG", x.Name,
            x.ExternalName, x.ParentName, x.Nodes.Select(n => n.Name).ToArray(), false)).ToArray();
        if (JsonSerializer.Serialize(blocks, PhysicalOccupancy.Json) != JsonSerializer.Serialize(skin.FinalBlocks, PhysicalOccupancy.Json))
            throw new InvalidDataException("Physical topology differs from the selected analytical reference.");
        if (blocks.Select(x => x.ExternalName).Distinct(StringComparer.Ordinal).Count() != blocks.Length ||
            !blocks.Select(x => x.ExternalName).ToHashSet(StringComparer.Ordinal).SetEquals(skin.FinalParameters.Select(x => x.Name)))
            throw new InvalidDataException("Physical external OSG bindings are duplicated or incomplete.");
        foreach (var block in blocks)
            if (skin.FinalParameters.Single(x => x.Name == block.ExternalName).NodeCount != block.NodeNames.Count)
                throw new InvalidDataException("Physical node count differs from the selected reference.");
        var validation = new SkinParameterValidator().Validate(blocks, skin.FinalParameters);
        if (!validation.IsValid) throw new InvalidDataException(string.Join("; ", validation.Issues.Select(x => x.Message)));
    }

    private static void ValidateModelOnly(MikuMikuLibrary.Objects.Object obj, NormalizedObject source)
    {
        if (source.OriginalBlocks.Any(x => x.Kind is "OSG" or "MOT") ||
            obj.Skin?.Blocks.Any(x => x is OsageBlock or MotionBlock) == true)
            throw new InvalidDataException("A model-only binding cannot omit physical OSG/MOT blocks or their parameters.");
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: XRomConverter write-objectset-stage --analysis JSON --source-set NAME --texture-db BIN --inventory JSON --objset-csv CSV --registry JSON --profile NAME --target-set NAME --target-object NAME [--model-reference FARC --reference-object UID] --output DIR");
        return 64;
    }
}
