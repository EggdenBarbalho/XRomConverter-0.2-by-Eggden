using System.Text.RegularExpressions;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.CriMw;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.Bones;
using MikuMikuLibrary.Databases;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Motions;
using XRomConverter.Content;

namespace XRomConverter;

internal sealed class EyebrowRetargetProfile
{
    private static readonly string[] sRidgeCandidates = { "1e-8", "1e-7", "1e-6", "1e-5", "1e-4" };
    private readonly string[] mBoneNames;
    private readonly double[,] mMatrix;
    private readonly double[] mMinimum;
    private readonly double[] mMaximum;

    public string Character { get; }
    public double ValidationRmse { get; }
    public double IdentityRmse { get; }
    public double Ridge { get; }
    internal IReadOnlyList<string> BoneNames => Array.AsReadOnly(mBoneNames);

    internal object Export() => new { Character, bones = mBoneNames,
        matrix = Enumerable.Range(0, mMatrix.GetLength(0)).Select(i => Enumerable.Range(0, mMatrix.GetLength(1)).Select(j => mMatrix[i,j]).ToArray()).ToArray(),
        minimum = mMinimum, maximum = mMaximum, ValidationRmse, IdentityRmse, Ridge };

    private EyebrowRetargetProfile(
        string character,
        string[] boneNames,
        double[,] matrix,
        double[] minimum,
        double[] maximum,
        double validationRmse,
        double identityRmse,
        double ridge)
    {
        Character = character;
        mBoneNames = boneNames;
        mMatrix = matrix;
        mMinimum = minimum;
        mMaximum = maximum;
        ValidationRmse = validationRmse;
        IdentityRmse = identityRmse;
        Ridge = ridge;
    }

    public static EyebrowRetargetProfile? Learn(
        string character,
        IReadOnlyList<(Motion Arcade, Motion DivaX)> motionPairs)
    {
        string[] boneNames = motionPairs
            .SelectMany(pair => pair.Arcade.Bind().BoneBindings.Select(binding => binding.Name))
            .Where(IsEyebrowTip)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        int dimensions = boneNames.Length * 3;
        if (dimensions == 0)
            return null;

        List<TrainingSample> samples = new();
        foreach ((Motion arcadeMotion, Motion divaXMotion) in motionPairs)
        {
            MotionBinding arcade = arcadeMotion.Bind();
            MotionBinding divaX = divaXMotion.Bind();
            SortedSet<ushort> frames = CollectFrames(arcade, divaX, boneNames);
            foreach (ushort frame in frames)
            {
                double[] source = ReadPose(arcade, boneNames, frame, derivative: false);
                double[] target = ReadPose(divaX, boneNames, frame, derivative: false);
                if (SquaredLength(source) < 1e-12 && SquaredLength(target) < 1e-12)
                    continue;
                samples.Add(new TrainingSample(arcadeMotion.Name ?? string.Empty, source, target));
            }
        }

        List<TrainingSample> training = samples.Where(sample => StableBucket(sample.MotionName) != 0).ToList();
        List<TrainingSample> validation = samples.Where(sample => StableBucket(sample.MotionName) == 0).ToList();
        if (training.Count <= dimensions || validation.Count < 3)
        {
            training = samples;
            validation = samples;
        }
        if (training.Count <= dimensions)
            return null;

        double bestRidge = 1e-4;
        double bestRmse = double.PositiveInfinity;
        foreach (string value in sRidgeCandidates)
        {
            double ridge = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            double[,] candidate = Fit(training, dimensions, ridge);
            double rmse = Measure(validation, candidate);
            if (rmse < bestRmse)
            {
                bestRmse = rmse;
                bestRidge = ridge;
            }
        }

        double identityRmse = MeasureIdentity(validation);
        if (!double.IsFinite(bestRmse) || bestRmse >= identityRmse)
            return null;

        double[,] matrix = Fit(samples, dimensions, bestRidge);
        double[] minimum = Enumerable.Repeat(double.PositiveInfinity, dimensions).ToArray();
        double[] maximum = Enumerable.Repeat(double.NegativeInfinity, dimensions).ToArray();
        foreach (TrainingSample sample in samples)
        for (int i = 0; i < dimensions; i++)
        {
            minimum[i] = Math.Min(minimum[i], sample.Target[i]);
            maximum[i] = Math.Max(maximum[i], sample.Target[i]);
        }
        for (int i = 0; i < dimensions; i++)
        {
            minimum[i] = Math.Min(minimum[i], 0);
            maximum[i] = Math.Max(maximum[i], 0);
            double margin = Math.Max((maximum[i] - minimum[i]) * 0.15, 0.00025);
            minimum[i] -= margin;
            maximum[i] += margin;
        }

        return new EyebrowRetargetProfile(
            character, boneNames, matrix, minimum, maximum,
            bestRmse, identityRmse, bestRidge);
    }

