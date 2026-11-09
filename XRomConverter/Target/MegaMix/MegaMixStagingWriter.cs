using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Parameters;
using XRomConverter.Analysis;
using XRomConverter.Ids;

namespace XRomConverter.Target.MegaMix;

internal sealed record StagedFile(string Path, string Kind, string Sha256);

internal sealed record ModuleTableRoundTrip(bool Valid, string EntryName,
    int Length, int Id, string Character, string Costume, string Name,
    int SortIndex, IReadOnlyList<string> Errors);

internal sealed record CharacterItemRoundTrip(bool Valid, string EntryName,
    int CostumeId, IReadOnlyList<int> CostumeItems, int ItemCount,
    IReadOnlyList<int> ItemNumbers, IReadOnlyList<string> Errors);

internal sealed record AllocatedObjectSetId(string TargetName, uint TargetId,
    string Authority);

internal sealed record MegaMixStagingManifest(int SchemaVersion, string Phase,
    bool Playable, string CanonicalId, int SourceModuleId, int SourceCosId,
    int TargetModuleId, string Character, string DisplayName,
    string AnalysisSha256, IReadOnlyList<StagedFile> EmittedFiles,
    IReadOnlyList<PlannedOutput> PendingOutputs,
    ModuleTableRoundTrip ModuleTableValidation,
    CharacterItemRoundTrip CharacterItemValidation,
    IReadOnlyList<AllocatedObjectSetId> ObjectSetIds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ModuleRegistrationReference? RegistrationReference = null);

internal static class MegaMixStagingWriterCommand
{
    public static bool IsRequested(string[] args) => args.Length > 0 &&
        args[0].Equals("write-megamix-stage", StringComparison.OrdinalIgnoreCase);

    public static int Run(string[] args)
    {
        string? analysisPath = null;
        string? outputPath = null;
        string? objectSetCsvPath = null;
        string? physicalStagePath = null;
        string? moduleReferencePath = null;
        int? referenceModuleId = null;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--analysis" && i + 1 < args.Length) analysisPath = args[++i];
            else if (args[i] == "--output" && i + 1 < args.Length) outputPath = args[++i];
            else if (args[i] == "--objset-csv" && i + 1 < args.Length) objectSetCsvPath = args[++i];
            else if (args[i] == "--physical-stage" && i + 1 < args.Length) physicalStagePath = args[++i];
            else if (args[i] == "--module-table-reference" && i + 1 < args.Length) moduleReferencePath = args[++i];
            else if (args[i] == "--reference-module" && i + 1 < args.Length && int.TryParse(args[++i], out int referenceId)) referenceModuleId = referenceId;
            else return Usage();
        }
        if (analysisPath == null || outputPath == null || objectSetCsvPath == null ||
            !File.Exists(analysisPath) || !File.Exists(objectSetCsvPath) || physicalStagePath != null && !Directory.Exists(physicalStagePath) ||
            (moduleReferencePath == null) != (referenceModuleId == null) || moduleReferencePath != null && !File.Exists(moduleReferencePath))
            return Usage();

        string analysisFullPath = Path.GetFullPath(analysisPath);
        string outputFullPath = Path.GetFullPath(outputPath);
        if (Directory.Exists(outputFullPath) || File.Exists(outputFullPath))
        {
            Console.Error.WriteLine("Output already exists; refusing to overwrite: " + outputFullPath);
            return 2;
        }

