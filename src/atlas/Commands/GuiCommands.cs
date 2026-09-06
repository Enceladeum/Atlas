// Gui launcher - `atlas gui [--port N] [--no-browser] [--server <path>] [args...]`.
// Spawns Atlas.Server as a child process (resolved ATLAS_GAME/ATLAS_SCHEMA plus
// settings-supplied ATLAS_LIBRARY/ATLAS_PATHS in its environment, remaining args
// forwarded verbatim), waits for
// /api/meta, then opens the default browser. Atlas.App is the WebView2 desktop
// variant of the exact same flow. Headless parity: gui adds nothing
// of its own - everything it shows is the web/ frontend over the same HTTP API.
// Server discovery: --server wins; else Atlas.Server.exe/.dll next to atlas;
// else the sibling project dir (last "/atlas/" path segment -> "/Atlas.Server/",
// covering both the default bin layout and the OutRoot= sandbox layout).
// Game path: --game/ATLAS_GAME, else the path Atlas.App's first-run picker
// saved to settings.json (LocalApplicationData/Atlas/settings.json).
// Settings keys (all optional strings): gamePath, schemaPath, libraryPath,
// pathsFile - each fills its env var only when flag/env left it unset.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Atlas.Cli.Commands;

public static class GuiCommands
{
    const string Usage = "usage: atlas gui [--port N] [--no-browser] [--server <Atlas.Server dll/exe>] [args forwarded to server]";

    public static int Run(string? gamePath, string? schemaDir, List<string> argv)
    {
        // Product fallback: --game/ATLAS_GAME absent -> the path Atlas.App's
        // first-run picker persisted to settings.json.
        gamePath ??= TryLoadSetting("gamePath");
        if (gamePath == null)
        {
            Console.Error.WriteLine("error: no game path (--game or ATLAS_GAME; or run Atlas.App once to pick it)");
            return 2;
        }
        string? Opt(string name) { var i = argv.IndexOf(name); if (i < 0 || i + 1 >= argv.Count) return null; var v = argv[i + 1]; argv.RemoveRange(i, 2); return v; }
        bool Flag(string name) { var i = argv.IndexOf(name); if (i < 0) return false; argv.RemoveAt(i); return true; }

        if (Flag("--help") || Flag("-h")) { Console.Error.WriteLine(Usage); return 1; }
        var noBrowser = Flag("--no-browser");
        var serverPath = Opt("--server");
        var port = int.TryParse(Opt("--port"), out var p) ? p : FreePort();

        var file = FindServer(serverPath, out var isDll);
        if (file == null)
        {
            Console.Error.WriteLine("error: Atlas.Server not found next to atlas (looked for Atlas.Server.exe/.dll and the sibling project dir; pass --server <path>)");
            return 1;
        }

        var psi = new ProcessStartInfo { FileName = isDll ? "dotnet" : file, UseShellExecute = false };
        if (isDll) psi.ArgumentList.Add(file);
        psi.ArgumentList.Add("--port"); psi.ArgumentList.Add(port.ToString());
        foreach (var a in argv) psi.ArgumentList.Add(a);
        psi.Environment["ATLAS_GAME"] = gamePath;
        // Data sources: flag/env win; the Atlas.App settings.json fills the gaps
        // (schemaPath/libraryPath/pathsFile -> ATLAS_SCHEMA/ATLAS_LIBRARY/ATLAS_PATHS).
        schemaDir ??= TryLoadSetting("schemaPath");
        if (schemaDir != null) psi.Environment["ATLAS_SCHEMA"] = schemaDir;
        var lib = Environment.GetEnvironmentVariable("ATLAS_LIBRARY") ?? TryLoadSetting("libraryPath");
        if (!string.IsNullOrEmpty(lib)) psi.Environment["ATLAS_LIBRARY"] = lib;
        var paths = Environment.GetEnvironmentVariable("ATLAS_PATHS") ?? TryLoadSetting("pathsFile");
        if (!string.IsNullOrEmpty(paths)) psi.Environment["ATLAS_PATHS"] = paths;

        Process child;
        try { child = Process.Start(psi) ?? throw new Exception("no process"); }
        catch (Exception e) { Console.Error.WriteLine($"error: failed to start {file}: {e.Message}"); return 1; }
        Console.CancelKeyPress += (_, ev) => { ev.Cancel = true; TryKill(child); };

        var url = $"http://localhost:{port}/";
        Console.Error.WriteLine($"atlas gui: server pid {child.Id} on port {port} ({file})");
        if (!WaitReady($"{url}api/meta", child, TimeSpan.FromSeconds(90)))
        {
            Console.Error.WriteLine("error: server did not become ready (see its output above)");
            TryKill(child);
            return 1;
        }
        Console.WriteLine($"Atlas ready -> {url}");
        if (!noBrowser)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { Console.WriteLine("(could not open a browser automatically - open the URL manually)"); }
        }
        child.WaitForExit();
        return child.ExitCode;
    }

    /// <summary>One string key from the Atlas.App settings file
    /// (LocalApplicationData/Atlas/settings.json); null when absent/unreadable.</summary>
    static string? TryLoadSetting(string key)
    {
        try
        {
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                 "Atlas", "settings.json");
            if (!File.Exists(p)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(p));
            return doc.RootElement.TryGetProperty(key, out var v) ? v.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>--server override, then Atlas.Server.exe/.dll beside atlas, then the
    /// sibling project output (swap the LAST "/atlas/" path segment - OutRoot-safe).</summary>

    static string? FindServer(string? explicitPath, out bool isDll)
    {
        var cands = new List<string>();
        if (explicitPath != null) cands.Add(explicitPath);
        else
        {
            var b = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, '/');
            cands.Add(Path.Combine(b, "Atlas.Server.exe"));
            cands.Add(Path.Combine(b, "Atlas.Server.dll"));
            var norm = b.Replace('\\', '/');
            var i = norm.LastIndexOf("/atlas/", StringComparison.OrdinalIgnoreCase);
            if (i >= 0)
            {
                var alt = norm[..i] + "/Atlas.Server/" + norm[(i + "/atlas/".Length)..];
                cands.Add($"{alt}/Atlas.Server.exe");
                cands.Add($"{alt}/Atlas.Server.dll");
            }
        }
        foreach (var c in cands)
            if (File.Exists(c)) { isDll = c.EndsWith(".dll", StringComparison.OrdinalIgnoreCase); return c; }
        isDll = false;
        return null;
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    static bool WaitReady(string metaUrl, Process child, TimeSpan max)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var t0 = DateTime.UtcNow;
        while (DateTime.UtcNow - t0 < max)
        {
            if (child.HasExited) return false;
            try { if (http.GetAsync(metaUrl).GetAwaiter().GetResult().IsSuccessStatusCode) return true; }
            catch { /* not up yet */ }
            Thread.Sleep(500);
        }
        return false;
    }

    static void TryKill(Process child) { try { child.Kill(entireProcessTree: true); } catch { /* already gone */ } }
}
