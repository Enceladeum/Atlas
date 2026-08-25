// Atlas.Server — HTTP API over Atlas.Core + static host for web/.
// Second headless surface: everything the GUI can do is a curl-able route.
// Contract: Server calls Core only; it never parses game files itself and never
// shells out to the CLI (CONTRACT.md, Server/API boundary).
//
// Args/env: --game|ATLAS_GAME  --schema|ATLAS_SCHEMA  --library|ATLAS_LIBRARY
//           --web|ATLAS_WEB  --work|ATLAS_WORK  --port|ATLAS_PORT (default 8780)
// XIVTOOL_GAME / XIVTOOL_SCHEMA accepted as fallback (same as the CLI).

using System.Collections.Concurrent;
using System.Text;
using Atlas.Core;
using Atlas.Core.Avfx;
using Atlas.Core.Compose;
using Atlas.Core.Exd;
using Atlas.Core.Gltf;
using Atlas.Core.Mdl;
using Atlas.Core.Mtrl;
using Atlas.Core.Pcb;
using Atlas.Core.Scd;
using Atlas.Core.Territory;
using Atlas.Core.Tex;

string? GetOpt(string name)
{
    for (var i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
    return null;
}
string? Env(params string[] names)
{
    foreach (var n in names) { var v = Environment.GetEnvironmentVariable(n); if (!string.IsNullOrEmpty(v)) return v; }
    return null;
}

var gamePath = GetOpt("--game") ?? Env("ATLAS_GAME", "XIVTOOL_GAME");
var schemaDir = GetOpt("--schema") ?? Env("ATLAS_SCHEMA", "XIVTOOL_SCHEMA");
var libraryDir = GetOpt("--library") ?? Env("ATLAS_LIBRARY");
var webDir = GetOpt("--web") ?? Env("ATLAS_WEB");
var workDir = GetOpt("--work") ?? Env("ATLAS_WORK") ?? Path.Combine(Path.GetTempPath(), "atlas-work");
var pathsFile = GetOpt("--paths") ?? Env("ATLAS_PATHS"); // ResLogger2-style path list (.txt or .gz), one game path per line
var port = int.TryParse(GetOpt("--port") ?? Env("ATLAS_PORT"), out var p) ? p : 8780;

if (gamePath == null)
{
    Console.Error.WriteLine("error: no game path (--game or ATLAS_GAME)");
    return 1;
}
Directory.CreateDirectory(workDir);

// web/ resolution: flag/env, else web/ beside the exe (dist layout), else probe
// cwd and upward for a web/ dir next to Atlas.slnx (repo layout).
if (webDir == null)
{
    var beside = Path.Combine(AppContext.BaseDirectory, "web");
    if (Directory.Exists(beside)) webDir = beside;
}
if (webDir == null)
{
    var probe = Directory.GetCurrentDirectory();
    for (var i = 0; i < 5 && probe != null; i++, probe = Path.GetDirectoryName(probe))
    {
        var cand = Path.Combine(probe, "web");
        if (Directory.Exists(cand)) { webDir = cand; break; }
    }
}

var env = new XivEnv(gamePath, schemaDir);
// Single-user desktop server: serialize game access (Lumina thread-safety unproven).
var gate = new SemaphoreSlim(1, 1);
async Task<T> WithEnv<T>(Func<XivEnv, T> f)
{
    await gate.WaitAsync();
    try { return f(env); }
    finally { gate.Release(); }
}

var builder = WebApplication.CreateSlimBuilder();
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.IncludeFields = true; // Vector3 etc. expose public fields
});
var app = builder.Build();

// ---- meta ----
app.MapGet("/api/meta", () => Results.Json(new
{
    game = gamePath,
    schema = schemaDir,
    library = libraryDir,
    work = workDir,
    web = webDir,
    paths = pathsFile,
    generator = "Atlas.Server"
}));

// ---- sheets ----
app.MapGet("/api/sheets", async (string? filter) =>
    Results.Json(await WithEnv(e => ExdOps.SheetNames(e, filter).ToArray())));

app.MapGet("/api/sheet/{name}/header", async (string name) =>
{
    var sw = new StringWriter();
    await WithEnv<object?>(e => { ExdOps.WriteHeader(e, name, sw); return null; });
    return Results.Text(sw.ToString(), "text/plain; charset=utf-8");
});

