// Main window: a status label while the child server boots (polls /api/meta up
// to 90 s), then a WebView2 (user data under %LOCALAPPDATA%\Atlas\WebView2)
// navigated to the server root. WebView2-runtime-missing degrades to opening
// the default browser - the GUI is the web/ frontend either way.

using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Atlas.App;

sealed class MainForm : Form
{
    static readonly string BuildStamp = System.Reflection.CustomAttributeExtensions
        .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
            System.Reflection.Assembly.GetEntryAssembly()!)?.InformationalVersion ?? "dev";
    readonly string _url;
    readonly Process? _child;
    readonly Label _status;
    WebView2? _web;

    public MainForm(string url, Process? child)
    {
        _url = url;
        _child = child;
        Text = $"Atlas \u00b7 {BuildStamp}";
        BackColor = Color.FromArgb(11, 13, 16);
        ClientSize = new Size(1480, 940);
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        _status = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(185, 190, 199),
            Font = new Font("Segoe UI", 11f),
            Text = "Starting Atlas server…",
        };
        Controls.Add(_status);
        Load += async (_, _) => await BootAsync();
        FormClosed += (_, _) => { try { _child?.Kill(entireProcessTree: true); } catch { /* already gone */ } };
    }

    async Task BootAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        var ready = false;
        while (DateTime.UtcNow < deadline)
        {
            if (_child is { HasExited: true }) break;
            try { if ((await http.GetAsync(_url + "api/meta")).IsSuccessStatusCode) { ready = true; break; } }
            catch { /* not up yet */ }
            await Task.Delay(400);
        }
        if (!ready)
        {
            _status.Text = _child is { HasExited: true }
                ? "Atlas.Server exited during startup - see %LOCALAPPDATA%\\Atlas\\server.log"
                : "Server did not become ready in 90 s - see %LOCALAPPDATA%\\Atlas\\server.log";
            return;
        }

        try
        {
            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Atlas", "WebView2");
            var envw = await CoreWebView2Environment.CreateAsync(null, dataDir);
            _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(11, 13, 16) };
            Controls.Add(_web);
            _web.BringToFront();
            await _web.EnsureCoreWebView2Async(envw);
            _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _web.CoreWebView2.DocumentTitleChanged += (_, _) =>
            {
                var t = _web.CoreWebView2.DocumentTitle;
                Text = (string.IsNullOrWhiteSpace(t) ? "Atlas" : t) + $" \u00b7 {BuildStamp}";
            };
            _web.CoreWebView2.Navigate(_url);
            _status.Visible = false;
        }
        catch (Exception e)   // typically: WebView2 Runtime not installed
        {
            _status.Text = $"WebView2 init failed: {e.Message}\nOpening in the default browser instead.";
            try { Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true }); } catch { /* open manually */ }
        }
    }
}
