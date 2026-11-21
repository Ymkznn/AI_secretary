using System.Text.RegularExpressions;

namespace AiSecretary.Desktop.Services;

public sealed record OtpPageSignal(
    string Url,
    string Host,
    string RegistrableDomain,
    string Title,
    IReadOnlyList<string> Headings,
    bool OtpFieldPresent,
    bool OtpFieldFocused,
    DateTimeOffset ObservedAtUtc);

public sealed record OtpMailEvent(
    string MailId,
    string SenderDomain,
    string SenderRegistrableDomain,
    string SenderName,
    string Subject,
    DateTimeOffset ReceivedAtUtc,
    string Handle);

public sealed record OtpMatchedCandidate(
    OtpMailEvent Mail,
    double Confidence,
    string Reason);

public enum OtpDecisionKind
{
    None,
    SingleMatch,
    MultipleMatches
}

public sealed record OtpCorrelationDecision(
    OtpDecisionKind Kind,
    OtpPageSignal Page,
    IReadOnlyList<OtpMatchedCandidate> Candidates);

/// <summary>
/// Pure deterministic, zero-LLM dual-signal OTP correlator.
/// Evaluates on BOTH page-signal arrival (page-first or mail-first) and OTP mail arrival within a ±3 minute window.
/// Never reads or stores plaintext OTP values (carries opaque <c>otp#&lt;hex&gt;</c> handles only).
/// </summary>
public sealed class OtpSituationDetector
{
    public static readonly TimeSpan CorrelationWindow = TimeSpan.FromMinutes(3);
    public const double MinCandidateConfidence = 0.40;
    public const double AutoFillL2Threshold = 0.85;

