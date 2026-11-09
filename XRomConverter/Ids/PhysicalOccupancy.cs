using System.Security.Cryptography;
using System.Text.Json;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.CriMw;
using MikuMikuLibrary.Databases;
using MikuMikuLibrary.IO;

namespace XRomConverter.Ids;

internal sealed record PhysicalInputStamp(string Path, long Length, DateTime LastWriteUtc);
internal sealed record PhysicalDatabaseSource(string Path, string? Entry, string Kind, string Sha256);
internal sealed record OccupiedPhysicalObject(string Name, uint Id);
internal sealed record OccupiedPhysicalSet(int Source, string Name, uint Id,
    string Archive, IReadOnlyList<OccupiedPhysicalObject> Objects);
internal sealed record OccupiedPhysicalTexture(int Source, string Name, uint Id);
internal sealed record PhysicalOccupancySnapshot(int SchemaVersion, string Installation,
    string Scope, bool Complete, IReadOnlyList<PhysicalInputStamp> DiscoveryFiles,
    IReadOnlyList<PhysicalDatabaseSource> Sources, IReadOnlyList<OccupiedPhysicalSet> ObjectSets,
    IReadOnlyList<OccupiedPhysicalTexture> Textures, IReadOnlyList<string> Diagnostics);

internal static class PhysicalOccupancy
{
    internal static readonly JsonSerializerOptions Json = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static IEnumerable<string> Discover(string root) =>
        Directory.EnumerateFiles(root, "*.cpk", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(root, "*.bin", SearchOption.AllDirectories)
                .Where(IsDatabase)).Order(StringComparer.OrdinalIgnoreCase);

    private static bool IsDatabase(string path) =>
        Path.GetFileName(path).EndsWith("obj_db.bin", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).EndsWith("tex_db.bin", StringComparison.OrdinalIgnoreCase);

    private static bool IsObject(string path) => Path.GetFileName(path).EndsWith("obj_db.bin", StringComparison.OrdinalIgnoreCase);

    public static PhysicalOccupancySnapshot Scan(string installation)
    {
        string root = Path.GetFullPath(installation);
        if (!File.Exists(Path.Combine(root, "diva_main.cpk")))
            throw new InvalidDataException("The installation has no diva_main.cpk.");
        var sources = new List<PhysicalDatabaseSource>();
        var sets = new List<OccupiedPhysicalSet>();
        var textures = new List<OccupiedPhysicalTexture>();
        var diagnostics = new List<string>();
        string[] discovered = Discover(root).ToArray();
        var stamps = discovered.Select(path =>
        {
            var file = new FileInfo(path);
            return new PhysicalInputStamp(path, file.Length, file.LastWriteTimeUtc);
        }).ToArray();

        void ReadDatabase(string path, string? entry, byte[] bytes)
        {
            int source = sources.Count;
            bool objects = IsObject(entry ?? path);
            using var stream = new MemoryStream(bytes, writable: false);
            if (objects)
            {
                var database = BinaryFile.Load<ObjectDatabase>(stream);
                sets.AddRange(database.ObjectSets.Select(set => new OccupiedPhysicalSet(source,
                    set.Name, set.Id, set.ArchiveFileName ?? "",
                    set.Objects.Select(obj => new OccupiedPhysicalObject(obj.Name, obj.Id)).ToArray())));
            }
            else
            {
                var database = BinaryFile.Load<TextureDatabase>(stream);
                textures.AddRange(database.Textures.Select(texture => new OccupiedPhysicalTexture(source, texture.Name ?? "", texture.Id)));
            }
            sources.Add(new(path, entry, objects ? "object-database" : "texture-database", Hash(bytes)));
        }

        foreach (string path in discovered)
        {
            try
            {
                if (path.EndsWith(".cpk", StringComparison.OrdinalIgnoreCase))
                {
                    using var cpk = BinaryFile.Load<CpkArchive>(path);
                    foreach (string entry in cpk.FileNames.Where(IsDatabase).Order(StringComparer.OrdinalIgnoreCase))
                    {
                        try
                        {
                            using var input = cpk.Open(entry, EntryStreamMode.MemoryStream);
                            using var data = new MemoryStream();
                            input.CopyTo(data);
                            ReadDatabase(path, entry, data.ToArray());
                        }
                        catch (Exception ex) { diagnostics.Add(path + "!" + entry + ": " + ex.Message); }
                    }
                }
                else ReadDatabase(path, null, File.ReadAllBytes(path));
            }
            catch (Exception ex) { diagnostics.Add(path + ": " + ex.Message); }
        }
        // Reserve all declared definitions, including disabled mods and backup
        // trees. The result does not infer a loader winner from directory order.
        var result = new PhysicalOccupancySnapshot(1, root, "all-discovered-database-definitions-including-disabled-mods",
            diagnostics.Count == 0 && sources.Any(x => x.Entry != null && x.Kind == "object-database") &&
            sources.Any(x => x.Entry != null && x.Kind == "texture-database"), stamps, sources, sets, textures, diagnostics);
        ValidateFresh(result);
        return result;
    }

    public static PhysicalOccupancySnapshot LoadFresh(string path)
    {
        var result = JsonSerializer.Deserialize<PhysicalOccupancySnapshot>(File.ReadAllBytes(path), Json)
            ?? throw new InvalidDataException("Empty physical occupancy snapshot.");
        if (result.SchemaVersion != 1 || !result.Complete || result.Diagnostics.Count != 0)
            throw new InvalidDataException("Physical occupancy snapshot is incomplete or unsupported.");
        ValidateFresh(result);
        return result;
    }

    private static void ValidateFresh(PhysicalOccupancySnapshot snapshot)
    {
        string[] actual = Discover(snapshot.Installation).ToArray();
        if (!actual.SequenceEqual(snapshot.DiscoveryFiles.Select(x => x.Path), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Physical occupancy snapshot is stale: discovered files changed.");
        foreach (var stamp in snapshot.DiscoveryFiles)
        {
            var file = new FileInfo(stamp.Path);
            if (file.Length != stamp.Length || file.LastWriteTimeUtc != stamp.LastWriteUtc)
                throw new InvalidDataException("Physical occupancy snapshot is stale: " + stamp.Path);
        }
        foreach (var source in snapshot.Sources.Where(x => x.Entry == null))
            if (Hash(File.ReadAllBytes(source.Path)) != source.Sha256)
                throw new InvalidDataException("Physical database content changed: " + source.Path);
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}

internal static class PhysicalOccupancyCommand
{
    public static bool IsRequested(string[] args) => args.Length > 0 && args[0] == "scan-physical-ids";
    public static int Run(string[] args)
    {
        if (args.Length != 4 || args[2] != "--output" || !Directory.Exists(args[1])) return 64;
        try
        {
            var result = PhysicalOccupancy.Scan(args[1]);
            string output = Path.GetFullPath(args[3]);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllBytes(output, JsonSerializer.SerializeToUtf8Bytes(result, PhysicalOccupancy.Json));
            Console.WriteLine($"{output}\nDatabases: {result.Sources.Count}; object-set definitions: {result.ObjectSets.Count}; texture definitions: {result.Textures.Count}; complete: {result.Complete}");
            return result.Complete ? 0 : 2;
        }
        catch (Exception ex) { Console.Error.WriteLine("Physical scan failed: " + ex.Message); return 2; }
    }
}
