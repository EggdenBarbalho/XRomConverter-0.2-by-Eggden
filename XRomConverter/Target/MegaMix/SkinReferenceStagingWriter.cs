using System.Text.Json;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Objects;
using MikuMikuLibrary.Objects.Extra.Blocks;
using XRomConverter.Analysis;
using XRomConverter.Transformations.Skin;

namespace XRomConverter.Target.MegaMix;

internal sealed record SkinReferenceStageManifest(string Phase, bool Playable,
    string CanonicalId, string SourceObject, string TargetObject, string Label,
    string ReferenceSha256, string TargetArchiveSha256, string AnalysisSha256,
    string OutputFile, string OutputSha256, int Parameters, int Fields,
    bool JsonRoundTripValid, bool ByteIdenticalToReference,
    IReadOnlyDictionary<string, int> TargetNodeCounts);

internal static class SkinReferenceStagingWriterCommand
{
    private static readonly JsonSerializerOptions sJson = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static bool IsRequested(string[] args) => args.Length > 0 &&
        args[0].Equals("stage-skin-reference", StringComparison.OrdinalIgnoreCase);

    public static int Run(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var allowed = new HashSet<string> { "--analysis", "--reference", "--object",
            "--objectset", "--label", "--output" };
        for (int i = 1; i < args.Length; i += 2)
            if (!allowed.Contains(args[i]) || i + 1 >= args.Length || !options.TryAdd(args[i], args[i + 1]))
                return Usage();
        if (!allowed.SetEquals(options.Keys) ||
            !File.Exists(options["--analysis"]) || !File.Exists(options["--reference"]) ||
            !File.Exists(options["--objectset"])) return Usage();

        string output = Path.GetFullPath(options["--output"]);
        if (Directory.Exists(output) || File.Exists(output))
        {
            Console.Error.WriteLine("Output already exists; refusing to overwrite: " + output);
            return 2;
        }
        string? staging = null;
        try
        {
            string analysisPath = Path.GetFullPath(options["--analysis"]);
            string referencePath = Path.GetFullPath(options["--reference"]);
            string archivePath = Path.GetFullPath(options["--objectset"]);
            string sourceObject = options["--object"];
            byte[] analysisBytes = File.ReadAllBytes(analysisPath);
            var analysis = JsonSerializer.Deserialize<ModuleAnalysisResult>(analysisBytes, sJson)
                ?? throw new InvalidDataException("Empty module analysis.");
            if (analysis.BuildPlan.Status != "ready" || analysis.NormalizedModule.Identity == null)
                throw new InvalidDataException("A ready analytical plan with a canonical identity is required.");
            var candidates = analysis.BuildPlan.ObjectSets.SelectMany(x => x.SkinParameters)
                .Where(x => x.SourceObject == sourceObject).ToArray();
            if (candidates.Length != 1)
                throw new InvalidDataException($"Expected one planned object '{sourceObject}', found {candidates.Length}.");
            var originalPlan = candidates[0];
            if (Path.GetFileName(originalPlan.OutputFile) != originalPlan.OutputFile)
                throw new InvalidDataException("The skin output must be a file name.");

            byte[] referenceBytes = File.ReadAllBytes(referencePath);
            var reference = SkinParameterTextCodec.Parse(referenceBytes, "external-reference");
            string archiveHash = SkinParameterTextCodec.Hash(File.ReadAllBytes(archivePath));
            using var archive = BinaryFile.Load<FarcArchive>(archivePath);
            string[] entries = archive.FileNames.Where(x => x.EndsWith("_obj.bin", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (entries.Length != 1)
                throw new InvalidDataException("Expected one target _obj.bin entry in the object-set archive.");
            var targetSet = archive.Open<ObjectSet>(entries[0]);
            var targets = targetSet.Objects.Where(x => x.Name == originalPlan.TargetObject).ToArray();
            if (targets.Length != 1 || targets[0].Skin == null)
                throw new InvalidDataException($"Expected one skinned target object '{originalPlan.TargetObject}'.");
            var target = targets[0];
            if (target.Skin.Blocks.OfType<MotionBlock>().Any())
                throw new InvalidDataException("The provided physical target still has MotionBlocks.");
            var targetBlocks = target.Skin.Blocks.OfType<OsageBlock>().Select(x =>
                new NormalizedSkinBlock("OSG", x.Name, x.ExternalName, x.ParentName,
                    x.Nodes.Select(node => node.Name).ToArray(), false)).ToArray();
            if (targetBlocks.Length == 0 || targetBlocks.Any(x => string.IsNullOrEmpty(x.ExternalName)))
                throw new InvalidDataException("The target has no complete external OSG bindings.");
            var provenance = new AssetProvenance("skin-reference", Path.GetDirectoryName(referencePath)!,
                "", 0, Path.GetFileName(referencePath), "SkinParameterTextReference", sourceObject);
            var parameters = reference.Parameters.Select(x => new NormalizedSkinParameter(x.Name,
                x.BocTargets.ToArray(), x.CollisionTarget, x.NodeCount, provenance, false,
                SkinParameterTextCodec.Clone(x.Physical))).ToArray();
            var parameterNames = parameters.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
            if (!parameterNames.SetEquals(targetBlocks.Select(x => x.ExternalName!)))
                throw new InvalidDataException("The reference parameter names differ from the target OSGs.");
            foreach (var block in targetBlocks)
                if (parameters.Single(x => x.Name == block.ExternalName).NodeCount != block.NodeNames.Count)
                    throw new InvalidDataException($"Node count mismatch at {block.ExternalName}.");
            var validation = new SkinParameterValidator().Validate(targetBlocks, parameters);
            if (!validation.IsValid)
                throw new InvalidDataException(string.Join("; ", validation.Issues.Select(x => x.Message)));

            var selected = originalPlan with
            {
                FinalBlocks = targetBlocks, FinalParameters = parameters, Validation = validation,
                ReferenceSelection = new(options["--label"], referencePath, reference.Sha256,
                    archivePath, archiveHash, reference.Layout)
            };
            var updated = analysis with { BuildPlan = analysis.BuildPlan with
            {
                ObjectSets = analysis.BuildPlan.ObjectSets.Select(set => set with
                { SkinParameters = set.SkinParameters.Select(skin => skin.SourceObject == sourceObject ? selected : skin).ToArray() }).ToArray(),
                RequiredOutputs = analysis.BuildPlan.RequiredOutputs.Select(item =>
                    item.Path == "rom/skin_param/" + selected.OutputFile ? item with
                    { Inputs = item.Inputs.Concat(new[] { referencePath, archivePath }).Distinct(StringComparer.Ordinal).ToArray() }
                    : item).ToArray()
            } };
            // Exercise the actual analysis representation, including field values,
            // optional-key presence, layout and separate reference provenance.
            byte[] transported = JsonSerializer.SerializeToUtf8Bytes(updated, sJson);
            var reopened = JsonSerializer.Deserialize<ModuleAnalysisResult>(transported, sJson)
                ?? throw new InvalidDataException("The normalized analysis could not be reopened.");
            var reopenedSkin = reopened.BuildPlan.ObjectSets.SelectMany(x => x.SkinParameters)
                .Single(x => x.SourceObject == sourceObject);
            byte[] emitted = SkinParameterTextCodec.Serialize(reopenedSkin.FinalParameters,
                reopenedSkin.ReferenceSelection!.Layout);
            if (!emitted.AsSpan().SequenceEqual(referenceBytes))
                throw new InvalidDataException("The normalized round-trip changed the selected reference bytes.");

            string parent = Path.GetDirectoryName(output)!;
            Directory.CreateDirectory(parent);
            staging = Path.Combine(parent, "." + Path.GetFileName(output) + ".staging-" + Guid.NewGuid().ToString("N"));
            string skinDirectory = Path.Combine(staging, "rom", "skin_param");
            Directory.CreateDirectory(skinDirectory);
            File.WriteAllBytes(Path.Combine(skinDirectory, selected.OutputFile), emitted);
            File.WriteAllBytes(Path.Combine(staging, "analysis-with-reference.json"), transported);
            var manifest = new SkinReferenceStageManifest("skin-reference-staging", false,
                analysis.NormalizedModule.Identity.CanonicalId, sourceObject, selected.TargetObject,
                options["--label"], reference.Sha256, archiveHash, SkinParameterTextCodec.Hash(analysisBytes),
                "rom/skin_param/" + selected.OutputFile, SkinParameterTextCodec.Hash(emitted),
                parameters.Length, parameters.Sum(x => x.Physical!.Fields.Count), true, true,
                targetBlocks.ToDictionary(x => x.ExternalName!, x => x.NodeNames.Count, StringComparer.Ordinal));
            File.WriteAllText(Path.Combine(staging, "skin-reference-manifest.json"), JsonSerializer.Serialize(manifest, sJson));
            Directory.Move(staging, output);
            staging = null;
            Console.WriteLine(output);
            return 0;
        }
        catch (Exception ex)
        {
            if (staging != null)
            {
                string parent = Path.GetDirectoryName(output)!;
                string resolved = Path.GetFullPath(staging);
                if (resolved.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(resolved).StartsWith("." + Path.GetFileName(output) + ".staging-", StringComparison.Ordinal) &&
                    Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
            }
            Console.Error.WriteLine("Skin reference staging failed: " + ex.Message);
            return 2;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: XRomConverter stage-skin-reference --analysis PATH --reference PATH --object SOURCE_OBJECT --objectset TARGET_FARC --label NAME --output PATH");
        return 64;
    }
}
