using Lumina.Excel;
using Atlas.Core;
using Atlas.Core.Exd;

namespace Atlas.Cli.Commands;

/// <summary>
/// env atlas — weather×territory atlas from the ENVB banks.
/// For each TerritoryType with a Bg: derive the .lvb, find the referenced .envb,
/// parse the ENVS set table (per-weather env sets), and cross with the WeatherRate pool.
/// CSV: TT,Bg,EnvbPath,Sets,EnvbWeathers,PoolWeathers,ExtraInEnvb,MissingFromEnvb,ExtraNames
/// </summary>
public static class EnvCommands
{
    public static int Run(XivEnv env, List<string> args)
    {
        if (args.Count == 0 || (args[0] != "atlas" && args[0] != "vfx"))
        { Console.Error.WriteLine("usage: atlas env atlas|vfx [--out <file>]"); return 1; }
        if (args[0] == "vfx") return RunVfx(env, args);
        string? outFile = null;
        for (var i = 1; i < args.Count - 1; i++) if (args[i] == "--out") outFile = args[i + 1];
        outFile ??= "weather-atlas.csv";

        // Weather names
        var wNames = new Dictionary<uint, string>();
        var wRaw = env.Game.Excel.GetRawSheet("Weather");
        int wStr = -1;
        for (var i = 0; i < wRaw.Columns.Count; i++)
            if (wRaw.Columns[i].Type == Lumina.Data.Structs.Excel.ExcelColumnDataType.String) { wStr = i; break; }
        foreach (var r in env.Game.Excel.GetSheet<RawRow>(null, "Weather"))
            wNames[r.RowId] = Csv.Str(r.ReadColumn(wStr));

        // WeatherRate pools: 8 (weather, rate) pairs in column order
        var pools = new Dictionary<uint, List<int>>();
        foreach (var r in env.Game.Excel.GetSheet<RawRow>(null, "WeatherRate"))
        {
            var ids = new List<int>();
            for (var c = 0; c + 1 < 16; c += 2)
            {
                var w = Convert.ToInt32(r.ReadColumn(c));
                var rate = Convert.ToInt32(r.ReadColumn(c + 1));
                if (w > 0 && rate > 0 && !ids.Contains(w)) ids.Add(w);
            }
            pools[r.RowId] = ids;
        }

        // TerritoryType: Bg (first string col with "/level/") + WeatherRate col via schema
        var ttRaw = env.Game.Excel.GetRawSheet("TerritoryType");
        var sn = SchemaNames.TryLoad(env.SchemaDir, "TerritoryType", ttRaw, _ => { });
        int wrCol = -1;
        if (sn != null)
        {
            var k = Array.IndexOf(sn.Names, "WeatherRate");
            if (k >= 0) wrCol = sn.OffsetOrder[k];
        }
        var envbCache = new Dictionary<string, (int Sets, List<int> Ws)?>();
        var lvbCache = new Dictionary<string, string?>();
        using var w2 = new StreamWriter(outFile);
        w2.WriteLine("TT,Bg,EnvbPath,Sets,EnvbWeathers,PoolWeathers,ExtraInEnvb,MissingFromEnvb,ExtraNames");
        int nTT = 0, nEnvb = 0, nExtra = 0;
        foreach (var row in env.Game.Excel.GetSheet<RawRow>(null, "TerritoryType"))
        {
            string? bg = null;
            for (var c = 0; c < ttRaw.Columns.Count; c++)
            {
                if (ttRaw.Columns[c].Type != Lumina.Data.Structs.Excel.ExcelColumnDataType.String) continue;
                var s = Csv.Str(row.ReadColumn(c));
                if (s.Contains("/level/")) { bg = s; break; }
            }
            if (string.IsNullOrEmpty(bg)) continue;
            nTT++;
            var dir = bg[..bg.LastIndexOf('/')];         // ffxiv/sea_s1/twn/s1t1/level
            var code = bg[(bg.LastIndexOf('/') + 1)..];
            var lvbPath = $"bg/{dir}/{code}.lvb";
            if (!lvbCache.TryGetValue(lvbPath, out var envbPath))
            {
                envbPath = FindEnvb(env, lvbPath);
                lvbCache[lvbPath] = envbPath;
            }
            var pool = new List<int>();
            if (wrCol >= 0 && pools.TryGetValue(Convert.ToUInt32(row.ReadColumn(wrCol)), out var p)) pool = p;
            if (envbPath == null)
            {
                w2.WriteLine($"{row.RowId},{bg},(no envb),,,{J(pool)},,,");
                continue;
            }
            if (!envbCache.TryGetValue(envbPath, out var parsed))
            {
                parsed = ParseEnvb(env, envbPath);
                envbCache[envbPath] = parsed;
            }
            if (parsed == null) { w2.WriteLine($"{row.RowId},{bg},{envbPath},PARSE-FAIL,,{J(pool)},,,"); continue; }
            nEnvb++;
            var ws = parsed.Value.Ws;
            var extra = ws.Where(x => !pool.Contains(x)).ToList();
            var missing = pool.Where(x => !ws.Contains(x)).ToList();
            if (extra.Count > 0) nExtra++;
            var exNames = string.Join(";", extra.Select(x => wNames.TryGetValue((uint)x, out var n) ? n : $"w{x}"));
            w2.WriteLine($"{row.RowId},{bg},{envbPath},{parsed.Value.Sets},{J(ws)},{J(pool)},{J(extra)},{J(missing)},{Q(exNames)}");
        }
        Console.WriteLine($"atlas: {nTT} TTs with Bg, {nEnvb} envb-resolved, {nExtra} with extra sets -> {outFile}");
        return 0;
    }

