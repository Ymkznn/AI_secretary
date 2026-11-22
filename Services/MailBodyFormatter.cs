using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace AiSecretary.Desktop.Services;

/// <summary>
/// Converts raw HTML or messy plain-text email bodies into clean, readable plain text
/// (stripping CSS/scripts/comments/zero-width preview spacers and preserving paragraphs, lists, and table columns).
/// </summary>
public static class MailBodyFormatter
{
    private static readonly Regex StripHiddenBlocksRegex = new(
        @"<(head|style|script|noscript|title|svg|template)\b[^>]*>[\s\S]*?</\1>|<!--[\s\S]*?-->",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AnchorTagRegex = new(
        @"<a\b[^>]*\bhref\s*=\s*(?:""([^""]+)""|'([^']+)'|([^\s>]+))[^>]*>([\s\S]*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HrTagRegex = new(
        @"<hr\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BrTagRegex = new(
        @"<br\s*/?>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ListItemOpenRegex = new(
        @"<li\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CellCloseRegex = new(
        @"</t[dh]\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DoubleBreakCloseRegex = new(
        @"</(?:p|h[1-6]|blockquote|section|article|table|ul|ol)\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SingleBreakCloseRegex = new(
        @"</(?:div|tr|li|header|footer|pre|dt|dd)\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AnyHtmlTagRegex = new(
        @"<[^>]+>",
        RegexOptions.Compiled);

    // Zero-width spaces, combining grapheme joiners (U+034F used in Gmail preheader padding), BOM, soft hyphens.
    private static readonly Regex InvisibleJunkRegex = new(
        @"[\u034F\u200B-\u200F\u202A-\u202E\u2060\uFEFF\u00AD]+",
        RegexOptions.Compiled);

    private static readonly Regex MultiSpaceRegex = new(
        @"[ \t]{3,}",
        RegexOptions.Compiled);

    private static readonly Regex UrlRegex = new(
        @"https?://[^\s<>""'\)\]\u3000-\u303F\uFF00-\uFFEF]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Converts HTML email content to clean, human-readable text preserving paragraph breaks, bullet lists,
    /// horizontal dividers, table cell spacing, and meaningful anchor URLs.
    /// </summary>
    public static string HtmlToReadableText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";

        var s = StripHiddenBlocksRegex.Replace(html, " ");

        // Preserve meaningful link destinations when anchor text differs from the URL (ignore tracking/mailto-duplicate noise).
        s = AnchorTagRegex.Replace(s, m =>
        {
            var href = WebUtility.HtmlDecode(
                !string.IsNullOrEmpty(m.Groups[1].Value) ? m.Groups[1].Value :
                !string.IsNullOrEmpty(m.Groups[2].Value) ? m.Groups[2].Value :
                m.Groups[3].Value).Trim();
            var innerRaw = AnyHtmlTagRegex.Replace(m.Groups[4].Value, " ");
            var label = Regex.Replace(WebUtility.HtmlDecode(innerRaw), @"\s+", " ").Trim();

            bool isHttp = href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                          href.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(label))
                return isHttp ? $" {href} " : " ";

            if (!isHttp || label.Contains(href, StringComparison.OrdinalIgnoreCase))
                return $" {label} ";

            return $" {label} ({href}) ";
        });

        s = HrTagRegex.Replace(s, "\n────────────────────────\n");
        s = BrTagRegex.Replace(s, "\n");
        s = ListItemOpenRegex.Replace(s, "\n• ");
        s = CellCloseRegex.Replace(s, "  ");
        s = DoubleBreakCloseRegex.Replace(s, "\n\n");
        s = SingleBreakCloseRegex.Replace(s, "\n");
        s = AnyHtmlTagRegex.Replace(s, "");
        s = WebUtility.HtmlDecode(s);

        return NormalizeReadablePlainText(s);
    }

    /// <summary>
    /// Normalizes plain text (or HTML-converted text): strips invisible preheader padding, normalizes line endings,
    /// cleans excessive horizontal/vertical whitespace, and keeps clean paragraph separation.
    /// </summary>
    public static string NormalizeReadablePlainText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var s = text.Replace("\r\n", "\n").Replace('\r', '\n');
        s = s.Replace('\u00A0', ' ');
        s = InvisibleJunkRegex.Replace(s, "");

        var lines = s.Split('\n');
        var sb = new StringBuilder(s.Length);
        int consecutiveBlanks = 0;

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0)
            {
                if (sb.Length > 0 && consecutiveBlanks == 0)
                {
                    sb.Append('\n');
                    consecutiveBlanks = 1;
                }
                continue;
            }

            // Preserve intentional 2-space table column separators while collapsing wide whitespace padding.
            var cleaned = MultiSpaceRegex.Replace(trimmed, "  ");
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(cleaned);
            consecutiveBlanks = 0;
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Cleans either HTML or plain-text mail bodies automatically (used by MailDetailWindow for any MailItem).
    /// </summary>
    public static string FormatForDisplay(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "";

        // Detect raw HTML fallback (e.g. if an older cached mail body still contains tags).
        if (body.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("<body", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("<div", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("<table", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("<br", StringComparison.OrdinalIgnoreCase))
        {
            return HtmlToReadableText(body);
        }

        return NormalizeReadablePlainText(body);
    }

    public static MatchCollection FindUrls(string text) => UrlRegex.Matches(text);

    /// <summary>
    /// Shortens long tracking URLs for display (e.g. "https://accounts.google.com/verify/…") while keeping full URL for tooltip & click.
    /// </summary>
    public static string FormatDisplayUrl(string url, int maxDisplayLength = 60)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "";

        if (url.Length <= maxDisplayLength)
            return url;

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var host = uri.Host;
            var path = uri.AbsolutePath?.TrimEnd('/') ?? "";
            if (path.Length > 24)
                path = path[..24] + "…";
            var compact = string.IsNullOrEmpty(path) ? $"{host}/…" : $"{host}{path}…";
            return compact.Length <= maxDisplayLength ? compact : compact[..maxDisplayLength] + "…";
        }

        return url[..maxDisplayLength] + "…";
    }
}
