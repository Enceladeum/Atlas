using System.Security.Cryptography;
using Atlas.Core;
using Atlas.Core.Cutb;
using Atlas.Core.Exd;

namespace Atlas.Cli.Commands;

/// <summary>
/// Per-patch intake pipeline (2026-08 design; formalizes the manual 2026-08-03
/// workflow in dumps/library-rebuild-20260803.md into repeatable verbs):
///
///   atlas patch census  --out exd-census.csv [--skip N] [--take N] [--append]
///   atlas patch intake  --staging &lt;dir&gt; [--baseline &lt;dir&gt;] [--report &lt;file&gt;]
///   atlas patch promote --staging &lt;dir&gt; [--baseline &lt;dir&gt;] [--archive &lt;dir&gt;]
///   atlas patch stages  --staging &lt;dir&gt; [--baseline &lt;dir&gt;] [--rows a,b,c]
///
/// intake is RESUMABLE: it builds whatever the staging dir is still missing
/// (library rebuild targets one by one, then the exd census), then diffs
/// staging vs baseline (LibraryDiff + ExdCensus.Diff) and writes
/// PATCH-DIFF-&lt;oldver&gt;-to-&lt;newver&gt;.md. Under a hard call-timeout
/// environment, re-run intake until it reports complete; each run continues
/// where the last stopped. Promotion is a separate, explicit, approved step:
/// archive baseline -> copy staging over baseline -> stamp VERSION (md5-verified).
/// Game version read from ffxivgame.ver next to the sqpack dir; library
/// version from &lt;baseline&gt;/VERSION (stamped at promote; "unknown" before).
/// </summary>
public static class PatchCommands
{
    const string CensusFile = "exd-census.csv";
    const string VersionFile = "VERSION";
    const string CutscenePathsFile = "cutscene-paths.csv";

    /// <summary>Library files owned by the pipeline (promoted/archived). The
    /// non-rebuild library files (rosetta, censuses, manifests) stay untouched,
    /// matching the 2026-08-03 promotion precedent.</summary>
    static readonly string[] PromotedFiles =
        LibraryDiff.Files.Select(f => f.FileName).Append(CensusFile).Append("leveldirs.txt")
        .Append(CutscenePathsFile).ToArray();

    public static int Run(XivEnv env, List<string> argv)
    {
        string? GetOpt(string name)
        {
            var i = argv.IndexOf(name);
            if (i < 0 || i + 1 >= argv.Count) return null;
            var v = argv[i + 1];
            argv.RemoveRange(i, 2);
            return v;
        }
        bool GetFlag(string name) { var i = argv.IndexOf(name); if (i < 0) return false; argv.RemoveAt(i); return true; }
        Action<string> log = s => Console.Error.WriteLine(s);

        if (argv.Count == 0) { Usage(); return 1; }
        switch (argv[0])
        {
            case "census":
            {
                var outPath = GetOpt("--out") ?? CensusFile;
                var skip = int.TryParse(GetOpt("--skip"), out var s0) ? s0 : 0;
                var take = int.TryParse(GetOpt("--take"), out var t0) ? t0 : int.MaxValue;
                var append = GetFlag("--append");
                using var w = new StreamWriter(outPath, append);
                if (!append) w.WriteLine(ExdCensus.Header);
                var n = ExdCensus.Run(env, w, skip, take, log);
                log($"# census: {n} sheet(s) -> {outPath} (skip={skip})");
                return 0;
            }
            case "intake":  return RunIntake(env, GetOpt, log);
            case "promote": return RunPromote(GetOpt, GetFlag, log);
            case "stages":  return RunStages(env, GetOpt, log);
            default: Usage(); return 1;
        }
    }

    // ------------------------------------------------------------------
    // intake
    // ------------------------------------------------------------------

