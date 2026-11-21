using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Models;

namespace AiSecretary.Desktop.Services;

/// <summary>
/// Gmail REST API v1 client exposed as Agent Skills.
/// Supports Google OAuth2 Desktop credentials (gmail_credentials.json or Gmail:ClientId/ClientSecret)
/// and persists refresh tokens in gmail_token.json.
/// </summary>
public sealed class GmailSkillService
{
    private const string Scope = "https://www.googleapis.com/auth/gmail.readonly";
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly GuardService _guard;
    private readonly UiLocalizer _localizer;
    private readonly ILogger<GmailSkillService> _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private GmailTokenState? _cachedToken;

    public GmailSkillService(IHttpClientFactory httpFactory, IConfiguration config, GuardService guard, UiLocalizer localizer, ILogger<GmailSkillService> logger)
    {
        _httpFactory = httpFactory;
        _config = config;
        _guard = guard;
        _localizer = localizer;
        _logger = logger;
    }

    private string TokenFilePath
    {
        get
        {
            var configured = _config.GetValue<string>("Gmail:TokenPath");
            return string.IsNullOrWhiteSpace(configured) ||
                   configured.Trim().Equals("gmail_token.json", StringComparison.OrdinalIgnoreCase)
                ? AppPaths.GmailTokenPath
                : ResolvePath(configured);
        }
    }
    private string CredentialsFilePath
    {
        get
        {
            var configured = _config.GetValue<string>("Gmail:CredentialsPath");
            if (string.IsNullOrWhiteSpace(configured) ||
                configured.Trim().Equals("gmail_credentials.json", StringComparison.OrdinalIgnoreCase))
            {
                var userCreds = Path.Combine(AppPaths.UserDataRoot, "gmail_credentials.json");
                if (File.Exists(userCreds)) return userCreds;
                var baseCreds = Path.Combine(AppContext.BaseDirectory, "gmail_credentials.json");
                if (File.Exists(baseCreds)) return baseCreds;

                // Fallback: walk up from BaseDirectory (e.g. bin/Release/net8.0-windows -> repo root)
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                for (int i = 0; i < 4 && dir is not null; i++, dir = dir.Parent)
                {
                    var candidate = Path.Combine(dir.FullName, "gmail_credentials.json");
                    if (File.Exists(candidate)) return candidate;
                }
                return userCreds;
            }
            return ResolvePath(configured);
        }
    }

    public bool HasClientCredentials => TryLoadClientCredentials(out _, out _);

    public void SaveClientCredentials(string clientId, string? clientSecret = null)
    {
        var payload = new
        {
            installed = new
            {
                client_id = clientId.Trim(),
                client_secret = clientSecret?.Trim() ?? ""
            }
        };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(CredentialsFilePath, SecretProtector.Protect(json));
    }

    public bool IsAuthorized
    {
        get
        {
            var tok = LoadTokenState();
            return tok is not null && (!string.IsNullOrWhiteSpace(tok.RefreshToken) || !string.IsNullOrWhiteSpace(tok.AccessToken));
        }
    }

