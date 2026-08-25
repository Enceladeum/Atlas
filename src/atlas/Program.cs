using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Excel;
using Atlas.Core;
using Atlas.Core.Exd;

// atlas — FFXIV game file inspection CLI built on Atlas.Core (Lumina + EXDSchema).
//   sheets  [filter]                       list sheet names (root.exl)
//   header  <sheet>                        column layout / variant / row count
//   dump    <sheet> [--lang en|ja|de|fr|none] [--out f] [--max n] [--no-schema]
//   extract <gamepath> [--out <file>]      extract any file from sqpack
//   exists  <gamepath>
//   lgb     <territoryId|bg path> [--out dir]   quick per-LGB instance dump
//   library rebuild|diff ...               full CSV library regen / patch-drift diff
//   territory [dump] <tt> --out <dir>      per-territory research workspace
//   gui     [--port N] [--no-browser]      start Atlas.Server + open the web GUI
// Env: ATLAS_GAME = sqpack dir, ATLAS_SCHEMA = EXDSchema dir (XIVTOOL_GAME/XIVTOOL_SCHEMA accepted as fallback)

var argv = new List<string>(args);
string? GetOpt(string name)
{
    var i = argv.IndexOf(name);
    if (i < 0 || i + 1 >= argv.Count) return null;
    var v = argv[i + 1];
    argv.RemoveRange(i, 2);
    return v;
}
bool GetFlag(string name) { var i = argv.IndexOf(name); if (i < 0) return false; argv.RemoveAt(i); return true; }

var gamePath = GetOpt("--game") ?? Environment.GetEnvironmentVariable("ATLAS_GAME") ?? Environment.GetEnvironmentVariable("XIVTOOL_GAME");
var schemaDir = GetOpt("--schema") ?? Environment.GetEnvironmentVariable("ATLAS_SCHEMA") ?? Environment.GetEnvironmentVariable("XIVTOOL_SCHEMA");
if (argv.Count == 0)
{
    Console.Error.WriteLine("usage: atlas <sheets|header|dump|extract|exists|lgb|library|territory|gui|mod> ...");
    return 1;
}
var cmd = argv[0];
if (cmd == "gui")
    // GUI launcher: spawns Atlas.Server (resolved env passed through) + opens
    // the browser. First-class so it skips the XivEnv/GameData boot below -
    // the server boots its own. Dispatched BEFORE the game-path check: gui
    // falls back to the Atlas.App-saved settings.json when env/--game are
    // absent. Atlas.App is the WebView2 variant of the same.
    return Atlas.Cli.Commands.GuiCommands.Run(gamePath, schemaDir, argv.GetRange(1, argv.Count - 1));
if (gamePath == null) { Console.Error.WriteLine("error: no game path (--game or ATLAS_GAME)"); return 1; }

var env = new XivEnv(gamePath, schemaDir);
var lumina = env.Game;
Action<string> log = s => Console.Error.WriteLine(s);

