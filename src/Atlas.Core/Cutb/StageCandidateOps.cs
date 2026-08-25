using System.Text.RegularExpressions;
using Lumina.Data;
using Lumina.Excel;
using Atlas.Core.Exd;

namespace Atlas.Core.Cutb;

/// <summary>
/// stage-candidates.csv builder — per-patch cutscene→venue auto-detection for
/// runtime "visits" (verified:
/// chain proven live on the 2026.08.05 build, 29/30 new cutscenes resolved).
///
/// For each new/changed Cutscene row: scan its cutb's printable strings for
/// level bgparts/collision paths (ScenePartsOps regexes); the dominant
/// bg/&lt;stagedir&gt;/ prefix IS the venue (this is how the rosetta's
/// StageLevelDirs column was built). Venues are then classified:
///   - level dir has a TerritoryType row  -> Tier B candidate (plain LoadZone),
///   - no TT row but bg/&lt;dir&gt;/level/bg.lgb exists -> swap-stage candidate,
/// and joined against the canonical leveldirs set (NewDir) + PlaceName.
/// Cutbs with no bg part/collision paths (vfx/overlay-only) are reported as
/// "(no venue)" rather than dropped — e.g. chrhdb95225 on 2026.08.05.
///
/// Core policy: no Console, no env vars — TextWriter + log callback only.
/// </summary>
public static class StageCandidateOps
{
    /// <summary>runtime-Stage-shaped candidate rows. QuestGuess is blank in v1
    /// (quest association needs the per-territory workspace joins).</summary>
    public const string Header = "Bg,TierB_TT,NewDir,CutbCount,SampleCutbs,PlaceNameGuess,QuestGuess,Note";

    // bg/<stagedir>/bgparts/... | bg/<stagedir>/collision/... -> stagedir
    static readonly Regex RxStageDir = new(@"^bg/(?<dir>.+?)/(?:bgparts|collision)/", RegexOptions.Compiled);

