using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using AiSecretary.Desktop.Agent;
using UglyToad.PdfPig;

namespace AiSecretary.Desktop.Skills;

/// <summary>
/// 純 C# 安全讀取公開網頁與線上 PDF 文字內容的技能（內建本機與私有網段 SSRF 阻擋）。
/// </summary>
public sealed class WebFetchSkill : IBasicSkill
{
    private const int DefaultMaxTextBytes = 512 * 1024;
    private const int MaxPdfBytes = 15 * 1024 * 1024;
    private const int DefaultMaxChars = 10000;

    private readonly GuardService _guard;
    private readonly IHttpClientFactory _httpFactory;

    public WebFetchSkill(GuardService guard, IHttpClientFactory httpFactory)
    {
        _guard = guard;
        _httpFactory = httpFactory;
    }

    public string Name => "web_fetch";
    public string DisplayDescription => "Safely fetch plain text from public web pages and PDFs";
    public string StatusText => "Fetching web content...";
    public string MainAgentSpec => """
            web_fetch
               - Purpose: Safely fetch plain text from a public http/https URL, including automatic PDF text extraction (SSRF-protected, no PowerShell).
               - Args: {"url": "https://...", "start_page": 1, "end_page": 10, "max_chars": 10000} (start_page/end_page optional for PDFs)
            
        """;
    public string PlannerSpec => """web_fetch: Fetch plain text from public URL or online PDF (auto-extracts PDF text via PdfPig). args: {"url":"https://...","start_page":1,"end_page":10,"max_chars":10000}""";

    public async Task ExecuteAsync(SkillExecutionContext ctx)
    {
        var skillName = ctx.EffectiveName(Name);
        var url = ctx.GetArg("url") ?? "";
        int? startPage = int.TryParse(ctx.GetArg("start_page"), out var sp) && sp > 0 ? sp : null;
        int? endPage = int.TryParse(ctx.GetArg("end_page"), out var ep) && ep > 0 ? ep : null;
        int maxChars = int.TryParse(ctx.GetArg("max_chars"), out var mc) ? Math.Clamp(mc, 1000, 20000) : DefaultMaxChars;

        await ctx.OnStatusAsync(await ctx.TextAsync($"skill_status:{skillName}", StatusText));
        var res = await WebFetchAsync(url, ctx.Ct, skillName, ctx, startPage, endPage, maxChars);
        ctx.Observations.Add(res);
        if (ctx.Outcome == StepOutcome.Ok)
            ctx.CompletedSteps.Add(skillName);
    }

