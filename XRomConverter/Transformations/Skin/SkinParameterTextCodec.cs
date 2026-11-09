using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Objects.Extra.Parameters;
using XRomConverter.Analysis;

namespace XRomConverter.Transformations.Skin;

internal sealed record ParsedSkinText(IReadOnlyList<XSkinParameter> Parameters,
    SkinTextLayout Layout, string Sha256);

internal static class SkinParameterTextCodec
{
    private static readonly UTF8Encoding sUtf8 = new(false, true);

    public static IReadOnlyList<XSkinParameter> ConvertOsp(byte[] sourceBytes)
    {
        using var input = new MemoryStream(sourceBytes, writable: false);
        var osp = new OsageSkinParameterSet();
        osp.Load(input);
        osp.Format = BinaryFormat.DT;
        using var output = new MemoryStream();
        osp.Save(output, leaveOpen: true);
        var exported = Parse(output.ToArray(), "source-osp-converted", Hash(sourceBytes))
            .Parameters.ToDictionary(x => x.Name, StringComparer.Ordinal);
        // DT text export sorts keys. Keep the source OSP parameter order in the
        // analytical representation, matching the previous reader behavior.
        return osp.Parameters.Select(parameter => exported[parameter.Name]).ToArray();
    }

    public static ParsedSkinText Parse(byte[] bytes, string originKind,
        string? originalSourceSha256 = null)
    {
        bool bom = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
        string text = sUtf8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        var fields = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var lines = new List<SkinTextLine>();
        int offset = 0;
        while (offset < text.Length)
        {
            int end = offset;
            while (end < text.Length && text[end] != '\r' && text[end] != '\n') end++;
            string line = text[offset..end];
            string ending = "";
            if (end < text.Length)
            {
                ending = text[end].ToString();
                if (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n') ending = "\r\n";
            }
            offset = end + ending.Length;
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
            {
                lines.Add(new(null, null, "", line, ending));
                continue;
            }
            int equals = line.IndexOf('=');
            if (equals < 1) throw new InvalidDataException($"Invalid skin_param line: {line}");
            string key = line[..equals].Trim();
            int dot = key.IndexOf('.');
            if (dot <= 0 || dot == key.Length - 1)
                throw new InvalidDataException($"Expected parameter.field key: {key}");
            string name = key[..dot];
            string field = key[(dot + 1)..];
            string value = line[(equals + 1)..];
            if (!fields.TryGetValue(name, out var parameterFields))
                fields.Add(name, parameterFields = new(StringComparer.Ordinal));
            if (!parameterFields.TryAdd(field, value))
                throw new InvalidDataException($"Duplicate skin_param key: {key}");
            ValidateValue(key, field, value);
            lines.Add(new(name, field, line[..(equals + 1)], null, ending));
        }
        if (fields.Count == 0) throw new InvalidDataException("No skin parameters were found.");
        foreach (var parameter in fields)
        {
            ValidateArray(parameter.Key, parameter.Value, "node");
            ValidateArray(parameter.Key, parameter.Value, "root.coli");
            ValidateArray(parameter.Key, parameter.Value, "root.boc");
        }

        // The library provides the structural interpretation. Physical fields
        // remain in their lexical representation and are never saved via it.
        var parsed = new OsageSkinParameterSet();
        using (var stream = new MemoryStream(bytes, writable: false)) parsed.Load(stream);
        if (parsed.Parameters.Count != fields.Count ||
            parsed.Parameters.GroupBy(x => x.Name).Any(x => x.Count() != 1))
            throw new InvalidDataException("Parameter names do not agree with the library parser.");
        string hash = Hash(bytes);
        var parameters = parsed.Parameters.Select(parameter =>
        {
            if (!fields.TryGetValue(parameter.Name, out var values))
                throw new InvalidDataException($"Parsed parameter is not present in the text: {parameter.Name}");
            int count = int.Parse(values["node.length"].Trim(), CultureInfo.InvariantCulture);
            if (count != parameter.Nodes.Count)
                throw new InvalidDataException($"Parsed node count differs for {parameter.Name}.");
            var physical = new SkinParameterPhysicalData(originKind, originalSourceSha256 ?? hash,
                new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(values, StringComparer.Ordinal)));
            return new XSkinParameter(parameter.Name,
                parameter.Bocs.Select(x => x.EdRoot).Where(x => !string.IsNullOrEmpty(x)).ToArray(),
                parameter.CollisionTargetOsage, parameter.Nodes.Count, physical);
        }).ToArray();
        return new(parameters, new(bom, lines.ToArray()), hash);
    }