    public async Task<bool> AuthorizeInteractiveAsync(CancellationToken ct = default)
    {
        if (!TryLoadClientCredentials(out var clientId, out var clientSecret))
        {
            throw new InvalidOperationException(
                await _localizer.TextAsync("GmailMissingCreds", "Gmail OAuth credentials are not configured. Set Gmail__ClientId / Gmail__ClientSecret in .env or place gmail_credentials.json in the app data folder.", Array.Empty<object?>(), ct));
        }

        var port = GetAvailablePort();
        var redirectUri = $"http://127.0.0.1:{port}/";
        var state = Guid.NewGuid().ToString("N");

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();

        var authUrl = "https://accounts.google.com/o/oauth2/v2/auth"
            + $"?client_id={Uri.EscapeDataString(clientId)}"
            + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
            + "&response_type=code"
            + $"&scope={Uri.EscapeDataString(Scope)}"
            + "&access_type=offline"
            + "&prompt=consent"
            + $"&state={Uri.EscapeDataString(state)}";

        Process.Start(new ProcessStartInfo { FileName = authUrl, UseShellExecute = true });

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));

        var ctxTask = listener.GetContextAsync();
        using var reg = timeoutCts.Token.Register(() => listener.Stop());
        var ctx = await ctxTask;

        var code = ctx.Request.QueryString["code"];
        var returnedState = ctx.Request.QueryString["state"];
        var error = ctx.Request.QueryString["error"];

        var html = string.IsNullOrWhiteSpace(code) || !string.IsNullOrWhiteSpace(error)
            ? await _localizer.TextAsync("GmailAuthFailedHtml", "<html><body style='font-family:sans-serif;padding:24px'><h2>Gmail authorization failed or canceled</h2><p>Please close this tab and try again.</p></body></html>", Array.Empty<object?>(), ct)
            : await _localizer.TextAsync("GmailAuthSuccessHtml", "<html><body style='font-family:sans-serif;padding:24px'><h2>Gmail authorized!</h2><p>You can close this tab and return to the desktop assistant.</p></body></html>", Array.Empty<object?>(), ct);

        var buffer = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = buffer.Length;
        await ctx.Response.OutputStream.WriteAsync(buffer, ct);
        ctx.Response.Close();
        listener.Stop();

        if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(code) || returnedState != state)
        {
            return false;
        }

        var http = _httpFactory.CreateClient();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code"
        });

        using var resp = await http.PostAsync("https://oauth2.googleapis.com/token", form, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Gmail OAuth token exchange failed ({(int)resp.StatusCode}): {json}");
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var accessToken = root.GetProperty("access_token").GetString() ?? "";
        var refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "";
        var expiresIn = root.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;

        var tokenState = new GmailTokenState(accessToken, refreshToken, DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60));
        SaveTokenState(tokenState);
        return true;
    }

    public async Task<IReadOnlyList<MailItem>> ReadInboxAsync(string? query = null, int? maxResults = null, CancellationToken ct = default)
    {
        var token = await EnsureAccessTokenAsync(ct);
        var limit = Math.Clamp(maxResults ?? _config.GetValue<int>("Gmail:MaxResults", 10), 1, 25);

        var url = $"https://gmail.googleapis.com/gmail/v1/users/me/messages?maxResults={limit}";
        if (!string.IsNullOrWhiteSpace(query))
        {
            url += $"&q={Uri.EscapeDataString(query)}";
        }

        var apiGuard = _guard.CheckRestApiAccess("gmail.messages.list", "GET", url);
        if (!apiGuard.Allowed) throw new UnauthorizedAccessException(apiGuard.Reason);

        var http = _httpFactory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        reqCts.CancelAfter(TimeSpan.FromSeconds(20));

        using var resp = await http.SendAsync(req, reqCts.Token);
        var json = await resp.Content.ReadAsStringAsync(reqCts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Gmail list fetch failed ({(int)resp.StatusCode}): {json}");
        }

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("messages", out var msgsEl) || msgsEl.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<MailItem>();
        }

        var ids = msgsEl.EnumerateArray()
            .Select(m => m.TryGetProperty("id", out var idEl) ? idEl.GetString() : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .Take(limit)
            .ToList();

        var results = new List<MailItem>();
        foreach (var id in ids)
        {
            var detail = await GetMailDetailAsync(id, ct);
            if (detail is not null)
            {
                results.Add(detail);
            }
        }
        return results;
    }

    public async Task<MailItem?> GetMailDetailAsync(string messageId, CancellationToken ct = default)
    {
        var rawId = messageId.StartsWith("gmail:", StringComparison.OrdinalIgnoreCase)
            ? messageId["gmail:".Length..]
            : messageId;

        if (string.IsNullOrWhiteSpace(rawId)) return null;

        var token = await EnsureAccessTokenAsync(ct);
        var url = $"https://gmail.googleapis.com/gmail/v1/users/me/messages/{Uri.EscapeDataString(rawId)}?format=full";

        var apiGuard = _guard.CheckRestApiAccess("gmail.messages.get", "GET", url);
        if (!apiGuard.Allowed) throw new UnauthorizedAccessException(apiGuard.Reason);

        var http = _httpFactory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        reqCts.CancelAfter(TimeSpan.FromSeconds(20));

        using var resp = await http.SendAsync(req, reqCts.Token);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;

        var json = await resp.Content.ReadAsStringAsync(reqCts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Gmail message fetch failed ({(int)resp.StatusCode}): {json}");
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var threadId = root.TryGetProperty("threadId", out var tid) ? tid.GetString() ?? rawId : rawId;
        var snippet = root.TryGetProperty("snippet", out var sn) ? WebUtility.HtmlDecode(sn.GetString() ?? "") : "";

        var isRead = true;
        var importance = MailImportance.Normal;
        if (root.TryGetProperty("labelIds", out var labels) && labels.ValueKind == JsonValueKind.Array)
        {
            foreach (var lbl in labels.EnumerateArray())
            {
                var name = lbl.GetString();
                if (name == "UNREAD") isRead = false;
                if (name == "IMPORTANT") importance = MailImportance.High;
            }
        }

        var subject = "(no subject)";
        var fromRaw = "";
        var received = DateTimeOffset.UtcNow;
        var body = "";

        if (root.TryGetProperty("internalDate", out var internalDateEl) &&
            long.TryParse(internalDateEl.GetString(), out var epochMs))
        {
            received = DateTimeOffset.FromUnixTimeMilliseconds(epochMs);
        }

        if (root.TryGetProperty("payload", out var payload))
        {
            if (payload.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Array)
            {
                foreach (var h in headers.EnumerateArray())
                {
                    var name = h.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var val = h.TryGetProperty("value", out var v) ? v.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name) || val is null) continue;

                    if (name.Equals("Subject", StringComparison.OrdinalIgnoreCase))
                        subject = val;
                    else if (name.Equals("From", StringComparison.OrdinalIgnoreCase))
                        fromRaw = val;
                    else if (name.Equals("Date", StringComparison.OrdinalIgnoreCase) && DateTimeOffset.TryParse(val, out var dto))
                        received = dto;
                }
            }

            body = ExtractBodyText(payload);
        }

        if (string.IsNullOrWhiteSpace(body)) body = snippet;
        var (senderName, senderEmail) = ParseFromHeader(fromRaw);

        return new MailItem(
            Id: $"gmail:{rawId}",
            Subject: subject,
            SenderName: senderName,
            SenderEmail: senderEmail,
            ReceivedAt: received,
            Importance: importance,
            Snippet: snippet,
            Body: body,
            IsRead: isRead,
            ThreadId: threadId);
    }

    private async Task<string> EnsureAccessTokenAsync(CancellationToken ct, bool forceRefresh = false)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            var directAccess = _config.GetValue<string>("Gmail:AccessToken");
            if (!string.IsNullOrWhiteSpace(directAccess))
            {
                return directAccess.Trim();
            }

            var token = LoadTokenState();
            if (!forceRefresh && token is not null && !string.IsNullOrWhiteSpace(token.AccessToken) && token.ExpiresAt > DateTimeOffset.UtcNow)
            {
                return token.AccessToken;
            }

            var refreshToken = token?.RefreshToken;
            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                refreshToken = _config.GetValue<string>("Gmail:RefreshToken");
            }

            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                throw new InvalidOperationException(
                    await _localizer.TextAsync("GmailNotAuthorized", "Gmail is not authorized. Please connect in Settings -> Skills -> Mail, or set Gmail__RefreshToken / Gmail__AccessToken in .env.", Array.Empty<object?>(), ct));
            }

            if (!TryLoadClientCredentials(out var clientId, out var clientSecret))
            {
                throw new InvalidOperationException(
                    await _localizer.TextAsync("GmailMissingClientForRefresh", "Missing Gmail ClientId / ClientSecret; cannot refresh Access Token.", Array.Empty<object?>(), ct));
            }

            var http = _httpFactory.CreateClient();
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token"
            });

            using var resp = await http.PostAsync("https://oauth2.googleapis.com/token", form, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Gmail token refresh failed ({(int)resp.StatusCode}): {json}");
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var newAccess = root.GetProperty("access_token").GetString() ?? "";
            var rotatedRefresh = root.TryGetProperty("refresh_token", out var rrt) && !string.IsNullOrWhiteSpace(rrt.GetString())
                ? rrt.GetString()!
                : refreshToken;
            var expiresIn = root.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;

            var updated = new GmailTokenState(newAccess, rotatedRefresh, DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60));
            SaveTokenState(updated);
            return newAccess;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private bool TryLoadClientCredentials(out string clientId, out string clientSecret)
    {
        clientId = _config.GetValue<string>("Gmail:ClientId")?.Trim() ?? "";
        clientSecret = _config.GetValue<string>("Gmail:ClientSecret")?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret))
        {
            return true;
        }

        if (File.Exists(CredentialsFilePath))
        {
            try
            {
                var raw = File.ReadAllText(CredentialsFilePath);
                var json = SecretProtector.Unprotect(raw);
                // Auto-encrypt legacy plaintext credentials on disk with DPAPI
                if (!raw.StartsWith("dpapi:", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(json))
                {
                    try { File.WriteAllText(CredentialsFilePath, SecretProtector.Protect(json)); } catch { }
                }

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                JsonElement section;
                if (root.TryGetProperty("installed", out section) || root.TryGetProperty("web", out section))
                {
                    clientId = section.TryGetProperty("client_id", out var cid) ? cid.GetString() ?? "" : "";
                    clientSecret = section.TryGetProperty("client_secret", out var cs) ? cs.GetString() ?? "" : "";
                    return !string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse {Path}", CredentialsFilePath);
            }
        }

        return false;
    }

    private GmailTokenState? LoadTokenState()
    {
        if (_cachedToken is not null) return _cachedToken;
        if (!File.Exists(TokenFilePath)) return null;
        try
        {
            var raw = File.ReadAllText(TokenFilePath);
            var json = SecretProtector.Unprotect(raw);
            // Auto-encrypt legacy plaintext token file on disk with DPAPI
            if (!raw.StartsWith("dpapi:", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(json))
            {
                try { File.WriteAllText(TokenFilePath, SecretProtector.Protect(json)); } catch { }
            }
            _cachedToken = JsonSerializer.Deserialize<GmailTokenState>(json);
            return _cachedToken;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read Gmail token file");
            return null;
        }
    }

    private void SaveTokenState(GmailTokenState state)
    {
        _cachedToken = state;
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(TokenFilePath, SecretProtector.Protect(json));
    }

    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        var userCandidate = Path.Combine(AppPaths.UserDataRoot, path);
        if (File.Exists(userCandidate)) return userCandidate;
        var baseCandidate = Path.Combine(AppContext.BaseDirectory, path);
        if (File.Exists(baseCandidate)) return baseCandidate;
        var cwdCandidate = Path.Combine(Directory.GetCurrentDirectory(), path);
        if (File.Exists(cwdCandidate)) return cwdCandidate;
        return userCandidate;
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static (string Name, string Email) ParseFromHeader(string fromRaw)
    {
        if (string.IsNullOrWhiteSpace(fromRaw)) return ("Unknown sender", "");
        var match = Regex.Match(fromRaw, @"^(?<name>.*?)<(?<email>[^>]+)>$");
        if (match.Success)
        {
            var name = match.Groups["name"].Value.Trim().Trim('"');
            var email = match.Groups["email"].Value.Trim();
            return (string.IsNullOrWhiteSpace(name) ? email : name, email);
        }
        return (fromRaw.Trim(), fromRaw.Trim());
    }

    private static string ExtractBodyText(JsonElement payload)
    {
        var plain = FindPartData(payload, "text/plain");
        if (!string.IsNullOrWhiteSpace(plain)) return plain.Trim();

        var html = FindPartData(payload, "text/html");
        if (!string.IsNullOrWhiteSpace(html))
        {
            var stripped = Regex.Replace(html, "<[^>]+>", " ");
            return WebUtility.HtmlDecode(stripped).Trim();
        }
        return "";
    }

    private static string? FindPartData(JsonElement part, string targetMime)
    {
        var mime = part.TryGetProperty("mimeType", out var m) ? m.GetString() : null;
        if (mime is not null && mime.Equals(targetMime, StringComparison.OrdinalIgnoreCase) &&
            part.TryGetProperty("body", out var body) &&
            body.TryGetProperty("data", out var dataEl))
        {
            var encoded = dataEl.GetString();
            if (!string.IsNullOrWhiteSpace(encoded))
            {
                return DecodeBase64Url(encoded);
            }
        }

        if (part.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in parts.EnumerateArray())
            {
                var found = FindPartData(child, targetMime);
                if (!string.IsNullOrWhiteSpace(found)) return found;
            }
        }
        return null;
    }

    private static string DecodeBase64Url(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        var bytes = Convert.FromBase64String(s);
        return Encoding.UTF8.GetString(bytes);
    }

    private sealed record GmailTokenState(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);
}