    /// <summary>Derive venue candidates for the given Cutscene RowIds and write
    /// them as CSV. Returns the number of data rows written.</summary>
    /// <param name="canonicalDirs">Level-dir tokens from the baseline leveldirs.txt
    /// (format "bg/&lt;expac&gt;/&lt;region&gt;/&lt;type&gt;/&lt;code&gt;/level"); a venue whose
    /// level dir is absent is flagged NewDir.</param>
    public static int Write(XivEnv env, IReadOnlyCollection<int> rows, ISet<string> canonicalDirs,
        TextWriter w, bool header, Action<string>? log = null)
    {
        // 1. Cutscene rows -> cutb paths (dedup'd; identical paths share one scan).
        var wanted = rows.ToHashSet();
        var cutbs = new List<(int Row, string Path)>();
        foreach (var (r, p) in CutsceneCatalog.FromSheet(env))
            if (wanted.Contains(r) && p.Contains('/')) cutbs.Add((r, p));
        var missing = wanted.Except(cutbs.Select(c => c.Row)).ToList();
        if (missing.Count > 0)
            log?.Invoke($"# {missing.Count} requested row(s) have no cutb path, skipped: {string.Join(",", missing)}");

        // 2. Per cutb: dominant bg/<stagedir>/ prefix over part+collision paths.
        var byVenue = new SortedDictionary<string, List<string>>(StringComparer.Ordinal); // stagedir -> cutbs
        var noVenue = new List<(string Cutb, string Why)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, p) in cutbs)
        {
            if (!seen.Add(p)) continue;
            var hits = ScenePartsOps.Scan(env.Game, p);
            if (hits == null) { noVenue.Add((p, "cutb missing from sqpack")); continue; }
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (kind, path) in hits)
            {
                if (kind == "prop") continue; // bgcommon set pieces carry no venue
                var m = RxStageDir.Match(path);
                if (m.Success) counts[m.Groups["dir"].Value] = counts.GetValueOrDefault(m.Groups["dir"].Value) + 1;
            }
            if (counts.Count == 0) { noVenue.Add((p, "no bg part/collision paths (vfx/overlay-only?)")); continue; }
            var dir = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
            if (!byVenue.TryGetValue(dir, out var list)) byVenue[dir] = list = new();
            list.Add(p);
        }

        // 3. TerritoryType join: level dir -> (lowest TT RowId, full Bg string, PlaceName id).
        //    Same detection as patch intake step 0 (first string column containing
        //    "/level/"), plus the PlaceName column (schema name, raw index 5 fallback
        //    per NOTES-Territory.md).
        var (ttRaw, ttRows) = OpenRaw(env, "TerritoryType");
        var pnIdCol = Col(SchemaNames.TryLoad(env.SchemaDir, "TerritoryType", ttRaw, log), "PlaceName", 5);
        var ttByLevelDir = new Dictionary<string, (uint Tt, string Bg, uint Pn)>(StringComparer.Ordinal);
        foreach (var row in ttRows)
            for (var c = 0; c < ttRaw.Columns.Count; c++)
                if (ttRaw.Columns[c].Type == Lumina.Data.Structs.Excel.ExcelColumnDataType.String)
                {
                    var s = Csv.Str(row.ReadColumn(c));
                    if (!s.Contains("/level/")) continue;
                    uint.TryParse(Csv.Str(row.ReadColumn(pnIdCol)), out var pn);
                    ttByLevelDir.TryAdd("bg/" + s[..s.LastIndexOf('/')], ((uint)row.RowId, s, pn));
                    break;
                }

        var (pnRaw, pnRows) = OpenRaw(env, "PlaceName");
        var pnNameCol = Col(SchemaNames.TryLoad(env.SchemaDir, "PlaceName", pnRaw, log), "Name", 0);
        string PlaceOf(uint id) => id != 0 && pnRows.HasRow(id) ? Csv.Str(pnRows.GetRow(id).ReadColumn(pnNameCol)) : "";

        // 4. Emit: new-dir venues first, then existing, then no-venue reports.
        if (header) w.WriteLine(Header);
        var written = 0;
        var ordered = byVenue
            .Select(kv =>
            {
                var stageDir = kv.Key;
                var lvlDir = "bg/" + stageDir + "/level";
                var hasTt = ttByLevelDir.TryGetValue(lvlDir, out var tt);
                return (stageDir, lvlDir, hasTt, tt, Cutbs: kv.Value, NewDir: !canonicalDirs.Contains(lvlDir));
            })
            .OrderByDescending(v => v.NewDir).ThenBy(v => v.stageDir, StringComparer.Ordinal)
            .ToList();
        foreach (var v in ordered)
        {
            string bg, ttCell, note;
            if (v.hasTt)
            {
                bg = v.tt.Bg;
                ttCell = v.tt.Tt.ToString();
                note = v.NewDir ? "Tier B (TT-backed, plain LoadZone)"
                                : "existing territory - likely no stage entry needed";
            }
            else
            {
                var code = v.stageDir[(v.stageDir.LastIndexOf('/') + 1)..];
                bg = $"{v.stageDir}/level/{code}";
                ttCell = "";
                note = env.Game.FileExists($"bg/{v.stageDir}/level/bg.lgb")
                    ? "swap-stage candidate (TT-less, bg.lgb present)"
                    : "no TT and no bg.lgb - investigate";
            }
            var samples = string.Join(" ", v.Cutbs.Take(4).Select(c => c[(c.LastIndexOf('/') + 1)..]))
                        + (v.Cutbs.Count > 4 ? $" +{v.Cutbs.Count - 4}" : "");
            w.WriteLine($"{bg},{ttCell},{(v.NewDir ? "yes" : "")},{v.Cutbs.Count},{Q(samples)},{Q(PlaceOf(v.tt.Pn))},,{Q(note)}");
            written++;
        }
        foreach (var (cutb, why) in noVenue)
        {
            w.WriteLine($"(no venue),,,1,{Q(cutb[(cutb.LastIndexOf('/') + 1)..])},,,{Q(why)}");
            written++;
        }
        w.Flush();
        log?.Invoke($"# stages: {byVenue.Count} venue(s) ({ordered.Count(v => v.NewDir)} new-dir), {noVenue.Count} no-venue cutb(s), {seen.Count} cutb(s) scanned");
        return written;
    }

    // ------------------------------------------------------------------

    static (RawExcelSheet Raw, ExcelSheet<RawRow> Rows) OpenRaw(XivEnv env, string name)
    {
        RawExcelSheet raw;
        try { raw = env.Game.Excel.GetRawSheet(name, Language.English); }
        catch (Lumina.Excel.Exceptions.UnsupportedLanguageException) { raw = env.Game.Excel.GetRawSheet(name, Language.None); }
        return (raw, env.Game.Excel.GetSheet<RawRow>(raw.Language, name));
    }

    static int Col(SchemaNames? sn, string name, int fallbackRaw)
    {
        if (sn != null) { var k = Array.IndexOf(sn.Names, name); if (k >= 0) return sn.OffsetOrder[k]; }
        return fallbackRaw;
    }

    static string Q(string s) =>
        s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
