using System.Text;

namespace XRomConverter.Ids;

internal sealed class ObjectSetArchiveIdCatalog
{
    internal sealed record Row(uint Id, string Name, string Source);

    private readonly IReadOnlyList<Row> mRows;

    private ObjectSetArchiveIdCatalog(IReadOnlyList<Row> rows) => mRows = rows;

    public IReadOnlyList<Row> ReservedRows => mRows
        .Where(x => !x.Source.Equals("Unused", StringComparison.OrdinalIgnoreCase)).ToArray();

    public static ObjectSetArchiveIdCatalog Load(string path)
    {
        var rows = File.ReadLines(path).Skip(1).Select(ParseLine)
            .Where(x => x.Count >= 3 && uint.TryParse(x[0], out _))
            .Select(x => new Row(uint.Parse(x[0]), x[1].Trim(), x[2].Trim()))
            .ToArray();
        if (rows.Length == 0) throw new InvalidDataException("Object-set spreadsheet is empty.");
        return new ObjectSetArchiveIdCatalog(rows);
    }

    public IReadOnlyDictionary<string, uint> Allocate(IEnumerable<string> names,
        uint firstCandidate = 40000, IEnumerable<uint>? additionalOccupied = null)
    {
        var additional = (additionalOccupied ?? Array.Empty<uint>()).ToHashSet();
        var occupied = mRows.Where(x => !x.Source.Equals("Unused", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Id).Concat(additional).ToHashSet();
        var result = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names.Distinct(StringComparer.OrdinalIgnoreCase).Order())
        {
            var known = mRows.Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                                         !x.Source.Equals("Unused", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Id).Distinct().ToArray();
            if (known.Length > 1)
                throw new InvalidDataException($"Object set '{name}' has multiple IDs in the spreadsheet.");
            if (known.Length == 1)
            {
                if (additional.Contains(known[0]))
                    throw new InvalidDataException($"Spreadsheet binding for '{name}' conflicts with an explicit physical set ID {known[0]}.");
                result[name] = known[0]; continue;
            }
            while (occupied.Contains(firstCandidate)) firstCandidate++;
            result[name] = firstCandidate;
            occupied.Add(firstCandidate++);
        }
        return result;
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
                { value.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (current == ',' && !quoted) { fields.Add(value.ToString()); value.Clear(); }
            else value.Append(current);
        }
        fields.Add(value.ToString());
        return fields;
    }
}
