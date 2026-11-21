using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Models;

namespace AiSecretary.Desktop.Services;

public sealed record AutonomyAcceptOutcome(
    int CurrentLevel,
    int AcceptedCount,
    bool ShouldOfferPromotion);

/// <summary>
/// Per-(action_class, domain) progressive autonomy policy manager (L0-L3) backed by SQLite <c>AutonomyPolicies</c>.
/// - Default for <c>fill_otp</c>: confidence &gt;= 0.85 -&gt; L2 (auto-fill + 10s Undo, NEVER auto-submit);
///   confidence &lt; 0.85 -&gt; L1 (prompt user to click).
/// - Accepting 3 times at L1 on the same domain offers a one-time promotion prompt ("以後直接幫你填？").
/// - Clicking Undo or Reject demotes by 1 level and logs to <c>LearningEvents</c>.
/// </summary>
public sealed class AutonomyPolicyService
{
    public const string ActionFillOtp = "fill_otp";
    public const int L1PromotionThreshold = 3;

    private readonly SqliteStore _store;

    public AutonomyPolicyService(SqliteStore store)
    {
        _store = store;
    }

    public async Task<int> ResolveEffectiveLevelAsync(
        string actionClass,
        string domain,
        double confidence,
        CancellationToken ct = default)
    {
        var normDomain = NormalizeDomain(domain);
        var existing = await _store.GetAutonomyPolicyAsync(actionClass, normDomain, ct);

        if (existing is null)
        {
            if (string.Equals(actionClass, ActionFillOtp, StringComparison.OrdinalIgnoreCase))
                return confidence >= OtpSituationDetector.AutoFillL2Threshold ? 2 : 1;
            return 1;
        }

        // For fill_otp: NEVER exceed L2 (never auto-submit).
        int capped = string.Equals(actionClass, ActionFillOtp, StringComparison.OrdinalIgnoreCase)
            ? Math.Clamp(existing.Level, 0, 2)
            : Math.Clamp(existing.Level, 0, 3);

        if (capped == 2 && confidence < 0.60)
            return 1;

        return capped;
    }

    public async Task<AutonomyAcceptOutcome> RecordAcceptedAsync(
        string actionClass,
        string domain,
        int atLevel,
        CancellationToken ct = default)
    {
        var normDomain = NormalizeDomain(domain);
        var existing = await _store.GetAutonomyPolicyAsync(actionClass, normDomain, ct);
        int level = existing?.Level ?? Math.Clamp(atLevel, 1, 2);
        int accepted = (existing?.Accepted ?? 0) + 1;
        int rejected = existing?.Rejected ?? 0;

        await _store.UpsertAutonomyPolicyAsync(new AutonomyPolicyRecord(
            actionClass, normDomain, level, accepted, rejected, DateTimeOffset.UtcNow), ct);

        bool shouldOffer = level == 1 && accepted == L1PromotionThreshold;
        return new AutonomyAcceptOutcome(level, accepted, shouldOffer);
    }

    public async Task PromoteToL2Async(string actionClass, string domain, string evidence = "user accepted L2 promotion", CancellationToken ct = default)
    {
        var normDomain = NormalizeDomain(domain);
        var existing = await _store.GetAutonomyPolicyAsync(actionClass, normDomain, ct);
        int before = existing?.Level ?? 1;
        int after = 2; // Max safe level for fill_otp (never auto-submit)

        await _store.UpsertAutonomyPolicyAsync(new AutonomyPolicyRecord(
            actionClass, normDomain, after, existing?.Accepted ?? L1PromotionThreshold, existing?.Rejected ?? 0, DateTimeOffset.UtcNow), ct);

        await _store.InsertLearningEventAsync(
            learner: "AutonomyPolicy",
            subjectKey: $"{actionClass}:{normDomain}",
            eventType: "promoted",
            beforeState: $"L{before}",
            afterState: $"L{after}",
            evidence: evidence,
            ct: ct);
    }

    public async Task KeepAtL1Async(string actionClass, string domain, CancellationToken ct = default)
    {
        var normDomain = NormalizeDomain(domain);
        var existing = await _store.GetAutonomyPolicyAsync(actionClass, normDomain, ct);
        await _store.UpsertAutonomyPolicyAsync(new AutonomyPolicyRecord(
            actionClass, normDomain, 1, existing?.Accepted ?? L1PromotionThreshold, (existing?.Rejected ?? 0) + 1, DateTimeOffset.UtcNow), ct);
    }

    public async Task<int> RecordRejectedOrUndoneAsync(
        string actionClass,
        string domain,
        int activeLevel,
        string reason,
        CancellationToken ct = default)
    {
        var normDomain = NormalizeDomain(domain);
        var existing = await _store.GetAutonomyPolicyAsync(actionClass, normDomain, ct);
        int before = existing?.Level ?? Math.Clamp(activeLevel, 1, 2);
        int after = Math.Max(0, before - 1);
        int accepted = existing?.Accepted ?? 0;
        int rejected = (existing?.Rejected ?? 0) + 1;

        await _store.UpsertAutonomyPolicyAsync(new AutonomyPolicyRecord(
            actionClass, normDomain, after, accepted, rejected, DateTimeOffset.UtcNow), ct);

        await _store.InsertLearningEventAsync(
            learner: "AutonomyPolicy",
            subjectKey: $"{actionClass}:{normDomain}",
            eventType: "demoted",
            beforeState: $"L{before}",
            afterState: $"L{after}",
            evidence: reason,
            ct: ct);

        return after;
    }

    private static string NormalizeDomain(string? domain)
        => string.IsNullOrWhiteSpace(domain) ? "unknown" : domain.Trim().ToLowerInvariant();
}
