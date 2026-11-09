using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XRomConverter.Target.MegaMix;

internal sealed record RecommendedBinding(int SourceModule, int RuntimeCharacter, int TargetCos);

internal static class RecommendedModulesWriter
{
    private static byte[] Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("XRomConverter.Recommended." + name)
            ?? throw new InvalidDataException("Missing recommendation resource: " + name);
        using var buffer = new MemoryStream(); stream.CopyTo(buffer); return buffer.ToArray();
    }

    public static int Write(string output, IEnumerable<RecommendedBinding> bindings)
    {
        var allocated = bindings.ToArray();
        using var recipe = JsonDocument.Parse(Resource("recommendations.json"));
        var rows = new List<string>();
        foreach (var entry in recipe.RootElement.GetProperty("entries").EnumerateArray())
        {
            int character = entry.GetProperty("runtimeCharacter").GetInt32();
            var sourceIds = entry.GetProperty("sourceModules").EnumerateArray().Select(x => x.GetInt32()).ToHashSet();
            var matches = allocated.Where(x => x.RuntimeCharacter == character && sourceIds.Contains(x.SourceModule)).ToArray();
            if (matches.Length == 0) continue; // Partial builds only recommend emitted modules.
            var costumes = matches.Select(x => x.TargetCos).Distinct().ToArray();
            if (costumes.Length != 1 || costumes[0] < 0 || costumes[0] > 65535)
                throw new InvalidDataException("Ambiguous or invalid recommendation allocation.");
            rows.Add($"{entry.GetProperty("pv").GetInt32()}\t{entry.GetProperty("performer").GetInt32()}\t{character}\t{costumes[0]}");
        }
        using var quirkyRecipe = JsonDocument.Parse(Resource("quirky-modules.json"));
        var quirkyRows = new SortedSet<string>();
        foreach (var entry in quirkyRecipe.RootElement.EnumerateArray())
        {
            int ch = entry.GetProperty("character").GetInt32();
            int native = ch == 4 ? 6 : ch == 5 ? 7 : ch;
            int source = entry.GetProperty("sourceModule").GetInt32();
            foreach (var binding in allocated.Where(x => x.SourceModule == source && x.RuntimeCharacter == native))
            {
                if (binding.TargetCos < 0 || binding.TargetCos > 65535) throw new InvalidDataException("Invalid Quirky allocation.");
                quirkyRows.Add($"{ch}\t{binding.TargetCos}");
            }
        }
        if (rows.Count == 0 && quirkyRows.Count == 0) return 0;
        string configPath = Path.Combine(output, "config.toml");
        string config = File.ReadAllText(configPath);
        config = Regex.Replace(config, @"(?m)^(\s*dll\s*=\s*\[)([^\]\r\n]*)(\]\s*)$", m =>
        {
            string values = m.Groups[2].Value;
            values = Regex.Replace(values, "\"(?:XRomRecommendedModules|QuirkyEyesRuntime)\\.dll\"\\s*,?", "");
            values = values.Trim().Trim(',').Trim();
            return m.Groups[1].Value + values + m.Groups[3].Value;
        });
        var dllLine = Regex.Match(config, @"(?m)^\s*dll\s*=\s*\[(?<values>[^\]\r\n]*)\]\s*$");
        if (Regex.IsMatch(config, @"(?m)^\s*dll\s*=") && !dllLine.Success)
            throw new InvalidDataException("Unsupported DLL configuration; refusing to replace it.");
        if (!dllLine.Success) config += "\ndll = [\"XRomRuntime.dll\"]\n";
        else if (!dllLine.Groups["values"].Value.Contains("\"XRomRuntime.dll\"", StringComparison.Ordinal))
        {
            string values = dllLine.Groups["values"].Value.Trim();
            config = config[..dllLine.Index] + "dll = [" + values + (values.Length == 0 ? "" : ", ") + "\"XRomRuntime.dll\"]" + config[(dllLine.Index + dllLine.Length)..];
        }
        File.WriteAllBytes(Path.Combine(output, "XRomRuntime.dll"), Resource("XRomRuntime.dll"));
        File.WriteAllBytes(Path.Combine(output, "LICENSE-MINHOOK.txt"), Resource("LICENSE-MINHOOK.txt"));
        File.WriteAllText(Path.Combine(output, "recommendations.tsv"), "# Existing PV\tperformer\tnative character\tallocated internal COS\n" + string.Join("\n", rows) + "\n", new UTF8Encoding(false));
        File.WriteAllBytes(Path.Combine(output, "quirky-curves.bin"), Resource("quirky-curves.bin"));
        File.WriteAllText(Path.Combine(output, "quirky-modules.tsv"), "# Catalog character\tallocated COS\n" + string.Join("\n", quirkyRows) + "\n", new UTF8Encoding(false));
        string ini = Path.Combine(output, "xrom-runtime.ini");
        if (!File.Exists(ini)) File.WriteAllText(ini, "[RecommendedModules]\nenabled=1\n[QuirkyEyes]\nenabled=1\n", new UTF8Encoding(false));
        File.WriteAllText(configPath, config, new UTF8Encoding(false));
        return rows.Count;
    }

    public static int Run(string[] args)
    {
        if (args.Length != 5 || args[1] != "--catalog" || args[3] != "--output")
        { Console.Error.WriteLine("attach-recommended-modules --catalog identity-catalog.json --output model-mod-directory"); return 2; }
        using var catalog = JsonDocument.Parse(File.ReadAllText(args[2]));
        var characters = new Dictionary<string, int> { ["MIKU"] = 0, ["RIN"] = 1, ["LEN"] = 2, ["LUKA"] = 3, ["KAITO"] = 6, ["MEIKO"] = 7 };
        var bindings = catalog.RootElement.EnumerateArray().Where(x => characters.ContainsKey(x.GetProperty("chara").GetString()!))
            .Select(x => new RecommendedBinding(x.GetProperty("sourceModule").GetInt32(), characters[x.GetProperty("chara").GetString()!], x.GetProperty("currentTargetCos").GetInt32())).ToArray();
        int count = Write(args[4], bindings); Console.WriteLine($"Integrated {count} recommendations; no PV database emitted."); return 0;
    }
}