    public bool Retarget(Motion motion, Skeleton skeleton, MotionDatabase motionDatabase)
    {
        MotionBinding binding = motion.Bind();
        SortedSet<ushort> frames = CollectFrames(binding, null, mBoneNames);
        if (frames.Count == 0)
            return false;

        int dimensions = mBoneNames.Length * 3;
        List<Key>[] outputKeys = Enumerable.Range(0, dimensions).Select(_ => new List<Key>(frames.Count)).ToArray();
        foreach (ushort frame in frames)
        {
            double[] sourcePose = ReadPose(binding, mBoneNames, frame, derivative: false);
            double[] sourceSlope = ReadPose(binding, mBoneNames, frame, derivative: true);
            double[] targetPose = Transform(sourcePose);
            double[] targetSlope = Transform(sourceSlope);
            for (int i = 0; i < dimensions; i++)
            {
                outputKeys[i].Add(new Key
                {
                    Frame = frame,
                    Value = (float)Math.Clamp(targetPose[i], mMinimum[i], mMaximum[i]),
                    Tangent = (float)targetSlope[i]
                });
            }
        }

        for (int boneIndex = 0; boneIndex < mBoneNames.Length; boneIndex++)
        {
            BoneBinding? bone = binding.BoneBindings.FirstOrDefault(candidate =>
                candidate.Name.Equals(mBoneNames[boneIndex], StringComparison.OrdinalIgnoreCase));
            if (bone == null)
            {
                bone = new BoneBinding { Name = mBoneNames[boneIndex] };
                binding.BoneBindings.Add(bone);
            }
            bone.Position ??= new KeyBinding();
            bone.Position.X = CreateKeySet(outputKeys[boneIndex * 3]);
            bone.Position.Y = CreateKeySet(outputKeys[boneIndex * 3 + 1]);
            bone.Position.Z = CreateKeySet(outputKeys[boneIndex * 3 + 2]);
        }

        binding.Unbind(skeleton, motionDatabase);
        return true;
    }

    private double[] Transform(double[] source)
    {
        double[] target = new double[source.Length];
        for (int output = 0; output < target.Length; output++)
        for (int input = 0; input < source.Length; input++)
            target[output] += mMatrix[output, input] * source[input];
        return target;
    }

    private static KeySet CreateKeySet(List<Key> keys)
    {
        KeySet keySet = new() { HasTangents = keys.Count > 1 };
        keySet.Keys.AddRange(keys);
        return keySet;
    }

    private static SortedSet<ushort> CollectFrames(
        MotionBinding first,
        MotionBinding? second,
        IEnumerable<string> boneNames)
    {
        SortedSet<ushort> frames = new();
        foreach (MotionBinding binding in second == null ? new[] { first } : new[] { first, second })
        foreach (string boneName in boneNames)
        {
            BoneBinding? bone = binding.BoneBindings.FirstOrDefault(candidate =>
                candidate.Name.Equals(boneName, StringComparison.OrdinalIgnoreCase));
            if (bone?.Position == null)
                continue;
            foreach (KeySet? keySet in new[] { bone.Position.X, bone.Position.Y, bone.Position.Z })
                if (keySet != null)
                    foreach (Key key in keySet.Keys)
                        frames.Add(key.Frame);
        }
        return frames;
    }

