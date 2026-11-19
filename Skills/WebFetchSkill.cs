using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using AiSecretary.Desktop.Agent;

namespace AiSecretary.Desktop.Skills;

/// <summary>
/// 純 C# 安全讀取公開網頁文字內容的技能（內建本機與私有網段 SSRF 阻擋）。
/// </summary>
public sealed class WebFetchSkill : IBasicSkill
{
    private readonly GuardService _guard;
    private readonly IHttpClientFactory _httpFactory;

    public WebFetchSkill(GuardService guard, IHttpClientFactory httpFactory)
    {
        _guard = guard;
        _httpFactory = httpFactory;
    }

    public string Name => "web_fetch";
    public string DisplayDescription => "Safely fetch plain text from public web pages";
    public string StatusText => "Fetching web page...";
    public string MainAgentSpec => """
            web_fetch
               - Purpose: Safely fetch plain text from a public http/https URL (SSRF-protected, no PowerShell).
               - Args: {"url": "https://..."}
            
        """;
    public string PlannerSpec => """web_fetch: Fetch plain text from public URL. args: {"url":"https://..."}""";

    public async Task ExecuteAsync(SkillExecutionContext ctx)
    {
        var skillName = ctx.EffectiveName(Name);
        var url = ctx.GetArg("url") ?? "";
        await ctx.OnStatusAsync(await ctx.TextAsync($"skill_status:{skillName}", StatusText));
        var res = await WebFetchAsync(url, ctx.Ct, skillName);
        ctx.Observations.Add(res);
        ctx.CompletedSteps.Add(skillName);
    }

    public async Task<string> WebFetchAsync(string url, CancellationToken ct = default, string? skillName = null)
    {
        var check = _guard.CheckSkillExecution(string.IsNullOrWhiteSpace(skillName) ? Name : skillName);
        if (!check.Allowed) throw new UnauthorizedAccessException(check.Reason);

        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "web_fetch: only http:// and https:// URLs are supported.";
        }

        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return "web_fetch: local/intranet host access is blocked.";
        }

        try
        {
            var addrs = await Dns.GetHostAddressesAsync(uri.Host, ct);
            if (addrs.Any(IsPrivateOrLoopback))
                return "web_fetch: private/loopback IP access is blocked.";
        }
        catch (Exception ex)
        {
            return $"web_fetch: DNS resolution failed: {ex.Message}";
        }

        var client = _httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.TryAddWithoutValidation("User-Agent", "AiSecretary/1.0");

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            return $"web_fetch failed: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int total = 0;
        const int maxBytes = 512 * 1024;
        while (total < maxBytes)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maxBytes - total)), ct);
            if (read == 0) break;
            ms.Write(buffer, 0, read);
            total += read;
        }

        var raw = Encoding.UTF8.GetString(ms.ToArray());
        var text = Regex.Replace(raw, @"<(script|style|noscript)\b[^>]*>[\s\S]*?</\1>", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"\s+", " ").Trim();

        if (text.Length > 4000)
            text = text[..4000] + "\n...(truncated)";
        return string.IsNullOrWhiteSpace(text) ? "(no readable plain text)" : text;
    }

    internal static bool IsPrivateOrLoopback(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10 ||
                   b[0] == 127 ||
                   (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                   (b[0] == 192 && b[1] == 168) ||
                   (b[0] == 169 && b[1] == 254) ||
                   b[0] == 0;
        }
        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast;
    }
}
