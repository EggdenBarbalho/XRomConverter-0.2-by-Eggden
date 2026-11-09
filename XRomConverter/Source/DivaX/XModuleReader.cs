using FirstLib.FirstRead;
using FirstLib.FirstRead.Common;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.Databases;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Objects;
using MikuMikuLibrary.Objects.Extra;
using MikuMikuLibrary.Objects.Extra.Blocks;
using MikuMikuLibrary.Objects.Extra.Parameters;
using XRomConverter.Analysis;
using XRomConverter.Content;
using XRomConverter.Transformations.Skin;

namespace XRomConverter.Source.DivaX;

internal sealed class XModuleReader
{
    public XModuleAggregate Read(ContentSource source, short moduleId) =>
        Read(source, Array.Empty<ContentSource>(), moduleId);

    public XModuleAggregate Read(ContentSource source,
        IReadOnlyList<ContentSource> dependencySources, short moduleId)
    {
        var diagnostics = new List<Diagnostic>();
        var containers = source.Containers;
        var located = (from pair in containers.Select((value, index) => (value, index))
                       from candidate in pair.value.ParameterDatabases.SelectMany(x => x.ModuleTable.Modules)
                       where candidate.ModuleID == moduleId
                       select (candidate, pair.index)).ToList();
        if (located.Count != 1)
            throw new InvalidDataException($"Expected exactly one ModuleInfo for {moduleId}, found {located.Count}.");

        var (module, moduleContainer) = located[0];
        int characterIndex = (int)module.Character;
        var costumes = containers.SelectMany(x => x.RobDatabases)
            .Where(x => x.CharacterItemTables.Count > characterIndex)
            .SelectMany(x => x.CharacterItemTables[characterIndex].Costumes)
            .Where(x => x.CostumeID == module.CosID).ToList();
        if (costumes.Count != 1)
            throw new InvalidDataException($"Expected exactly one costume {module.CosID}, found {costumes.Count}.");

        var itemGroups = containers.SelectMany(x => x.RobDatabases)
            .Where(x => x.CharacterItemTables.Count > characterIndex)
            .SelectMany(x => x.CharacterItemTables[characterIndex].Items)
            .GroupBy(x => x.ItemNumber).ToDictionary(x => x.Key, x => x.ToList());

        var enabledSources = new[] { source }.Concat(dependencySources)
            .Distinct().ToArray();
        var objectSetIndex = BuildObjectSetIndex(enabledSources, diagnostics);
        var textureNames = objectSetIndex.Values.SelectMany(x => x.TextureNames)
            .GroupBy(x => x.Key).ToDictionary(x => x.Key, x => x.First().Value);
        var components = new List<XComponentRecord>();
        foreach (var part in costumes[0].Parts.Where(x => x.ItemNumber != 0))
        {
            if (!itemGroups.TryGetValue(part.ItemNumber, out var matching) || matching.Count == 0)
            {
                diagnostics.Add(new Diagnostic("ITEM_NOT_UNIQUE", "error",
                    $"Item {part.ItemNumber} resolved to {matching?.Count ?? 0} records."));
                continue;
            }
            if (matching.Count > 1)
                diagnostics.Add(new Diagnostic("ITEM_DUPLICATE_SOURCE_RECORD", "warning",
                    $"Item {part.ItemNumber} occurs {matching.Count} times; using the first source-priority record."));
            var item = matching[0];
            var sets = new List<XObjectSetRecord>();
            foreach (uint setId in item.ObjectSets)
            {
                if (!objectSetIndex.TryGetValue(setId, out var indexed))
                {
                    diagnostics.Add(new Diagnostic("OBJECT_SET_NOT_FOUND", "error",
                        $"Object set hash {setId} referenced by item {item.ItemNumber} was not found."));
                    continue;
                }
                sets.Add(ReadObjectSet(indexed, diagnostics));
            }
            var changes = item.TextureChanges.Select(x => new XTextureChange(
                x.OriginalTextureID, textureNames.GetValueOrDefault(x.OriginalTextureID),
                x.ReplacementTextureID, textureNames.GetValueOrDefault(x.ReplacementTextureID))).ToArray();
            var objectNames = sets.SelectMany(x => x.Objects).GroupBy(x => x.SourceId)
                .ToDictionary(x => x.Key, x => x.First().SourceName);
            var objects = item.Objects.Select(x => new XItemObjectReference(x.ObjectID,
                x.ObjectID == MikuMikuLibrary.Hashes.MurmurHash.Calculate("NULL")
                    ? "NULL" : objectNames.GetValueOrDefault(x.ObjectID), x.RPK)).ToArray();
            components.Add(new XComponentRecord((int)part.ItemSlot, part.ItemSlot.ToString(),
                item.ItemNumber, item.SubID.ToString(), item.Attribute, item.U04,
                item.Type, item.DestID, objects, sets, changes,
                Provenance(source, moduleContainer, $"firstread/RBDB/{module.Character}/item/{item.ItemNumber}", "FirstReadRecord")));
        }

        string name = containers.SelectMany(x => x.StringArrays)
            .Select(x => x.GetStringById(10010000 + module.ModuleID))
            .FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? $"module_{module.ModuleID}";
        var sprites = containers.SelectMany(x => x.Auth2DFlist)
            .Where(x => x.Equals($"spr_mdl_thmb{module.ModuleID:D3}l.farc", StringComparison.OrdinalIgnoreCase)
                     || x.Equals($"spr_mdl_thmb{module.ModuleID:D3}s.farc", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var presentation = new XPresentationRecord(sprites,
            new Dictionary<string, string> { ["source"] = name });
        var moduleRecord = new XModuleRecord(module.ModuleID, module.CosID,
            module.Character.ToString(), module.SortIndex, name,
            Provenance(source, moduleContainer, $"firstread/PRDB/module/{module.ModuleID}", "FirstReadRecord"));
        return new XModuleAggregate(moduleRecord, components, presentation, diagnostics);
    }

    private static Dictionary<uint, IndexedObjectSet> BuildObjectSetIndex(
        IReadOnlyList<ContentSource> sources, List<Diagnostic> diagnostics)
    {
        var result = new Dictionary<uint, IndexedObjectSet>();
        foreach (ContentSource source in sources)
        foreach (string fileName in source.EnumerateObjectSetFiles().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!source.TryOpen(fileName, out Stream? stream) || stream == null) continue;
            using (stream)
            using (var farc = BinaryFile.Load<FarcArchive>(stream))
            {
                string stem = Path.GetFileNameWithoutExtension(fileName);
                var db = farc.Open<ObjectDatabase>(stem + ".osi");
                var tex = farc.Open<TextureDatabase>(stem + ".txi");
                var set = db.ObjectSets.Single();
                var candidate = new IndexedObjectSet(set.Id, set.Name, fileName, source,
                    tex.Textures.ToDictionary(x => x.Id, x => x.Name));
                if (!result.TryAdd(set.Id, candidate))
                {
                    var selected = result[set.Id];
                    diagnostics.Add(new Diagnostic("OBJECT_SET_SHADOWED", "warning",
                        $"Object set hash {set.Id} from '{source.Id}:{fileName}' was shadowed by higher-priority '{selected.Source.Id}:{selected.FileName}'.",
                        fileName));
                }
            }
        }
        return result;
    }

    private static XObjectSetRecord ReadObjectSet(IndexedObjectSet indexed,
        List<Diagnostic> diagnostics)
    {
        ContentSource source = indexed.Source;
        if (!source.TryOpen(indexed.FileName, out Stream? stream) || stream == null)
            throw new FileNotFoundException(indexed.FileName);
        using (stream)
        using (var farc = BinaryFile.Load<FarcArchive>(stream))
        {
            string stem = Path.GetFileNameWithoutExtension(indexed.FileName);
            var set = farc.Open<ObjectSet>(stem + ".osd");
            var setProv = Provenance(source, 0, indexed.FileName, "ContentSource");
            var objects = new List<XObjectRecord>();
            foreach (var obj in set.Objects)
            {
                string sourceName = obj.Name.ToLowerInvariant();
                string ospName = "ext_skp_" + sourceName + ".osp";
                XSkinParameterAsset skinAsset;
                // Deliberately source-bound: never search another ContentSource here.
                if (source.TryOpen(ospName, out Stream? ospStream) && ospStream != null)
                {
                    using (ospStream)
                    {
                        using var raw = new MemoryStream();
                        ospStream.CopyTo(raw);
                        var parameters = SkinParameterTextCodec.ConvertOsp(raw.ToArray());
                        skinAsset = new XSkinParameterAsset(ospName, true,
                            Provenance(source, 0, ospName, "ContentSource", indexed.FileName), parameters);
                    }
                }
                else skinAsset = new XSkinParameterAsset(ospName, false, null, Array.Empty<XSkinParameter>());

                IEnumerable<IBlock> sourceBlocks = obj.Skin == null
                    ? Array.Empty<IBlock>()
                    : obj.Skin.Blocks;
                var blocks = sourceBlocks.Select(block => block switch
                {
                    OsageBlock osg => new XSkinBlock("OSG", osg.Name, osg.ExternalName, osg.ParentName,
                        osg.Nodes.Select(node => node.Name).ToArray()),
                    MotionBlock mot => new XSkinBlock("MOT", mot.Name, null, mot.ParentName,
                        mot.Nodes.Select(node => node.Name).ToArray()),
                    _ => new XSkinBlock(block.Signature, null, null, null, Array.Empty<string>())
                }).ToArray();
                objects.Add(new XObjectRecord(obj.Id, sourceName, blocks, skinAsset,
                    Provenance(source, 0, indexed.FileName + "/" + sourceName, "FarcEntry", indexed.FileName)));
            }
            var textures = indexed.TextureNames.Select(x => new XTextureReference(x.Key, x.Value,
                Provenance(source, 0, indexed.FileName + "/" + stem + ".txi/" + x.Value,
                    "FarcEntry", indexed.FileName))).ToArray();
            return new XObjectSetRecord(indexed.Id, indexed.Name, objects, textures, setProv);
        }
    }

    private static AssetProvenance Provenance(ContentSource source, int container,
        string path, string storage, string? parent = null) =>
        new(source.Id, source.RootPath,
            source.FirstRead.AddonContentPackage.PackageID.ToString(), container,
            path, storage, parent);

    private sealed record IndexedObjectSet(uint Id, string Name, string FileName,
        ContentSource Source,
        Dictionary<uint, string> TextureNames);
}
