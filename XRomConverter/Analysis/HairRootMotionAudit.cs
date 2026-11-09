using System.Text.Json;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.CriMw;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.Bones;
using MikuMikuLibrary.Databases;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Motions;

namespace XRomConverter.Analysis;

internal static class HairRootMotionAudit
{
    private static float Evaluate(KeySet c, float frame)
    {
        var keys = c.Keys;
        if (keys.Count == 0) return 0;
        if (frame <= keys[0].Frame) return keys[0].Value;
        if (frame >= keys[^1].Frame) return keys[^1].Value;
        int j = keys.FindIndex(k => k.Frame > frame);
        var a = keys[j - 1]; var b = keys[j];
        float span = b.Frame - a.Frame, t = (frame - a.Frame) / span;
        return c.HasTangents
            ? (2*t*t*t-3*t*t+1)*a.Value+(t*t*t-2*t*t+t)*span*a.Tangent+(-2*t*t*t+3*t*t)*b.Value+(t*t*t-t*t)*span*b.Tangent
            : a.Value+t*(b.Value-a.Value);
    }

    public static int Run(string[] args)
    {
        if (args.Length != 4) return 64;
        using var game = BinaryFile.Load<CpkArchive>(Path.Combine(args[1], "diva_main.cpk"));
        var skeleton = game.Open<BoneData>("rom/bone_data.bin").Skeletons.Single(x => x.Name == "MIK");
        using var dbArchive = game.Open<FarcArchive>("rom_ps4/rom/rob/mot_db.farc");
        var db = dbArchive.Open<MotionDatabase>("mot_db.bin");
        using var archive = BinaryFile.Load<FarcArchive>(args[2]);
        using var stream = archive.Open(archive.FileNames.Single(), EntryStreamMode.MemoryStream);
        var motions = new MotionSet(); motions.Load(stream, skeleton, db);
        var result = motions.Motions.Select(m => new {
            m.FrameCount,
            Channels = m.Bind().BoneBindings.Where(b => b.Name.Contains("kao") || b.Name.Contains("kubi"))
                .Select(b => new { b.Name,
                    Rotation = b.Rotation == null ? null : new[] { b.Rotation.X, b.Rotation.Y, b.Rotation.Z }
                        .Select(c => new { Keys = c.Keys.Select(k => new { k.Frame, k.Value, k.Tangent }).ToArray(),
                            Samples = Enumerable.Range(0, m.FrameCount + 1).Where(f => f % 60 == 0 || Math.Abs(f - 4080) <= 120 || Math.Abs(f - 10440) <= 120)
                                .Select(f => new { Frame = f, Value = Evaluate(c, f) }).ToArray() }).ToArray()
                }).ToArray()
        }).ToArray();
        File.WriteAllText(args[3], JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(args[3]);
        return 0;
    }
}
