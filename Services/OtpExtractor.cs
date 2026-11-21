using System.Text.RegularExpressions;
using AiSecretary.Desktop.Models;

namespace AiSecretary.Desktop.Services;

/// <summary>
/// Zero-LLM deterministic OTP code extractor and registrable domain parser (handling two-level public suffixes like .com.tw / .co.jp).
/// </summary>
public static class OtpExtractor
{
    // Common two-level public suffixes (effective TLDs) so auth.pchome.com.tw -> pchome.com.tw
    private static readonly HashSet<string> TwoLevelPublicSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "com.tw", "org.tw", "net.tw", "edu.tw", "gov.tw", "idv.tw", "game.tw", "ebiz.tw", "club.tw",
        "co.jp", "ne.jp", "or.jp", "go.jp", "ac.jp", "ed.jp", "gr.jp",
        "co.uk", "org.uk", "gov.uk", "ac.uk", "me.uk", "net.uk",
        "com.hk", "org.hk", "net.hk", "edu.hk", "gov.hk",
        "com.au", "net.au", "org.au", "edu.au", "gov.au",
        "com.sg", "org.sg", "net.sg", "edu.sg", "gov.sg",
        "com.cn", "net.cn", "org.cn", "gov.cn", "edu.cn",
        "co.kr", "or.kr", "ne.kr", "go.kr", "ac.kr",
        "co.nz", "org.nz", "net.nz", "govt.nz",
        "com.br", "com.my", "com.mx", "com.tr", "co.in", "co.id", "co.th", "com.vn", "com.ph"
    };

    private static readonly Regex OtpContextRegex = new(
        @"(驗證碼|認證碼|動態密碼|安全碼|一次性密碼|一次密碼|登入碼|確認碼|確認コード|認証コード|ワンタイム|verification\s*code|security\s*code|one[- ]?time\s*(?:password|passcode|code)|passcode|login\s*code|sign[- ]?in\s*code|\botp\b|\b2fa\b|\bmfa\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Explicit "label: 123456" pattern (highest confidence)
    private static readonly Regex LabeledCodeRegex = new(
        @"(?:驗證碼|認證碼|動態密碼|安全碼|確認碼|確認コード|認証コード|verification\s*code|security\s*code|one[- ]?time\s*(?:password|passcode|code)|passcode|login\s*code|code|\botp\b)[\s:：為是is\(\[【「]*([0-9]{4,8})(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Fallback standalone 6-digit (or 4-8 digit) code on its own line or surrounded by whitespace
    private static readonly Regex StandaloneCodeRegex = new(
        @"(?<![0-9A-Za-z\-/.])([0-9]{6})(?![0-9A-Za-z\-/.])",
        RegexOptions.Compiled);

    public static bool TryExtractFromMail(MailItem mail, out string code)
    {
        code = "";
        if (mail is null) return false;

        var combined = $"{mail.Subject}\n{mail.Snippet}\n{mail.Body}";
        if (!OtpContextRegex.IsMatch(combined))
            return false;

        foreach (Match m in LabeledCodeRegex.Matches(combined))
        {
            var cand = m.Groups[1].Value;
            if (!LooksLikeYearOrNoise(cand, combined, m.Index))
            {
                code = cand;
                return true;
            }
        }

        foreach (Match m in StandaloneCodeRegex.Matches(combined))
        {
            var cand = m.Groups[1].Value;
            if (!LooksLikeYearOrNoise(cand, combined, m.Index))
            {
                code = cand;
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeYearOrNoise(string digits, string text, int matchIndex)
    {
        if (digits.Length == 4 && int.TryParse(digits, out var yr) && yr is >= 1990 and <= 2099)
            return true;

        // Avoid matching amounts like "$123456" or "NT$123456"
        if (matchIndex > 0)
        {
            var prefix = text[Math.Max(0, matchIndex - 4)..matchIndex];
            if (prefix.Contains('$') || prefix.Contains('¥') || prefix.Contains('€') || prefix.Contains("NT", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static string ExtractSenderDomain(string? senderEmail)
    {
        var raw = (senderEmail ?? "").Trim();
        var at = raw.LastIndexOf('@');
        if (at < 0 || at >= raw.Length - 1) return "";
        var domain = raw[(at + 1)..].Trim().TrimEnd('>', ')', ';', ',').ToLowerInvariant();
        return domain;
    }

    public static string ExtractHostFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return (uri.Host ?? "").Trim().ToLowerInvariant();
        return "";
    }

    /// <summary>
    /// Extracts the registrable domain (eTLD+1) from a host, URL, or email address,
    /// properly handling two-level suffixes such as <c>.com.tw</c>, <c>.co.jp</c>, <c>.co.uk</c>.
    /// </summary>
    public static string ExtractRegistrableDomain(string? hostOrUrlOrEmail)
    {
        if (string.IsNullOrWhiteSpace(hostOrUrlOrEmail)) return "";
        var input = hostOrUrlOrEmail.Trim();

        string host;
        if (input.Contains('@'))
            host = ExtractSenderDomain(input);
        else if (input.Contains("://", StringComparison.Ordinal))
            host = ExtractHostFromUrl(input);
        else
            host = input.ToLowerInvariant().TrimEnd('.');

        if (string.IsNullOrWhiteSpace(host) || host == "localhost" || System.Net.IPAddress.TryParse(host, out _))
            return host;

        var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 2)
            return string.Join('.', parts);

        var lastTwo = $"{parts[^2]}.{parts[^1]}";
        if (TwoLevelPublicSuffixes.Contains(lastTwo))
            return $"{parts[^3]}.{lastTwo}";

        return lastTwo;
    }

    /// <summary>
    /// Extracts the core brand label from a registrable domain (e.g., <c>tenso.com</c> -&gt; <c>tenso</c>,
    /// <c>pchome.com.tw</c> -&gt; <c>pchome</c>).
    /// </summary>
    public static string ExtractDomainStem(string? registrableDomain)
    {
        if (string.IsNullOrWhiteSpace(registrableDomain)) return "";
        var dot = registrableDomain.IndexOf('.');
        return dot > 0 ? registrableDomain[..dot].ToLowerInvariant() : registrableDomain.ToLowerInvariant();
    }
}