    private static double[] ReadPose(
        MotionBinding binding,
        IReadOnlyList<string> boneNames,
        ushort frame,
        bool derivative)
    {
        double[] values = new double[boneNames.Count * 3];
        for (int boneIndex = 0; boneIndex < boneNames.Count; boneIndex++)
        {
            BoneBinding? bone = binding.BoneBindings.FirstOrDefault(candidate =>
                candidate.Name.Equals(boneNames[boneIndex], StringComparison.OrdinalIgnoreCase));
            if (bone?.Position == null)
                continue;
            values[boneIndex * 3] = Evaluate(bone.Position.X, frame, derivative);
            values[boneIndex * 3 + 1] = Evaluate(bone.Position.Y, frame, derivative);
            values[boneIndex * 3 + 2] = Evaluate(bone.Position.Z, frame, derivative);
        }
        return values;
    }

    private static double Evaluate(KeySet? keySet, ushort frame, bool derivative)
    {
        if (keySet == null || keySet.Keys.Count == 0)
            return 0;
        if (keySet.Keys.Count == 1)
            return derivative ? 0 : keySet.Keys[0].Value;

        List<Key> keys = keySet.Keys;
        if (frame <= keys[0].Frame)
            return derivative ? 0 : keys[0].Value;
        if (frame >= keys[^1].Frame)
            return derivative ? 0 : keys[^1].Value;

        int nextIndex = keys.FindIndex(key => key.Frame >= frame);
        Key previous = keys[nextIndex - 1];
        Key next = keys[nextIndex];
        double duration = next.Frame - previous.Frame;
        double t = (frame - previous.Frame) / duration;

        if (keySet.HasTangents)
        {
            if (!derivative)
            {
                double h00 = 2 * t * t * t - 3 * t * t + 1;
                double h10 = t * t * t - 2 * t * t + t;
                double h01 = -2 * t * t * t + 3 * t * t;
                double h11 = t * t * t - t * t;
                return h00 * previous.Value + h10 * duration * previous.Tangent +
                       h01 * next.Value + h11 * duration * next.Tangent;
            }
            double dh00 = (6 * t * t - 6 * t) / duration;
            double dh10 = 3 * t * t - 4 * t + 1;
            double dh01 = (-6 * t * t + 6 * t) / duration;
            double dh11 = 3 * t * t - 2 * t;
            return dh00 * previous.Value + dh10 * previous.Tangent +
                   dh01 * next.Value + dh11 * next.Tangent;
        }

        double difference = next.Value - previous.Value;
        if (derivative)
            return 6 * t * (1 - t) * difference / duration;
        double smooth = 3 * t * t - 2 * t * t * t;
        return previous.Value + smooth * difference;
    }

    private static bool IsEyebrowTip(string name) =>
        name.StartsWith("tl_mayu_", StringComparison.OrdinalIgnoreCase);

