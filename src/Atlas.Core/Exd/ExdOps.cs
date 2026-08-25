using System.Text;
using Lumina.Data;
using Lumina.Excel;

namespace Atlas.Core.Exd;

/// <summary>
/// Sheet operations (sheets/header/dump) — logic ported from xivtool/Program.cs.
/// Core policy: no Console, no env vars; TextWriter + log callback only.
/// </summary>
public static class ExdOps
{
    public static IEnumerable<string> SheetNames(XivEnv env, string? filter = null) =>
        env.Game.Excel.SheetNames
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Where(n => filter == null || n.Contains(filter, StringComparison.OrdinalIgnoreCase));

    public static void WriteHeader(XivEnv env, string sheetName, TextWriter w)
    {
        var sheet = env.Game.Excel.GetRawSheet(sheetName);
        var isSub = sheet is RawSubrowExcelSheet;
        var names = SchemaNames.TryLoad(env.SchemaDir, sheetName, sheet);
        w.WriteLine($"sheet:    {sheetName}");
        w.WriteLine($"variant:  {(isSub ? "Subrows" : "Default")}");
        w.WriteLine($"language: {sheet.Language}");
        w.WriteLine($"rows:     {sheet.Count}" + (isSub ? $"  (total subrows: {((RawSubrowExcelSheet)sheet).TotalSubrowCount})" : ""));
        w.WriteLine($"columns:  {sheet.Columns.Count}");
        for (var i = 0; i < sheet.Columns.Count; i++)
        {
            var nm = names != null ? "  " + names.NameOfRawIndex(i) : "";
            var lt = names?.TargetsOfRawIndex(i) ?? [];
            var link = lt.Length > 0 ? "  -> " + string.Join("|", lt) : "";
            w.WriteLine($"  [{i,3}] {sheet.Columns[i].Type,-12} @0x{sheet.Columns[i].Offset:X3}{nm}{link}");
        }
    }

    /// <summary>
    /// Link columns of a sheet per EXDSchema: CSV-header column name -> target
    /// sheet names (conditional links = union of case targets). Targets that are
    /// not real sheets are dropped. Empty when no schema.
    /// </summary>
    public static Dictionary<string, string[]> SheetLinks(XivEnv env, string sheetName)
    {
        var res = new Dictionary<string, string[]>();
        var raw = env.Game.Excel.GetRawSheet(sheetName);
        var schema = SchemaNames.TryLoad(env.SchemaDir, sheetName, raw);
        if (schema?.Targets == null) return res;
        var real = new HashSet<string>(env.Game.Excel.SheetNames, StringComparer.Ordinal);
        for (var k = 0; k < schema.Names.Length; k++)
        {
            if (schema.Targets[k] is not { Length: > 0 } t) continue;
            var kept = t.Where(real.Contains).ToArray();
            if (kept.Length > 0) res.TryAdd(schema.Names[k], kept);
        }
        return res;
    }

    /// <summary>Full CSV dump. Returns rows written.</summary>
    public static int Dump(XivEnv env, string sheetName, Language lang, TextWriter writer,
        int max = int.MaxValue, bool useSchema = true, Action<string>? log = null)
    {
        RawExcelSheet raw;
        try { raw = env.Game.Excel.GetRawSheet(sheetName, lang); }
        catch (Lumina.Excel.Exceptions.UnsupportedLanguageException) { raw = env.Game.Excel.GetRawSheet(sheetName, Language.None); }

        var colCount = raw.Columns.Count;
        var schema = useSchema ? SchemaNames.TryLoad(env.SchemaDir, sheetName, raw, log) : null;
        // order[k] = raw column index of k-th output column
        int[] order; string[] header;
        if (schema != null) { order = schema.OffsetOrder; header = schema.Names; }
        else
        {
            order = Enumerable.Range(0, colCount).ToArray();
            header = order.Select(i => $"col{i}").ToArray();
        }

        var sb = new StringBuilder();
        var written = 0;
        if (raw is RawSubrowExcelSheet)
        {
            var sheet = env.Game.Excel.GetSubrowSheet<RawSubrow>(raw.Language, sheetName);
            writer.WriteLine("RowId,SubrowId," + string.Join(",", header.Select(Csv.Escape)));
            foreach (var subrows in sheet)
                foreach (var row in subrows)
                {
                    sb.Clear(); sb.Append(row.RowId).Append(',').Append(row.SubrowId);
                    foreach (var c in order) sb.Append(',').Append(Csv.Escape(Csv.Str(row.ReadColumn(c))));
                    writer.WriteLine(sb.ToString());
                    if (++written >= max) { writer.Flush(); return written; }
                }
        }
        else
        {
            var sheet = env.Game.Excel.GetSheet<RawRow>(raw.Language, sheetName);
            writer.WriteLine("RowId," + string.Join(",", header.Select(Csv.Escape)));
            foreach (var row in sheet)
            {
                sb.Clear(); sb.Append(row.RowId);
                foreach (var c in order) sb.Append(',').Append(Csv.Escape(Csv.Str(row.ReadColumn(c))));
                writer.WriteLine(sb.ToString());
                if (++written >= max) { writer.Flush(); return written; }
            }
        }
        writer.Flush();
        return written;
    }

    public static Language ParseLang(string l) => l switch
    {
        "en" => Language.English, "ja" => Language.Japanese,
        "de" => Language.German, "fr" => Language.French,
        "none" => Language.None,
        _ => throw new ArgumentException($"unknown lang {l}")
    };
}