    static int RunIntake(XivEnv env, Func<string, string?> getOpt, Action<string> log)
    {
        var staging = getOpt("--staging");
        var baseline = getOpt("--baseline") ?? Path.Combine("dumps", "library");
        var censusChunk = int.TryParse(getOpt("--census-chunk"), out var cc) ? cc : int.MaxValue;
        if (staging == null) { Usage(); return 1; }
        if (!Directory.Exists(baseline)) { Console.Error.WriteLine($"error: baseline not found: {baseline}"); return 1; }
        Directory.CreateDirectory(staging);

        var newVer = ReadGameVersion(env) ?? "unknown";
        var oldVer = ReadStamp(baseline) ?? "unknown";
        log($"# intake: baseline={baseline} (v{oldVer})  staging={staging}  game=v{newVer}");
        if (newVer == oldVer && newVer != "unknown")
            log($"# WARNING: game version equals baseline VERSION ({newVer}) — diff should be clean");
        File.WriteAllText(Path.Combine(staging, VersionFile), newVer + Environment.NewLine);

        // 0. Level-dir list: canonical ∪ dirs derived live from TerritoryType.Bg.
        // Guards against the false-clean trap: a patch adds new zones whose level
        // dirs a static leveldirs.txt has never heard of, so the LGB rebuild
        // silently skips them and the diff reports IDENTICAL (hit on 2026.08.05:
        // x6ec/o6b2/w1en/w1eo were on disk but invisible). Baseline copy wins if
        // present (living canonical, refreshed at promote), else --dirs.
        var canonicalDirs = new[] { Path.Combine(baseline, "leveldirs.txt"), getOpt("--dirs") ?? "" }
            .FirstOrDefault(p => p.Length > 0 && File.Exists(p));
        if (canonicalDirs == null) { Console.Error.WriteLine("error: no leveldirs.txt (baseline copy or --dirs)"); return 1; }
        // Canonical line format: "<dir>\tTT<id>" — compare on the dir token only;
        // preserve canonical order/annotations, append new dirs (annotated) at end.
        var dirLines = File.ReadAllLines(canonicalDirs)
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var dirSet = dirLines
            .Select(l => l.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0])
            .ToHashSet(StringComparer.Ordinal);
        var newDirs = new List<string>();
        var tt = env.Game.Excel.GetSheet<Lumina.Excel.RawRow>(null, "TerritoryType");
        foreach (var row in tt)
            for (var c = 0; c < row.Columns.Count; c++)
                if (row.Columns[c].Type == Lumina.Data.Structs.Excel.ExcelColumnDataType.String)
                {
                    var s = Csv.Str(row.ReadColumn(c));
                    if (!s.Contains("/level/")) continue;
                    var lvlDir = "bg/" + s[..s.LastIndexOf('/')];
                    if (dirSet.Add(lvlDir)) { newDirs.Add(lvlDir); dirLines.Add($"{lvlDir}\tTT{row.RowId}"); }
                    break;
                }
        var stagedDirs = Path.Combine(staging, "leveldirs.txt");
        var dirsText = string.Join("\n", dirLines) + "\n";
        var dirsChanged = !File.Exists(stagedDirs) || File.ReadAllText(stagedDirs) != dirsText;
        if (dirsChanged)
        {
            File.WriteAllText(stagedDirs, dirsText);
            // Dir-derived targets staged with a stale dir list must be redone.
            foreach (var t in new[] { "layer-sets.csv", "layer-filters.csv", "layers.csv", "instances.csv" })
            {
                var p = Path.Combine(staging, t);
                if (File.Exists(p)) File.Delete(p);
            }
        }
        if (newDirs.Count > 0)
            log($"# NEW LEVEL DIRS ({newDirs.Count}, not in {canonicalDirs}): {string.Join(" ", newDirs)}");

        // 1. Library rebuild — only the targets whose output is missing (resumable).
        var missing = LibraryDiff.Files
            .Where(f => !File.Exists(Path.Combine(staging, f.FileName)))
            .Select(f => f.Target).ToList();
        if (missing.Count > 0)
        {
            log($"# rebuild: {missing.Count} missing target(s): {string.Join(",", missing)}");
            var rc = LibraryCommands.Run(env, ["rebuild", "--out", staging, "--only", string.Join(",", missing), "--dirs", stagedDirs]);
            if (rc != 0) return rc;
        }
        else log("# rebuild: staging complete, skipping");

