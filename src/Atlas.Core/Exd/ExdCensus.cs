using System.Security.Cryptography;
using System.Text;
using Lumina.Data;
using Lumina.Data.Files.Excel;
using Lumina.Excel;

namespace Atlas.Core.Exd;

/// <summary>
/// Patch-drift census over EVERY EXD sheet (root.exl): one CSV row per sheet
/// with row/column counts, a column-schema hash, and a content hash over all
/// .exd data pages in all languages. Diffing two censuses classifies drift as
/// SCHEMA (columns moved/added — breaks readers), ROWS (count changed) or
/// DATA (content-only, e.g. text edits). Because sqpack patches in place, the
/// census taken on patch N is the only baseline available when patch N+1 lands.
/// Core policy: no Console, no env vars — TextWriter + log callback only.
/// </summary>
public static class ExdCensus
{
    public const string Header = "Sheet,Variant,Language,Rows,Subrows,Cols,ColHash,Pages,Langs,DataHash";

    /// <summary>Write census rows for sheets [skip, skip+take). Returns sheets written.
    /// Deterministic order: root.exl names sorted OrdinalIgnoreCase.</summary>
    public static int Run(XivEnv env, TextWriter w, int skip = 0, int take = int.MaxValue,
        Action<string>? log = null)
    {
        var names = ExdOps.SheetNames(env).ToList();
        var written = 0;
        foreach (var name in names.Skip(skip).Take(take))
        {
            try { w.WriteLine(RowFor(env, name)); written++; }
            catch (Exception e) { log?.Invoke($"# {name}: ERROR {e.Message}"); w.WriteLine($"{Csv.Escape(name)},ERROR,,,,,,,,"); written++; }
            if (written % 500 == 0) { w.Flush(); log?.Invoke($"# census: {written} sheets (at {name})"); }
        }
        w.Flush();
        return written;
    }

    static string RowFor(XivEnv env, string name)
    {
        var sheet = env.Game.Excel.GetRawSheet(name);
        var isSub = sheet is RawSubrowExcelSheet;
        var subrows = isSub ? ((RawSubrowExcelSheet)sheet).TotalSubrowCount : 0;

        // Column-schema hash: type@offset per column, order as stored.
        var colSig = string.Join(";", sheet.Columns.Select(c => $"{c.Type}@{c.Offset:X}"));
        var colHash = ShortHash(Encoding.UTF8.GetBytes(colSig));

        // Content hash: every .exd page in every language, deterministic order.
        var pages = 0; var langsStr = ""; var dataHash = "";
        var exh = env.Game.GetFile<ExcelHeaderFile>($"exd/{name}.exh");
        if (exh != null)
        {
            pages = exh.DataPages.Length;
            var langs = exh.Languages.OrderBy(l => (int)l).ToArray();
            langsStr = string.Join("+", langs.Select(LangCode));
            using var inc = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            foreach (var lang in langs)
            {
                var suffix = lang == Language.None ? "" : "_" + LangCode(lang);
                foreach (var page in exh.DataPages.OrderBy(p => p.StartId))
                {
                    var f = env.Game.GetFile($"exd/{name}_{page.StartId}{suffix}.exd");
                    if (f != null) inc.AppendData(f.Data);
                }
            }
            dataHash = Convert.ToHexString(inc.GetHashAndReset())[..8].ToLowerInvariant();
        }

        return string.Join(",",
            Csv.Escape(name), isSub ? "Subrows" : "Default", sheet.Language,
            sheet.Count, subrows, sheet.Columns.Count, colHash, pages,
            Csv.Escape(langsStr), dataHash);
    }

    static string LangCode(Language l) => l switch
    {
        Language.Japanese => "ja", Language.English => "en", Language.German => "de",
        Language.French => "fr", Language.ChineseSimplified => "chs",
        Language.ChineseTraditional => "cht", Language.Korean => "ko",
        _ => "none"
    };

    static string ShortHash(byte[] data) =>
        Convert.ToHexString(MD5.HashData(data))[..8].ToLowerInvariant();

    // ------------------------------------------------------------------
    // Diff
    // ------------------------------------------------------------------

    public sealed class DiffResult
    {
        public int SheetsA, SheetsB, Added, Removed, Schema, Rows, Data;
        public bool Identical => Added == 0 && Removed == 0 && Schema == 0 && Rows == 0 && Data == 0;
    }

    /// <summary>Diff census A (old) vs B (new). Comprehensive by design: every
    /// added/removed/changed sheet is listed (no example cap) — filter later.</summary>
    public static DiffResult Diff(string fileA, string fileB, TextWriter report, Action<string>? log = null)
    {
        var a = Load(fileA);
        var b = Load(fileB);
        var r = new DiffResult { SheetsA = a.Count, SheetsB = b.Count };
        report.WriteLine($"# exd census diff");
        report.WriteLine($"#   A (old): {fileA}  ({a.Count} sheets)");
        report.WriteLine($"#   B (new): {fileB}  ({b.Count} sheets)");
        report.WriteLine();

        foreach (var name in b.Keys.Except(a.Keys).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var n = b[name];
            report.WriteLine($"+ {name}  rows={n[3]} cols={n[5]} langs={n[8]}");
            r.Added++;
        }
        foreach (var name in a.Keys.Except(b.Keys).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            report.WriteLine($"- {name}");
            r.Removed++;
        }
        foreach (var name in a.Keys.Intersect(b.Keys).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var oa = a[name]; var ob = b[name];
            if (oa.SequenceEqual(ob)) continue;
            var tags = new List<string>();
            var schema = oa[5] != ob[5] || oa[6] != ob[6] || oa[1] != ob[1];
            var rows = oa[3] != ob[3] || oa[4] != ob[4];
            if (schema) { tags.Add($"SCHEMA cols {oa[5]}->{ob[5]} colhash {oa[6]}->{ob[6]}"); r.Schema++; }
            if (rows)
            {
                var t = $"ROWS {oa[3]}->{ob[3]}";
                if (oa[4] != "0" || ob[4] != "0") t += $" subrows {oa[4]}->{ob[4]}";
                tags.Add(t); r.Rows++;
            }
            if (!schema && !rows) { tags.Add("DATA"); r.Data++; }
            else if (oa[9] != ob[9]) tags.Add("data-changed");
            if (oa[8] != ob[8]) tags.Add($"langs {oa[8]}->{ob[8]}");
            report.WriteLine($"~ {name}  [{string.Join("] [", tags)}]");
        }

        report.WriteLine();
        report.WriteLine($"# summary: +{r.Added} added, -{r.Removed} removed, " +
                         $"{r.Schema} schema-changed, {r.Rows} row-count-changed, {r.Data} content-only");
        report.Flush();
        return r;
    }

    static Dictionary<string, string[]> Load(string path)
    {
        var d = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || line.StartsWith("Sheet,")) continue;
            var cols = Split(line);
            if (cols.Length < 10) continue;
            d[cols[0]] = cols;
        }
        return d;
    }

    /// <summary>Minimal CSV split (handles quoted fields; census fields never embed newlines).</summary>
    static string[] Split(string line)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        var inQ = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQ)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') inQ = false;
                else sb.Append(c);
            }
            else if (c == '"') inQ = true;
            else if (c == ',') { parts.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        parts.Add(sb.ToString());
        return parts.ToArray();
    }
}