    private static int StableBucket(string value)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char character in value.ToUpperInvariant())
                hash = (hash ^ character) * 16777619;
            return (int)(hash % 5);
        }
    }

    private static double SquaredLength(double[] values) => values.Sum(value => value * value);

    private static double[,] Fit(IReadOnlyList<TrainingSample> samples, int dimensions, double ridge)
    {
        double[,] normal = new double[dimensions, dimensions];
        double[,] targets = new double[dimensions, dimensions];
        foreach (TrainingSample sample in samples)
        for (int row = 0; row < dimensions; row++)
        for (int column = 0; column < dimensions; column++)
        {
            normal[row, column] += sample.Source[row] * sample.Source[column];
            targets[row, column] += sample.Source[row] * sample.Target[column];
        }
        for (int i = 0; i < dimensions; i++)
            normal[i, i] += ridge;

        double[,] matrix = new double[dimensions, dimensions];
        for (int output = 0; output < dimensions; output++)
        {
            double[] right = new double[dimensions];
            for (int i = 0; i < dimensions; i++)
                right[i] = targets[i, output];
            double[] coefficients = Solve(normal, right);
            for (int input = 0; input < dimensions; input++)
                matrix[output, input] = coefficients[input];
        }
        return matrix;
    }

    private static double[] Solve(double[,] source, double[] right)
    {
        int size = right.Length;
        double[,] matrix = (double[,])source.Clone();
        double[] result = (double[])right.Clone();
        for (int column = 0; column < size; column++)
        {
            int pivot = column;
            for (int row = column + 1; row < size; row++)
                if (Math.Abs(matrix[row, column]) > Math.Abs(matrix[pivot, column]))
                    pivot = row;
            if (Math.Abs(matrix[pivot, column]) < 1e-18)
                throw new InvalidDataException("Singular eyebrow retarget matrix.");
            if (pivot != column)
            {
                for (int i = column; i < size; i++)
                    (matrix[column, i], matrix[pivot, i]) = (matrix[pivot, i], matrix[column, i]);
                (result[column], result[pivot]) = (result[pivot], result[column]);
            }

            double scale = matrix[column, column];
            for (int i = column; i < size; i++)
                matrix[column, i] /= scale;
            result[column] /= scale;
            for (int row = 0; row < size; row++)
            {
                if (row == column)
                    continue;
                double factor = matrix[row, column];
                for (int i = column; i < size; i++)
                    matrix[row, i] -= factor * matrix[column, i];
                result[row] -= factor * result[column];
            }
        }
        return result;
    }

    private static double Measure(IReadOnlyList<TrainingSample> samples, double[,] matrix)
    {
        int dimensions = matrix.GetLength(0);
        double squaredError = 0;
        foreach (TrainingSample sample in samples)
        for (int output = 0; output < dimensions; output++)
        {
            double predicted = 0;
            for (int input = 0; input < dimensions; input++)
                predicted += matrix[output, input] * sample.Source[input];
            double error = predicted - sample.Target[output];
            squaredError += error * error;
        }
        return Math.Sqrt(squaredError / (samples.Count * dimensions));
    }

    private static double MeasureIdentity(IReadOnlyList<TrainingSample> samples)
    {
        double squaredError = 0;
        int count = 0;
        foreach (TrainingSample sample in samples)
        for (int i = 0; i < sample.Source.Length; i++)
        {
            double error = sample.Source[i] - sample.Target[i];
            squaredError += error * error;
            count++;
        }
        return Math.Sqrt(squaredError / count);
    }

    private readonly record struct TrainingSample(string MotionName, double[] Source, double[] Target);
}

