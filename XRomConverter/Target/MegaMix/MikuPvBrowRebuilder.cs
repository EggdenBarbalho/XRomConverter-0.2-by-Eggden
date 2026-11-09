using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.CriMw;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.Bones;
using MikuMikuLibrary.Databases;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Motions;
using XRomConverter.Content;

namespace XRomConverter.Target.MegaMix;

internal static class MikuPvBrowRebuildCommand
{
    internal static int Run(string[] args)
    {
        string? staging = null;
        try
        {
            if (args.Length != 4) throw new ArgumentException("Usage: rebuild-miku-pv-brows X_ROM GAME NEW_OUTPUT");
            string game = Path.GetFullPath(args[2]), output = Path.GetFullPath(args[3]);
            if (Directory.Exists(output) || File.Exists(output)) throw new InvalidDataException("Output already exists.");
            if (output.StartsWith(game.TrimEnd('\\','/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Use an output outside the installation.");
            using var source = ContentSource.Load("brow-training-X", args[1]);
            using var main = BinaryFile.Load<CpkArchive>(Path.Combine(game, "diva_main.cpk"));
            using var dlc = BinaryFile.Load<CpkArchive>(Path.Combine(game, "diva_dlc00.cpk"));
            var bones = main.Open<BoneData>("rom/bone_data.bin");
            var skeleton = bones.Skeletons.Single(x => x.Name == "MIK");
            using var dbArchive = main.Open<FarcArchive>("rom_ps4/rom/rob/mot_db.farc");
            var db = dbArchive.Open<MotionDatabase>("mot_db.bin");
            var xBones = BinaryFile.Load<BoneData>(source.OpenRequired("bone_data.bon"));
            var xSkeleton = xBones.Skeletons.Single(x => x.Name == "MIK");
            using var genericArchive = main.Open<FarcArchive>("rom/rob/mot_MIK.farc");
            var generic = Read(genericArchive, skeleton, db);
            var names = db.GetMotionSetInfo("MIK").Motions.Select(x => x.Name).ToArray();
            using var xArchive = BinaryFile.Load<FarcArchive>(source.OpenRequired("mot_mik.farc"));
            var pairs = new List<(Motion Arcade, Motion DivaX)>();
            foreach (string entry in xArchive.FileNames.Where(x => x.EndsWith(".mot", StringComparison.OrdinalIgnoreCase)))
            {
                using var stream = xArchive.Open(entry, EntryStreamMode.MemoryStream);
                var motion = new Motion(); motion.Load(stream, xSkeleton);
                int index = Array.FindIndex(names, x => x == motion.Name);
                if (index >= 0 && names[index].Contains("_FACE_")) pairs.Add((generic.Motions[index], motion));
            }
            var profile = EyebrowRetargetProfile.Learn("MIK", pairs) ?? throw new InvalidDataException("No improved eyebrow profile could be learned.");
            if (profile.BoneNames.Count != 6) throw new InvalidDataException("Expected the six established eyebrow controls.");
            var paths = main.FileNames.Select(p => (archive: main, path: p))
                .Concat(dlc.FileNames.Select(p => (archive: dlc, path: p)))
                .Where(p => Regex.IsMatch(p.path.Replace('\\','/'), @"(^|/)rom/rob/mot_PV\d+_FACE_MIK\.farc$", RegexOptions.IgnoreCase))
                .GroupBy(p => Path.GetFileName(p.path), StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).OrderBy(p => p.path).ToArray();
            if (paths.Length == 0) throw new InvalidDataException("No Miku PV facial archives found.");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            staging = Path.Combine(Path.GetDirectoryName(output)!, ".miku-brows-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(staging, "rom/rob"));
            var entries = new List<object>(); int motionCount = 0, changedCount = 0;
            foreach (var item in paths)
            {
                using var archive = item.archive.Open<FarcArchive>(item.path);
                var original = Read(archive, skeleton, db); var result = Read(archive, skeleton, db);
                int changed = result.Motions.Count(m => profile.Retarget(m, skeleton, db));
                using var stream = new MemoryStream(); result.Save(stream, skeleton, db, leaveOpen: true); stream.Position = 0;
                var reopened = new MotionSet(); reopened.Load(stream, skeleton, db);
                VerifyPreserved(original, reopened);
                string filename = Path.GetFileName(item.path);
                using (var generated = new FarcArchive())
                { generated.Add(archive.FileNames.Single(), new MemoryStream(stream.ToArray()), leaveOpen: false); generated.Save(Path.Combine(staging, "rom/rob", filename)); }
                entries.Add(new { filename, sourceEntry = item.path, motionCount = result.Motions.Count, changedMotions = changed,
                    sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(staging, "rom/rob", filename)))) });
                motionCount += result.Motions.Count; changedCount += changed;
            }
            using var hashStream = source.OpenRequired("mot_mik.farc");
            File.WriteAllText(Path.Combine(staging, "brow-build-report.json"), JsonSerializer.Serialize(new {
                phase = "Miku-Arcade-to-X-PV-eyebrow-rebuild", trainingPairs = pairs.Count, trainingRoot = source.RootPath,
                trainingMotionsSha256 = Convert.ToHexString(SHA256.HashData(hashStream)), profile = profile.Export(),
                archiveCount = paths.Length, motionCount, changedMotions = changedCount,
                mouthEyesBodyAndTimingPreservedWithinTolerance = true, scope = "six tl_mayu position controls",
                tolerance = 0.000002, runtimeVerified = false, entries
            }, new JsonSerializerOptions { WriteIndented = true }));
            Directory.Move(staging, output); staging = null;
            Console.WriteLine($"Rebuilt {changedCount} eyebrow motions in {paths.Length} Miku PV archives. Mouth/eyes/body/timing verified unchanged; gameplay pending.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
        finally { if (staging != null && Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }

    private static MotionSet Read(FarcArchive archive, Skeleton skeleton, MotionDatabase db)
    { using var s = archive.Open(archive.FileNames.Single(), EntryStreamMode.MemoryStream); var result = new MotionSet(); result.Load(s, skeleton, db); return result; }
    private static KeySet?[] Channels(KeyBinding? b) => new[] { b?.X, b?.Y, b?.Z };
    private static void VerifyPreserved(MotionSet first, MotionSet second)
    {
        if (first.Motions.Count != second.Motions.Count) throw new InvalidDataException("PV motion count changed.");
        for (int i = 0; i < first.Motions.Count; i++)
        {
            var a = first.Motions[i]; var b = second.Motions[i];
            if (a.FrameCount != b.FrameCount || a.HighBits != b.HighBits || a.DivFrameCount != b.DivFrameCount || a.DivFileCount != b.DivFileCount)
                throw new InvalidDataException("PV timing metadata changed.");
            var before = a.Bind().BoneBindings.ToDictionary(x => x.Name);
            var after = b.Bind().BoneBindings.ToDictionary(x => x.Name);
            if (!before.Keys.Order().SequenceEqual(after.Keys.Order())) throw new InvalidDataException("PV bone names changed.");
            foreach (var pair in before)
                foreach (string kind in new[] { "position", "rotation" })
                {
                    if (kind == "position" && pair.Key.StartsWith("tl_mayu_", StringComparison.OrdinalIgnoreCase))
                    {
                        var changed = Channels(after[pair.Key].Position);
                        if (changed.Where(c => c != null).SelectMany(c => c!.Keys).Any(k => !float.IsFinite(k.Value) || !float.IsFinite(k.Tangent)))
                            throw new InvalidDataException("Nonfinite eyebrow curve.");
                        continue;
                    }
                    var x = Channels(kind == "position" ? pair.Value.Position : pair.Value.Rotation);
                    var y = Channels(kind == "position" ? after[pair.Key].Position : after[pair.Key].Rotation);
                    for (int axis = 0; axis < 3; axis++)
                    {
                        if ((x[axis] == null) != (y[axis] == null)) throw new InvalidDataException("Non-eyebrow channel presence changed.");
                        if (x[axis] == null) continue;
                        if (x[axis]!.HasTangents != y[axis]!.HasTangents || x[axis]!.Keys.Count != y[axis]!.Keys.Count)
                            throw new InvalidDataException("Non-eyebrow curve shape changed.");
                        for (int k = 0; k < x[axis]!.Keys.Count; k++)
                        {
                            var p = x[axis]!.Keys[k]; var q = y[axis]!.Keys[k];
                            if (p.Frame != q.Frame || !float.IsFinite(q.Value) || !float.IsFinite(q.Tangent) ||
                                Math.Abs(p.Value-q.Value)>0.000002 || Math.Abs(p.Tangent-q.Tangent)>0.000002)
                                throw new InvalidDataException("Non-eyebrow curve changed.");
                        }
                    }
                }
        }
    }
}
