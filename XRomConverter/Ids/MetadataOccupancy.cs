using System.Text.Json;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.CriMw;
using MikuMikuLibrary.Databases;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Parameters;

namespace XRomConverter.Ids;

internal sealed record OccupiedMetadataId(int Source, string Namespace, string Scope, string Name, uint Id);
internal sealed record MetadataOccupancySnapshot(int SchemaVersion, string Installation, bool Complete,
    IReadOnlyList<PhysicalInputStamp> DiscoveryFiles, IReadOnlyList<PhysicalDatabaseSource> Sources,
    IReadOnlyList<OccupiedMetadataId> Ids, IReadOnlyList<string> Diagnostics);

internal static class MetadataOccupancy
{
    private static bool IsTable(string name) => name.EndsWith(".farc", StringComparison.OrdinalIgnoreCase) &&
        (Path.GetFileName(name).Contains("gm_module_tbl", StringComparison.OrdinalIgnoreCase) ||
         Path.GetFileName(name).Contains("chritm_prop", StringComparison.OrdinalIgnoreCase));
    private static bool IsSpriteDb(string name) => name.EndsWith("spr_db.bin", StringComparison.OrdinalIgnoreCase);
    public static IEnumerable<string> Discover(string root) => Directory.EnumerateFiles(root, "*.cpk", SearchOption.TopDirectoryOnly)
        .Concat(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(x => IsTable(x) || IsSpriteDb(x)))
        .Order(StringComparer.OrdinalIgnoreCase);

    public static MetadataOccupancySnapshot Scan(string installation)
    {
        string root = Path.GetFullPath(installation);
        if (!File.Exists(Path.Combine(root, "diva_main.cpk"))) throw new InvalidDataException("The installation has no diva_main.cpk.");
        var sources = new List<PhysicalDatabaseSource>();
        var ids = new List<OccupiedMetadataId>();
        var errors = new List<string>();
        var stamps = Discover(root).Select(path => { var file = new FileInfo(path); return new PhysicalInputStamp(path, file.Length, file.LastWriteTimeUtc); }).ToArray();
        void Read(string path, string? entry, byte[] bytes)
        {
            string name = entry ?? path;
            int source = sources.Count;
            void Add(string ns, string scope, string label, int id)
            { if (id >= 0) ids.Add(new(source, ns, scope, label, (uint)id)); }
            if (IsSpriteDb(name))
            {
                var db = BinaryFile.Load<SpriteDatabase>(new MemoryStream(bytes, writable: false));
                foreach (var set in db.SpriteSets)
                {
                    ids.Add(new(source, "sprite-set", "global", set.Name, set.Id));
                    ids.AddRange(set.Sprites.Select(x => new OccupiedMetadataId(source, "sprite", "global", x.Name, x.Id)));
                    ids.AddRange(set.Textures.Select(x => new OccupiedMetadataId(source, "sprite-texture", "global", x.Name, x.Id)));
                }
                sources.Add(new(path, entry, "sprite-database", PhysicalOccupancy.Hash(bytes)));
                return;
            }
            using var archive = BinaryFile.Load<FarcArchive>(new MemoryStream(bytes, writable: false));
            foreach (string tableName in archive.FileNames)
            {
                bool module = tableName.Equals("gm_module_id.bin", StringComparison.OrdinalIgnoreCase);
                bool item = tableName.EndsWith("itm_tbl.txt", StringComparison.OrdinalIgnoreCase);
                if (!module && !item) continue;
                using var stream = archive.Open(tableName, EntryStreamMode.MemoryStream);
                var tree = ParameterTree.Load(stream);
                string scope = item ? tableName[..^"itm_tbl.txt".Length].ToUpperInvariant() : "global";
                void ReadScope(string section, string field, string ns)
                {
                    if (!tree.OpenScope(section)) return;
                    foreach (string key in tree.Keys.ToArray())
                    {
                        if (!tree.OpenScope(key)) continue;
                        Add(ns, scope, tableName + ":" + section + "." + key, tree.Get(field, -1));
                        tree.CloseScope();
                    }
                    tree.CloseScope();
                }
                if (module) ReadScope("module", "id", "module");
                else { ReadScope("cos", "id", "costume"); ReadScope("item", "no", "item"); }
            }
            sources.Add(new(path, entry, "module-item-table", PhysicalOccupancy.Hash(bytes)));
        }
        foreach (var stamp in stamps)
        {
            try
            {
                if (!stamp.Path.EndsWith(".cpk", StringComparison.OrdinalIgnoreCase)) { Read(stamp.Path, null, File.ReadAllBytes(stamp.Path)); continue; }
                using var cpk = BinaryFile.Load<CpkArchive>(stamp.Path);
                foreach (string entry in cpk.FileNames.Where(x => IsTable(x) || IsSpriteDb(x)).Order(StringComparer.OrdinalIgnoreCase))
                    try
                    {
                        using var stream = cpk.Open(entry, EntryStreamMode.MemoryStream);
                        using var buffer = new MemoryStream(); stream.CopyTo(buffer); Read(stamp.Path, entry, buffer.ToArray());
                    }
                    catch (Exception ex) { errors.Add(stamp.Path + "!" + entry + ": " + ex.Message); }
            }
            catch (Exception ex) { errors.Add(stamp.Path + ": " + ex.Message); }
        }
        var snapshot = new MetadataOccupancySnapshot(1, root, errors.Count == 0 &&
            new[] { "module", "costume", "item", "sprite-set", "sprite", "sprite-texture" }.All(ns => ids.Any(x => x.Namespace == ns && sources[x.Source].Entry != null)),
            stamps, sources, ids, errors);
        ValidateFresh(snapshot); return snapshot;
    }

