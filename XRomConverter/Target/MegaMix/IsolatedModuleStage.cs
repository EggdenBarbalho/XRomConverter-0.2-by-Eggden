using System.Text.Json;
using System.Text.RegularExpressions;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.Databases;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Parameters;
using MikuMikuLibrary.Sprites;
using XRomConverter.Analysis;
using XRomConverter.Ids;

namespace XRomConverter.Target.MegaMix;

internal static class IsolatedModuleStageCommand
{
    public static bool IsRequested(string[] args) => args.Length > 0 && args[0] == "prepare-isolated-module-stage";

    public static int Run(string[] args)
    {
        var allowed = new HashSet<string> { "--physical-stage", "--inventory", "--module-csv", "--registry", "--profile",
            "--sprite-reference", "--sprite-db", "--osage-reference", "--output" };
        var options = new Dictionary<string, string>();
        for (int i = 1; i < args.Length; i += 2)
            if (i + 1 >= args.Length || !allowed.Contains(args[i]) || !options.TryAdd(args[i], args[i + 1])) return 64;
        if (!allowed.SetEquals(options.Keys)) return 64;
        string output = Path.GetFullPath(options["--output"]);
        if (Directory.Exists(output) || File.Exists(output))
        { Console.Error.WriteLine("Output already exists; refusing to overwrite: " + output); return 2; }
        string? staging = null;
        try
        {
            string root = Path.GetFullPath(options["--physical-stage"]);
            string registryPath = Path.GetFullPath(options["--registry"]);
            string inventoryPath = Path.GetFullPath(options["--inventory"]);
            string csvPath = Path.GetFullPath(options["--module-csv"]);
            string spritePath = Path.GetFullPath(options["--sprite-reference"]);
            string spriteDbPath = Path.GetFullPath(options["--sprite-db"]);
            string osagePath = Path.GetFullPath(options["--osage-reference"]);
            string profile = options["--profile"];
            if (!Regex.IsMatch(profile, @"^[A-Za-z][A-Za-z0-9_-]{0,95}$")) throw new InvalidDataException("Invalid registry profile.");
            var occupancy = MetadataOccupancy.LoadFresh(inventoryPath);
            string installationPrefix = Path.GetFullPath(occupancy.Installation).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (output.StartsWith(installationPrefix, StringComparison.OrdinalIgnoreCase) || registryPath.StartsWith(installationPrefix, StringComparison.OrdinalIgnoreCase) ||
                registryPath.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                new[] { inventoryPath, csvPath, spritePath, spriteDbPath, osagePath }.Contains(registryPath, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Keep controlled output and registry outside the installation and separate from input files.");
            byte[] originalBytes = File.ReadAllBytes(Path.Combine(root, "analysis-with-physical-bindings.json"));
            var analysis = JsonSerializer.Deserialize<ModuleAnalysisResult>(originalBytes, PhysicalOccupancy.Json)
                ?? throw new InvalidDataException("Empty input analysis.");
            var physical = PhysicalStageImport.Prepare(root, analysis, originalBytes);
            if (analysis.BuildPlan.ObjectSets.Any(x => x.PhysicalSelection == null) || analysis.BuildPlan.TableSelection != null)
                throw new InvalidDataException("A physically bound module without an existing table selection is required.");
            var composition = JsonSerializer.Deserialize<PhysicalCompositionManifest>(File.ReadAllBytes(Path.Combine(root, "physical-composition-manifest.json")), PhysicalOccupancy.Json)!;
            Directory.CreateDirectory(Path.GetDirectoryName(registryPath)!);
            using var registryLock = new FileStream(registryPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var registry = new MetadataIdRegistry(registryPath);
            string key = "canonical:" + analysis.NormalizedModule.Identity!.CanonicalId;
            string character = analysis.NormalizedModule.Character;
            IReadOnlySet<uint> Occupied(string ns, string scope) => occupancy.Ids.Where(x => x.Namespace == ns && x.Scope == scope).Select(x => x.Id).ToHashSet();
            uint Allocate(string ns, string scope, string suffix, string name, uint first, uint max, IEnumerable<uint>? extra = null) =>
                registry.Allocate(profile, ns, scope, key + suffix, name, Occupied(ns, scope).Concat(extra ?? Array.Empty<uint>()).ToHashSet(), first, max);
            var reservedModules = ModuleArchiveIdCatalog.LoadOccupiedModuleIds(csvPath).Where(x => x >= 0).Select(x => (uint)x).ToArray();
            int moduleId = (int)Allocate("module", "global", "/module", key, 600, (uint)short.MaxValue, reservedModules);
            int costumeId = (int)Allocate("costume", character, "/costume", key + "/costume", 1000, (uint)short.MaxValue);
            var itemIds = analysis.NormalizedModule.Components.OrderBy(x => x.SlotIndex).Select(x => new TargetItemIdBinding(x.SourceItemNumber,
                (int)Allocate("item", character, "/item:" + x.SourceItemNumber, key + "/item:" + x.SourceItemNumber, 10000, (uint)short.MaxValue))).ToArray();
            string stem = $"spr_sel_md{moduleId:D3}cmn";
            string setName = stem.ToUpperInvariant();
            uint spriteSetId = Allocate("sprite-set", "global", "/sprite-set", setName, 10020000, uint.MaxValue - 1);
            byte[] spriteArchiveBytes = File.ReadAllBytes(spritePath);
            byte[] spriteDatabaseBytes = File.ReadAllBytes(spriteDbPath);
            var sourceSpriteDb = BinaryFile.Load<SpriteDatabase>(new MemoryStream(spriteDatabaseBytes, writable: false));
            using var sourceArchive = BinaryFile.Load<FarcArchive>(new MemoryStream(spriteArchiveBytes, writable: false));
            string sourceEntry = sourceArchive.FileNames.Single();
            var sourceSets = sourceSpriteDb.SpriteSets.Where(x => x.FileName == sourceEntry).ToArray();
            if (sourceSets.Length != 1 || sourceSets[0].Sprites.Count != 1 || sourceSets[0].Textures.Count != 1)
                throw new InvalidDataException("This controlled presentation requires one uniquely declared image and texture.");
            var sourceSet = sourceSets.Single();
            var spriteSet = sourceArchive.Open<SpriteSet>(sourceEntry);
            if (!spriteSet.Format.IsClassic() || spriteSet.Sprites.Count != 1 || spriteSet.TextureSet.Textures.Count != 1 ||
                spriteSet.Sprites[0].TextureIndex != 0 || sourceSet.Sprites[0].Index != 0 || sourceSet.Textures[0].Index != 0 ||
                sourceSet.Sprites[0].Name != sourceSet.Name + "_" + spriteSet.Sprites[0].Name ||
                !sourceSet.Textures[0].Name.StartsWith(sourceSet.Name + "_", StringComparison.Ordinal))
                throw new InvalidDataException("Sprite payload does not match its declared indices/names.");
            string spriteName = setName + "_" + spriteSet.Sprites[0].Name;
            string textureName = setName + sourceSet.Textures[0].Name[sourceSet.Name.Length..];
            uint spriteId = Allocate("sprite", "global", "/sprite", spriteName, 10021000, uint.MaxValue - 1);
            uint spriteTextureId = Allocate("sprite-texture", "global", "/sprite-texture", textureName, 10022000, uint.MaxValue - 1);
            if (new[] { ("sprite-set", setName), ("sprite", spriteName), ("sprite-texture", textureName) }
                .Any(pair => occupancy.Ids.Any(x => x.Namespace == pair.Item1 && x.Name.Equals(pair.Item2, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidDataException("Target presentation name is already declared.");
            using var payloadStream = sourceArchive.Open(sourceEntry, EntryStreamMode.MemoryStream);
            using var payloadBuffer = new MemoryStream(); payloadStream.CopyTo(payloadBuffer);
            byte[] payload = payloadBuffer.ToArray();
            var presentation = new ModulePresentationSelection(spritePath, PhysicalOccupancy.Hash(spriteArchiveBytes), spriteDbPath,
                PhysicalOccupancy.Hash(spriteDatabaseBytes), sourceEntry, PhysicalOccupancy.Hash(payload),
                "rom/2d/" + stem + ".farc", stem + ".bin", setName, spriteSetId, spriteName, spriteId, textureName, spriteTextureId);
            byte[] osageReference = File.ReadAllBytes(osagePath);
            var osageTree = ParameterTree.Load(new MemoryStream(osageReference, writable: false));
            string sourceObject = "", targetObject = "", sourceCategory = "";
            if (!osageTree.OpenScope("obj")) throw new InvalidDataException("Osage setting has no object bindings.");
            foreach (string objKey in osageTree.Keys.ToArray())
            {
                if (!osageTree.OpenScope(objKey)) continue;
                string name = osageTree.Get("name", "");
                var candidates = physical.Sets.SelectMany(x => x.Selection.Objects).Where(x => x.SourceName.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (candidates.Length == 1)
                {
                    if (sourceObject != "") throw new InvalidDataException("This controlled osage reference requires one selected object.");
                    sourceObject = name; targetObject = candidates[0].TargetName; sourceCategory = osageTree.Get("cat", "");
                }
                osageTree.CloseScope();
            }
            osageTree.CloseScope();
            if (sourceObject == "" || sourceCategory == "") throw new InvalidDataException("Osage object has no selected physical binding.");
            var roots = new List<(int Exf, string Parts, string Root)>();
            int matchedCategories = 0;
            if (!osageTree.OpenScope("cat")) throw new InvalidDataException("Osage categories are absent.");
            foreach (string catKey in osageTree.Keys.ToArray())
            {
                if (!osageTree.OpenScope(catKey)) continue;
                if (osageTree.Get("name", "") == sourceCategory)
                {
                    matchedCategories++;
                    if (!osageTree.OpenScope("osg")) throw new InvalidDataException("Osage category has no OSGs.");
                    int count = osageTree.Get("length", -1);
                    for (int i = 0; i < count; i++)
                    {
                        if (!osageTree.OpenScope(i)) throw new InvalidDataException("Osage root index is absent.");
                        roots.Add((osageTree.Get("exf", 0), osageTree.Get("parts", ""), osageTree.Get("root", ""))); osageTree.CloseScope();
                    }
                    osageTree.CloseScope();
                }
                osageTree.CloseScope();
            }
            osageTree.CloseScope();
            var targetSkin = analysis.BuildPlan.ObjectSets.SelectMany(x => x.SkinParameters).Single(x => x.TargetObject == targetObject);
            if (matchedCategories != 1 || roots.Count == 0 || roots.Select(x => x.Root).Distinct().Count() != roots.Count ||
                roots.Any(x => !targetSkin.FinalBlocks.Any(b => b.ExternalName == x.Root) || x.Parts is not ("LEFT" or "RIGHT" or "LONG_C")))
                throw new InvalidDataException("Osage roots/category do not resolve to the selected model.");
            string category = $"xrom02_{moduleId}_" + sourceCategory;
            var osageWriter = new ParameterTreeWriter();
            osageWriter.PushScope("cat"); osageWriter.PushScope(0); osageWriter.Write("name", category);
            osageWriter.Write("osg", roots, x => { osageWriter.Write("exf", x.Exf); osageWriter.Write("parts", x.Parts); osageWriter.Write("root", x.Root); });
            osageWriter.PopScope(); osageWriter.Write("length", 1); osageWriter.PopScope();
            osageWriter.PushScope("obj"); osageWriter.PushScope(0); osageWriter.Write("cat", category); osageWriter.Write("name", targetObject.ToUpperInvariant());
            osageWriter.PopScope(); osageWriter.Write("length", 1); osageWriter.PopScope();
            using var osageOutput = new MemoryStream(); osageWriter.Flush(osageOutput);
            byte[] osageBytes = osageOutput.ToArray();
            var osageSelection = new OsageSettingSelection(osagePath, PhysicalOccupancy.Hash(osageReference), sourceObject,
                targetObject, category, roots.Select(x => x.Root).ToArray(), "rom/skin_param/mod_osage_setting.txt", PhysicalOccupancy.Hash(osageBytes));
            var tableSelection = new TargetTableSelection(character, costumeId, itemIds, profile, inventoryPath,
                PhysicalOccupancy.Hash(File.ReadAllBytes(inventoryPath)), csvPath, PhysicalOccupancy.Hash(File.ReadAllBytes(csvPath)), PhysicalOccupancy.Hash(originalBytes));
            var updated = analysis with { BuildPlan = analysis.BuildPlan with
            {
                ModuleBinding = analysis.BuildPlan.ModuleBinding with { TargetId = moduleId, Status = "allocated-isolated", Source = "metadata-registry",
                    BindingKind = "local_isolated", Authority = "metadata-occupancy-and-reservations", ObservedTargetId = analysis.BuildPlan.ModuleBinding.TargetId, Action = "isolated-additive-test" },
                TableSelection = tableSelection, PresentationSelection = presentation, OsageSettingSelection = osageSelection,
                RequiredOutputs = analysis.BuildPlan.RequiredOutputs.Select(x => x.Kind == "ModuleSprite" ? x with { Path = presentation.OutputArchive } : x)
                    .Append(new PlannedOutput(osageSelection.OutputPath, "OsageSetting", "required", new[] { osagePath, targetObject })).ToArray()
            } };
            var ids = updated.BuildPlan.ObjectSets.Select(x => new AllocatedObjectSetId(x.TargetName, x.PhysicalSelection!.TargetSetId, "physical-plan")).ToArray();
            _ = CharacterItemTargetResolver.Resolve(updated, ids);
            string parent = Path.GetDirectoryName(output)!; Directory.CreateDirectory(parent);
            staging = Path.Combine(parent, "." + Path.GetFileName(output) + ".staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var emitted = new List<StagedFile>();
            physical.CopyTo(staging, emitted);
            void Write(string relative, byte[] bytes)
            {
                string path = Path.Combine(staging, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path)) throw new InvalidDataException("Isolated stage output collides: " + relative);
                File.WriteAllBytes(path, bytes); emitted.Add(new(relative, "IsolatedModuleReference", PhysicalOccupancy.Hash(bytes)));
            }
            using (var archive = new FarcArchive())
            {
                archive.Add(presentation.OutputEntry, new MemoryStream(payload, writable: false), leaveOpen: false);
                using var stream = new MemoryStream(); archive.Save(stream, leaveOpen: true); Write(presentation.OutputArchive, stream.ToArray());
            }
            var targetSpriteDb = new SpriteDatabase { Format = BinaryFormat.DT };
            var info = new SpriteSetInfo { Name = setName, Id = spriteSetId, FileName = presentation.OutputEntry };
            info.Sprites.Add(new SpriteInfo { Name = spriteName, Id = spriteId, Index = 0 });
            info.Textures.Add(new SpriteTextureInfo { Name = textureName, Id = spriteTextureId, Index = 0 }); targetSpriteDb.SpriteSets.Add(info);
            using (var stream = new MemoryStream()) { targetSpriteDb.Save(stream, leaveOpen: true); Write("rom/2d/mod_spr_db.bin", stream.ToArray()); }
            Write(osageSelection.OutputPath, osageBytes);
            Write("analysis-with-physical-bindings.json", JsonSerializer.SerializeToUtf8Bytes(updated, PhysicalOccupancy.Json));
            byte[] registryBytes = JsonSerializer.SerializeToUtf8Bytes(registry.Document, PhysicalOccupancy.Json);
            Write("metadata-id-registry.json", registryBytes);
            var manifest = composition with { EmittedFiles = emitted };
            File.WriteAllBytes(Path.Combine(staging, "physical-composition-manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, PhysicalOccupancy.Json));
            PhysicalStageImport.Prepare(staging, updated, File.ReadAllBytes(Path.Combine(staging, "analysis-with-physical-bindings.json")));
            SupplementalAssetValidator.Validate(staging, updated);
            MetadataOccupancy.LoadFresh(inventoryPath);
            string temporary = registryPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllBytes(temporary, registryBytes); Directory.Move(staging, output);
                try { File.Move(temporary, registryPath, overwrite: true); } catch { Directory.Move(output, staging); throw; }
                staging = null;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            Console.WriteLine(output); return 0;
        }
        catch (Exception ex)
        {
            if (staging != null)
            {
                string parent = Path.GetDirectoryName(output)!; string full = Path.GetFullPath(staging);
                if (full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(full).StartsWith("." + Path.GetFileName(output) + ".staging-", StringComparison.Ordinal) && Directory.Exists(full)) Directory.Delete(full, recursive: true);
            }
            Console.Error.WriteLine("Isolated module staging failed without publishing output: " + ex.Message); return 2;
        }
    }
}

internal static class SupplementalAssetValidator
{
    public static void Validate(string root, ModuleAnalysisResult analysis)
    {
        if (analysis.BuildPlan.PresentationSelection is { } selected)
        {
            string stem = $"spr_sel_md{analysis.BuildPlan.ModuleBinding.TargetId:D3}cmn";
            if (selected.SetName != stem.ToUpperInvariant() || selected.OutputEntry != stem + ".bin" || selected.OutputArchive != "rom/2d/" + stem + ".farc")
                throw new InvalidDataException("Presentation does not match the target module ID.");
            var database = BinaryFile.Load<SpriteDatabase>(Path.Combine(root, "rom/2d/mod_spr_db.bin"));
            var info = database.SpriteSets.Single();
            if (info.Name != selected.SetName || info.Id != selected.SetId || info.FileName != selected.OutputEntry ||
                info.Sprites.Count != 1 || info.Textures.Count != 1 || info.Sprites[0].Id != selected.SpriteId ||
                info.Sprites[0].Name != selected.SpriteName || info.Sprites[0].Index != 0 || info.Textures[0].Id != selected.TextureId ||
                info.Textures[0].Name != selected.TextureName || info.Textures[0].Index != 0)
                throw new InvalidDataException("Sprite database differs from its selected binding.");
            using var archive = BinaryFile.Load<FarcArchive>(Path.Combine(root, selected.OutputArchive));
            using var stream = archive.Open(selected.OutputEntry, EntryStreamMode.MemoryStream); using var copy = new MemoryStream(); stream.CopyTo(copy);
            if (PhysicalOccupancy.Hash(copy.ToArray()) != selected.PayloadSha256) throw new InvalidDataException("Sprite payload changed.");
            var sprite = archive.Open<SpriteSet>(selected.OutputEntry);
            if (sprite.Sprites.Count != 1 || sprite.TextureSet.Textures.Count != 1 || sprite.Sprites[0].TextureIndex != 0 ||
                selected.SpriteName != selected.SetName + "_" + sprite.Sprites[0].Name) throw new InvalidDataException("Sprite indices/names do not resolve.");
        }
        if (analysis.BuildPlan.OsageSettingSelection is { } osage)
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(root, osage.OutputPath));
            if (PhysicalOccupancy.Hash(bytes) != osage.OutputSha256) throw new InvalidDataException("Osage setting bytes differ from selection.");
            var tree = ParameterTree.Load(new MemoryStream(bytes, writable: false));
            if (!tree.OpenScope("obj") || tree.Get("length", -1) != 1 || !tree.OpenScope(0) ||
                tree.Get("name", "") != osage.TargetObject.ToUpperInvariant() || tree.Get("cat", "") != osage.Category)
                throw new InvalidDataException("Osage object/category binding differs.");
            tree.CloseScope(); tree.CloseScope();
            if (!tree.OpenScope("cat") || tree.Get("length", -1) != 1 || !tree.OpenScope(0) || tree.Get("name", "") != osage.Category || !tree.OpenScope("osg") ||
                tree.Get("length", -1) != osage.Roots.Count) throw new InvalidDataException("Osage category/root count differs.");
            var blocks = analysis.BuildPlan.ObjectSets.SelectMany(x => x.SkinParameters).Single(x => x.TargetObject == osage.TargetObject).FinalBlocks;
            for (int i = 0; i < osage.Roots.Count; i++)
            {
                if (!tree.OpenScope(i) || tree.Get("root", "") != osage.Roots[i] || !blocks.Any(x => x.ExternalName == osage.Roots[i]))
                    throw new InvalidDataException("Osage root is outside the selected model.");
                tree.CloseScope();
            }
        }
    }
}
