using System.Diagnostics;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AiSecretary.Desktop.Resources;

namespace AiSecretary.Desktop.Services;

/// <summary>
/// Manages the dedicated isolated browser profile (%LOCALAPPDATA%\AiSecretary.Desktop\browser_profile)
/// using the user's installed Edge/Chrome binary so manual logins persist cleanly without touching
/// the user's primary browser profile or passwords.
/// </summary>
public sealed class BrowserHostService : IDisposable
{
    public int CdpDebugPort { get; }

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<BrowserHostService> _logger;
    private readonly SemaphoreSlim _launchLock = new(1, 1);
    private Process? _process;

    public BrowserHostService(IHttpClientFactory httpFactory, IConfiguration config, ILogger<BrowserHostService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
        CdpDebugPort = Math.Clamp(config.GetValue<int>("Browser:CdpPort", 19877), 1024, 65535);
    }

    public bool IsRunning => _process is { HasExited: false };

    public static string? FindSystemBrowserExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Opens the dedicated isolated browser window (visible UI) so the user can manually sign in
    /// to X, e-commerce sites, or work accounts without sharing credentials with the AI.
    /// </summary>
    public async Task<(bool Ok, string Message)> OpenLoginBrowserAsync(string? startUrl = null, CancellationToken ct = default)
    {
        await _launchLock.WaitAsync(ct);
        try
        {
            var exe = FindSystemBrowserExecutable();
            if (exe is null)
                return (false, AppMessages.Localize("BrowserExeMissing", "Microsoft Edge or Google Chrome was not found on this system."));

            Directory.CreateDirectory(AppPaths.BrowserProfileDir);

            // If our isolated browser is already running on the CDP port, open a new tab via CDP instead of fighting the profile lock.
            if (IsRunning && await IsCdpReachableAsync(ct))
            {
                if (!string.IsNullOrWhiteSpace(startUrl))
                    await OpenTabViaCdpAsync(startUrl, ct);
                return (true, AppMessages.Localize("BrowserAlreadyRunning", "Dedicated browser is already running and a new tab was opened. Background watchers will reuse this login session."));
            }

            if (_process is { HasExited: false })
            {
                try { _process.Kill(entireProcessTree: true); } catch { }
            }

            var target = string.IsNullOrWhiteSpace(startUrl) ? "about:blank" : startUrl.Trim();
            var args = $"--user-data-dir=\"{AppPaths.BrowserProfileDir}\" --remote-debugging-port={CdpDebugPort} --no-first-run --no-default-browser-check \"{target}\"";

            _process = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false
            });

            if (_process is null)
                return (false, AppMessages.Localize("BrowserStartFailed", "Failed to launch dedicated browser."));

            return (true, AppMessages.LocalizeFormat("BrowserStarted", "Opened isolated dedicated browser (profile: {0}). Please sign in manually inside that window; session cookies stay isolated.", AppPaths.BrowserProfileDir));
        }
        finally
        {
            _launchLock.Release();
        }
    }

    /// <summary>
    /// Reads a DOM selector from a URL using an existing isolated CDP browser session (read-only).
    /// Returns (Status, ValueOrReason) where Status is "ok" | "needs_auth" | "unavailable" | "error".
    /// </summary>
    public async Task<(string Status, string Value)> ReadDomSelectorViaCdpAsync(
        string url,
        string cssSelector,
        CancellationToken ct = default)
    {
        if (!await IsCdpReachableAsync(ct))
            return ("unavailable", "Dedicated browser is not running or CDP debug port is unreachable.");

        try
        {
            var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            var targetsJson = await client.GetStringAsync($"http://127.0.0.1:{CdpDebugPort}/json/list", ct);
            using var doc = JsonDocument.Parse(targetsJson);
            string? wsUrl = null;
            string? tabId = null;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var type = item.TryGetProperty("type", out var tp) ? tp.GetString() : null;
                var pageUrl = item.TryGetProperty("url", out var up) ? up.GetString() : null;
                if (type == "page" && pageUrl is not null && pageUrl.StartsWith(url, StringComparison.OrdinalIgnoreCase))
                {
                    wsUrl = item.TryGetProperty("webSocketDebuggerUrl", out var wp) ? wp.GetString() : null;
                    tabId = item.TryGetProperty("id", out var ip) ? ip.GetString() : null;
                    break;
                }
            }

            bool createdNewTab = false;
            if (string.IsNullOrWhiteSpace(wsUrl))
            {
                using var putReq = new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{CdpDebugPort}/json/new?{Uri.EscapeDataString(url)}");
                using var putResp = await client.SendAsync(putReq, ct);
                if (!putResp.IsSuccessStatusCode)
                    return ("error", $"Failed to open tab via CDP ({(int)putResp.StatusCode})");

                var tabJson = await putResp.Content.ReadAsStringAsync(ct);
                using var tabDoc = JsonDocument.Parse(tabJson);
                wsUrl = tabDoc.RootElement.GetProperty("webSocketDebuggerUrl").GetString();
                tabId = tabDoc.RootElement.GetProperty("id").GetString();
                createdNewTab = true;
                await Task.Delay(2500, ct);
            }

            if (string.IsNullOrWhiteSpace(wsUrl))
                return ("error", "CDP did not return webSocketDebuggerUrl.");

            try
            {
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(wsUrl), ct);

                var escapedSel = JsonSerializer.Serialize(cssSelector);
                var expr = $$"""
                    (() => {
                      const href = location.href || '';
                      if (/\/login|accounts\.google\.com\/v3\/signin|\/i\/flow\/login/i.test(href)) return { auth: true, text: href };
                      const el = document.querySelector({{escapedSel}});
                      return { auth: false, text: el ? (el.innerText || el.textContent || '').trim() : '' };
                    })()
                    """;

                var payload = JsonSerializer.Serialize(new
                {
                    id = 1,
                    method = "Runtime.evaluate",
                    @params = new { expression = expr, returnByValue = true }
                });
                await ws.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, ct);

                var buf = new byte[16384];
                var sb = new StringBuilder();
                while (true)
                {
                    var res = await ws.ReceiveAsync(buf, ct);
                    sb.Append(Encoding.UTF8.GetString(buf, 0, res.Count));
                    if (res.EndOfMessage) break;
                }

                using var evalDoc = JsonDocument.Parse(sb.ToString());
                var val = evalDoc.RootElement
                    .GetProperty("result")
                    .GetProperty("result")
                    .GetProperty("value");

                if (val.TryGetProperty("auth", out var authEl) && authEl.GetBoolean())
                    return ("needs_auth", val.GetProperty("text").GetString() ?? "login required");

                var text = val.TryGetProperty("text", out var txtEl) ? txtEl.GetString() ?? "" : "";
                return ("ok", text);
            }
            finally
            {
                if (createdNewTab && !string.IsNullOrWhiteSpace(tabId))
                {
                    try { await client.GetAsync($"http://127.0.0.1:{CdpDebugPort}/json/close/{tabId}", CancellationToken.None); }
                    catch { }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "CDP read failed for {Url}", url);
            return ("error", ex.Message);
        }
    }

    private async Task<bool> IsCdpReachableAsync(CancellationToken ct)
    {
        try
        {
            var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromMilliseconds(800);
            using var resp = await client.GetAsync($"http://127.0.0.1:{CdpDebugPort}/json/version", ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task OpenTabViaCdpAsync(string url, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{CdpDebugPort}/json/new?{Uri.EscapeDataString(url)}");
        await client.SendAsync(req, ct);
    }

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
            _process?.Dispose();
        }
        catch { }
        _launchLock.Dispose();
    }
}
