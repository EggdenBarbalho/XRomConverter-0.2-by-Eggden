using System.Security.Cryptography;
using System.Text.Json;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.CriMw;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.Bones;
using MikuMikuLibrary.Databases;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Motions;
using XRomConverter.Content;

namespace XRomConverter.Target.MegaMix;

internal static class GenericFaceRebuildCommand
{
    private const float Tolerance = 0.000002f;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static int Run(string[] args)
    {
        string? temporary = null;
        try
        {
            if (args.Length != 6) throw new ArgumentException("Usage: rebuild-miku-generic-face X_ROM GAME COMPLETE_FARC PROFILE NEW_OUTPUT");
            string output = Path.GetFullPath(args[5]), game = Path.GetFullPath(args[2]);
            if (Directory.Exists(output) || File.Exists(output)) throw new InvalidDataException("Output must not exist.");
            if (output.StartsWith(game.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Use a staging output outside the game installation.");
            byte[] profileBytes = File.ReadAllBytes(args[4]), referenceBytes = File.ReadAllBytes(args[3]);
            using var profile = JsonDocument.Parse(profileBytes);
            var policy = profile.RootElement;
            if (policy.GetProperty("schemaVersion").GetInt32() != 1 || policy.GetProperty("character").GetString() != "MIK" ||
                policy.GetProperty("referenceSha256").GetString() != Hash(referenceBytes))
                throw new InvalidDataException("The profile does not identify this Complete Modules reference.");
            using var xSource = ContentSource.Load("face-source", args[1]);
            byte[] sourceArchive = Read(xSource.OpenRequired("mot_mik.farc")), sourceBones = Read(xSource.OpenRequired("bone_data.bon"));
            if (Hash(sourceArchive) != policy.GetProperty("sourceArchiveSha256").GetString() ||
                Hash(sourceBones) != policy.GetProperty("sourceBonesSha256").GetString())
                throw new InvalidDataException("X source differs from the verified reconstruction profile.");
            var xBones = BinaryFile.Load<BoneData>(new MemoryStream(sourceBones));
            var xSkeleton = xBones.Skeletons.Single(s => s.Name == "MIK");
            using var cpk = BinaryFile.Load<CpkArchive>(Path.Combine(game, "diva_main.cpk"));
            var bones = cpk.Open<BoneData>("rom/bone_data.bin");
            var skeleton = bones.Skeletons.Single(s => s.Name == "MIK");
            using var dbArchive = cpk.Open<FarcArchive>("rom_ps4/rom/rob/mot_db.farc");
            var db = dbArchive.Open<MotionDatabase>("mot_db.bin");
            var info = db.GetMotionSetInfo("MIK");
            string[] targetNames = policy.GetProperty("targetMotionNames").EnumerateArray().Select(x => x.GetString()!).ToArray();
            if (!targetNames.SequenceEqual(info.Motions.Select(m => m.Name)) || targetNames.Length != 166)
                throw new InvalidDataException("Game motion order differs from the profile.");
            var mappings = policy.GetProperty("mappings").EnumerateArray().ToDictionary(x => x.GetProperty("target").GetString()!,
                x => x.GetProperty("source").GetString()!, StringComparer.Ordinal);
            var retained = policy.GetProperty("referenceRetainedMotions").EnumerateArray().Select(x => x.GetString()!).ToArray();
            if (mappings.Keys.Intersect(retained).Any() || retained.Distinct().Count() != retained.Length ||
                !mappings.Keys.Concat(retained).Order().SequenceEqual(targetNames.Order()))
                throw new InvalidDataException("Every motion must have exactly one declared authority.");
            var adjustments = policy.GetProperty("adjustments").EnumerateArray().ToDictionary(PatchKey, x => x.GetProperty("value").GetSingle());
            var usedAdjustments = new HashSet<string>();
            var sourceMotions = new Dictionary<string, Motion>(StringComparer.Ordinal);
            using (var archive = BinaryFile.Load<FarcArchive>(new MemoryStream(sourceArchive)))
                foreach (string entry in archive.FileNames.Where(x => x.EndsWith(".mot", StringComparison.OrdinalIgnoreCase)))
                {
                    using var stream = archive.Open(entry, EntryStreamMode.MemoryStream);
                    var motion = new Motion(); motion.Load(stream, xSkeleton); sourceMotions.Add(motion.Name, motion);
                }
            foreach (var mapping in mappings)
                if ((sourceMotions.ContainsKey(mapping.Key) && mapping.Key != mapping.Value) ||
                    (mapping.Key != mapping.Value && !new[] { "_OLD", "_CL", "_OLD_CL" }.Any(suffix => mapping.Key == mapping.Value + suffix)))
                    throw new InvalidDataException("Invalid explicit expression alias: " + mapping.Key);
            if (retained.Any(sourceMotions.ContainsKey)) throw new InvalidDataException("Reference retention hides a direct X source.");
            var result = Load(referenceBytes, skeleton, db);
            if (result.Motions.Count != targetNames.Length) throw new InvalidDataException("Reference motion count differs.");
            int keysFromSource = 0, constants = 0, adjustedKeys = 0;
            var rows = new List<object>();
            for (int i = 0; i < result.Motions.Count; i++)
            {
                string name = targetNames[i];
                if (!mappings.TryGetValue(name, out string? sourceName)) continue;
                if (!sourceMotions.TryGetValue(sourceName, out var sourceMotion))
                    throw new InvalidDataException("Declared source motion absent: " + sourceName);
                var source = sourceMotion.Bind(); var target = result.Motions[i].Bind();
                int derived = 0, constant = 0, adjusted = 0;
                foreach (var bone in target.BoneBindings.Where(b => IsFacial(b.Name)))
                {
                    var inputBone = source.BoneBindings.SingleOrDefault(b => b.Name == bone.Name);
                    foreach (string kind in new[] { "position", "rotation" })
                    {
                        var targets = Channels(kind == "position" ? bone.Position : bone.Rotation);
                        var inputs = Channels(kind == "position" ? inputBone?.Position : inputBone?.Rotation);
                        for (int axis = 0; axis < 3; axis++)
                        {
                            var channel = targets[axis]; var input = inputs[axis];
                            if (channel == null) continue;
                            for (int k = 0; k < channel.Keys.Count; k++)
                            {
                                var key = channel.Keys[k];
                                string patchKey = $"{name}|{bone.Name}|{kind}|{axis}|{key.Frame}";
                                float value;
                                if (adjustments.TryGetValue(patchKey, out float adjustment))
                                {
                                    if (!float.IsFinite(adjustment) || Math.Abs(adjustment - key.Value) > Tolerance)
                                        throw new InvalidDataException("Adjustment disagrees with reference: " + patchKey);
                                    value = adjustment; adjusted++; adjustedKeys++; usedAdjustments.Add(patchKey);
                                }
                                else
                                {
                                    bool fullPose = channel.Keys.Count == 1 && input?.Keys.Count > 1;
                                    value = fullPose ? input!.Keys[^1].Value : Evaluate(input, key.Frame);
                                    if (!float.IsFinite(value) || Math.Abs(value - key.Value) > Tolerance)
                                        throw new InvalidDataException("Unexplained facial difference: " + patchKey);
                                    derived++; keysFromSource++;
                                    if (fullPose) { constant++; constants++; }
                                }
                                // The reference controls the target timeline/interpolation;
                                // the X source supplies poses. Never borrow a substring match.
                                key.Value = value; channel.Keys[k] = key;
                            }
                        }
                    }
                }
                target.Unbind(skeleton, db);
                rows.Add(new { target = name, source = sourceName, sourceKeys = derived, fullPoseConstants = constant, adjustmentKeys = adjusted });
            }
            if (usedAdjustments.Count != adjustments.Count) throw new InvalidDataException("Profile has unused adjustments.");
            using var data = new MemoryStream(); result.Save(data, skeleton, db, leaveOpen: true);
            data.Position = 0; var reopened = new MotionSet(); reopened.Load(data, skeleton, db);
            Compare(Load(referenceBytes, skeleton, db), reopened);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            temporary = Path.Combine(Path.GetDirectoryName(output)!, ".generic-face-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(temporary, "rom/rob"));
            using (var archive = new FarcArchive())
            { archive.Add("mot_MIK.bin", new MemoryStream(data.ToArray()), leaveOpen: false); archive.Save(Path.Combine(temporary, "rom/rob/mot_MIK.farc")); }
            if (Hash(File.ReadAllBytes(args[3])) != Hash(referenceBytes) || Hash(File.ReadAllBytes(args[4])) != Hash(profileBytes))
                throw new InvalidDataException("Reference/profile changed during build.");
            File.WriteAllText(Path.Combine(temporary, "face-build-report.json"), JsonSerializer.Serialize(new {
                schemaVersion = 1, phase = "reference-guided-X-generic-face-rebuild", character = "MIK",
                sourceRoot = xSource.RootPath, sourceArchiveSha256 = Hash(sourceArchive), sourceBonesSha256 = Hash(sourceBones),
                referencePath = Path.GetFullPath(args[3]), referenceSha256 = Hash(referenceBytes), profileSha256 = Hash(profileBytes),
                motionCount = result.Motions.Count, mappedMotions = mappings.Count, referenceRetainedMotions = retained,
                sourceKeys = keysFromSource, fullPoseConstants = constants, adjustmentKeys = adjustedKeys,
                mappedMotionDetails = rows, comparedAllBoneCurvesAfterSerialization = true, tolerance = Tolerance,
                pvFacialRetargetIncluded = false, runtimeVerified = false,
                outputSha256 = Hash(File.ReadAllBytes(Path.Combine(temporary, "rom/rob/mot_MIK.farc")))
            }, Json));
            Directory.Move(temporary, output); temporary = null;
            Console.WriteLine($"Rebuilt {result.Motions.Count} motions: {keysFromSource} X-derived keys, {constants} full-pose constants, {adjustedKeys} explicit adjustments. Reference comparison passed; gameplay pending.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
        finally { if (temporary != null && Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }
    }

    private static string PatchKey(JsonElement x) => $"{x.GetProperty("motion").GetString()}|{x.GetProperty("bone").GetString()}|{x.GetProperty("kind").GetString()}|{x.GetProperty("axis").GetInt32()}|{x.GetProperty("frame").GetInt32()}";
    private static bool IsFacial(string name) => name.Contains("mayu") || name.Contains("ago") || name.Contains("ha_") || name.Contains("kuti");
    private static KeySet?[] Channels(KeyBinding? value) => new[] { value?.X, value?.Y, value?.Z };
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
    private static byte[] Read(Stream stream) { using (stream) { using var data = new MemoryStream(); stream.CopyTo(data); return data.ToArray(); } }
    private static MotionSet Load(byte[] bytes, Skeleton skeleton, MotionDatabase db)
    { using var a = BinaryFile.Load<FarcArchive>(new MemoryStream(bytes)); using var s = a.Open("mot_MIK.bin", EntryStreamMode.MemoryStream); var result = new MotionSet(); result.Load(s, skeleton, db); return result; }

    private static float Evaluate(KeySet? value, ushort frame)
    {
        if (value == null || value.Keys.Count == 0) return 0;
        var keys = value.Keys;
        if (frame <= keys[0].Frame) return keys[0].Value;
        if (frame >= keys[^1].Frame) return keys[^1].Value;
        int next = keys.FindIndex(k => k.Frame >= frame);
        var left = keys[next - 1]; var right = keys[next];
        double span = right.Frame - left.Frame, t = (frame - left.Frame) / span;
        if (!value.HasTangents) return (float)(left.Value + t * (right.Value - left.Value));
        return (float)((2*t*t*t-3*t*t+1)*left.Value + (t*t*t-2*t*t+t)*span*left.Tangent +
            (-2*t*t*t+3*t*t)*right.Value + (t*t*t-t*t)*span*right.Tangent);
    }

    private static void Compare(MotionSet reference, MotionSet output)
    {
        if (reference.Motions.Count != output.Motions.Count) throw new InvalidDataException("Rebuilt motion count changed.");
        for (int i = 0; i < reference.Motions.Count; i++)
        {
            var a = reference.Motions[i]; var b = output.Motions[i];
            if (a.FrameCount != b.FrameCount || a.HighBits != b.HighBits || a.DivFrameCount != b.DivFrameCount || a.DivFileCount != b.DivFileCount)
                throw new InvalidDataException("Rebuilt motion metadata changed: " + i);
            var first = a.Bind().BoneBindings.ToDictionary(x => x.Name);
            var second = b.Bind().BoneBindings.ToDictionary(x => x.Name);
            if (!first.Keys.Order().SequenceEqual(second.Keys.Order())) throw new InvalidDataException("Rebuilt bone binding names changed.");
            foreach (var pair in first)
                foreach (string kind in new[] { "position", "rotation" })
                {
                    var x = Channels(kind == "position" ? pair.Value.Position : pair.Value.Rotation);
                    var y = Channels(kind == "position" ? second[pair.Key].Position : second[pair.Key].Rotation);
                    for (int axis = 0; axis < 3; axis++)
                    {
                        if ((x[axis] == null) != (y[axis] == null)) throw new InvalidDataException("Rebuilt channel presence changed.");
                        if (x[axis] == null) continue;
                        if (x[axis]!.HasTangents != y[axis]!.HasTangents || x[axis]!.Keys.Count != y[axis]!.Keys.Count)
                            throw new InvalidDataException("Rebuilt curve shape changed.");
                        for (int k = 0; k < x[axis]!.Keys.Count; k++)
                        {
                            var p = x[axis]!.Keys[k]; var q = y[axis]!.Keys[k];
                            if (p.Frame != q.Frame || !float.IsFinite(q.Value) || !float.IsFinite(q.Tangent) ||
                                Math.Abs(p.Value-q.Value)>Tolerance || Math.Abs(p.Tangent-q.Tangent)>Tolerance)
                                throw new InvalidDataException($"Rebuilt curve differs: {i}/{pair.Key}/{kind}/{axis}/{k}");
                        }
                    }
                }
        }
    }
}