    public async Task<string> WebFetchAsync(
        string url,
        CancellationToken ct = default,
        string? skillName = null,
        SkillExecutionContext? ctx = null,
        int? startPage = null,
        int? endPage = null,
        int maxChars = DefaultMaxChars)
    {
        var check = _guard.CheckSkillExecution(string.IsNullOrWhiteSpace(skillName) ? Name : skillName);
        if (!check.Allowed) throw new UnauthorizedAccessException(check.Reason);

        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ctx?.MarkFailed();
            return "web_fetch: only http:// and https:// URLs are supported.";
        }

        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            ctx?.MarkDenied();
            return "web_fetch: local/intranet host access is blocked.";
        }

        try
        {
            var addrs = await Dns.GetHostAddressesAsync(uri.Host, ct);
            if (addrs.Any(IsPrivateOrLoopback))
            {
                ctx?.MarkDenied();
                return "web_fetch: private/loopback IP access is blocked.";
            }
        }
        catch (Exception ex)
        {
            ctx?.MarkFailed();
            return $"web_fetch: DNS resolution failed: {ex.Message}";
        }

        var client = _httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.TryAddWithoutValidation("User-Agent", "AiSecretary/1.0");

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            ctx?.MarkFailed();
            return $"web_fetch failed: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
        }

        var mediaType = resp.Content.Headers.ContentType?.MediaType?.Trim().ToLowerInvariant() ?? "";
        bool expectPdf = IsPdfHint(mediaType, uri);
        int byteLimit = expectPdf ? MaxPdfBytes : DefaultMaxTextBytes;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[16384];
        int total = 0;
        bool exceededCap = false;

        while (true)
        {
            int toRead = Math.Min(buffer.Length, byteLimit - total);
            if (toRead <= 0)
            {
                // Peek 1 more byte to detect whether content exceeded byteLimit
                if (await stream.ReadAsync(buffer.AsMemory(0, 1), ct) > 0)
                    exceededCap = true;
                break;
            }
            int read = await stream.ReadAsync(buffer.AsMemory(0, toRead), ct);
            if (read == 0) break;
            ms.Write(buffer, 0, read);
            total += read;

            // If server served application/octet-stream or missing Content-Type, upgrade cap immediately upon seeing %PDF- magic bytes!
            if (!expectPdf && total >= 5 && HasPdfMagicHeader(ms.GetBuffer(), total))
            {
                expectPdf = true;
                byteLimit = MaxPdfBytes;
            }
        }

        var bytes = ms.ToArray();
        if (expectPdf && exceededCap)
        {
            ctx?.MarkFailed();
            return $"web_fetch: PDF exceeds {MaxPdfBytes / (1024 * 1024)} MB download limit.";
        }

        var (ok, output) = ExtractTextFromPayload(bytes, mediaType, uri, startPage, endPage, Math.Clamp(maxChars, 1000, 20000));
        if (!ok)
            ctx?.MarkFailed();
        return output;
    }

    internal static bool IsPdfHint(string? mediaType, Uri? uri)
    {
        if (!string.IsNullOrWhiteSpace(mediaType) &&
            (mediaType.Contains("pdf", StringComparison.OrdinalIgnoreCase)))
            return true;
        return uri is not null && uri.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool HasPdfMagicHeader(byte[] bytes, int length)
    {
        if (bytes is null || length < 5) return false;
        // Skip optional leading whitespace / UTF-8 BOM
        int i = 0;
        if (length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            i = 3;
        while (i < length && i < 16 && (bytes[i] == (byte)' ' || bytes[i] == (byte)'\r' || bytes[i] == (byte)'\n' || bytes[i] == (byte)'\t'))
            i++;
        if (i + 5 > length) return false;
        return bytes[i] == (byte)'%' &&
               bytes[i + 1] == (byte)'P' &&
               bytes[i + 2] == (byte)'D' &&
               bytes[i + 3] == (byte)'F' &&
               bytes[i + 4] == (byte)'-';
    }

    internal static (bool Ok, string Text) ExtractTextFromPayload(
        byte[] bytes,
        string? mediaType,
        Uri? uri = null,
        int? startPage = null,
        int? endPage = null,
        int maxChars = DefaultMaxChars)
    {
        if (bytes is null || bytes.Length == 0)
            return (true, "(empty response)");

        if (HasPdfMagicHeader(bytes, bytes.Length) || IsPdfHint(mediaType, uri))
            return ExtractPdfBytes(bytes, startPage, endPage, maxChars);

        if (LooksLikeBinaryPayload(bytes, mediaType))
        {
            var typeLabel = string.IsNullOrWhiteSpace(mediaType) ? "binary" : mediaType;
            return (false, $"web_fetch: binary content ({typeLabel}, {bytes.Length:N0} bytes) cannot be read as plain text.");
        }

        var raw = Encoding.UTF8.GetString(bytes);
        var text = Regex.Replace(raw, @"<(script|style|noscript)\b[^>]*>[\s\S]*?</\1>", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"\s+", " ").Trim();

        var textLimit = Math.Min(maxChars, 12000);
        if (text.Length > textLimit)
            text = text[..textLimit] + "\n...(truncated)";
        return (true, string.IsNullOrWhiteSpace(text) ? "(no readable plain text)" : text);
    }

    private static (bool Ok, string Text) ExtractPdfBytes(byte[] bytes, int? startPage, int? endPage, int maxChars)
    {
        try
        {
            using var doc = PdfDocument.Open(bytes);
            int totalPages = doc.NumberOfPages;
            if (totalPages <= 0)
                return (true, "(PDF has 0 pages)");

            int from = Math.Clamp(startPage ?? 1, 1, totalPages);
            int to = Math.Clamp(endPage ?? totalPages, from, totalPages);

            var sb = new StringBuilder();
            int lastShownPage = from - 1;
            bool truncated = false;

            for (int p = from; p <= to; p++)
            {
                var page = doc.GetPage(p);
                var pageText = (page.Text ?? "").Trim();
                var block = string.IsNullOrWhiteSpace(pageText)
                    ? $"--- Page {p} ---\n(no embedded text; possibly scanned image)\n"
                    : $"--- Page {p} ---\n{pageText}\n";

                if (sb.Length + block.Length > maxChars && sb.Length > 0)
                {
                    var remaining = maxChars - sb.Length;
                    if (remaining > 120)
                    {
                        sb.Append(block[..remaining]);
                        sb.AppendLine("\n...(truncated)");
                        lastShownPage = p;
                    }
                    truncated = true;
                    break;
                }

                sb.AppendLine(block.TrimEnd());
                lastShownPage = p;
            }

            if (sb.Length == 0)
                return (true, $"PDF fetched ({totalPages} page(s)), but no selectable text was found (likely scanned/image-only PDF).");

            var moreHint = (truncated || lastShownPage < totalPages)
                ? $" [truncated; call web_fetch again with start_page={Math.Min(totalPages, lastShownPage + 1)} to continue]"
                : "";
            var header = $"PDF extracted ({totalPages} page(s), showing pages {from}-{Math.Max(from, lastShownPage)}){moreHint}:\n";
            return (true, header + sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            return (false, $"web_fetch: failed to parse PDF ({ex.Message}).");
        }
    }

    private static bool LooksLikeBinaryPayload(byte[] bytes, string? mediaType)
    {
        if (!string.IsNullOrWhiteSpace(mediaType))
        {
            if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
                mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
                mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
                mediaType.Contains("zip", StringComparison.OrdinalIgnoreCase) ||
                mediaType.Contains("octet-stream", StringComparison.OrdinalIgnoreCase))
            {
                // Still check for null bytes for octet-stream in case a misconfigured server serves plain text as octet-stream
                if (!mediaType.Contains("octet-stream", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        int sampleLen = Math.Min(bytes.Length, 4096);
        int controlCount = 0;
        for (int i = 0; i < sampleLen; i++)
        {
            byte b = bytes[i];
            if (b == 0) return true;
            if (b < 0x09 || (b > 0x0D && b < 0x20))
                controlCount++;
        }
        return sampleLen > 32 && (double)controlCount / sampleLen > 0.10;
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