    /// <summary>env vfx — per-set embedded .avfx inventory across every envb referenced by TerritoryType.</summary>
    static int RunVfx(XivEnv env, List<string> args)
    {
        string? outFile = null;
        for (var i = 1; i < args.Count - 1; i++) if (args[i] == "--out") outFile = args[i + 1];
        outFile ??= "weather-vfx.csv";
        var ttRaw = env.Game.Excel.GetRawSheet("TerritoryType");
        var envbs = new SortedSet<string>();
        var lvbCache = new Dictionary<string, string?>();
        foreach (var row in env.Game.Excel.GetSheet<RawRow>(null, "TerritoryType"))
        {
            string? bg = null;
            for (var c = 0; c < ttRaw.Columns.Count; c++)
            {
                if (ttRaw.Columns[c].Type != Lumina.Data.Structs.Excel.ExcelColumnDataType.String) continue;
                var s = Csv.Str(row.ReadColumn(c));
                if (s.Contains("/level/")) { bg = s; break; }
            }
            if (string.IsNullOrEmpty(bg)) continue;
            var dir = bg[..bg.LastIndexOf('/')];
            var code = bg[(bg.LastIndexOf('/') + 1)..];
            var lvbPath = $"bg/{dir}/{code}.lvb";
            if (!lvbCache.TryGetValue(lvbPath, out var e)) { e = FindEnvb(env, lvbPath); lvbCache[lvbPath] = e; }
            if (e != null) envbs.Add(e);
        }
        using var w = new StreamWriter(outFile);
        w.WriteLine("Envb,WeatherId,Avfx");
        int nFiles = 0, nHits = 0;
        foreach (var path in envbs)
        {
            var f = env.Game.GetFile(path);
            if (f == null) continue;
            var d = f.Data;
            if (d.Length < 0x30 || d[0] != 'E' || d[3] != 'B') continue;
            var nSets = BitConverter.ToInt32(d, 0x1C);
            var rowSize = BitConverter.ToInt32(d, 0x18);
            if (nSets <= 0 || nSets > 64 || rowSize < 16) continue;
            nFiles++;
            var rows = new List<(int Start, int W)>();
            for (var i = 0; i < nSets; i++)
            {
                var off = 0x20 + i * rowSize;
                rows.Add((BitConverter.ToInt32(d, off + 4), BitConverter.ToInt32(d, off + 12)));
            }
            rows.Sort();
            for (var i = 0; i < rows.Count; i++)
            {
                var lo = rows[i].Start;
                var hi = i + 1 < rows.Count ? rows[i + 1].Start : d.Length;
                foreach (var s in AsciiStrings(d, lo, hi))
                    if (s.EndsWith(".avfx")) { w.WriteLine($"{path},{rows[i].W},{s}"); nHits++; }
            }
        }
        Console.WriteLine($"vfx: {nFiles} envbs scanned, {nHits} avfx refs -> {outFile}");
        return 0;
    }

    static IEnumerable<string> AsciiStrings(byte[] d, int lo, int hi)
    {
        hi = Math.Min(hi, d.Length);
        var start = -1;
        for (var i = lo; i <= hi; i++)
        {
            var ok = i < hi && d[i] >= 0x20 && d[i] < 0x7f;
            if (ok) { if (start < 0) start = i; continue; }
            if (start >= 0 && i - start >= 8)
                yield return System.Text.Encoding.ASCII.GetString(d, start, i - start);
            start = -1;
        }
    }

    static string J(List<int> xs) => string.Join(";", xs);
    static string Q(string s) => s.Contains(',') ? $"\"{s}\"" : s;

    static string? FindEnvb(XivEnv env, string lvbPath)
    {
        var f = env.Game.GetFile(lvbPath);
        if (f == null) return null;
        var d = f.Data; var start = -1;
        for (var i = 0; i <= d.Length; i++)
        {
            var ok = i < d.Length && d[i] >= 0x20 && d[i] < 0x7f;
            if (ok) { if (start < 0) start = i; continue; }
            if (start >= 0 && i - start >= 10)
            {
                var s = System.Text.Encoding.ASCII.GetString(d, start, i - start);
                if (s.EndsWith(".envb")) return s;
            }
            start = -1;
        }
        return null;
    }

    /// <summary>
    /// ENVB → ENVS set table: rows of (end,start,nBlocks,weatherId).
    /// NOTE: true set count is at 0x1C; the u32 at 0x14 is a constant 6 (slot
    /// capacity) — reading it as the count over-reads single-set indoor banks.
    /// </summary>
    static (int Sets, List<int> Ws)? ParseEnvb(XivEnv env, string path)
    {
        var f = env.Game.GetFile(path);
        if (f == null) return null;
        var d = f.Data;
        if (d.Length < 0x30 || d[0] != 'E' || d[1] != 'N' || d[2] != 'V' || d[3] != 'B') return null;
        if (!(d[0xC] == 'E' && d[0xD] == 'N' && d[0xE] == 'V' && d[0xF] == 'S')) return null;
        var nSets = BitConverter.ToInt32(d, 0x1C);
        var rowSize = BitConverter.ToInt32(d, 0x18);
        if (nSets <= 0 || nSets > 64 || rowSize < 16) return null;
        var ws = new List<int>();
        for (var i = 0; i < nSets; i++)
        {
            var off = 0x20 + i * rowSize;
            if (off + rowSize > d.Length) break;
            ws.Add(BitConverter.ToInt32(d, off + 12));
        }
        return (nSets, ws);
    }
}