internal static class ArcadeEyebrowRetargeter
{
    private static readonly Regex sArchivePattern = new(
        @"(^|/)rom/rob/mot_PV\d+_FACE_(?<character>[A-Z]{3})\.farc$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static void ConvertOnly(ContentSource xSource, string gamePath, string outputRoot)
    {
        using CpkArchive mainArchive = BinaryFile.Load<CpkArchive>(Path.Combine(gamePath, "diva_main.cpk"));
        string dlcPath = Path.Combine(gamePath, "diva_dlc00.cpk");
        using CpkArchive? dlcArchive = File.Exists(dlcPath) ? BinaryFile.Load<CpkArchive>(dlcPath) : null;
        BoneData targetBoneData = mainArchive.Open<BoneData>("rom/bone_data.bin");
        MotionDatabase motionDatabase = mainArchive.Open<FarcArchive>("rom_ps4/rom/rob/mot_db.farc").Open<MotionDatabase>("mot_db.bin");
        using Stream xBoneStream = xSource.OpenRequired("bone_data.bon");
        BoneData xBoneData = BinaryFile.Load<BoneData>(xBoneStream);
        Dictionary<string, EyebrowRetargetProfile> profiles = new(StringComparer.OrdinalIgnoreCase);

        foreach (string character in new[] { "MIK", "RIN", "LEN", "LUK", "NER", "HAK", "KAI", "MEI", "SAK", "TET" })
        {
            Skeleton targetSkeleton = targetBoneData.Skeletons.First(s => s.Name.Equals(character, StringComparison.OrdinalIgnoreCase));
            Skeleton xSkeleton = xBoneData.Skeletons.First(s => s.Name.Equals(character, StringComparison.OrdinalIgnoreCase));
            MotionSetInfo info = motionDatabase.GetMotionSetInfo(character);
            MotionSet arcade = mainArchive.Open<FarcArchive>($"rom/rob/mot_{character}.farc").Open($"mot_{character}.bin", stream =>
            {
                MotionSet set = new();
                set.Load(stream, targetSkeleton, motionDatabase);
                return set;
            });
            using Stream xMotionStream = xSource.OpenRequired($"mot_{character.ToLowerInvariant()}.farc");
            using FarcArchive xMotions = BinaryFile.Load<FarcArchive>(xMotionStream);
            List<(Motion Arcade, Motion DivaX)> pairs = new();
            foreach (string fileName in xMotions.FileNames.Where(n => n.EndsWith(".mot", StringComparison.OrdinalIgnoreCase)))
            {
                Motion xMotion = xMotions.Open(fileName, stream =>
                {
                    Motion motion = new();
                    motion.Load(stream, xSkeleton);
                    return motion;
                });
                int index = info.Motions.FindIndex(m => m.Name.Equals(xMotion.Name, StringComparison.OrdinalIgnoreCase));
                if (index >= 0 && info.Motions[index].Name.Contains("_FACE_", StringComparison.OrdinalIgnoreCase))
                    pairs.Add((arcade.Motions[index], xMotion));
            }

            EyebrowRetargetProfile? profile = EyebrowRetargetProfile.Learn(character, pairs);
            if (profile == null)
            {
                Console.WriteLine($"[EYEBROW_RETARGET] {character}: no usable training pairs");
                continue;
            }
            profiles[character] = profile;
            Console.WriteLine($"[EYEBROW_RETARGET] {character}: {pairs.Count} pairs, validation RMSE {profile.ValidationRmse:F6} (raw {profile.IdentityRmse:F6}), ridge {profile.Ridge:E0}");
        }

        ConvertPvFaceArchives(mainArchive, dlcArchive, profiles, targetBoneData, motionDatabase, outputRoot);
    }

    public static void ConvertPvFaceArchives(
        CpkArchive mainArchive,
        CpkArchive? dlcArchive,
        IReadOnlyDictionary<string, EyebrowRetargetProfile> profiles,
        BoneData targetBoneData,
        MotionDatabase motionDatabase,
        string outputRoot)
    {
        List<(CpkArchive Archive, string Path)> sources = mainArchive.FileNames
            .Select(path => (Archive: mainArchive, Path: path))
            .Concat(dlcArchive == null
                ? Enumerable.Empty<(CpkArchive, string)>()
                : dlcArchive.FileNames.Select(path => (Archive: dlcArchive, Path: path)))
            .Where(source => sArchivePattern.IsMatch(source.Item2.Replace('\\', '/')))
            .GroupBy(source => Path.GetFileName(source.Item2), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(source => source.Item2, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int convertedArchives = 0;
        int convertedMotions = 0;
        foreach ((CpkArchive archive, string sourcePath) in sources)
        {
            Match match = sArchivePattern.Match(sourcePath.Replace('\\', '/'));
            string character = match.Groups["character"].Value.ToUpperInvariant();
            if (!profiles.TryGetValue(character, out EyebrowRetargetProfile? profile))
                continue;
            Skeleton? skeleton = targetBoneData.Skeletons.FirstOrDefault(candidate =>
                candidate.Name.Equals(character, StringComparison.OrdinalIgnoreCase));
            if (skeleton == null)
                continue;

            using FarcArchive sourceFarc = archive.Open<FarcArchive>(sourcePath);
            string? binaryName = sourceFarc.FileNames.FirstOrDefault(name =>
                name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase));
            if (binaryName == null)
                continue;
            MotionSet motionSet = sourceFarc.Open(binaryName, stream =>
            {
                MotionSet value = new();
                value.Load(stream, skeleton, motionDatabase);
                return value;
            });

            int changed = motionSet.Motions.Count(motion => profile.Retarget(motion, skeleton, motionDatabase));
            if (changed == 0)
                continue;

            using MemoryStream outputMotion = new();
            motionSet.Save(outputMotion, skeleton, motionDatabase, leaveOpen: true);
            using FarcArchive outputFarc = new();
            outputFarc.Add(binaryName, outputMotion, leaveOpen: false);
            string outputPath = Path.Combine(outputRoot, "rom", "rob", Path.GetFileName(sourcePath));
            outputFarc.Save(outputPath);
            convertedArchives++;
            convertedMotions += changed;
        }

        Console.WriteLine($"[EYEBROW_RETARGET] Converted {convertedMotions} motion(s) in {convertedArchives} Arcade PV face archive(s).");
    }
}
