using System.Text.Json;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.Databases;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Objects;
using XRomConverter.Analysis;
using XRomConverter.Ids;

namespace XRomConverter.Target.MegaMix;

internal sealed record CharacterItemPhysicalLink(int ItemNumber, string ObjectName, string ObjectSet,
    uint ExpectedSetId, uint? ExpectedObjectId, uint? ActualObjectId, string Status);
internal sealed record CharacterItemLinkReport(bool Valid, bool Complete, int VerifiedObjects,
    IReadOnlyList<CharacterItemPhysicalLink> Links, IReadOnlyList<string> Pending, IReadOnlyList<string> Errors);

internal static class CharacterItemLinkValidator
{
    public static CharacterItemLinkReport Validate(string root, ModuleAnalysisResult analysis, IReadOnlyList<TargetItemBinding> bindings,
        IReadOnlySet<string>? requiredSets = null)
    {
        var errors = new List<string>();
        var pending = new List<string>();
        var links = new List<CharacterItemPhysicalLink>();
        string databasePath = Path.Combine(root, "rom", "objset", "mod_obj_db.bin");
        ObjectDatabase? database = null;
        try { if (File.Exists(databasePath)) database = BinaryFile.Load<ObjectDatabase>(databasePath); }
        catch (Exception ex) { errors.Add("Object database could not be read: " + ex.Message); }
        foreach (var item in bindings)
        foreach (var obj in item.Objects)
        {
            var expectedSet = item.ObjectSets.Single(x => x.TargetName == obj.TargetSet);
            var sets = database?.ObjectSets.Where(x => x.Name.Equals(obj.TargetSet, StringComparison.OrdinalIgnoreCase)).ToArray()
                ?? Array.Empty<ObjectSetInfo>();
            if (sets.Length == 0)
            {
                bool required = requiredSets?.Contains(obj.TargetSet) == true;
                if (required) errors.Add($"Item {item.ItemNumber}: emitted object set {obj.TargetSet} is absent from the object database.");
                else pending.Add($"Item {item.ItemNumber}: {obj.TargetSet}/{obj.TargetName} is not emitted.");
                links.Add(new(item.ItemNumber, obj.TargetName, obj.TargetSet, expectedSet.SetId, obj.ObjectId, null, required ? "invalid" : "pending"));
                continue;
            }
            int before = errors.Count;
            uint? actualId = null;
            try
            {
                if (sets.Length != 1 || sets[0].Id != expectedSet.SetId)
                    throw new InvalidDataException("Object-set database binding differs from the build plan.");
                var set = sets[0];
                var objects = set.Objects.Where(x => x.Name.Equals(obj.TargetName, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (objects.Length != 1 || obj.ObjectId.HasValue && objects[0].Id != obj.ObjectId.Value)
                    throw new InvalidDataException("Object database UID/ID differs from the selected binding.");
                actualId = objects[0].Id;
                var plan = analysis.BuildPlan.ObjectSets.Single(x => x.TargetName == obj.TargetSet);
                string archivePath = Path.Combine(root, "rom", "objset", set.ArchiveFileName);
                if (Path.GetFileName(set.ArchiveFileName) != set.ArchiveFileName ||
                    Path.GetFileName(plan.OutputArchive) != set.ArchiveFileName || !File.Exists(archivePath))
                    throw new InvalidDataException("Object database archive path is absent or differs from the build plan.");
                using var farc = BinaryFile.Load<FarcArchive>(archivePath);
                if (!farc.FileNames.Contains(set.FileName, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException("Object database entry is absent from the archive.");
                var model = farc.Open<ObjectSet>(set.FileName);
                var actual = model.Objects.Where(x => x.Name.Equals(obj.TargetName, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (actual.Length != 1 || actual[0].Id != objects[0].Id)
                    throw new InvalidDataException("UID/ID is not present exactly once in the emitted model.");
            }
            catch (Exception ex) { errors.Add($"Item {item.ItemNumber}, {obj.TargetName}: {ex.Message}"); }
            links.Add(new(item.ItemNumber, obj.TargetName, obj.TargetSet, expectedSet.SetId, obj.ObjectId, actualId,
                errors.Count == before ? "verified" : "invalid"));
        }
        return new(errors.Count == 0, errors.Count == 0 && pending.Count == 0,
            links.Count(x => x.Status == "verified"), links, pending, errors);
    }
}

internal static class CharacterItemLinkValidationCommand
{
    public static bool IsRequested(string[] args) => args.Length > 0 && args[0] == "validate-item-links";
    public static int Run(string[] args)
    {
        string? analysisPath = null, stagePath = null, output = null;
        bool allowPending = false;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--analysis" && i + 1 < args.Length) analysisPath = args[++i];
            else if (args[i] == "--stage" && i + 1 < args.Length) stagePath = args[++i];
            else if (args[i] == "--output" && i + 1 < args.Length) output = args[++i];
            else if (args[i] == "--allow-pending") allowPending = true;
            else return 64;
        }
        if (analysisPath == null || stagePath == null || !File.Exists(analysisPath) || !Directory.Exists(stagePath)) return 64;
        try
        {
            var analysis = JsonSerializer.Deserialize<ModuleAnalysisResult>(File.ReadAllBytes(analysisPath), PhysicalOccupancy.Json)
                ?? throw new InvalidDataException("Empty analysis.");
            var manifest = JsonSerializer.Deserialize<MegaMixStagingManifest>(File.ReadAllBytes(Path.Combine(stagePath, "build-manifest.json")), PhysicalOccupancy.Json)
                ?? throw new InvalidDataException("Empty metadata manifest.");
            if (analysis.NormalizedModule.Identity == null || analysis.BuildPlan.ModuleBinding.TargetId == null ||
                manifest.CanonicalId != analysis.NormalizedModule.Identity.CanonicalId ||
                manifest.TargetModuleId != analysis.BuildPlan.ModuleBinding.TargetId.Value)
                throw new InvalidDataException("Metadata manifest identity differs from the build plan.");
            var bindings = CharacterItemTargetResolver.Resolve(analysis, manifest.ObjectSetIds);
            var moduleTable = MegaMixStagingWriterCommand.ValidateModuleTable(Path.Combine(stagePath, "rom", "gm_module_tbl.farc"),
                analysis.BuildPlan.ModuleBinding.TargetId.Value, MegaMixStagingWriterCommand.NormalizeCharacter(analysis.NormalizedModule.Character),
                "COS_" + (CharacterItemTargetResolver.CostumeId(analysis) + 1), analysis.NormalizedModule.Identity.DisplayName, analysis.Aggregate.Module.SortIndex);
            var table = MegaMixStagingWriterCommand.ValidateCharacterItemTable(Path.Combine(stagePath, "rom", "mod_chritm_prop.farc"),
                analysis.NormalizedModule.Character.ToLowerInvariant() + "itm_tbl.txt", CharacterItemTargetResolver.CostumeId(analysis),
                analysis.NormalizedModule.Components.OrderBy(x => x.SlotIndex).ToArray(), bindings);
            var emittedPaths = manifest.EmittedFiles.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var requiredSets = analysis.BuildPlan.ObjectSets.Where(x => emittedPaths.Contains(x.OutputArchive))
                .Select(x => x.TargetName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var links = CharacterItemLinkValidator.Validate(stagePath, analysis, bindings, requiredSets);
            SupplementalAssetValidator.Validate(stagePath, analysis);
            bool accepted = moduleTable.Valid && table.Valid && links.Valid && (allowPending || links.Complete);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(new { accepted, allowPending, moduleTable, table, links }, PhysicalOccupancy.Json);
            if (output == null) Console.WriteLine(System.Text.Encoding.UTF8.GetString(json));
            else
            {
                string destination = Path.GetFullPath(output);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllBytes(destination, json);
                Console.WriteLine(destination);
            }
            return accepted ? 0 : 2;
        }
        catch (Exception ex) { Console.Error.WriteLine("Item-link validation failed: " + ex.Message); return 2; }
    }
}
