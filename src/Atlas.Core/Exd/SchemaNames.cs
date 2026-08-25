using Lumina.Excel;

namespace Atlas.Core.Exd;

/// <summary>
/// EXDSchema (.yml) column-name loader. With a schema, dumps emit columns in
/// offset-sorted order with real names (community CSV convention); without,
/// EXH definition order with colN indices (= RawRow.ReadColumn indices).
/// Ported verbatim from xivtool/Program.cs (Wave 1).
/// </summary>
public sealed class SchemaNames
{
    public required string[] Names;        // per output column, in offset-sorted order
    public required int[] OffsetOrder;     // output column -> raw EXH column index
    private readonly Dictionary<int, string> _byRaw = new();
    public string NameOfRawIndex(int i) => _byRaw.TryGetValue(i, out var n) ? n : $"col{i}";

    public static SchemaNames? TryLoad(string? dir, string sheet, RawExcelSheet raw, Action<string>? log = null)
    {
        if (dir == null) return null;
        var path = Path.Combine(dir, sheet + ".yml");
        if (!File.Exists(path)) return null;
        try
        {
            var fields = ParseFields(File.ReadAllLines(path));
            var flat = new List<string>();
            foreach (var f in fields) Flatten(f, null, flat);

            // offset-sorted raw column order; packed bools at same offset sort by bit (Type enum order)
            var order = Enumerable.Range(0, raw.Columns.Count)
                .OrderBy(i => raw.Columns[i].Offset)
                .ThenBy(i => (int)raw.Columns[i].Type)
                .ToArray();
            if (flat.Count != order.Length)
            {
                log?.Invoke($"warning: schema fields ({flat.Count}) != columns ({order.Length}) for {sheet}; falling back to raw order");
                return null;
            }
            var res = new SchemaNames { Names = flat.ToArray(), OffsetOrder = order };
            for (var k = 0; k < order.Length; k++) res._byRaw[order[k]] = flat[k];
            return res;
        }
        catch (Exception e)
        {
            log?.Invoke($"warning: failed to parse schema for {sheet}: {e.Message}");
            return null;
        }
    }

    sealed class Field
    {
        public string? Name;
        public bool IsArray;
        public int Count = 1;
        public List<Field>? Sub;
    }

    static void Flatten(Field f, string? prefix, List<string> outNames)
    {
        var baseName = (prefix ?? "") + (f.Name ?? "Unknown");
        if (!f.IsArray) { outNames.Add(baseName); return; }
        for (var i = 0; i < f.Count; i++)
        {
            if (f.Sub == null || f.Sub.Count == 0) { outNames.Add($"{baseName}[{i}]"); continue; }
            if (f.Sub.Count == 1 && f.Sub[0].Name == null && !f.Sub[0].IsArray) { outNames.Add($"{baseName}[{i}]"); continue; }
            foreach (var sf in f.Sub)
            {
                if (sf.Name == null && !sf.IsArray) outNames.Add($"{baseName}[{i}]");
                else Flatten(sf, $"{baseName}[{i}].", outNames);
            }
        }
    }

    static List<Field> ParseFields(string[] lines)
    {
        // find top-level "fields:" then parse the indented list (also used recursively)
        var i = 0;
        while (i < lines.Length && !lines[i].StartsWith("fields:")) i++;
        if (i == lines.Length) throw new Exception("no fields: block");
        i++;
        return ParseList(lines, ref i, IndentOf(lines, i));
    }

    static int IndentOf(string[] lines, int i) =>
        i < lines.Length ? lines[i].Length - lines[i].TrimStart().Length : 0;

    static List<Field> ParseList(string[] lines, ref int i, int listIndent)
    {
        var result = new List<Field>();
        while (i < lines.Length)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }
            var indent = line.Length - line.TrimStart().Length;
            var t = line.TrimStart();
            if (indent < listIndent || !t.StartsWith("- ")) break;
            if (indent > listIndent) break; // deeper list belongs to a nested key handled below
            var f = new Field();
            result.Add(f);
            // first key on the "- " line
            ConsumeKey(f, t.Substring(2), lines, ref i, listIndent + 2);
        }
        return result;
    }

    static void ConsumeKey(Field f, string firstKey, string[] lines, ref int i, int keyIndent)
    {
        ApplyKey(f, firstKey, lines, ref i, keyIndent);
        while (i < lines.Length)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }
            var indent = line.Length - line.TrimStart().Length;
            var t = line.TrimStart();
            if (indent != keyIndent || t.StartsWith("- ")) break;
            ApplyKey(f, t, lines, ref i, keyIndent);
        }
    }

    static void ApplyKey(Field f, string kv, string[] lines, ref int i, int keyIndent)
    {
        i++; // consume current line by default
        var ci = kv.IndexOf(':');
        if (ci < 0) return;
        var key = kv.Substring(0, ci).Trim();
        var val = kv.Substring(ci + 1).Trim();
        switch (key)
        {
            case "name": f.Name = val; break;
            case "type": if (val == "array") f.IsArray = true; break;
            case "count": f.Count = int.Parse(val); break;
            case "fields":
                f.Sub = ParseList(lines, ref i, IndentOf(lines, i));
                break;
            // pendingFields/targets/condition/comment etc: ignore scalar values;
            // block values (indented deeper) skipped:
            default:
                while (i < lines.Length &&
                       (string.IsNullOrWhiteSpace(lines[i]) ||
                        lines[i].Length - lines[i].TrimStart().Length > keyIndent))
                    i++;
                break;
        }
    }
}
