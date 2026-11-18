using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Resources;
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

    public FillOtpSkill(
        BrowserFormBridgeService browserBridge,
        ActivityMonitorService activity,
        SkillRuntime skills)
    {
        _browserBridge = browserBridge;
        _activity = activity;
        _skills = skills;
    }

    public string Name => "fill_otp";
    public string DisplayDescription => "Fill OTP verification code into active browser/app";
    public string MainAgentSpec => """
            fill_otp
               - Purpose: Fill an OTP/verification code into the active browser verification page via Extension DOM (or UIA fallback), never auto-submitting.
               - Args: {"code": "optional; omit to use active event/latest OTP", "service_hint": "optional window/service keyword"}
            
        """;
    public string PlannerSpec => """fill_otp: Fill OTP into active verification page via Extension DOM (or UIA fallback), never submits. args: {"code":"optional","service_hint":"optional"}""";

    public async Task ExecuteAsync(SkillExecutionContext ctx)
    {
        var ev = _skills.CurrentEventNotification;
        var code = ctx.GetArg("code");
        if (string.IsNullOrWhiteSpace(code) || code.Contains("{{", StringComparison.Ordinal))
            code = ev?.Otp ?? _skills.LastObservedOtpCode;

        if (string.IsNullOrWhiteSpace(code))
        {
            ctx.Observations.Add("fill_otp: no OTP code available to fill.");
            return;
        }

        var hint = ctx.GetArg("service_hint");
        if (string.IsNullOrWhiteSpace(hint) || hint.Contains("{{", StringComparison.Ordinal))
            hint = ev?.Title ?? _skills.LastObservedOtpHint;

        await ctx.OnStatusAsync(AppMessages.Localize($"skill_status:{Name}", "Filling verification code..."));
        var extRes = await _browserBridge.RequestFillOtpAsync(code, TimeSpan.FromMilliseconds(1500), ctx.Ct);
        if (extRes is { Filled: true })
        {
            var title = string.IsNullOrWhiteSpace(extRes.Title) ? "browser tab" : extRes.Title.Trim();
            ctx.Observations.Add($"fill_otp succeeded via Browser Extension ({extRes.Mode ?? "dom"}) on \"{title}\" (code={code}, not submitted).");
            ctx.CompletedSteps.Add("fill_otp");
            return;
        }

        var (ok, msg) = _activity.TryFillOtpInExternalWindow(code, hint);
        ctx.Observations.Add(ok
            ? $"fill_otp succeeded via UIA: {msg}"
            : $"fill_otp failed: {msg}");
        if (ok)
            ctx.CompletedSteps.Add("fill_otp");
    }
}
