namespace AiSecretary.Desktop.Services;

/// <summary>
/// Pure RFC/DNS domain &amp; URL parsing utility (handling two-level public suffixes like <c>.com.tw</c> / <c>.co.jp</c>).
/// Contains zero language keywords or content-matching heuristics.
/// </summary>
public static class DomainParser
{
    // Standard IANA two-level public suffixes (effective TLDs) so auth.pchome.com.tw -> pchome.com.tw
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

    public static string ExtractSenderDomain(string? senderEmail)
    {
        var raw = (senderEmail ?? "").Trim();
        var at = raw.LastIndexOf('@');
        if (at < 0 || at >= raw.Length - 1) return "";
        return raw[(at + 1)..].Trim().TrimEnd('>', ')', ';', ',').ToLowerInvariant();
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
    /// Extracts the leading label from a registrable domain (e.g., <c>tenso.com</c> -&gt; <c>tenso</c>,
    /// <c>pchome.com.tw</c> -&gt; <c>pchome</c>).
    /// </summary>
    public static string ExtractDomainStem(string? registrableDomain)
    {
        if (string.IsNullOrWhiteSpace(registrableDomain)) return "";
        var dot = registrableDomain.IndexOf('.');
        return dot > 0 ? registrableDomain[..dot].ToLowerInvariant() : registrableDomain.ToLowerInvariant();
    }
}