app.MapGet("/api/sheet/{name}.csv", async (string name, string? lang, int? max) =>
{
    var sw = new StringWriter();
    await WithEnv<object?>(e =>
    {
        ExdOps.Dump(e, name, ExdOps.ParseLang(lang ?? "en"), sw, max: max ?? int.MaxValue);
        return null;
    });
    return Results.Text(sw.ToString(), "text/csv; charset=utf-8");
});

// ---- raw sqpack ----
app.MapGet("/api/exists", async (string path) =>
    Results.Json(new { path, exists = await WithEnv(e => e.Game.FileExists(path)) }));

app.MapGet("/api/extract", async (string path) =>
{
    var data = await WithEnv(e => e.Game.GetFile(path)?.Data);
    return data == null
        ? Results.NotFound(new { error = "not found", path })
        : Results.Bytes(data, "application/octet-stream",
            fileDownloadName: Path.GetFileName(path.Replace('/', Path.DirectorySeparatorChar)));
});

// ---- asset previews ----
app.MapGet("/api/mdl/info", async (string path) =>
    Results.Json(await WithEnv(e => MdlOps.Info(e, path))));

app.MapGet("/api/mdl/obj", async (string path, int? lod) =>
{
    var sw = new StringWriter();
    await WithEnv<object?>(e => { MdlOps.ExportObj(e, path, sw, lod ?? 0); return null; });
    return Results.Text(sw.ToString(), "text/plain; charset=utf-8");
});

app.MapGet("/api/mdl/gltf", async (string path, int? lod, int? textured, int? texsize) =>
{
    // self-contained glTF (buffer + diffuse PNGs as data: URIs) - the asset
    // browser's Textured 3D preview; single response, nothing cached on disk.
    var r = await WithEnv(e => MdlGltf.BuildEmbedded(e, path, new MdlGltfOptions
    { Lod = lod ?? 0, Textured = textured == 1, MaxTexDim = texsize ?? 1024 }));
    return Results.Text(r.Json, "model/gltf+json");
});

// ---- scd (sound) previews ----
app.MapGet("/api/scd/info", async (string path) =>
    Results.Json(await WithEnv(e => ScdOps.Info(e, path))));

app.MapGet("/api/scd/audio", async (string path, int? entry) =>
{
    // decoded audio for the browser: Ogg Vorbis passes through as-is (WebView2
    // is Chromium, plays it natively), MS-ADPCM is decoded server-side to PCM16
    // WAV so no client codec support is needed.
    try
    {
        var (data, ext, mime) = await WithEnv(e => ScdOps.Export(e, path, entry ?? 0));
        var stem = Path.GetFileNameWithoutExtension(path.Replace('/', Path.DirectorySeparatorChar));
        return Results.Bytes(data, mime, fileDownloadName: $"{stem}.{ext}");
    }
    catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException)
    {
        // Empty entries and out-of-range indexes are client errors, not faults.
        return Results.BadRequest(new { error = ex.Message, path, entry = entry ?? 0 });
    }
});

// ---- avfx (particle effect) previews ----
app.MapGet("/api/avfx/info", async (string path) =>
    Results.Json(await WithEnv(e => AvfxOps.Info(e, path))));

app.MapGet("/api/avfx/gltf", async (string path) =>
{
    // embedded particle models as a self-contained glTF (data: URI buffer);
    // 404s cleanly when the avfx has no drawable geometry.
    var (json, count) = await WithEnv(e =>
    {
        var gltf = new GltfWriter();
        var models = AvfxOps.BuildModelsGltf(e, path, gltf);
        var drawable = models.Count(m => m.VertexCount > 0 && m.TriCount > 0);
        return drawable == 0 ? ((string?)null, 0) : (gltf.WriteEmbedded(Path.GetFileName(path)), drawable);
    });
    return json == null
        ? Results.NotFound(new { error = "no embedded models in this avfx", path })
        : Results.Text(json, "model/gltf+json");
});