    public static byte[] Serialize(IReadOnlyList<NormalizedSkinParameter> parameters, SkinTextLayout layout)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            if (parameter.Physical == null)
                throw new InvalidDataException($"Full physical fields are unavailable for {parameter.Name}.");
            foreach (var pair in parameter.Physical.Fields)
                if (!fields.TryAdd(parameter.Name + "." + pair.Key, pair.Value))
                    throw new InvalidDataException($"Duplicate normalized field: {parameter.Name}.{pair.Key}");
        }
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var text = new StringBuilder();
        foreach (var line in layout.Lines)
        {
            if (line.ParameterName == null)
                text.Append(line.Literal);
            else
            {
                string key = line.ParameterName + "." + line.FieldName;
                if (!emitted.Add(key) || !fields.TryGetValue(key, out string? value))
                    throw new InvalidDataException($"Layout and physical fields disagree: {key}");
                if (line.Prefix.TrimEnd('=').Trim() != key || value.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                    throw new InvalidDataException($"Invalid field layout: {key}");
                text.Append(line.Prefix).Append(value);
            }
            if (line.Ending is not ("" or "\n" or "\r" or "\r\n"))
                throw new InvalidDataException("Invalid line ending in skin_param layout.");
            text.Append(line.Ending);
        }
        if (!emitted.SetEquals(fields.Keys))
            throw new InvalidDataException("Some physical fields have no serialized layout entry.");
        byte[] body = sUtf8.GetBytes(text.ToString());
        byte[] result = layout.Utf8Bom ? new byte[] { 0xef, 0xbb, 0xbf }.Concat(body).ToArray() : body;
        Parse(result, "serialized-validation");
        return result;
    }

    public static SkinParameterPhysicalData? Clone(SkinParameterPhysicalData? source) => source == null ? null :
        source with { Fields = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(source.Fields, StringComparer.Ordinal)) };

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static void ValidateValue(string key, string field, string value)
    {
        if (field.EndsWith(".name", StringComparison.Ordinal) ||
            field.EndsWith(".ed_root", StringComparison.Ordinal) || field == "root.colli_tgt_osg") return;
        if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ||
            !double.IsFinite(number))
            throw new InvalidDataException($"Invalid numeric skin_param value at {key}: {value}");
    }

    private static void ValidateArray(string name, IReadOnlyDictionary<string, string> fields, string prefix)
    {
        string lengthKey = prefix + ".length";
        if (!fields.TryGetValue(lengthKey, out string? literal))
        {
            if (prefix == "node" || fields.Keys.Any(x => x.StartsWith(prefix + ".", StringComparison.Ordinal)))
                throw new InvalidDataException($"Missing array length: {name}.{lengthKey}");
            return;
        }
        if (!int.TryParse(literal.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int length) || length < 0)
            throw new InvalidDataException($"Invalid array length: {name}.{lengthKey}");
        var regex = new Regex("^" + Regex.Escape(prefix) + @"\.(\d+)\.", RegexOptions.CultureInvariant);
        int[] indices = fields.Keys.Select(key => regex.Match(key)).Where(match => match.Success)
            .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().Order().ToArray();
        if (indices.Length != length || indices.Where((index, position) => index != position).Any())
            throw new InvalidDataException($"Non-contiguous or inconsistent array: {name}.{prefix}, declared {length}.");
    }
}