        // 2. EXD census — resumable via marker file counting sheets done so far.
        var censusPath = Path.Combine(staging, CensusFile);
        var markPath = censusPath + ".progress";
        var total = ExdOps.SheetNames(env).Count();
        var done = File.Exists(markPath) && int.TryParse(File.ReadAllText(markPath), out var d) ? d : 0;
        if (!File.Exists(censusPath) || done < total)
        {
            using (var w = new StreamWriter(censusPath, append: done > 0))
            {
                if (done == 0) w.WriteLine(ExdCensus.Header);
                var take = Math.Min(censusChunk, total - done);
                log($"# census: sheets {done}..{done + take} of {total}");
                done += ExdCensus.Run(env, w, done, take, log);
            }
            File.WriteAllText(markPath, done.ToString());
            if (done < total)
            {
                log($"# census: {done}/{total} — re-run intake to continue");
                return 3; // partial: caller should re-run
            }
        }
        log($"# census: {done}/{total} sheets complete");

        // 2b. Cutscene path catalog (RowId,Path) — baseline for `patch stages`
        // row selection next patch. Cheap (one sheet), so always rewritten.
        var cutPaths = Path.Combine(staging, CutscenePathsFile);
        using (var cw = new StreamWriter(cutPaths, append: false))
        {
            cw.WriteLine("RowId,Path");
            foreach (var (r, p) in CutsceneCatalog.FromSheet(env)) cw.WriteLine($"{r},{p}");
        }

