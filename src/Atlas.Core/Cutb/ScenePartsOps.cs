using System.Text;
using System.Text.RegularExpressions;
using Lumina;
using Lumina.Excel;

namespace Atlas.Core.Cutb;

/// <summary>
/// Cutscene row catalog: (RowId, cutb path) pairs in RowId order. The old CutScan
/// pipeline read these from a dumped Cutscene.csv; the port can also read the
/// Cutscene sheet directly via Lumina (identical order/content — paths are unique).
/// </summary>
public static class CutsceneCatalog
{
    /// <summary>Read from the live Cutscene sheet (first string column = cutb path).</summary>
    public static List<(int Row, string Path)> FromSheet(XivEnv env)
    {
        var raw = env.Game.Excel.GetRawSheet("Cutscene");
        var strCol = -1;
        for (var i = 0; i < raw.Columns.Count; i++)
            if (raw.Columns[i].Type == Lumina.Data.Structs.Excel.ExcelColumnDataType.String) { strCol = i; break; }
        if (strCol < 0) throw new InvalidOperationException("Cutscene sheet has no string column");
        var sheet = env.Game.Excel.GetSheet<RawRow>(raw.Language, "Cutscene");
        var list = new List<(int, string)>();
        foreach (var row in sheet)
            list.Add(((int)row.RowId, Csv.Str(row.ReadColumn(strCol))));
        return list;
    }

    /// <summary>Faithful port of the CutScan csv-line parser (RowId col 0, path col 1, fallback scan).</summary>
    public static List<(int Row, string Path)> FromCsv(string csvPath)
    {
        var list = new List<(int, string)>();
        foreach (var line in File.ReadLines(csvPath))
        {
            var parts = line.Split(',');
            if (parts.Length < 2 || parts[0] == "RowId") continue;
            if (!int.TryParse(parts[0], out var rowId)) continue;
            var p = parts[1].Trim('"');
            if (!p.Contains('/')) { foreach (var c in parts) if (c.Contains('/')) { p = c.Trim('"'); break; } }
            list.Add((rowId, p));
        }
        return list;
    }
}

/// <summary>
/// scene-parts.csv builder — ported from CutScan `sceneparts`. Greps every cutb's
/// printable strings (CTRL resource tables) for level bgparts (part), bgcommon
/// sgb/mdl set pieces (prop) and collision pcbs. Golden header:
/// CutsceneRow,Cutb,Kind,Path
/// </summary>
public static class ScenePartsOps
{
    static readonly Regex RxPart = new(@"^bg/[ -~]+?/bgparts/[ -~]+?\.mdl$", RegexOptions.Compiled);
    static readonly Regex RxColl = new(@"^bg/[ -~]+?/collision/[ -~]+?\.pcb$", RegexOptions.Compiled);
    static readonly Regex RxProp = new(@"^bgcommon/[ -~]+?\.(mdl|sgb)$", RegexOptions.Compiled);

    public const string Header = "CutsceneRow,Cutb,Kind,Path";

    /// <summary>Printable-string scan of one cutb (path as in the Cutscene sheet,
    /// without the cut/ prefix or .cutb suffix). Null when absent from the sqpack.</summary>
    public static HashSet<(string Kind, string Path)>? Scan(GameData gd, string cutbPath)
    {
        var f = gd.GetFile($"cut/{cutbPath}.cutb");
        if (f == null) return null;
        var data = f.Data;
        var start = -1;
        var hits = new HashSet<(string, string)>();
        for (var i = 0; i <= data.Length; i++)
        {
            var ok = i < data.Length && data[i] >= 0x20 && data[i] < 0x7f;
            if (ok) { if (start < 0) start = i; continue; }
            if (start >= 0 && i - start >= 10)
            {
                var s = Encoding.ASCII.GetString(data, start, i - start);
                if (RxPart.IsMatch(s)) hits.Add(("part", s));
                else if (RxProp.IsMatch(s)) hits.Add(("prop", s));
                else if (RxColl.IsMatch(s)) hits.Add(("collision", s));
            }
            start = -1;
        }
        return hits;
    }

    static readonly Regex RxLevelDir = new(@"^bg/(?<dir>[ -~]+?)/level/[ -~]+$", RegexOptions.Compiled);

    /// <summary>Printable-string scan for level-dir references (lgb/lvb/lcb/svb
    /// paths) — cutscenes that jump to a stage carry these rather than bgparts.
    /// Returns stage dirs ("ffxiv/zon_z1/evt/z1e3"); null when the cutb is
    /// absent. Additive: the golden Scan/Write outputs are untouched.</summary>
    public static HashSet<string>? ScanLevelDirs(GameData gd, string cutbPath)
    {
        var f = gd.GetFile($"cut/{cutbPath}.cutb");
        if (f == null) return null;
        var data = f.Data;
        var start = -1;
        var dirs = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i <= data.Length; i++)
        {
            var ok = i < data.Length && data[i] >= 0x20 && data[i] < 0x7f;
            if (ok) { if (start < 0) start = i; continue; }
            if (start >= 0 && i - start >= 10)
            {
                var s = Encoding.ASCII.GetString(data, start, i - start);
                var m = RxLevelDir.Match(s);
                if (m.Success) dirs.Add(m.Groups["dir"].Value);
            }
            start = -1;
        }
        return dirs;
    }

    /// <summary>Rows for cutscene RowIds in [lo, hi). Returns cutbs scanned.</summary>
    public static int Write(GameData gd, IEnumerable<(int Row, string Path)> cutscenes,
        int lo, int hi, TextWriter w, bool header, Action<string>? log = null)
    {
        if (header) w.WriteLine(Header);
        var scanned = 0;
        var seenCutb = new HashSet<string>();
        foreach (var (rowId, p) in cutscenes)
        {
            if (rowId < lo || rowId >= hi) continue;
            if (!p.Contains('/') || !seenCutb.Add(p)) continue;
            var hits = Scan(gd, p);
            if (hits == null) continue;
            scanned++;
            foreach (var (k, s) in hits) w.WriteLine($"{rowId},{p},{k},{s}");
        }
        w.Flush();
        log?.Invoke($"scanned={scanned}");
        return scanned;
    }
}