    private static readonly HashSet<string> GenericSenderTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "noreply", "no-reply", "donotreply", "mail", "email", "info", "support", "service",
        "account", "accounts", "auth", "login", "verify", "security", "notification", "notifications",
        "admin", "team", "hello", "system", "gmail", "outlook", "yahoo", "hotmail", "icloud",
        "系統", "通知", "客服", "信箱", "官方", "帳號", "安全"
    };

    private readonly SecretVaultService _vault;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _lock = new();
    private OtpPageSignal? _activePage;
    private readonly List<OtpMailEvent> _recentOtpMails = new();
    private readonly HashSet<string> _dispatchedKeys = new(StringComparer.OrdinalIgnoreCase);

    public event Action<OtpCorrelationDecision>? SituationDetected;

    public OtpSituationDetector(SecretVaultService vault) : this(vault, () => DateTimeOffset.UtcNow) { }

    internal OtpSituationDetector(SecretVaultService vault, Func<DateTimeOffset> utcNow)
    {
        _vault = vault;
        _utcNow = utcNow;
    }

    public OtpCorrelationDecision? OnPageSignal(
        string url,
        string title,
        IReadOnlyList<string>? headings,
        bool otpFieldPresent,
        bool otpFieldFocused,
        DateTimeOffset? observedAtUtc = null)
    {
        var now = observedAtUtc ?? _utcNow();
        var host = OtpExtractor.ExtractHostFromUrl(url);
        var regDomain = OtpExtractor.ExtractRegistrableDomain(host);

        OtpCorrelationDecision? decision;
        lock (_lock)
        {
            if (!otpFieldPresent || string.IsNullOrWhiteSpace(url))
            {
                _activePage = null;
                return null;
            }

            _activePage = new OtpPageSignal(
                Url: url.Trim(),
                Host: host,
                RegistrableDomain: regDomain,
                Title: (title ?? "").Trim(),
                Headings: headings ?? Array.Empty<string>(),
                OtpFieldPresent: true,
                OtpFieldFocused: otpFieldFocused,
                ObservedAtUtc: now);

            decision = EvaluateLocked(now);
        }

        if (decision is not null)
            SituationDetected?.Invoke(decision);
        return decision;
    }

    public OtpCorrelationDecision? OnOtpMailEvent(OtpMailEvent mailEvent)
    {
        if (mailEvent is null || string.IsNullOrWhiteSpace(mailEvent.Handle))
            return null;

        var now = _utcNow();
        OtpCorrelationDecision? decision;
        lock (_lock)
        {
            PruneLocked(now);
            _recentOtpMails.RemoveAll(m => string.Equals(m.Handle, mailEvent.Handle, StringComparison.OrdinalIgnoreCase));
            _recentOtpMails.Add(mailEvent);
            decision = EvaluateLocked(now);
        }

        if (decision is not null)
            SituationDetected?.Invoke(decision);
        return decision;
    }

    public static (double Score, string Reason) ComputeConfidence(OtpPageSignal page, OtpMailEvent mail, DateTimeOffset now)
    {
        if (!page.OtpFieldPresent)
            return (0.0, "no_otp_field");

        var pairGap = Math.Abs((page.ObservedAtUtc - mail.ReceivedAtUtc).TotalSeconds);
        if (pairGap > CorrelationWindow.TotalSeconds)
            return (0.0, "outside_window");

        if ((now - mail.ReceivedAtUtc) > CorrelationWindow || (now - page.ObservedAtUtc) > CorrelationWindow)
            return (0.0, "expired_window");

        double score = 0.0;
        var reasons = new List<string>();

        // 1) Strong signal: exact registrable domain match (e.g. mail.pchome.com.tw <-> login.pchome.com.tw)
        bool domainMatched = false;
        if (!string.IsNullOrWhiteSpace(page.RegistrableDomain) &&
            !string.IsNullOrWhiteSpace(mail.SenderRegistrableDomain))
        {
            if (string.Equals(page.RegistrableDomain, mail.SenderRegistrableDomain, StringComparison.OrdinalIgnoreCase))
            {
                score += 0.60;
                domainMatched = true;
                reasons.Add($"domain={page.RegistrableDomain}");
            }
            else
            {
                // Cross-TLD same brand stem (e.g., tenso.com email vs tenso.jp page)
                var pageStem = OtpExtractor.ExtractDomainStem(page.RegistrableDomain);
                var mailStem = OtpExtractor.ExtractDomainStem(mail.SenderRegistrableDomain);
                if (pageStem.Length >= 3 &&
                    string.Equals(pageStem, mailStem, StringComparison.OrdinalIgnoreCase) &&
                    !GenericSenderTokens.Contains(pageStem))
                {
                    score += 0.35;
                    domainMatched = true;
                    reasons.Add($"stem={pageStem}");
                }
            }
        }

        // 2) Medium signal: sender name / brand token appears in page title or headings
        var pageText = page.Title + " " + string.Join(" ", page.Headings);
        if (MatchesBrandInPage(mail.SenderName, mail.SenderRegistrableDomain, pageText))
        {
            score += 0.25;
            reasons.Add("brand_in_title");
        }

        // 3) Freshness signal
        if (pairGap <= 60)
        {
            score += 0.15;
            reasons.Add("fresh<=60s");
        }
        else if (pairGap <= 120)
        {
            score += 0.10;
            reasons.Add("fresh<=120s");
        }
        else
        {
            score += 0.05;
            reasons.Add("fresh<=180s");
        }

        // 4) Focus signal
        if (page.OtpFieldFocused)
        {
            score += 0.10;
            reasons.Add("focused");
        }

        // Safety cap: if neither domain nor title/brand matched at all, cap below MinCandidateConfidence
        // so an unrelated OTP email arriving by coincidence never gets filled into a different site.
        if (!domainMatched && !reasons.Contains("brand_in_title"))
            score = Math.Min(score, 0.25);

        return (Math.Round(Math.Clamp(score, 0.0, 1.0), 3), string.Join("+", reasons));
    }

    private OtpCorrelationDecision? EvaluateLocked(DateTimeOffset now)
    {
        PruneLocked(now);
        if (_activePage is null || !_activePage.OtpFieldPresent || _recentOtpMails.Count == 0)
            return null;

        var matched = new List<OtpMatchedCandidate>();
        foreach (var mail in _recentOtpMails)
        {
            if (!_vault.TryPeekMetadata(mail.Handle, out _))
                continue;

            var (score, reason) = ComputeConfidence(_activePage, mail, now);
            if (score >= MinCandidateConfidence)
                matched.Add(new OtpMatchedCandidate(mail, score, reason));
        }

        if (matched.Count == 0)
            return null;

        matched.Sort((a, b) =>
        {
            var cmp = b.Confidence.CompareTo(a.Confidence);
            return cmp != 0 ? cmp : b.Mail.ReceivedAtUtc.CompareTo(a.Mail.ReceivedAtUtc);
        });

        // Deduplicate: do not re-emit the exact same (page domain + active candidate handles) pair
        var key = $"{_activePage.RegistrableDomain}|{string.Join(",", matched.Select(c => c.Mail.Handle))}|{(matched.Count == 1 && matched[0].Confidence >= AutoFillL2Threshold ? "L2" : "L1")}";
        if (!_dispatchedKeys.Add(key))
            return null;

        var kind = matched.Count >= 2 ? OtpDecisionKind.MultipleMatches : OtpDecisionKind.SingleMatch;
        return new OtpCorrelationDecision(kind, _activePage, matched);
    }

    private void PruneLocked(DateTimeOffset now)
    {
        if (_activePage is not null && (now - _activePage.ObservedAtUtc) > CorrelationWindow)
            _activePage = null;

        _recentOtpMails.RemoveAll(m =>
            (now - m.ReceivedAtUtc) > CorrelationWindow ||
            !_vault.TryPeekMetadata(m.Handle, out _));

        if (_dispatchedKeys.Count > 64)
            _dispatchedKeys.Clear();
    }

    private static bool MatchesBrandInPage(string? senderName, string? senderRegDomain, string pageText)
    {
        if (string.IsNullOrWhiteSpace(pageText)) return false;

        var tokens = new List<string>();
        if (!string.IsNullOrWhiteSpace(senderName))
        {
            foreach (Match m in Regex.Matches(senderName, @"[A-Za-z0-9]{3,}|[\u4E00-\u9FFF]{2,}"))
            {
                var t = m.Value.Trim();
                if (!GenericSenderTokens.Contains(t))
                    tokens.Add(t);
            }
        }

        var stem = OtpExtractor.ExtractDomainStem(senderRegDomain);
        if (stem.Length >= 3 && !GenericSenderTokens.Contains(stem))
            tokens.Add(stem);

        return tokens.Any(t => pageText.Contains(t, StringComparison.OrdinalIgnoreCase));
    }
}
