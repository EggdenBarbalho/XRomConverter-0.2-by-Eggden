using System.Security.Cryptography;
using System.Text.Json;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Objects;
using MikuMikuLibrary.Objects.Extra.Blocks;
using MikuMikuLibrary.Textures;

namespace XRomConverter.Analysis;

internal sealed record PairBlock(string Signature, string Name, string? ExternalName,
    string? ParentName, int Nodes, IReadOnlyList<string> NodeNames,
    IReadOnlyList<string> Expressions, float[] Position, float[] Rotation, float[] Scale);
internal sealed record PairMaterial(string Name, string Shader,
    IReadOnlyList<uint> TextureIds);
internal sealed record PairObject(string Name, uint Id, int Meshes, int Bones,
    IReadOnlyList<PairMaterial> Materials, IReadOnlyList<PairBlock> Blocks, string RigDetails);
internal sealed record ObjectSetPairInspection(string Archive, string Sha256,
    IReadOnlyList<string> Entries, string? ObjectEntry, string? TextureEntry,
    string? Format, IReadOnlyList<uint> ObjectSetTextureIds,
    IReadOnlyList<uint> TexturePayloadIds, IReadOnlyList<uint> UnresolvedMaterialTextureIds,
    IReadOnlyList<PairObject> Objects, string? Error);

internal static class ObjectSetPairInspectionCommand
{
    public static bool IsRequested(string[] args) => args.Length > 0 &&
        args[0].Equals("inspect-objectset-pairs", StringComparison.OrdinalIgnoreCase);

    public static int Run(string[] args)
    {
        if (args.Length != 4 || args[2] != "--output" || !Directory.Exists(args[1])) return 64;
        string root = Path.GetFullPath(args[1]);
        string output = Path.GetFullPath(args[3]);
        var results = Directory.EnumerateFiles(root, "*.farc", SearchOption.AllDirectories)
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}objset{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(Inspect).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(results, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
        Console.WriteLine(output);
        return results.Any(result => result.Error != null) ? 2 : 0;
    }

    private static ObjectSetPairInspection Inspect(string path)
    {
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        try
        {
            using var farc = BinaryFile.Load<FarcArchive>(path);
            string[] entries = farc.FileNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
            string? objectEntry = entries.FirstOrDefault(name => name.EndsWith(".osd", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("_obj.bin", StringComparison.OrdinalIgnoreCase));
            string? textureEntry = entries.FirstOrDefault(name => name.EndsWith(".txd", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("_tex.bin", StringComparison.OrdinalIgnoreCase));
            if (objectEntry == null || textureEntry == null)
                return new(path, hash, entries, objectEntry, textureEntry, null, Array.Empty<uint>(),
                    Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<PairObject>(), "OSD/TXD pair is incomplete.");
            var objectSet = farc.Open<ObjectSet>(objectEntry);
            var textureSet = farc.Open<TextureSet>(textureEntry);
            uint[] materialIds = objectSet.Objects.SelectMany(obj => obj.Materials)
                .SelectMany(material => material.MaterialTextures)
                .Where(texture => texture != null && texture.TextureId != uint.MaxValue)
                .Select(texture => texture.TextureId).Distinct().Order().ToArray();
            uint[] knownIds = objectSet.TextureIds.Distinct().Order().ToArray();
            uint[] unresolved = materialIds.Except(knownIds).ToArray();
            var objects = objectSet.Objects.Select(obj => new PairObject(obj.Name, obj.Id,
                obj.Meshes.Count, obj.Skin?.Bones.Count ?? 0,
                obj.Materials.Select(material => new PairMaterial(material.Name, material.ShaderName,
                    material.MaterialTextures.Where(texture => texture != null && texture.TextureId != uint.MaxValue)
                        .Select(texture => texture.TextureId).ToArray())).ToArray(),
                obj.Skin?.Blocks.Select(block => new PairBlock(block.Signature,
                    block is NodeBlock node ? node.Name : "",
                    block is OsageBlock osage ? osage.ExternalName : null,
                    block is NodeBlock parented ? parented.ParentName : null,
                    block switch { OsageBlock osg => osg.Nodes.Count, MotionBlock motion => motion.Nodes.Count, _ => 0 },
                    block switch
                    {
                        OsageBlock osg => osg.Nodes.Select(node => node.Name).ToArray(),
                        MotionBlock motion => motion.Nodes.Select(node => node.Name).ToArray(),
                        _ => Array.Empty<string>()
                    }, block is ExpressionBlock expression ? expression.Expressions.ToArray() : Array.Empty<string>(),
                    block is NodeBlock t ? new[]{t.Position.X,t.Position.Y,t.Position.Z} : Array.Empty<float>(),
                    block is NodeBlock r ? new[]{r.Rotation.X,r.Rotation.Y,r.Rotation.Z} : Array.Empty<float>(),
                    block is NodeBlock s ? new[]{s.Scale.X,s.Scale.Y,s.Scale.Z} : Array.Empty<float>()))
                    .ToArray() ?? Array.Empty<PairBlock>(), JsonSerializer.Serialize(new { bones = obj.Skin?.Bones.Select(b => new { b.Name, b.Id, b.IsEx, Parent = b.Parent?.Name, b.InverseBindPoseMatrix }).ToArray(), osage = obj.Skin?.Blocks.OfType<OsageBlock>().Select(b => new { b.Name, b.ExternalName, b.Nodes, b.InternalSkinParameter }).ToArray() }, new JsonSerializerOptions { IncludeFields = true }))).ToArray();
            return new(path, hash, entries, objectEntry, textureEntry, objectSet.Format.ToString(),
                objectSet.TextureIds.ToArray(), textureSet.Textures.Select(texture => texture.Id).ToArray(),
                unresolved, objects, null);
        }
        catch (Exception exception)
        {
            return new(path, hash, Array.Empty<string>(), null, null, null, Array.Empty<uint>(),
                Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<PairObject>(),
                exception.GetType().Name + ": " + exception.Message);
        }
    }
}
