using System.Text;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.IO;
using XRomConverter.Analysis;
using XRomConverter.Ids;

namespace XRomConverter.Target.MegaMix;

internal sealed record ModuleRegistrationReference(string Path, string Sha256, int ModuleId,
    int SourceCount, IReadOnlyDictionary<string, string> Fields);

internal static class ModuleTableReference
{
    internal static Dictionary<string, string> ReadFields(string path)
    {
        using var archive = BinaryFile.Load<FarcArchive>(path);
        using var stream = archive.Open("gm_module_id.bin", EntryStreamMode.MemoryStream);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in reader.ReadToEnd().Split('\n'))
        {
            string text = line.TrimEnd('\r').TrimStart('\uFEFF');
            if (string.IsNullOrWhiteSpace(text)) continue;
            int equals = text.IndexOf('=');
            if (equals <= 0 || !fields.TryAdd(text[..equals], text[(equals + 1)..]))
                throw new InvalidDataException("Invalid or duplicate module reference field.");
        }
        return fields;
    }

    internal static ModuleRegistrationReference Select(string path, int moduleId, string character)
    {
        string full = System.IO.Path.GetFullPath(path);
        var fields = ReadFields(full);
        if (!fields.TryGetValue("module.data_list.length", out string? countText) ||
            !int.TryParse(countText, out int count) || count < 1 || fields.ContainsKey("data_list.length"))
            throw new InvalidDataException("Reference must declare module.data_list.length inside module.");
        var prefixes = fields.Where(x => x.Key.StartsWith("module.") && x.Key.EndsWith(".id") &&
            x.Value == moduleId.ToString()).Select(x => x.Key[..^2]).ToArray();
        if (prefixes.Length != 1) throw new InvalidDataException("Reference module ID is missing or ambiguous.");
        string prefix = prefixes[0];
        var row = fields.Where(x => x.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(x => x.Key[prefix.Length..], x => x.Value, StringComparer.Ordinal);
        string[] required = { "attr", "chara", "cos", "id", "name", "ng", "shop_ed_day", "shop_ed_month",
            "shop_ed_year", "shop_price", "shop_st_day", "shop_st_month", "shop_st_year", "sort_index" };
        if (required.Any(x => !row.ContainsKey(x)) || row["chara"] != character || row["attr"] is not ("2" or "8"))
            throw new InvalidDataException("Reference does not have the supported XRomConverter/Complete Modules registration structure.");
        return new(full, PhysicalOccupancy.Hash(File.ReadAllBytes(full)), moduleId, count, row);
    }

    internal static IReadOnlyDictionary<string, string> TargetFields(ModuleRegistrationReference reference,
        ModuleAnalysisResult result) => reference.Fields.ToDictionary(x => x.Key, x => x.Key switch
        {
            "id" => result.BuildPlan.ModuleBinding.TargetId!.Value.ToString(),
            "chara" => MegaMixStagingWriterCommand.NormalizeCharacter(result.NormalizedModule.Character),
            "cos" => "COS_" + (CharacterItemTargetResolver.CostumeId(result) + 1),
            "name" => result.NormalizedModule.Identity!.DisplayName,
            "sort_index" => result.Aggregate.Module.SortIndex.ToString(),
            _ => x.Value
        });

    internal static void Validate(string output, ModuleRegistrationReference reference, ModuleAnalysisResult result)
    {
        if (PhysicalOccupancy.Hash(File.ReadAllBytes(reference.Path)) != reference.Sha256)
            throw new InvalidDataException("Selected module table reference changed.");
        var selected = Select(reference.Path, reference.ModuleId,
            MegaMixStagingWriterCommand.NormalizeCharacter(result.NormalizedModule.Character));
        if (selected.SourceCount != reference.SourceCount || !selected.Fields.OrderBy(x => x.Key).SequenceEqual(reference.Fields.OrderBy(x => x.Key)))
            throw new InvalidDataException("Selected module registration provenance differs.");
        var expected = TargetFields(reference, result).ToDictionary(x => "module.0." + x.Key, x => x.Value);
        expected.Add("module.data_list.length", "1");
        var actual = ReadFields(output);
        if (actual.Count != expected.Count || expected.Any(x => !actual.TryGetValue(x.Key, out string? value) || value != x.Value))
            throw new InvalidDataException("Module registration differs from Complete Modules/converter reference structure.");
    }
}
