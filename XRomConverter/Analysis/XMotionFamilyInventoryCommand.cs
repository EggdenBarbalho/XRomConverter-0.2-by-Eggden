using System.Text.Json;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.Databases;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Objects;
using MikuMikuLibrary.Objects.Extra.Blocks;
using XRomConverter.Content;

namespace XRomConverter.Analysis;

internal sealed record XMotionOccurrence(short ModuleId, short CosId, string Character,
    string ModuleName, short ItemNumber, string Slot, string ObjectSet, string Object,
    string MotionName, string ParentName, IReadOnlyList<string> NodeNames,
    AssetProvenance Provenance);
internal sealed record XMotionFamily(string Name, int Occurrences, int DistinctModules,
    IReadOnlyList<int> NodeCounts, IReadOnlyList<string> Parents,
    IReadOnlyList<XMotionOccurrence> Members);
internal sealed record XMotionFamilyInventory(string Source, int ModulesAttempted,
    int ModulesRead, IReadOnlyList<XMotionFamily> Families,
    IReadOnlyList<string> Diagnostics);

internal static class XMotionFamilyInventoryCommand
{
    private sealed record SetReference(short ModuleId, short CosId, string Character,
        string ModuleName, short ItemNumber, string Slot);

    public static bool IsRequested(string[] args) => args.Length > 0 &&
        args[0].Equals("inventory-x-motion-families", StringComparison.OrdinalIgnoreCase);

    public static int Run(string[] args)
    {
        if (args.Length != 4 || args[2] != "--output" || !Directory.Exists(args[1]))
        {
            Console.Error.WriteLine("Usage: XRomConverter inventory-x-motion-families DIVA_X_ROM --output JSON");
            return 64;
        }
        string sourcePath = Path.GetFullPath(args[1]);
        string output = Path.GetFullPath(args[3]);
        TextWriter original = Console.Out;
        ContentSource? source = null;
        try
        {
            Console.SetOut(TextWriter.Null);
            source = ContentSource.Load("diva-x-base", sourcePath);
        }
        finally { Console.SetOut(original); }

        using (source)
        {
            short[] moduleIds = source.Containers.SelectMany(container =>
                    container.ParameterDatabases.SelectMany(database => database.ModuleTable.Modules))
                .Select(module => module.ModuleID).Distinct().Order().ToArray();
            var references = BuildReferences(source);
            var occurrences = new List<XMotionOccurrence>();
            var diagnostics = new List<string>();
            int read = moduleIds.Length;
            foreach (string fileName in source.EnumerateObjectSetFiles()
                         .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!source.TryOpen(fileName, out Stream? stream) || stream == null)
                    {
                        diagnostics.Add("object-set-open: " + fileName);
                        continue;
                    }
                    using (stream)
                    using (var farc = BinaryFile.Load<FarcArchive>(stream))
                    {
                        string stem = Path.GetFileNameWithoutExtension(fileName);
                        var database = farc.Open<ObjectDatabase>(stem + ".osi");
                        var databaseSet = database.ObjectSets.Single();
                        var objectSet = farc.Open<ObjectSet>(stem + ".osd");
                        var owners = references.GetValueOrDefault(databaseSet.Id) ??
                            new List<SetReference> { new(-1, -1, "", "unreferenced", -1, "") };
                        foreach (var obj in objectSet.Objects)
                        foreach (var motion in obj.Skin?.Blocks.OfType<MotionBlock>() ?? Array.Empty<MotionBlock>())
                        foreach (var owner in owners)
                            occurrences.Add(new XMotionOccurrence(owner.ModuleId, owner.CosId,
                                owner.Character, owner.ModuleName, owner.ItemNumber, owner.Slot,
                                databaseSet.Name, obj.Name.ToLowerInvariant(), motion.Name,
                                motion.ParentName, motion.Nodes.Select(node => node.Name).ToArray(),
                                new AssetProvenance(source.Id, source.RootPath,
                                    source.FirstRead.AddonContentPackage.PackageID.ToString(), 0,
                                    fileName + "/" + obj.Name.ToLowerInvariant(), "FarcEntry", fileName)));
                    }
                }
                catch (Exception ex)
                {
                    Console.SetOut(original);
                    diagnostics.Add($"object set {fileName}: {ex.GetType().Name}: {ex.Message}");
                }
            }
            var families = occurrences.GroupBy(item => item.MotionName, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new XMotionFamily(group.Key, group.Count(),
                    group.Select(item => item.ModuleId).Distinct().Count(),
                    group.Select(item => item.NodeNames.Count).Distinct().Order().ToArray(),
                    group.Select(item => item.ParentName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                    group.OrderBy(item => item.ModuleId).ThenBy(item => item.ItemNumber).ToArray()))
                .ToArray();
            var report = new XMotionFamilyInventory(sourcePath, moduleIds.Length, read, families, diagnostics);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
        }
        Console.WriteLine(output);
        return 0;
    }

    private static Dictionary<uint, List<SetReference>> BuildReferences(ContentSource source)
    {
        var result = new Dictionary<uint, List<SetReference>>();
        string Name(short moduleId) => source.Containers.SelectMany(container => container.StringArrays)
            .Select(strings => strings.GetStringById(10010000 + moduleId))
            .FirstOrDefault(value => !string.IsNullOrEmpty(value)) ?? $"module_{moduleId}";
        foreach (var module in source.Containers.SelectMany(container =>
                     container.ParameterDatabases.SelectMany(database => database.ModuleTable.Modules))
                 .GroupBy(module => module.ModuleID).Select(group => group.First()))
        {
            int characterIndex = (int)module.Character;
            var costume = source.Containers.SelectMany(container => container.RobDatabases)
                .Where(database => database.CharacterItemTables.Count > characterIndex)
                .SelectMany(database => database.CharacterItemTables[characterIndex].Costumes)
                .FirstOrDefault(value => value.CostumeID == module.CosID);
            if (costume == null) continue;
            var items = source.Containers.SelectMany(container => container.RobDatabases)
                .Where(database => database.CharacterItemTables.Count > characterIndex)
                .SelectMany(database => database.CharacterItemTables[characterIndex].Items)
                .GroupBy(item => item.ItemNumber).ToDictionary(group => group.Key, group => group.First());
            foreach (var part in costume.Parts.Where(part => part.ItemNumber != 0))
            {
                if (!items.TryGetValue(part.ItemNumber, out var item)) continue;
                foreach (uint setId in item.ObjectSets)
                {
                    if (!result.TryGetValue(setId, out var owners)) result[setId] = owners = new();
                    owners.Add(new SetReference(module.ModuleID, module.CosID, module.Character.ToString(),
                        Name(module.ModuleID), item.ItemNumber, part.ItemSlot.ToString()));
                }
            }
        }
        return result;
    }
}
