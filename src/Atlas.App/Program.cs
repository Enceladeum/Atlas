// Atlas.App - thin WebView2 desktop shell around Atlas.Server.
// Spawns the server as a child process (free port, environment inherited, args
// forwarded), then hosts the same web/ frontend in a WebView2 window. Everything
// shown here stays reachable headless (CLI verb or API route) - the shell adds
// nothing but a window (CONTRACT, Server/API boundary). Child stdout/stderr go
// to %LOCALAPPDATA%\Atlas\server.log for post-mortems.
//   Atlas.App [--game <sqpack>] [--port N] [--url http://host:port/] [--server <path>] [args...]
//   --url attaches to an already-running server (no spawn, no kill-on-close).
//   Game path: --game > ATLAS_GAME/XIVTOOL_GAME > %LOCALAPPDATA%\Atlas\settings.json;
//   none present -> first-run folder picker, persisted to settings.json.
//   Data sources: ATLAS_SCHEMA/ATLAS_LIBRARY/ATLAS_PATHS from env, else settings.json
//   keys schemaPath/libraryPath/pathsFile (env wins; picker persists gamePath only).
// Server discovery mirrors GuiCommands: beside the exe, else the sibling
// project dir (last "/Atlas.App/" path segment -> "/Atlas.Server/").

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Atlas.App;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var argv = new List<string>(args);
        string? Opt(string name) { var i = argv.IndexOf(name); if (i < 0 || i + 1 >= argv.Count) return null; var v = argv[i + 1]; argv.RemoveRange(i, 2); return v; }

        var url = Opt("--url");
        Process? child = null;
        if (url == null)
        {
            var serverPath = Opt("--server");
            var port = int.TryParse(Opt("--port"), out var p) ? p : FreePort();
            var file = FindServer(serverPath, out var isDll);
            if (file == null)
            {
                MessageBox.Show(
                    "Atlas.Server not found next to Atlas.App (looked for Atlas.Server.exe/.dll and the sibling project dir).\n" +
                    "Pass --server <path to Atlas.Server.dll/exe> or --url <already-running server>.",
                    "Atlas", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Game path: --game > env > saved settings > first-run folder picker.
            var game = Opt("--game") ?? Environment.GetEnvironmentVariable("ATLAS_GAME")
                        ?? Environment.GetEnvironmentVariable("XIVTOOL_GAME") ?? LoadSetting("gamePath");
            if (game != null && !Directory.Exists(game)) game = null;   // stale/moved install
            if (game == null)
            {
                game = PickGamePath();
                if (game == null) return;           // user cancelled first run
                SaveGamePath(game);                 // persist only picker-supplied paths
            }

            var psi = new ProcessStartInfo
            {
                FileName = isDll ? "dotnet" : file, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            if (isDll) psi.ArgumentList.Add(file);
            psi.Environment["ATLAS_GAME"] = game;
            // Data sources: env wins; settings.json fills the gaps.
            Forward(psi, "ATLAS_SCHEMA", "schemaPath", altEnv: "XIVTOOL_SCHEMA");
            Forward(psi, "ATLAS_LIBRARY", "libraryPath");
            Forward(psi, "ATLAS_PATHS", "pathsFile");
            psi.ArgumentList.Add("--port"); psi.ArgumentList.Add(port.ToString());
            foreach (var a in argv) psi.ArgumentList.Add(a);

            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Atlas");
            Directory.CreateDirectory(logDir);
            var log = new StreamWriter(Path.Combine(logDir, "server.log"), append: false) { AutoFlush = true };
            try { child = Process.Start(psi) ?? throw new Exception("no process"); }
            catch (Exception e)
            {
                MessageBox.Show($"Failed to start Atlas.Server: {e.Message}", "Atlas", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            child.OutputDataReceived += (_, ev) => { if (ev.Data != null) lock (log) log.WriteLine(ev.Data); };
            child.ErrorDataReceived += (_, ev) => { if (ev.Data != null) lock (log) log.WriteLine(ev.Data); };
            child.BeginOutputReadLine();
            child.BeginErrorReadLine();
            url = $"http://localhost:{port}/";
        }

        Application.Run(new MainForm(url, child));
    }

    // ---- settings (%LOCALAPPDATA%\Atlas\settings.json): gamePath + optional data-source keys ----

    static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Atlas", "settings.json");

    static string? LoadSetting(string key)
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            return doc.RootElement.TryGetProperty(key, out var v) ? v.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>Set a server env var from: current env (or altEnv), else the settings
    /// key. Env always wins over settings; unset everywhere = leave unset.</summary>
    static void Forward(ProcessStartInfo psi, string env, string key, string? altEnv = null)
    {
        var v = Environment.GetEnvironmentVariable(env)
                ?? (altEnv != null ? Environment.GetEnvironmentVariable(altEnv) : null)
                ?? LoadSetting(key);
        if (!string.IsNullOrEmpty(v)) psi.Environment[env] = v;
    }

    static void SaveGamePath(string game)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            // Preserve any hand-added keys (schemaPath/libraryPath/pathsFile).
            var map = new Dictionary<string, string?>();
            try
            {
                if (File.Exists(SettingsPath))
                    using (var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath)))
                        foreach (var prop in doc.RootElement.EnumerateObject())
                            if (prop.Value.ValueKind == JsonValueKind.String)
                                map[prop.Name] = prop.Value.GetString();
            }
            catch { /* corrupt file - rewrite from scratch */ }
            map["gamePath"] = game;
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(
                map, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort - the picker just reappears next run */ }
    }

    static string? PickGamePath()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Select the FINAL FANTASY XIV install folder (the one containing game\\sqpack).",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        while (true)
        {
            if (dlg.ShowDialog() != DialogResult.OK) return null;
            var picked = dlg.SelectedPath;
            foreach (var c in new[] { picked, Path.Combine(picked, "game", "sqpack"), Path.Combine(picked, "sqpack") })
                if (Directory.Exists(Path.Combine(c, "ffxiv"))) return c;
            if (MessageBox.Show(
                    $"No sqpack found under:\n{picked}\n\nPick the install folder that contains game\\sqpack.",
                    "Atlas", MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning) != DialogResult.Retry)
                return null;
        }
    }

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
            var i = norm.LastIndexOf("/Atlas.App/", StringComparison.OrdinalIgnoreCase);
            if (i >= 0)
            {
                var alt = norm[..i] + "/Atlas.Server/" + norm[(i + "/Atlas.App/".Length)..];
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
}