// ---- asset-usage inventory ----
// tt-scoped: live walk (cached per level dir). Global: usage-index.csv in the
// work dir, built on demand (POST /api/usages/build; the dumps/library sweep
// excludes BgParts, so visual-asset usage needs this dedicated index).
var usageWalkCache = new ConcurrentDictionary<string, List<UsageRow>>();
List<UsageRow>? usageIndexRows = null;
var usageIndexLock = new object();
int usageBuilding = 0, usageBuildDirs = 0, usageBuildRows = 0;
string? usageBuildError = null;
string UsageIndexPath() => Path.Combine(workDir, "usage-index.csv");
string? LeveldirsFile()
{
    var beside = Path.Combine(AppContext.BaseDirectory, "leveldirs.txt");
    if (File.Exists(beside)) return beside;
    return File.Exists("leveldirs.txt") ? Path.GetFullPath("leveldirs.txt") : null;
}
// version stamp: usage-index.ver records the game version the index was built
// from; a mismatch with the live ffxivgame.ver flags the index as stale.
string? UsageIndexVer()
{
    var f = UsageIndexPath() + ".ver";
    try { return File.Exists(f) ? File.ReadAllText(f).Trim() : null; } catch { return null; }
}
bool UsageStale(out string? iv, out string? gv)
{
    iv = UsageIndexVer(); gv = LevelDirs.GameVersion(gamePath);
    return iv != null && gv != null && iv != gv;
}

app.MapGet("/api/usages/status", () =>
{
    var stale = UsageStale(out var iv, out var gv);
    return Results.Json(new
    {
        indexed = File.Exists(UsageIndexPath()),
        building = usageBuilding == 1,
        dirs = usageBuildDirs, rows = usageBuildRows, error = usageBuildError,
        indexVersion = iv, gameVersion = gv, stale,
    });
});

app.MapPost("/api/usages/build", () =>
{
    if (Interlocked.CompareExchange(ref usageBuilding, 1, 0) != 0)
        return Results.Json(new { building = true, dirs = usageBuildDirs, rows = usageBuildRows });
    usageBuildDirs = 0; usageBuildRows = 0; usageBuildError = null;
    _ = Task.Run(async () =>
    {
        var tmp = UsageIndexPath() + ".tmp";
        try
        {
            // level dirs discovered live (TerritoryType + cutscene stages) so a
            // rebuild after a patch picks up new territories automatically; the
            // static leveldirs.txt (when present) backstops curated extras and
            // is the sole source if discovery itself fails.
            List<(string Dir, string Label)> dirs;
            try
            {
                dirs = await WithEnv(e => LevelDirs.Discover(e));
                var f = LeveldirsFile();
                if (f != null) dirs = LevelDirs.Merge(dirs, File.ReadLines(f));
            }
            catch when (LeveldirsFile() != null)
            {
                dirs = LevelDirs.Merge(new(), File.ReadLines(LeveldirsFile()!));
            }
            await using (var w = Csv.OpenWriter(tmp))
            {
                w.WriteLine(UsageOps.IndexHeader);
                foreach (var (dir, lbl) in dirs)
                {
                    // one semaphore hold per dir so interactive routes interleave
                    var rows = await WithEnv(e => UsageOps.Walk(e.Game, dir, lbl));
                    foreach (var r in rows) UsageOps.WriteRow(w, r);
                    usageBuildRows += rows.Count;
                    usageBuildDirs++;
                }
            }
            File.Move(tmp, UsageIndexPath(), overwrite: true);
            var gv = LevelDirs.GameVersion(gamePath);
            var verFile = UsageIndexPath() + ".ver";
            if (gv != null) File.WriteAllText(verFile, gv);
            else { try { File.Delete(verFile); } catch { } }
            lock (usageIndexLock) usageIndexRows = null;   // reload on next query
        }
        catch (Exception ex) { usageBuildError = ex.Message; try { File.Delete(tmp); } catch { } }
        finally { usageBuilding = 0; }
    });
    return Results.Json(new { building = true, dirs = 0, rows = 0 });
});

app.MapGet("/api/usages", async (string? q, string? tt, int? limit) =>
{
    var cap = Math.Clamp(limit ?? 2000, 1, 50000);
    List<UsageRow> rows;
    if (!string.IsNullOrEmpty(tt))
    {
        rows = await WithEnv(e =>
        {
            var dir = ComposeOps.ResolveLevelDir(e.Game, tt, out var label);
            return usageWalkCache.GetOrAdd(dir, _ => UsageOps.Walk(e.Game, dir, $"TT{label}"));
        });
    }
    else
    {
        if (!File.Exists(UsageIndexPath()))
            return Results.Json(new { indexed = false, building = usageBuilding == 1, total = 0,
                rows = Array.Empty<UsageRow>() });
        lock (usageIndexLock)
        {
            if (usageIndexRows == null)
            {
                var intern = new Dictionary<string, string>();
                string I(string v) { if (!intern.TryGetValue(v, out var iv)) intern[v] = iv = v; return iv; }
                using var r = new StreamReader(UsageIndexPath());
                usageIndexRows = UsageOps.ReadIndex(r)
                    .Select(u => u with { Label = I(u.Label), LgbFile = I(u.LgbFile),
                        AssetType = I(u.AssetType), Asset = I(u.Asset), Via = I(u.Via) })
                    .ToList();
            }
            rows = usageIndexRows;
        }
    }
    var hits = UsageOps.Filter(rows, q ?? "").ToList();
    return Results.Json(new { indexed = true, building = usageBuilding == 1,
        stale = string.IsNullOrEmpty(tt) && UsageStale(out _, out _),
        total = hits.Count, rows = hits.Take(cap) });
});