        byte[] analysisBytes = File.ReadAllBytes(analysisFullPath);
        var result = JsonSerializer.Deserialize<ModuleAnalysisResult>(analysisBytes,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Empty module analysis report.");
        if (result.BuildPlan.Status != "ready")
        {
            Console.Error.WriteLine("Build plan is not ready; writer refused the input.");
            return 2;
        }
        if (!result.BuildPlan.ModuleBinding.TargetId.HasValue)
        {
            Console.Error.WriteLine("Build plan has no target module ID.");
            return 2;
        }
        if (result.NormalizedModule.Identity == null)
        {
            Console.Error.WriteLine("Normalized module has no canonical identity.");
            return 2;
        }
        IReadOnlyList<AllocatedObjectSetId> objectSetIds;
        IReadOnlyList<TargetItemBinding> bindings;
        PhysicalStageImport? physical;
        ModuleRegistrationReference? registration;
        try
        {
            objectSetIds = CharacterItemTargetResolver.AllocateSets(result, objectSetCsvPath);
            bindings = CharacterItemTargetResolver.Resolve(result, objectSetIds);
            physical = physicalStagePath == null ? null : PhysicalStageImport.Prepare(physicalStagePath, result, analysisBytes);
            registration = moduleReferencePath == null ? null : ModuleTableReference.Select(moduleReferencePath, referenceModuleId!.Value,
                NormalizeCharacter(result.NormalizedModule.Character));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Writer preflight failed without publishing output: " + ex.Message);
            return 2;
        }

        string parent = Path.GetDirectoryName(outputFullPath)
            ?? throw new InvalidOperationException("Output has no parent directory.");
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, "." + Path.GetFileName(outputFullPath) +
            ".staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            var emitted = new List<StagedFile>();
            Emit(staging, "config.toml", "ModConfig",
                "enabled = " + (physical == null ? "true" : "false") + "\ninclude = [\".\"]\nname = \"" +
                EscapeToml(result.NormalizedModule.Identity.DisplayName) +
                " (X module writer staging)\"\n", emitted);
            Emit(staging, "rom/lang2/mod_str_array.toml", "Strings",
                $"module.{result.BuildPlan.ModuleBinding.TargetId.Value} = \"" +
                EscapeToml(result.NormalizedModule.Identity.DisplayName) + "\"\n", emitted);
            var moduleValidation = EmitModuleTable(staging, result, emitted, registration);
            if (!moduleValidation.Valid)
                throw new InvalidDataException("Generated module table failed round-trip validation: " +
                    string.Join("; ", moduleValidation.Errors));
            var characterItemValidation = EmitCharacterItemTable(staging, result, bindings, emitted);
            if (!characterItemValidation.Valid)
                throw new InvalidDataException("Generated character item table failed round-trip validation: " +
                    string.Join("; ", characterItemValidation.Errors));

            physical?.CopyTo(staging, emitted);
            if (physical != null) SupplementalAssetValidator.Validate(staging, result);
            if (result.BuildPlan.ObjectSets.Any(x => x.PhysicalSelection != null))
            {
                var requiredSets = physical?.Sets.Select(x => x.TargetSet).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var links = CharacterItemLinkValidator.Validate(staging, result, bindings, requiredSets);
                if (!links.Valid || physical != null && links.VerifiedObjects != physical.Sets.Sum(x => x.Selection.Objects.Count))
                    throw new InvalidDataException("Character item does not resolve to the imported physical model: " + string.Join("; ", links.Errors));
                Emit(staging, "character-item-bindings.json", "CharacterItemBindings",
                    JsonSerializer.Serialize(new { bindings, links }, PhysicalOccupancy.Json) + "\n", emitted);
            }
            int runtimeCharacter = NormalizeCharacter(result.NormalizedModule.Character) switch
            {
                "MIKU" => 0, "RIN" => 1, "LEN" => 2, "LUKA" => 3,
                "KAITO" => 6, "MEIKO" => 7, _ => -1
            };
            int recommendationCount = RecommendedModulesWriter.Write(staging, new[] {
                new RecommendedBinding(result.NormalizedModule.SourceModuleId, runtimeCharacter, characterItemValidation.CostumeId)
            });
            if (File.Exists(Path.Combine(staging, "XRomRuntime.dll")))
            {
                emitted.RemoveAll(x => x.Path == "config.toml");
                foreach (string file in new[] { "config.toml", "XRomRuntime.dll", "recommendations.tsv", "LICENSE-MINHOOK.txt", "quirky-curves.bin", "quirky-modules.tsv", "xrom-runtime.ini" })
                    emitted.Add(new StagedFile(file, "RecommendedRuntime", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(staging, file))))));
            }
            var emittedPaths = emitted.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var pending = physical != null ? physical.Pending(result, emitted) : result.BuildPlan.RequiredOutputs
                .Where(x => !emittedPaths.Contains(x.Path.Replace('\\', '/'))).ToArray();
            var manifest = new MegaMixStagingManifest(1, physical == null ? "metadata-staging" : "metadata-with-physical-reference-staging",
                false, result.NormalizedModule.Identity.CanonicalId,
                result.NormalizedModule.SourceModuleId, result.NormalizedModule.SourceCosId,
                result.BuildPlan.ModuleBinding.TargetId.Value,
                result.NormalizedModule.Character, result.NormalizedModule.Identity.DisplayName,
                Convert.ToHexString(SHA256.HashData(analysisBytes)), emitted, pending,
                moduleValidation, characterItemValidation, objectSetIds, registration);
            string manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }) + "\n";
            Emit(staging, "build-manifest.json", "BuildManifest", manifestJson, emitted: null);
            Directory.Move(staging, outputFullPath);
            Console.WriteLine(outputFullPath);
            return 0;
        }
        catch (Exception ex)
        {
            string resolved = Path.GetFullPath(staging);
            if (resolved.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(resolved).StartsWith("." + Path.GetFileName(outputFullPath) + ".staging-", StringComparison.Ordinal) &&
                Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
            Console.Error.WriteLine("Writer failed without publishing output: " + ex.Message);
            return 2;
        }
    }

    private static CharacterItemRoundTrip EmitCharacterItemTable(string root,
        ModuleAnalysisResult result, IReadOnlyList<TargetItemBinding> bindings, List<StagedFile> emitted)
    {
        var components = result.NormalizedModule.Components.OrderBy(x => x.SlotIndex).ToArray();
        if (components.Select(x => x.SourceItemNumber).Distinct().Count() != components.Length)
            throw new InvalidDataException("A costume contains duplicate item numbers.");
        foreach (var component in components)
        {
            if (component.Objects.Any(x => string.IsNullOrWhiteSpace(x.SourceName)))
                throw new InvalidDataException($"Item {component.SourceItemNumber} has an unresolved object name.");
            if (component.TextureChanges.Any(x => string.IsNullOrWhiteSpace(x.OriginalName) ||
                                                  string.IsNullOrWhiteSpace(x.ReplacementName)))
                throw new InvalidDataException($"Item {component.SourceItemNumber} has an unresolved texture change.");
        }

        var writer = new ParameterTreeWriter();
        writer.PushScope("cos");
        writer.PushScope(0);
        writer.Write("id", CharacterItemTargetResolver.CostumeId(result));
        writer.PushScope("item");
        for (int i = 0; i < components.Length; i++)
            writer.Write(i.ToString(), bindings[i].ItemNumber);
        writer.Write("length", components.Length);
        writer.PopScope();
        writer.PopScope();
        writer.Write("length", 1);
        writer.PopScope();

        writer.PushScope("item");
        for (int i = 0; i < components.Length; i++)
        {
            var component = components[i];
            var binding = bindings[i];
            writer.PushScope(i);
            writer.Write("attr", (int)component.Attribute);
            if (component.Objects.Count > 0)
                writer.Write("data.obj", binding.Objects, obj =>
                {
                    writer.Write("rpk", unchecked((sbyte)obj.Rpk));
                    writer.Write("uid", obj.TargetName.ToUpperInvariant());
                });
            if (component.TextureChanges.Count > 0)
                writer.Write("data.tex", binding.TextureChanges, change =>
                {
                    writer.Write("chg", change.ReplacementName);
                    writer.Write("org", change.OriginalName);
                });
            writer.Write("des_id", (int)component.DestinationId);
            writer.Write("exclusion", 0);
            writer.Write("face_depth", 0);
            writer.Write("flag", 0);
            writer.Write("name", $"0ITM{binding.ItemNumber:D3}");
            writer.Write("no", binding.ItemNumber);
            writer.PushScope("objset");
            for (int j = 0; j < component.ObjectSets.Count; j++)
                writer.Write(j.ToString(), binding.ObjectSets[j].TargetName);
            writer.Write("length", component.ObjectSets.Count);
            writer.PopScope();
            writer.Write("org_itm", 0);
            writer.Write("point", 0);
            writer.Write("sub_id", component.SlotIndex);
            writer.Write("type", (int)component.Type);
            writer.PopScope();
        }
        writer.Write("length", components.Length);
        writer.PopScope();

        using var tableStream = new MemoryStream();
        writer.Flush(tableStream);
        tableStream.Position = 0;
        string relativePath = "rom/mod_chritm_prop.farc";
        string fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string entryName = result.NormalizedModule.Character.ToLowerInvariant() + "itm_tbl.txt";
        using (var farc = new FarcArchive())
        {
            farc.Add(entryName, tableStream, leaveOpen: false);
            farc.Save(fullPath);
        }
        byte[] bytes = File.ReadAllBytes(fullPath);
        emitted.Add(new StagedFile(relativePath, "CharacterItemTable",
            Convert.ToHexString(SHA256.HashData(bytes))));
        return ValidateCharacterItemTable(fullPath, entryName,
            CharacterItemTargetResolver.CostumeId(result), components, bindings);
    }

    internal static CharacterItemRoundTrip ValidateCharacterItemTable(string path,
        string entryName, int expectedCostumeId, IReadOnlyList<NormalizedComponent> components,
        IReadOnlyList<TargetItemBinding> bindings)
    {
        var errors = new List<string>();
        using var farc = BinaryFile.Load<FarcArchive>(path);
        if (!farc.FileNames.Contains(entryName, StringComparer.OrdinalIgnoreCase))
            return new(false, entryName, -1, Array.Empty<int>(), 0,
                Array.Empty<int>(), new[] { $"{entryName} is absent" });
        using var stream = farc.Open(entryName, EntryStreamMode.MemoryStream);
        var tree = ParameterTree.Load(stream);
        int costumeId = -1;
        var costumeItems = new List<int>();
        if (!tree.OpenScope("cos") || !tree.OpenScope(0)) errors.Add("cos.0 scope is absent");
        else
        {
            costumeId = tree.Get("id", -1);
            if (!tree.OpenScope("item")) errors.Add("cos.0.item scope is absent");
            else
            {
                int count = tree.Get("length", -1);
                for (int i = 0; i < count; i++) costumeItems.Add(tree.Get(i.ToString(), -1));
                tree.CloseScope();
            }
            tree.CloseScope();
            tree.CloseScope();
        }
        var itemNumbers = new List<int>();
        int itemCount = -1;
        if (!tree.OpenScope("item")) errors.Add("item scope is absent");
        else
        {
            itemCount = tree.Get("length", -1);
            for (int i = 0; i < itemCount; i++)
            {
                if (!tree.OpenScope(i)) { errors.Add($"item.{i} scope is absent"); continue; }
                itemNumbers.Add(tree.Get("no", -1));
                if (i >= bindings.Count) { errors.Add($"item.{i} is unexpected"); tree.CloseScope(); continue; }
                var binding = bindings[i];
                if (!tree.OpenScope("objset")) errors.Add($"item.{i}.objset is absent");
                else
                {
                    if (tree.Get("length", -1) != binding.ObjectSets.Count)
                        errors.Add($"item.{i} object-set count differs");
                    for (int j = 0; j < binding.ObjectSets.Count; j++)
                        if (tree.Get(j.ToString(), "") != binding.ObjectSets[j].TargetName)
                            errors.Add($"item.{i} object-set {j} name differs");
                    tree.CloseScope();
                }
                if (tree.OpenScope("data"))
                {
                    if (tree.OpenScope("obj"))
                    {
                        if (tree.Get("length", -1) != binding.Objects.Count) errors.Add($"item.{i} object count differs");
                        for (int j = 0; j < binding.Objects.Count; j++)
                        {
                            if (!tree.OpenScope(j)) { errors.Add($"item.{i}.data.obj.{j} is absent"); continue; }
                            if (tree.Get("uid", "") != binding.Objects[j].TargetName.ToUpperInvariant() ||
                                tree.Get("rpk", 0) != unchecked((sbyte)binding.Objects[j].Rpk))
                                errors.Add($"item.{i} object {j} UID/rpk differs");
                            tree.CloseScope();
                        }
                        tree.CloseScope();
                    }
                    else if (binding.Objects.Count > 0) errors.Add($"item.{i}.data.obj is absent");
                    if (tree.OpenScope("tex"))
                    {
                        if (tree.Get("length", -1) != binding.TextureChanges.Count) errors.Add($"item.{i} texture-change count differs");
                        for (int j = 0; j < binding.TextureChanges.Count; j++)
                        {
                            if (!tree.OpenScope(j)) { errors.Add($"item.{i}.data.tex.{j} is absent"); continue; }
                            if (tree.Get("org", "") != binding.TextureChanges[j].OriginalName ||
                                tree.Get("chg", "") != binding.TextureChanges[j].ReplacementName)
                                errors.Add($"item.{i} texture change {j} differs");
                            tree.CloseScope();
                        }
                        tree.CloseScope();
                    }
                    else if (binding.TextureChanges.Count > 0) errors.Add($"item.{i}.data.tex is absent");
                    tree.CloseScope();
                }
                else if (binding.Objects.Count + binding.TextureChanges.Count > 0) errors.Add($"item.{i}.data is absent");
                tree.CloseScope();
            }
            tree.CloseScope();
        }
        var expectedItems = bindings.Select(x => x.ItemNumber).ToArray();
        if (costumeId != expectedCostumeId) errors.Add("costume ID differs");
        if (!costumeItems.SequenceEqual(expectedItems)) errors.Add("costume item list differs");
        if (itemCount != components.Count) errors.Add("item count differs");
        if (!itemNumbers.SequenceEqual(expectedItems)) errors.Add("item number list differs");
        return new(errors.Count == 0, entryName, costumeId, costumeItems,
            itemCount, itemNumbers, errors);
    }

    private static ModuleTableRoundTrip EmitModuleTable(string root,
        ModuleAnalysisResult result, List<StagedFile> emitted, ModuleRegistrationReference? reference)
    {
        int targetId = result.BuildPlan.ModuleBinding.TargetId!.Value;
        string character = NormalizeCharacter(result.NormalizedModule.Character);
        string costume = $"COS_{CharacterItemTargetResolver.CostumeId(result) + 1}";
        var writer = new ParameterTreeWriter();
        writer.PushScope("module");
        writer.PushScope(0);
        if (reference != null)
        {
            foreach (var field in ModuleTableReference.TargetFields(reference, result))
                if (int.TryParse(field.Value, out int numeric)) writer.Write(field.Key, numeric);
                else writer.Write(field.Key, field.Value);
        }
        else
        {
        // Registration follows the original converter, including the scope of
        // its count. Physical correctness cannot establish menu registration.
        writer.Write("attr", 2);
        writer.Write("chara", character);
        writer.Write("cos", costume);
        writer.Write("id", targetId);
        writer.Write("name", result.NormalizedModule.Identity!.DisplayName);
        writer.Write("ng", 0);
        writer.Write("shop_ed_day", 1);
        writer.Write("shop_ed_month", 1);
        writer.Write("shop_ed_year", 2029);
        writer.Write("shop_price", 0);
        writer.Write("shop_st_day", 9);
        writer.Write("shop_st_month", 3);
        writer.Write("shop_st_year", 2025);
        writer.Write("sort_index", (int)result.Aggregate.Module.SortIndex);
        }
        writer.PopScope();
        writer.Write("data_list.length", 1);
        writer.PopScope();

        using var tableStream = new MemoryStream();
        writer.Flush(tableStream);
        tableStream.Position = 0;
        string relativePath = "rom/gm_module_tbl.farc";
        string fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        using (var farc = new FarcArchive())
        {
            farc.Add("gm_module_id.bin", tableStream, leaveOpen: false);
            farc.Save(fullPath);
        }
        byte[] bytes = File.ReadAllBytes(fullPath);
        emitted.Add(new StagedFile(relativePath, "ModuleTable",
            Convert.ToHexString(SHA256.HashData(bytes))));
        if (reference != null) ModuleTableReference.Validate(fullPath, reference, result);
        return ValidateModuleTable(fullPath, targetId, character, costume,
            result.NormalizedModule.Identity!.DisplayName, result.Aggregate.Module.SortIndex);
    }

    internal static ModuleTableRoundTrip ValidateModuleTable(string path, int expectedId,
        string expectedCharacter, string expectedCostume, string expectedName, int expectedSortIndex)
    {
        var errors = new List<string>();
        using var farc = BinaryFile.Load<FarcArchive>(path);
        const string entryName = "gm_module_id.bin";
        if (!farc.FileNames.Contains(entryName, StringComparer.OrdinalIgnoreCase))
            return new(false, entryName, 0, -1, "", "", "", -1,
                new[] { "gm_module_id.bin is absent" });
        using var stream = farc.Open(entryName, EntryStreamMode.MemoryStream);
        var tree = ParameterTree.Load(stream);
        int length = -1;
        if (tree.OpenScope("data_list"))
        {
            errors.Add("data_list must be inside module, not at root");
            tree.CloseScope();
        }
        if (!tree.OpenScope("module")) errors.Add("module scope is absent");
        else
        {
        if (tree.OpenScope("data_list"))
        {
            length = tree.Get("length", -1);
            tree.CloseScope();
        }
        tree.CloseScope();
        }
        int id = -1;
        int sortIndex = -1;
        string character = "";
        string costume = "";
        string name = "";
        int shopEndYear = -1;
        int attribute = -1;
        if (!tree.OpenScope("module") || !tree.OpenScope(0))
            errors.Add("module.0 scope is absent");
        else
        {
            id = tree.Get("id", -1);
            attribute = tree.Get("attr", -1);
            character = tree.Get("chara", "");
            costume = tree.Get("cos", "");
            name = tree.Get("name", "");
            shopEndYear = tree.Get("shop_ed_year", -1);
            sortIndex = tree.Get("sort_index", -1);
            tree.CloseScope();
            tree.CloseScope();
        }
        if (length != 1) errors.Add($"length {length}, expected 1");
        if (id != expectedId) errors.Add($"id {id}, expected {expectedId}");
        if (character != expectedCharacter) errors.Add($"character {character}, expected {expectedCharacter}");
        if (costume != expectedCostume) errors.Add($"costume {costume}, expected {expectedCostume}");
        if (name != expectedName) errors.Add($"name {name}, expected {expectedName}");
        if (attribute is not (2 or 8)) errors.Add($"attr {attribute}, expected converter 2 or Complete Modules 8");
        if (shopEndYear != 2029) errors.Add($"shop_ed_year {shopEndYear}, expected 2029");
        if (sortIndex != expectedSortIndex) errors.Add($"sort_index {sortIndex}, expected {expectedSortIndex}");
        return new(errors.Count == 0, entryName, length, id, character, costume,
            name, sortIndex, errors);
    }

    internal static string NormalizeCharacter(string value) => value.ToUpperInvariant() switch
    {
        "MIK" => "MIKU",
        "RIN" => "RIN",
        "LEN" => "LEN",
        "LUK" => "LUKA",
        "NER" => "NERU",
        "HAK" => "HAKU",
        "KAI" => "KAITO",
        "MEI" => "MEIKO",
        "SAK" => "SAKINE",
        "TET" => "TETO",
        "EXT" => throw new InvalidDataException("EXT character modules are unsupported."),
        _ => throw new InvalidDataException("Unknown character code: " + value)
    };

    private static void Emit(string root, string relativePath, string kind, string content,
        List<StagedFile>? emitted)
    {
        string normalized = relativePath.Replace('\\', '/');
        string fullPath = Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        byte[] bytes = new UTF8Encoding(false).GetBytes(content);
        File.WriteAllBytes(fullPath, bytes);
        emitted?.Add(new StagedFile(normalized, kind,
            Convert.ToHexString(SHA256.HashData(bytes))));
    }

    private static string EscapeToml(string value) => value.Replace("\\", "\\\\")
        .Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: XRomConverter write-megamix-stage --analysis PATH --objset-csv PATH [--physical-stage DIRECTORY] [--module-table-reference FARC --reference-module ID] --output DIRECTORY");
        return 64;
    }
}