        // 3. Diffs + report.
        var report = getOpt("--report")
            ?? Path.Combine(staging, $"PATCH-DIFF-{oldVer}-to-{newVer}.md");
        using var rw = new StreamWriter(report, append: false);
        rw.WriteLine($"# Patch diff: {oldVer} -> {newVer}");
        rw.WriteLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm} by `atlas patch intake`.");
        rw.WriteLine($"Baseline: `{baseline}` · Staging: `{staging}`");
        rw.WriteLine();

        rw.WriteLine("## New level dirs (from TerritoryType.Bg vs canonical leveldirs.txt)");
        rw.WriteLine(newDirs.Count == 0 ? "None."
            : string.Join("\n", newDirs.Select(d => $"- `{d}`")));
        rw.WriteLine();

        rw.WriteLine("## Library (LGB/LVB layers, instances, cutscene, SGB)");
        rw.WriteLine("```");
        var libResults = LibraryDiff.Run(baseline, staging, rw, null, maxExamples: 25, log);
        rw.WriteLine("```");
        var dirty = libResults.Count(r => !r.Identical);
        rw.WriteLine(dirty == 0 ? "**CLEAN** — no library drift." : $"**DRIFT** in {dirty} file(s).");
        rw.WriteLine();

        rw.WriteLine("## EXD sheets (census)");
        var basCensus = Path.Combine(baseline, CensusFile);
        if (File.Exists(basCensus))
        {
            rw.WriteLine("```");
            var cd = ExdCensus.Diff(basCensus, censusPath, rw, log);
            rw.WriteLine("```");
            rw.WriteLine(cd.Identical ? "**CLEAN** — no sheet drift."
                : $"**DRIFT**: +{cd.Added}/-{cd.Removed} sheets, {cd.Schema} schema, {cd.Rows} row-count, {cd.Data} content-only.");
        }
        else
        {
            rw.WriteLine($"No baseline census at `{basCensus}` — **this census establishes the baseline** " +
                          "(sqpack patches in place; pre-patch sheet data is unrecoverable). " +
                          "Full sheet diff starts with the next patch.");
        }
        rw.WriteLine();

        rw.WriteLine("## Fragility watch (runtime side — manual)");
        rw.WriteLine("- [ ] Re-resolve sigs (outbound send, CreateScene, EmoteController access)");
        rw.WriteLine("- [ ] Re-verify struct offsets used by runtime consumers (LayoutManager, MoveController, EmoteController)");
        rw.WriteLine("- [ ] Opcode re-map if packet interception in use");
        rw.WriteLine("- [ ] Refresh EXDSchema (ATLAS_SCHEMA) once upstream catches up");
        rw.WriteLine("- [ ] Cross-check this report against official patch notes");
        rw.Flush();

        log($"# report -> {report}");
        log($"# next: review, then `atlas patch promote --staging {staging} --baseline {baseline}`");
        return dirty == 0 ? 0 : 2;
    }

    // ------------------------------------------------------------------
    // promote
    // ------------------------------------------------------------------

    static int RunPromote(Func<string, string?> getOpt, Func<string, bool> getFlag, Action<string> log)
    {
        var staging = getOpt("--staging");
        var baseline = getOpt("--baseline") ?? Path.Combine("dumps", "library");
        var archive = getOpt("--archive");
        var newVer = getOpt("--version"); // optional override
        if (staging == null) { Usage(); return 1; }
        if (!Directory.Exists(staging)) { Console.Error.WriteLine($"error: staging not found: {staging}"); return 1; }
        if (!Directory.Exists(baseline)) { Console.Error.WriteLine($"error: baseline not found: {baseline}"); return 1; }

        var oldVer = ReadStamp(baseline) ?? "unknown";
        newVer ??= ReadStamp(staging) ?? "unknown";

        // 1. Archive current baseline files (only the ones being replaced).
        // FIRST-WRITE-WINS: if an archive file already exists it is NOT overwritten.
        // Rationale: promote can be killed mid-copy (hard call timeouts); on re-run
        // the baseline may already be partially overwritten by staging, and
        // re-archiving it would contaminate the good pre-patch archive.
        archive ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(baseline))!, "archive", $"library-{oldVer}");
        Directory.CreateDirectory(archive);
        var archived = 0;
        foreach (var f in PromotedFiles.Append(VersionFile))
        {
            var src = Path.Combine(baseline, f);
            if (!File.Exists(src)) continue;
            var dst = Path.Combine(archive, f);
            if (File.Exists(dst)) continue; // first write wins
            File.Copy(src, dst, overwrite: false);
            if (!Md5Equal(src, dst)) { Console.Error.WriteLine($"error: archive copy mismatch: {f}"); return 1; }
            archived++;
        }
        log($"# archived baseline (v{oldVer}) -> {archive} ({archived} new file(s))");

        // 2. Copy staging files over baseline, md5-verified. Resumable: files
        // already md5-identical (from an earlier interrupted run) are skipped.
        var promoted = 0;
        foreach (var f in PromotedFiles)
        {
            var src = Path.Combine(staging, f);
            if (!File.Exists(src)) { log($"# {f}: not in staging, baseline copy kept"); continue; }
            var dst = Path.Combine(baseline, f);
            if (File.Exists(dst) && Md5Equal(src, dst)) { promoted++; continue; }
            File.Copy(src, dst, overwrite: true);
            if (!Md5Equal(src, dst)) { Console.Error.WriteLine($"error: promote copy mismatch: {f}"); return 1; }
            promoted++;
        }

        // 3. Stamp.
        File.WriteAllText(Path.Combine(baseline, VersionFile), newVer + Environment.NewLine);
        log($"# promoted {promoted} file(s); {baseline} is now v{newVer} (was v{oldVer})");
        return 0;
    }

    // ------------------------------------------------------------------
    // stages
    // ------------------------------------------------------------------

    /// <summary>Cutscene-venue auto-detection for runtime visits: derive stage
    /// candidates from new/changed Cutscene rows (see StageCandidateOps).
    /// Runs between intake and promote — the baseline leveldirs.txt/
    /// cutscene-paths.csv must still be pre-patch for NewDir/row selection
    /// to be meaningful.</summary>
    static int RunStages(XivEnv env, Func<string, string?> getOpt, Action<string> log)
    {
        var staging = getOpt("--staging");
        var baseline = getOpt("--baseline") ?? Path.Combine("dumps", "library");
        var rowsOpt = getOpt("--rows");
        if (staging == null) { Usage(); return 1; }
        if (!Directory.Exists(baseline)) { Console.Error.WriteLine($"error: baseline not found: {baseline}"); return 1; }
        Directory.CreateDirectory(staging);

        // Row selection: explicit --rows, else live Cutscene sheet vs baseline catalog.
        List<int> rows;
        if (rowsOpt != null)
        {
            rows = rowsOpt.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(int.Parse).ToList();
        }
        else
        {
            var basPath = Path.Combine(baseline, CutscenePathsFile);
            if (!File.Exists(basPath))
            {
                Console.Error.WriteLine($"error: no {basPath} — pass --rows explicitly " +
                    "(intake stages the catalog from this version on, so future patches auto-select)");
                return 1;
            }
            var bas = new Dictionary<int, string>();
            foreach (var (r, p) in CutsceneCatalog.FromCsv(basPath)) bas[r] = p;
            rows = CutsceneCatalog.FromSheet(env)
                .Where(c => c.Path.Contains('/') && (!bas.TryGetValue(c.Row, out var bp) || bp != c.Path))
                .Select(c => c.Row).ToList();
            log($"# stages: {rows.Count} new/changed cutscene row(s) vs {basPath}");
            if (rows.Count == 0) { log("# stages: nothing to do"); return 0; }
        }

        // Canonical level dirs (NewDir flag).
        var dirsPath = Path.Combine(baseline, "leveldirs.txt");
        var canonical = File.Exists(dirsPath)
            ? File.ReadAllLines(dirsPath).Select(l => l.Trim()).Where(l => l.Length > 0)
                .Select(l => l.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0])
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        if (canonical.Count == 0) log($"# WARNING: no {dirsPath} — every venue will be flagged NewDir");

        var outPath = getOpt("--out") ?? Path.Combine(staging, "stage-candidates.csv");
        using var w = new StreamWriter(outPath, append: false);
        var n = StageCandidateOps.Write(env, rows, canonical, w, header: true, log);
        log($"# {n} candidate row(s) -> {outPath}");
        log("# next: review/name candidates, hand to the runtime consumer (Experimental stubs or extra-stages.json)");
        return 0;
    }

    // ------------------------------------------------------------------

    /// <summary>ffxivgame.ver lives beside the sqpack dir (game/ffxivgame.ver).</summary>
    static string? ReadGameVersion(XivEnv env)
    {
        var gameDir = env.Game.DataPath.Parent?.FullName;
        if (gameDir == null) return null;
        var ver = Path.Combine(gameDir, "ffxivgame.ver");
        return File.Exists(ver) ? File.ReadAllText(ver).Trim() : null;
    }

    static string? ReadStamp(string dir)
    {
        var p = Path.Combine(dir, VersionFile);
        return File.Exists(p) ? File.ReadAllText(p).Trim() : null;
    }

    static bool Md5Equal(string a, string b)
    {
        using var md5 = MD5.Create();
        using var fa = File.OpenRead(a);
        var ha = md5.ComputeHash(fa);
        using var fb = File.OpenRead(b);
        var hb = md5.ComputeHash(fb);
        return ha.AsSpan().SequenceEqual(hb);
    }

    static void Usage()
    {
        Console.Error.WriteLine("usage: atlas patch census  --out <file> [--skip N] [--take N] [--append]");
        Console.Error.WriteLine("       atlas patch intake  --staging <dir> [--baseline <dir>] [--report <file>] [--census-chunk N] [--dirs <leveldirs.txt>]");
        Console.Error.WriteLine("       atlas patch promote --staging <dir> [--baseline <dir>] [--archive <dir>] [--version V]");
        Console.Error.WriteLine("       atlas patch stages  --staging <dir> [--baseline <dir>] [--rows a,b,c] [--out <file>]");
        Console.Error.WriteLine("  intake exit codes: 0 clean, 2 drift (report written), 3 partial (re-run to continue), 1 error");
        Console.Error.WriteLine("  stages: run between intake and promote (needs pre-patch baseline for NewDir/row-select)");
    }
}