app.MapGet("/api/mtrl", async (string path) =>
    Results.Json(await WithEnv(e => MtrlOps.Dump(e, path))));

app.MapGet("/api/tex/info", async (string path) =>
    Results.Json(await WithEnv(e => TexOps.Info(e, path))));

app.MapGet("/api/tex/png", async (string path, int? mip) =>
{
    var ms = new MemoryStream();
    await WithEnv<object?>(e => { TexOps.WritePng(e, path, ms, mip ?? 0); return null; });
    return Results.Bytes(ms.ToArray(), "image/png");
});

// ---- territory workspace (cached under workDir) ----
string TtDir(uint tt) => Path.Combine(workDir, $"tt-{tt}");

app.MapGet("/api/territory/{tt}", async (uint tt, int? refresh) =>
{
    if (libraryDir == null)
        return Results.BadRequest(new { error = "server started without --library/ATLAS_LIBRARY; territory workspaces need dumps/library" });
    var dir = TtDir(tt);
    var marker = Path.Combine(dir, "summary.md");
    if (refresh == 1 || !File.Exists(marker))
    {
        Directory.CreateDirectory(dir);
        var log = new StringBuilder();
        await WithEnv<object?>(e =>
        {
            TerritoryWorkspace.Run(e, tt, dir, libraryDir, collision: false, log: s => log.AppendLine(s));
            return null;
        });
    }
    var files = Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(f => f).ToArray();
    return Results.Json(new { territory = tt, dir, files });
});

app.MapGet("/api/territory/{tt}/file/{**name}", (uint tt, string name) =>
{
    // catch-all: the textured glTF references tex/<name>.png subpaths. Binary
    // files must stream (ReadAllText would mangle .bin/.png through UTF-8).
    if (name.Contains("..") || name.Contains('\\')) return Results.BadRequest();
    var f = Path.Combine(TtDir(tt), name);
    if (!File.Exists(f)) return Results.NotFound(new { error = "not generated; GET /api/territory/{tt} first", name });
    var ct = name.EndsWith(".csv") ? "text/csv; charset=utf-8"
        : name.EndsWith(".png") ? "image/png"
        : name.EndsWith(".gltf") ? "model/gltf+json"
        : name.EndsWith(".bin") || name.EndsWith(".obj") ? "application/octet-stream"
        : "text/plain; charset=utf-8";
    return ct.StartsWith("text/")
        ? Results.Text(File.ReadAllText(f), ct)
        : Results.Stream(File.OpenRead(f), ct);
});

app.MapGet("/api/territory/{tt}/collision.obj", async (uint tt, int? refresh) =>
{
    var outRoot = Path.Combine(TtDir(tt), "pcb");
    var objs = Directory.Exists(outRoot)
        ? Directory.GetFiles(outRoot, "collision-mesh.obj", SearchOption.AllDirectories) : [];
    if (refresh == 1 || objs.Length == 0)
    {
        Directory.CreateDirectory(outRoot);
        await WithEnv<object?>(e =>
        {
            TerritoryDump.Run(e.Game, tt.ToString(), outRoot, new TerritoryDumpOptions { ExportObj = true });
            return null;
        });
        objs = Directory.GetFiles(outRoot, "collision-mesh.obj", SearchOption.AllDirectories);
    }
    return objs.Length == 0
        ? Results.NotFound(new { error = "no collision produced", territory = tt })
        : Results.Stream(File.OpenRead(objs[0]), "text/plain; charset=utf-8");
});

