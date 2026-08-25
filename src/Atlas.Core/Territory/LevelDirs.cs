using System.Diagnostics;
using System.Text.RegularExpressions;
using Lumina;
using Lumina.Data;
using Lumina.Excel;
using Atlas.Core.Exd;

namespace Atlas.Core.Territory;

/// <summary>
/// Live level-dir discovery — replaces the static leveldirs.txt for the usage
/// index so it auto-tracks patches:
///   - TT dirs: every TerritoryType row's bg level dir (schema-free: first
///     string column containing "/level/"), label TT&lt;lowest RowId&gt;.
///   - Cutscene stages: every cutb's dominant bg/&lt;stagedir&gt;/ prefix
///     (same chain as StageCandidateOps / the rosetta build) — venues with no
///     TT row but a real bg.lgb are "TTs organised differently"; label
///     STAGE:&lt;code&gt;. TT wins on collision.
/// Also home of the game-version stamp (ffxivgame.ver beside sqpack) used to
/// flag a built index as stale after a patch.
/// </summary>
public static class LevelDirs
{
    static readonly Regex RxStageDir = new(@"^bg/(?<dir>.+?)/(?:bgparts|collision)/", RegexOptions.Compiled);

    /// <summary>Discover all walkable level dirs from the live game data.
    /// Returns (dir, label) pairs: TT dirs ordered by TT id, then TT-less
    /// cutscene stage dirs (bg.lgb-backed) ordered by dir.</summary>
    public static List<(string Dir, string Label)> Discover(XivEnv env, Action<string>? log = null)
    {
        var sw = Stopwatch.StartNew();

        // 1. TerritoryType -> level dirs (same detection as patch intake step 0
        //    / StageCandidateOps step 3; TryAdd = lowest RowId wins).
        RawExcelSheet ttRaw;
        try { ttRaw = env.Game.Excel.GetRawSheet("TerritoryType", Language.English); }
        catch (Lumina.Excel.Exceptions.UnsupportedLanguageException) { ttRaw = env.Game.Excel.GetRawSheet("TerritoryType", Language.None); }
        var ttRows = env.Game.Excel.GetSheet<RawRow>(ttRaw.Language, "TerritoryType");
        var ttByDir = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var row in ttRows)
            for (var c = 0; c < ttRaw.Columns.Count; c++)
                if (ttRaw.Columns[c].Type == Lumina.Data.Structs.Excel.ExcelColumnDataType.String)
                {
                    var s = Csv.Str(row.ReadColumn(c));
                    if (!s.Contains("/level/")) continue;
                    ttByDir.TryAdd("bg/" + s[..s.LastIndexOf('/')], (uint)row.RowId);
                    break;
                }

        // 2. Cutscene sheet -> cutb scan -> ALL referenced bg dirs (union over
        //    every cutb; a cutb can visit several stages, so no dominant-only
        //    selection). Two string classes carry venues: direct level-dir refs
        //    (lgb/lvb — how stage-jump cutscenes name their venue) and
        //    bgparts/collision paths ("prop" carries no venue). TT-covered
        //    dirs fall out in step 3.
        var stageDirs = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int scanned = 0, missing = 0;
        foreach (var (_, p) in Cutb.CutsceneCatalog.FromSheet(env))
        {
            if (!p.Contains('/') || !seen.Add(p)) continue;
            var lvl = Cutb.ScenePartsOps.ScanLevelDirs(env.Game, p);
            scanned++;
            if (lvl == null) { missing++; continue; }
            stageDirs.UnionWith(lvl);
            var hits = Cutb.ScenePartsOps.Scan(env.Game, p);
            if (hits == null) continue;
            foreach (var (kind, path) in hits)
            {
                if (kind == "prop") continue;
                var m = RxStageDir.Match(path);
                if (m.Success) stageDirs.Add(m.Groups["dir"].Value);
            }
        }

        // 3. Merge: TT dirs by id, then TT-less stages that really have a bg.lgb.
        var outList = ttByDir.OrderBy(kv => kv.Value)
            .Select(kv => (kv.Key, $"TT{kv.Value}")).ToList();
        var stageAdded = 0;
        foreach (var sd in stageDirs.OrderBy(s => s, StringComparer.Ordinal))
        {
            var lvl = "bg/" + sd + "/level";
            if (ttByDir.ContainsKey(lvl)) continue;
            if (!env.Game.FileExists(lvl + "/bg.lgb")) continue;
            outList.Add((lvl, "STAGE:" + sd[(sd.LastIndexOf('/') + 1)..]));
            stageAdded++;
        }
        log?.Invoke($"# discovered {outList.Count} level dirs ({ttByDir.Count} TT, {stageAdded} stage) " +
                    $"from {scanned} cutb scans ({missing} missing) in {sw.Elapsed.TotalSeconds:0.0}s");
        return outList;
    }

    /// <summary>Union discovery with a static "dir\tlabel" list (the checked-in
    /// leveldirs.txt): discovered entries win; static-only dirs are appended so
    /// curated extras (e.g. cutscene-orphaned stages) never regress. Static
    /// stage labels are normalized to per-dir STAGE:&lt;code&gt; form.</summary>
    public static List<(string Dir, string Label)> Merge(
        List<(string Dir, string Label)> discovered, IEnumerable<string> staticLines)
    {
        var have = discovered.Select(d => d.Dir).ToHashSet(StringComparer.Ordinal);
        var merged = new List<(string, string)>(discovered);
        foreach (var line in staticLines)
        {
            var i = line.IndexOf('\t');
            var dir = (i < 0 ? line : line[..i]).TrimEnd('/');
            if (dir.Length == 0 || !have.Add(dir)) continue;
            var lbl = i < 0 ? dir : line[(i + 1)..];
            if (!lbl.StartsWith("TT", StringComparison.Ordinal))
            {
                var sd = dir.EndsWith("/level", StringComparison.Ordinal) ? dir[..^6] : dir;
                lbl = "STAGE:" + sd[(sd.LastIndexOf('/') + 1)..];
            }
            merged.Add((dir, lbl));
        }
        return merged;
    }

    /// <summary>As "dir\tlabel" lines (the UsageOps.BuildIndex input shape).</summary>
    public static IEnumerable<string> DiscoverLines(XivEnv env, Action<string>? log = null)
        => Discover(env, log).Select(d => d.Dir + "\t" + d.Label);

    /// <summary>Game version from ffxivgame.ver beside the sqpack dir, or null.</summary>
    public static string? GameVersion(GameData gd)
    {
        try { return GameVersion(gd.DataPath.FullName); } catch { return null; }
    }

    /// <summary>Same, from the sqpack path (no GameData needed).</summary>
    public static string? GameVersion(string sqpackDir)
    {
        try
        {
            var parent = Directory.GetParent(sqpackDir)?.FullName;
            if (parent == null) return null;
            var f = Path.Combine(parent, "ffxivgame.ver");
            return File.Exists(f) ? File.ReadAllText(f).Trim() : null;
        }
        catch { return null; }
    }
}