switch (cmd)
{
    case "sheets":
    {
        var filter = argv.Count > 1 ? argv[1] : null;
        foreach (var n in ExdOps.SheetNames(env, filter))
            Console.WriteLine(n);
        return 0;
    }
    case "header":
    {
        if (argv.Count < 2) { Console.Error.WriteLine("usage: atlas header <sheet>"); return 1; }
        ExdOps.WriteHeader(env, argv[1], Console.Out);
        return 0;
    }
    case "dump":
    {
        if (argv.Count < 2) { Console.Error.WriteLine("usage: atlas dump <sheet> [--lang en] [--out f] [--max n] [--no-schema]"); return 1; }
        var lang = ExdOps.ParseLang(GetOpt("--lang") ?? "en");
        var outPath = GetOpt("--out");
        var max = int.TryParse(GetOpt("--max"), out var m) ? m : int.MaxValue;
        var noSchema = GetFlag("--no-schema");
        using var writer = outPath != null ? Csv.OpenWriter(outPath) : Console.Out;
        ExdOps.Dump(env, argv[1], lang, writer, max, !noSchema, log);
        return 0;
    }
    case "extract":
    {
        if (argv.Count < 2) { Console.Error.WriteLine("usage: atlas extract <gamepath> [--out <file>]"); return 1; }
        var outPath = GetOpt("--out") ?? argv[1];
        var file = lumina.GetFile(argv[1]);
        if (file == null) { Console.Error.WriteLine($"not found: {argv[1]}"); return 1; }
        var dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(outPath, file.Data);
        Console.WriteLine($"wrote {file.Data.Length} bytes -> {outPath}");
        return 0;
    }
    case "exists":
    {
        if (argv.Count < 2) { Console.Error.WriteLine("usage: atlas exists <gamepath>"); return 1; }
        var ok = lumina.FileExists(argv[1]);
        Console.WriteLine(ok ? "EXISTS" : "MISSING");
        return ok ? 0 : 1;
    }

    case "lgb":
    {
        // Quick per-LGB dump (Lumina LgbFile). Superseded by the DumpCore-based
        // Lgb module (agent A) for full analytics; kept for parity with old tool.
        if (argv.Count < 2) { Console.Error.WriteLine("usage: atlas lgb <territoryId|bg path> [--out dir]"); return 1; }
        var outDir = GetOpt("--out");
        string levelDir;
        string label = argv[1];
        if (uint.TryParse(argv[1], out var terrId))
        {
            var tt = lumina.Excel.GetSheet<RawRow>(null, "TerritoryType");
            if (!tt.HasRow(terrId)) { Console.Error.WriteLine($"TerritoryType {terrId} not found"); return 1; }
            var row = tt.GetRow(terrId);
            string? bg = null;
            for (var c = 0; c < row.Columns.Count && bg == null; c++)
                if (row.Columns[c].Type == Lumina.Data.Structs.Excel.ExcelColumnDataType.String)
                {
                    var s = Csv.Str(row.ReadColumn(c));
                    if (s.Contains("/level/")) bg = s;
                    else if (s.Contains('/') && s.Count(ch => ch == '/') >= 3) bg ??= s;
                }
            if (bg == null) { Console.Error.WriteLine($"no Bg path on TerritoryType {terrId}"); return 1; }
            levelDir = "bg/" + bg.Substring(0, bg.LastIndexOf('/'));
            label = $"{terrId} ({bg.Split('/').Last()})";
        }
        else levelDir = argv[1].TrimEnd('/');

        var any = false;
        TextWriter w = Console.Out; StreamWriter? fw = null;
        if (outDir != null) Directory.CreateDirectory(outDir);
        Console.Error.WriteLine($"# LGB dump for {label}  dir={levelDir}");
        foreach (var f in InstanceIdentity.LgbFiles)
        {
            var path = $"{levelDir}/{f}.lgb";
            LgbFile? lgb;
            try { lgb = lumina.GetFile<LgbFile>(path); } catch (Exception e) { Console.Error.WriteLine($"# {f}.lgb: parse error: {e.Message}"); continue; }
            if (lgb == null) continue;
            any = true;
            if (outDir != null) { fw?.Flush(); fw?.Dispose(); fw = Csv.OpenWriter(Path.Combine(outDir, f + ".csv")); w = fw; }
            w.WriteLine("File,LayerId,LayerName,FestivalID,AssetType,InstanceId,Name,X,Y,Z,RotX,RotY,RotZ,ScaleX,ScaleY,ScaleZ,Extra");
            var count = 0;
            foreach (var layer in lgb.Layers)
            {
                foreach (var io in layer.InstanceObjects)
                {
                    var t = io.Transform;
                    var extra = io.Object switch
                    {
                        LayerCommon.ENPCInstanceObject e2 => $"BaseId={e2.ParentData.ParentData.BaseId}",
                        LayerCommon.PopRangeInstanceObject p2 => $"PopType={p2.PopType};Index={p2.Index}",
                        LayerCommon.ExitRangeInstanceObject x2 => $"ExitType={x2.ExitType};TerritoryType={x2.TerritoryType};Index={x2.Index}",
                        LayerCommon.MapRangeInstanceObject m2 => $"Map={m2.Map};PlaceNameBlock={m2.PlaceNameBlock};PlaceNameSpot={m2.PlaceNameSpot}",
                        LayerCommon.EventInstanceObject ev => $"BaseId={ev.ParentData.BaseId}",
                        LayerCommon.AetheryteInstanceObject ae => $"BaseId={ae.ParentData.BaseId}",
                        LayerCommon.BGInstanceObject bg2 => $"Asset={bg2.AssetPath}",
                        LayerCommon.SharedGroupInstanceObject sg => $"Asset={sg.AssetPath}",
                        _ => ""
                    };
                    w.WriteLine(string.Join(",",
                        f, layer.LayerId, Csv.Escape(layer.Name ?? ""), layer.FestivalID,
                        io.AssetType, io.InstanceId, Csv.Escape(io.Name ?? ""),
                        t.Translation.X.ToString("R"), t.Translation.Y.ToString("R"), t.Translation.Z.ToString("R"),
                        t.Rotation.X.ToString("R"), t.Rotation.Y.ToString("R"), t.Rotation.Z.ToString("R"),
                        t.Scale.X.ToString("R"), t.Scale.Y.ToString("R"), t.Scale.Z.ToString("R"),
                        Csv.Escape(extra)));
                    count++;
                }
            }
            Console.Error.WriteLine($"# {f}.lgb: {lgb.Layers.Length} layers, {count} instance objects" + (outDir != null ? $" -> {Path.Combine(outDir, f + ".csv")}" : ""));
        }
        fw?.Flush(); fw?.Dispose();
        if (!any) { Console.Error.WriteLine("no lgb files found"); return 1; }
        return 0;
    }

    case "library":
    {
        // First-class promotion of `mod Library` (rebuild + diff). Same flags,
        // outputs, exit codes; `mod Library ...` remains as a back-compat alias.
        return Atlas.Cli.Commands.LibraryCommands.Run(env, argv.GetRange(1, argv.Count - 1));
    }

    case "territory":
    {
        // First-class promotion of `mod Territory`. `territory <tt> ...` with a
        // numeric first arg implies the dump verb; alias `mod Territory dump` stays.
        var rest = argv.GetRange(1, argv.Count - 1);
        if (rest.Count > 0 && uint.TryParse(rest[0], out _)) rest.Insert(0, "dump");
        return Atlas.Cli.Commands.TerritoryCommands.Run(env, rest);
    }
    case "patch":
    {
        // Per-patch intake pipeline (census/intake/promote); alias: mod Patch.
        return Atlas.Cli.Commands.PatchCommands.Run(env, argv.GetRange(1, argv.Count - 1));
    }
    case "mod":
    {
        // Module dispatch (Wave 2): atlas mod <Module> <verb> [...]
        // Invokes Atlas.Cli.Commands.<Module>Commands.Run(XivEnv, List<string>).
        // Agents add atlas/Commands/<Module>Commands.cs — never this file.
        // Wave 3 promotes stable verbs to first-class cases (library, territory).
        if (argv.Count < 3) { Console.Error.WriteLine("usage: atlas mod <Module> <verb> ..."); return 1; }
        var t = Type.GetType($"Atlas.Cli.Commands.{argv[1]}Commands");
        if (t == null) { Console.Error.WriteLine($"no commands class: Atlas.Cli.Commands.{argv[1]}Commands"); return 1; }
        var mi = t.GetMethod("Run", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (mi == null) { Console.Error.WriteLine($"{argv[1]}Commands has no public static Run(XivEnv, List<string>)"); return 1; }
        return (int)mi.Invoke(null, [env, argv.GetRange(2, argv.Count - 2)])!;
    }

    default:
        Console.Error.WriteLine($"unknown command: {cmd}");
        return 1;
}
