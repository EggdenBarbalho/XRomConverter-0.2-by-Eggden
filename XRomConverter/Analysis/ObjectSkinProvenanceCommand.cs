using System.Security.Cryptography;
using System.Text.Json;

namespace XRomConverter.Analysis;

internal sealed record SkinReferenceIssue(string Code, string Severity, string Message);
internal sealed record ObjectSkinProvenance(string ObjectSetArchive, string ObjectName,
    string ObjectProvenanceRoot, IReadOnlyList<string> ExternalNames, string ExpectedSkinPath,
    bool Found, string Status, string? SkinSha256, IReadOnlyList<string> ParameterNames,
    IReadOnlyList<string> BocTargets, IReadOnlyList<SkinReferenceIssue> Issues);

internal static class ObjectSkinProvenanceCommand
{
    public static bool IsRequested(string[] args) => args.Length > 0 &&
        args[0].Equals("validate-object-skin-provenance", StringComparison.OrdinalIgnoreCase);

    public static int Run(string[] args)
    {
        if (args.Length < 4 || !File.Exists(args[1])) return 64;
        string? outputValue = null;
        string? skinRootValue = null;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--output" && i + 1 < args.Length) outputValue = args[++i];
            else if (args[i] == "--skin-root" && i + 1 < args.Length) skinRootValue = args[++i];
            else return 64;
        }
        if (outputValue == null || skinRootValue != null && !Directory.Exists(skinRootValue)) return 64;
        string? skinRoot = skinRootValue == null ? null : Path.GetFullPath(skinRootValue);
        var pairs = JsonSerializer.Deserialize<ObjectSetPairInspection[]>(File.ReadAllText(args[1]),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? Array.Empty<ObjectSetPairInspection>();
        var results = new List<ObjectSkinProvenance>();
        foreach (var pair in pairs)
        foreach (var obj in pair.Objects)
        {
            string root = Path.GetDirectoryName(Path.GetDirectoryName(pair.Archive)!)!;
            string expected = FindSkinPath(pair.Archive, obj.Name, root, skinRoot);
            string[] externalNames = obj.Blocks.Where(block => block.Signature == "OSG" &&
                    !string.IsNullOrWhiteSpace(block.ExternalName))
                .Select(block => block.ExternalName!).ToArray();
            var issues = new List<SkinReferenceIssue>();
            var parameters = new List<string>();
            var bocTargets = new List<string>();
            bool found = File.Exists(expected);
            if (found)
            {
                string[] lines = File.ReadAllLines(expected);
                parameters.AddRange(lines.Select(line => line.Split('=', 2)[0])
                    .Where(key => key.EndsWith(".node.length", StringComparison.Ordinal))
                    .Select(key => key[..^".node.length".Length]));
                bocTargets.AddRange(lines.Select(line => line.Split('=', 2))
                    .Where(parts => parts.Length == 2 && parts[0].EndsWith(".ed_root", StringComparison.Ordinal))
                    .Select(parts => parts[1].Trim()).Where(value => value.Length > 0));
                foreach (string externalName in externalNames.Distinct(StringComparer.Ordinal))
                {
                    int count = parameters.Count(name => name == externalName);
                    if (count != 1) issues.Add(new("OSP_CARDINALITY", "error",
                        $"{externalName} has {count} matching parameters; exactly one is required."));
                }
                foreach (string target in bocTargets.Distinct(StringComparer.Ordinal))
                    if (!parameters.Contains(target, StringComparer.Ordinal))
                        issues.Add(new("BOC_TARGET_MISSING", "error", target + " is not a parameter in the same file."));
            }
            else if (externalNames.Length > 0)
                issues.Add(new("EXPECTED_SKIN_MISSING", "error", "Object has OSG blocks but its provenance-local skin_param is absent."));
            string status = !found && externalNames.Length == 0 ? "not-used" :
                !found ? "expected-but-missing" : issues.Count == 0 ? "valid" : "invalid";
            results.Add(new(pair.Archive, obj.Name, root, externalNames, expected, found, status,
                found ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(expected))) : null,
                parameters, bocTargets, issues));
        }
        string output = Path.GetFullPath(outputValue);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(results, new JsonSerializerOptions
        { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Console.WriteLine(output);
        return results.Any(result => result.Issues.Any(issue => issue.Severity == "error")) ? 2 : 0;
    }

    private static string FindSkinPath(string archive, string objectName,
        string localRoot, string? externalRoot)
    {
        string fileName = "ext_skp_" + objectName + ".txt";
        if (externalRoot == null) return Path.Combine(localRoot, "skin_param", fileName);
        string[] parts = Path.GetFullPath(archive).Split(Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        string? layer = parts.LastOrDefault(part =>
            part.StartsWith("rom_", StringComparison.OrdinalIgnoreCase));
        layer ??= parts.LastOrDefault(part => part.Equals("rom", StringComparison.OrdinalIgnoreCase));
        if (layer != null)
        {
            string candidate = layer.Equals("rom", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(externalRoot, "rom", "skin_param", fileName)
                : Path.Combine(externalRoot, layer, "rom", "skin_param", fileName);
            if (File.Exists(candidate)) return candidate;
        }
        string[] candidates = Directory.EnumerateFiles(externalRoot, fileName,
            SearchOption.AllDirectories).ToArray();
        return candidates.Length == 1 ? candidates[0] : Path.Combine(externalRoot,
            layer ?? "unknown-layer", "rom", "skin_param", fileName);
    }
}