app.MapGet("/api/territory/{tt}/map.gltf", async (uint tt, int? refresh, int? textured) =>
{
    var dir = TtDir(tt);
    var stem = textured == 1 ? $"map-{tt}-tex" : $"map-{tt}";
    var gltf = Path.Combine(dir, $"{stem}.gltf");
    if (refresh == 1 || !File.Exists(gltf))
    {
        Directory.CreateDirectory(dir);
        await WithEnv<object?>(e =>
        {
            ComposeOps.MapGltf(e.Game, tt.ToString(), dir, new ComposeOptions { Textured = textured == 1 });
            return null;
        });
    }
    return File.Exists(gltf)
        ? Results.Stream(File.OpenRead(gltf), "model/gltf+json")
        : Results.NotFound(new { error = "compose produced no gltf", territory = tt });
});

app.MapGet("/api/territory/{tt}/map.bin", (uint tt, int? textured) =>
{
    var stem = textured == 1 ? $"map-{tt}-tex" : $"map-{tt}";
    var bin = Path.Combine(TtDir(tt), $"{stem}.bin");
    return File.Exists(bin)
        ? Results.Stream(File.OpenRead(bin), "application/octet-stream")
        : Results.NotFound(new { error = "GET map.gltf first", territory = tt });
});

// ---- path index (ResLogger2-style list; powers the asset browser) ----
// Lazy: nothing is loaded until the first /api/paths hit. Sorted array; prefix
// queries answer folder drill-down, q= does a bounded substring scan.
string[]? pathIndex = null;
var pathsGate = new SemaphoreSlim(1, 1);
async Task<string[]?> GetPathIndex()
{
    if (pathIndex != null) return pathIndex;
    if (pathsFile == null || !File.Exists(pathsFile)) return null;
    await pathsGate.WaitAsync();
    try
    {
        if (pathIndex != null) return pathIndex;
        using Stream raw = File.OpenRead(pathsFile);
        using Stream src = pathsFile.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new System.IO.Compression.GZipStream(raw, System.IO.Compression.CompressionMode.Decompress) : raw;
        using var rd = new StreamReader(src);
        var list = new List<string>(1 << 21);
        while (rd.ReadLine() is { } line)
            if (line.Length > 0) list.Add(line);
        list.Sort(StringComparer.Ordinal);
        pathIndex = list.ToArray();
        Console.WriteLine($"path index: {pathIndex.Length} paths from {pathsFile}");
        return pathIndex;
    }
    finally { pathsGate.Release(); }
}

app.MapGet("/api/paths", async (string? prefix, string? q, int? limit) =>
{
    var idx = await GetPathIndex();
    if (idx == null) return Results.NotFound(new { error = "no path list (--paths or ATLAS_PATHS)" });
    var lim = Math.Clamp(limit ?? 200, 1, 2000);

    if (!string.IsNullOrEmpty(q))
    {
        // bounded substring scan (optionally within a prefix)
        var hits = new List<string>(lim);
        foreach (var s in idx)
        {
            if (prefix != null && !s.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (s.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(s);
                if (hits.Count >= lim) break;
            }
        }
        return Results.Ok(new { mode = "search", q, prefix, hits, truncated = hits.Count >= lim });
    }

    // folder listing: distinct immediate children under prefix
    var pre = prefix ?? "";
    int lo = LowerBound(idx, pre);
    var dirs = new SortedDictionary<string, int>(StringComparer.Ordinal);
    var files = new List<string>();
    for (int i = lo; i < idx.Length; i++)
    {
        var s = idx[i];
        if (!s.StartsWith(pre, StringComparison.Ordinal)) break;
        var rest = s.AsSpan(pre.Length);
        var slash = rest.IndexOf('/');
        if (slash < 0) { if (files.Count < lim) files.Add(s); }
        else
        {
            var seg = rest[..slash].ToString();
            dirs[seg] = dirs.TryGetValue(seg, out var n) ? n + 1 : 1;
        }
    }
    return Results.Ok(new
    {
        mode = "list", prefix = pre,
        dirs = dirs.Select(kv => new { name = kv.Key, count = kv.Value }).ToArray(),
        files,
    });

    static int LowerBound(string[] a, string key)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi) { var mid = (lo + hi) >> 1; if (string.CompareOrdinal(a[mid], key) < 0) lo = mid + 1; else hi = mid; }
        return lo;
    }
});

// ---- static frontend ----
if (webDir != null)
{
    var fp = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.GetFullPath(webDir));
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fp });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = fp,
        ServeUnknownFileTypes = true // .gltf/.bin/vendored assets
    });
}

Console.WriteLine($"Atlas.Server on http://127.0.0.1:{port}  game={gamePath}  web={webDir ?? "(none)"}  work={workDir}");
app.Run();
return 0;
