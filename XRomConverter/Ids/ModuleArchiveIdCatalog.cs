using System.Text;

namespace XRomConverter.Ids;

internal static class ModuleArchiveIdCatalog
{
    internal sealed record Row(int Id, string NameEn, string NameJp, string SourceOrReservation);

    public static IReadOnlyList<Row> LoadRows(string path) => File.ReadLines(path).Skip(1)
        .Select(ParseLine)
        .Where(x => x.Count >= 4 && int.TryParse(x[0], out _))
        .Select(x => new Row(int.Parse(x[0]), x[1].Trim(), x[2].Trim(), x[3].Trim()))
        .ToArray();

    public static IReadOnlySet<int> LoadOccupiedModuleIds(string path)
    {
        return LoadRows(path).Where(x => !x.SourceOrReservation.Equals("Unused",
            StringComparison.OrdinalIgnoreCase)).Select(x => x.Id).ToHashSet();
    }

    private static IReadOnlyList<string> ParseLine(string line)
    {
        var fields = new List<string>();
        var value = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char current = line[i];
            if (current == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    value.Append('"');
                    i++;
                }
                else quoted = !quoted;
            }
            else if (current == ',' && !quoted)
            {
                fields.Add(value.ToString());
                value.Clear();
            }
            else value.Append(current);
        }
        fields.Add(value.ToString());
        return fields;
    }
}