    public static MetadataOccupancySnapshot LoadFresh(string path)
    {
        var snapshot = JsonSerializer.Deserialize<MetadataOccupancySnapshot>(File.ReadAllBytes(path), PhysicalOccupancy.Json)
            ?? throw new InvalidDataException("Empty metadata occupancy.");
        if (snapshot.SchemaVersion != 1 || !snapshot.Complete || snapshot.Diagnostics.Count != 0)
            throw new InvalidDataException("Metadata occupancy is incomplete or unsupported.");
        ValidateFresh(snapshot); return snapshot;
    }

    private static void ValidateFresh(MetadataOccupancySnapshot snapshot)
    {
        if (!Discover(snapshot.Installation).SequenceEqual(snapshot.DiscoveryFiles.Select(x => x.Path), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Metadata occupancy is stale: discovered files changed.");
        foreach (var stamp in snapshot.DiscoveryFiles)
        {
            var file = new FileInfo(stamp.Path);
            if (file.Length != stamp.Length || file.LastWriteTimeUtc != stamp.LastWriteUtc) throw new InvalidDataException("Metadata occupancy is stale: " + stamp.Path);
        }
        foreach (var source in snapshot.Sources.Where(x => x.Entry == null))
            if (PhysicalOccupancy.Hash(File.ReadAllBytes(source.Path)) != source.Sha256) throw new InvalidDataException("Metadata source content changed: " + source.Path);
    }
}

internal static class MetadataOccupancyCommand
{
    public static bool IsRequested(string[] args) => args.Length > 0 && args[0] == "scan-metadata-ids";
    public static int Run(string[] args)
    {
        if (args.Length != 4 || args[2] != "--output") return 64;
        try
        {
            var snapshot = MetadataOccupancy.Scan(args[1]);
            string path = Path.GetFullPath(args[3]); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(snapshot, PhysicalOccupancy.Json));
            Console.WriteLine($"{path}\nSources: {snapshot.Sources.Count}; declarations: {snapshot.Ids.Count}; complete: {snapshot.Complete}");
            return snapshot.Complete ? 0 : 2;
        }
        catch (Exception ex) { Console.Error.WriteLine("Metadata scan failed: " + ex.Message); return 2; }
    }
}
