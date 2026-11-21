using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Services;

namespace AiSecretary.Desktop.Skills;

/// <summary>
/// Atomic skill that fills an OTP / verification code into the active verification page
/// via Browser Extension DOM (preferred) or Windows UIA/SendKeys fallback, without auto-submitting.
/// </summary>
public sealed class FillOtpSkill : IBasicSkill
{
    private readonly BrowserFormBridgeService _browserBridge;
    private readonly ActivityMonitorService _activity;
    private readonly SkillRuntime _skills;
    private readonly SecretVaultService _vault;

    public FillOtpSkill(
        BrowserFormBridgeService browserBridge,
        ActivityMonitorService activity,
        SkillRuntime skills,
        SecretVaultService vault)
    {
        _browserBridge = browserBridge;
        _activity = activity;
        _skills = skills;
        _vault = vault;
    }

    public string Name => "fill_otp";
    public string DisplayDescription => "Fill OTP verification code into active browser/app";
    public string StatusText => "Filling verification code...";
    public string MainAgentSpec => """
            fill_otp
               - Purpose: Fill a vaulted OTP/verification code into the active browser verification page via Extension DOM (or UIA fallback), never auto-submitting. ALWAYS use `fill_otp` (NEVER `browser_form`) for any OTP or `otp#xxxxxx` vault handle.
               - Args: {"handle": "exact otp#xxxxxx handle verbatim (do NOT strip 'otp#'); omit to use latest vaulted OTP", "service_hint": "optional window/service keyword"}
            
        """;
    public string PlannerSpec => """fill_otp: ALWAYS use fill_otp (NEVER browser_form) to fill any OTP or otp#xxxxxx vault handle (pass exact "otp#xxxxxx" verbatim, never strip prefix), never submits. args: {"handle":"optional otp#xxxxxx","service_hint":"optional"}""";

    public async Task ExecuteAsync(SkillExecutionContext ctx)
    {
        var ev = _skills.CurrentEventNotification;
        var rawArg = ctx.GetArg("handle") ?? ctx.GetArg("code");
        if (string.IsNullOrWhiteSpace(rawArg) || rawArg.Contains("{{", StringComparison.Ordinal))
            rawArg = ev?.OtpHandle ?? _skills.LastObservedOtpHandle ?? _vault.GetLatestActiveHandle();

        if (string.IsNullOrWhiteSpace(rawArg))
        {
            ctx.MarkFailed();
            ctx.Observations.Add("fill_otp: no active OTP handle available to fill.");
            return;
        }

        // Resolve handle WITHOUT consuming it yet; only consume after fill succeeds.
        string handle;
        string realCode;
        SecretMetadata? meta;
        if (SecretVaultService.IsOtpHandle(rawArg))
        {
            handle = rawArg.Trim().ToLowerInvariant();
            if (!_vault.TryResolve(handle, out realCode, out meta))
            {
                ctx.MarkFailed();
                ctx.Observations.Add($"fill_otp: handle {handle} has already been used or expired.");
                return;
            }
        }
        else
        {
            handle = _vault.StoreOtp(rawArg);
            if (!_vault.TryResolve(handle, out realCode, out meta))
            {
                ctx.MarkFailed();
                ctx.Observations.Add("fill_otp: failed to resolve OTP handle.");
                return;
            }
        }

        var hint = ctx.GetArg("service_hint");
        if (string.IsNullOrWhiteSpace(hint) || hint.Contains("{{", StringComparison.Ordinal))
            hint = ev?.Title ?? _skills.LastObservedOtpHint;

        var expectedDomain = !string.IsNullOrWhiteSpace(meta?.SenderDomain) &&
                             !string.Equals(meta.SenderDomain, "unknown", StringComparison.OrdinalIgnoreCase)
            ? meta.SenderDomain
            : null;

        var skillName = ctx.EffectiveName(Name);
        await ctx.OnStatusAsync(await ctx.TextAsync($"skill_status:{skillName}", StatusText));
        var extRes = await _browserBridge.RequestFillOtpAsync(
            realCode,
            TimeSpan.FromMilliseconds(1500),
            ctx.Ct,
            expectedDomain: expectedDomain,
            requireOtpField: false);
        if (extRes is { Filled: true })
        {
            _vault.MarkConsumed(handle);
            var title = string.IsNullOrWhiteSpace(extRes.Title) ? "browser tab" : extRes.Title.Trim();
            ctx.Observations.Add($"{skillName} succeeded via Browser Extension ({extRes.Mode ?? "dom"}) on \"{title}\" (filled {handle}, not submitted).");
            ctx.CompletedSteps.Add(skillName);
            return;
        }

        bool domainMismatched = string.Equals(extRes?.Error, "domain_mismatch", StringComparison.OrdinalIgnoreCase);
        if (domainMismatched &&
            _activity.GetLastExternalTarget() is { } lastWin &&
            ActivityMonitorService.IsBrowserProcess(lastWin.ProcessName))
        {
            ctx.MarkFailed();
            ctx.Observations.Add($"{skillName} rejected: active browser tab domain does not match expected domain '{expectedDomain}' ({handle} remains available).");
            return;
        }

        var (ok, msg) = await _activity.TryFillOtpInExternalWindowAsync(
            realCode,
            hint,
            ctx.Ct,
            displayHandle: handle,
            expectedDomain: expectedDomain,
            senderName: meta?.SenderName,
            allowBrowserFallback: !domainMismatched);
        if (ok)
        {
            _vault.MarkConsumed(handle);
            ctx.Observations.Add($"{skillName} succeeded via UIA: filled {handle} ({msg})");
            ctx.CompletedSteps.Add(skillName);
        }
        else
        {
            ctx.Observations.Add($"{skillName} failed: {msg} ({handle} remains available).");
            ctx.MarkFailed();
        }
    }
}
