using System.Text.Json;
using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Services;
using AiSecretary.Desktop.Skills;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiSecretary.Desktop.Tests;

public sealed class OtpCorrelatorAndVaultTests
{
    [Theory]
    [InlineData("noreply@mail.tenso.com", "tenso.com")]
    [InlineData("https://login.tenso.com/auth/verify", "tenso.com")]
    [InlineData("service@auth.pchome.com.tw", "pchome.com.tw")]
    [InlineData("https://member.pchome.com.tw/otp", "pchome.com.tw")]
    [InlineData("security@mail.rakuten.co.jp", "rakuten.co.jp")]
    [InlineData("https://accounts.example.co.uk/2fa", "example.co.uk")]
    [InlineData("https://www.esunbank.com.tw/verify", "esunbank.com.tw")]
    [InlineData("github.com", "github.com")]
    public void ExtractRegistrableDomain_HandlesTwoLevelSuffixes(string input, string expected)
    {
        Assert.Equal(expected, DomainParser.ExtractRegistrableDomain(input));
    }

    [Fact]
    public void SecretVault_HandleFormat_SingleUse_And_TtlExpiry()
    {
        var clock = new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);
        var vault = new SecretVaultService(() => clock);

        var handle = vault.StoreOtp("135883", "tenso.com", "tenso", "m1", TimeSpan.FromMinutes(8));
        Assert.True(SecretVaultService.IsOtpHandle(handle));
        Assert.StartsWith("otp#", handle);
        Assert.Equal(10, handle.Length); // "otp#" + 6 hex chars
        Assert.Equal(handle, vault.TryGetActiveHandleForMail("m1"));

        // Redaction replaces plaintext with handle
        var redacted = vault.RedactKnownSecrets("tenso 登入驗證碼 135883，有效期限 10 分鐘");
        Assert.DoesNotContain("135883", redacted);
        Assert.Contains(handle, redacted);

        // First consume succeeds and returns real code
        Assert.True(vault.TryConsume(handle, out var secret, out var meta));
        Assert.Equal("135883", secret);
        Assert.Equal("tenso.com", meta?.SenderDomain);
        Assert.Null(vault.TryGetActiveHandleForMail("m1"));

        // Second consume MUST fail (single-use!)
        Assert.False(vault.TryConsume(handle, out var second, out _));
        Assert.Equal("", second);

        // Even AFTER consumption, delayed triage/snippets still redact the plaintext code and cannot resurrect it
        var redactedAfterConsume = vault.RedactKnownSecrets("Delayed triage: code 135883");
        Assert.DoesNotContain("135883", redactedAfterConsume);
        Assert.Contains(handle, redactedAfterConsume);
        var reStored = vault.StoreOtp("135883", "tenso.com", "tenso", "m1");
        Assert.Equal(handle, reStored);
        Assert.False(vault.TryConsume(reStored, out _, out _));

        // TTL expiry test
        var h2 = vault.StoreOtp("987654", "github.com", "GitHub", "m2", TimeSpan.FromMinutes(5));
        clock = clock.AddMinutes(6);
        Assert.False(vault.TryPeekMetadata(h2, out _));
        Assert.False(vault.TryConsume(h2, out _, out _));
    }

    [Theory]
    [InlineData("304891", "tenso.com", "support@tenso.com")]
    [InlineData("9K4MX", "Steam", "noreply@steampowered.com")]
    [InlineData("G-482910", "Google", "no-reply@accounts.google.com")]
    [InlineData("482 910", "Service", "auth@example.co.jp")]
    public void VaultAndRedactTriage_SupportsAnyCodeFormat_WithoutHardcodedRegex(
        string rawCode, string senderName, string senderEmail)
    {
        var vault = new SecretVaultService();
        var mail = new MailItem(
            Id: $"m-{rawCode}",
            Subject: $"[{senderName}] Security verification",
            SenderName: senderName,
            SenderEmail: senderEmail,
            ReceivedAt: DateTimeOffset.UtcNow,
            Importance: MailImportance.High,
            Snippet: $"Your code is {rawCode}",
            Body: $"Hello, enter {rawCode} to continue.",
            IsRead: false,
            ThreadId: "th-1");

        // MailTriage LLM extracts the verification code directly regardless of format/language
        var triage = new TriageResult(
            Urgent: true,
            Title: $"{senderName} 驗證碼 {rawCode}",
            Summary: $"{senderName} 寄來驗證碼 {rawCode}，請儘速使用。",
            ReRemindMinutes: 10,
            Actions: new List<NotificationButtonOption>
            {
                new("fill_otp", "填入驗證碼", rawCode, IsPrimary: true),
                new("copy_otp", "複製驗證碼", rawCode, IsPrimary: false),
                new("view_mail", "查看郵件", null, IsPrimary: false)
            });

        var (safeTitle, safeSummary, safeActions, primaryHandle) = MailSyncService.VaultAndRedactTriage(vault, mail, triage);

        Assert.NotNull(primaryHandle);
        Assert.True(SecretVaultService.IsOtpHandle(primaryHandle));
        Assert.NotNull(safeActions);

        var fillBtn = Assert.Single(safeActions!, a => a.Type == "fill_otp");
        var copyBtn = Assert.Single(safeActions!, a => a.Type == "copy_otp");
        Assert.Equal(primaryHandle, fillBtn.Prompt);
        Assert.Equal(primaryHandle, copyBtn.Prompt);

        // Vault resolves back to the exact raw code (numeric, alphanumeric, prefixed, or spaced)
        Assert.True(vault.TryResolve(primaryHandle, out var resolved, out var meta));
        Assert.Equal(rawCode, resolved);
        Assert.Equal(DomainParser.ExtractRegistrableDomain(senderEmail), meta?.SenderDomain);

        // Neither plaintext code nor internal otp#xxxxxx handle leaks into human-facing Title/Summary
        Assert.DoesNotContain(rawCode, safeTitle);
        Assert.DoesNotContain(rawCode, safeSummary);
        Assert.DoesNotContain("otp#", safeTitle, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("otp#", safeSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StateMachine_PageFirst_ThenEvent_ExactDomainMatchesWithZeroLlmCalls()
    {
        var clock = new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
        using var harness = CreateStateMachineHarness(() => clock);
        ContextEventMatchDecision? captured = null;
        harness.Agent.ContextEventMatchHandler = (d, _) => { captured = d; return Task.FromResult(true); };

        // Step 1: User opens verification page on pchome.com.tw (with 1 visible editable input)
        await harness.Agent.HandleBrowserPageSignalAsync(
            "conv-1",
            new CachedBrowserPageSignal(
                Url: "https://member.pchome.com.tw/login/otp",
                Title: "PChome 24h 登入驗證",
                InputCount: 1,
                Labels: new[] { "輸入驗證碼" },
                Headings: new[] { "登入驗證" },
                Excerpt: "請輸入驗證碼",
                CapturedAt: clock,
                HasOneTimeCodeInput: true,
                HasFocusedInput: true));
        Assert.Null(captured);

        // Step 2: 25 seconds later, MailTriage vaults code and emits generic ContextEvent
        clock = clock.AddSeconds(25);
        var handle = harness.Vault.StoreOtp("482910", "pchome.com.tw", "PChome", "m-pc");
        var handled = await harness.Agent.HandleContextEventAsync(new StateMachineContextEvent(
            EventId: "ev-m-pc",
            ActionClass: AutonomyPolicyService.ActionFillOtp,
            SourceId: "m-pc",
            SourceDomain: "pchome.com.tw",
            SourceName: "PChome",
            Title: "PChome 登入驗證碼",
            Summary: "PChome 寄來登入驗證碼",
            ResourceHandle: handle,
            OccurredAtUtc: clock));

        Assert.True(handled);
        Assert.NotNull(captured);
        Assert.Equal(ContextEventMatchKind.SingleMatch, captured!.Kind);
        Assert.Single(captured.Matches);
        Assert.Equal(1.0, captured.Matches[0].Confidence);
        Assert.Equal(handle, captured.Matches[0].Event.ResourceHandle);
        // Exact RFC registrable domain match MUST use 0 EventMatch LLM calls!
        Assert.DoesNotContain(harness.Gateway.RecordedCallers, c => c == "StateMachine.EventMatch");
    }

    [Fact]
    public async Task StateMachine_EventFirst_ThenPage_ExactDomainMatchesImmediately()
    {
        var clock = new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
        using var harness = CreateStateMachineHarness(() => clock);
        ContextEventMatchDecision? captured = null;
        harness.Agent.ContextEventMatchHandler = (d, _) => { captured = d; return Task.FromResult(true); };

        // Step 1: Event arrives before user opens page
        var handle = harness.Vault.StoreOtp("771234", "tenso.com", "tenso", "m-tenso");
        var handledInitially = await harness.Agent.HandleContextEventAsync(new StateMachineContextEvent(
            "ev-tenso", AutonomyPolicyService.ActionFillOtp, "m-tenso", "tenso.com", "tenso",
            "tenso 驗證碼", "登入驗證碼已送達", handle, clock));
        Assert.False(handledInitially);
        Assert.Null(captured);

        // Step 2: 40 seconds later, user opens tenso page -> immediately correlated!
        clock = clock.AddSeconds(40);
        await harness.Agent.HandleBrowserPageSignalAsync(
            "conv-1",
            new CachedBrowserPageSignal(
                Url: "https://login.tenso.com/verify",
                Title: "tenso - Two-Step Verification",
                InputCount: 1,
                Labels: new[] { "Code" },
                Headings: new[] { "Enter verification code" },
                Excerpt: "",
                CapturedAt: clock));

        Assert.NotNull(captured);
        Assert.Equal(ContextEventMatchKind.SingleMatch, captured!.Kind);
        Assert.Equal(1.0, captured.Matches[0].Confidence);
        Assert.Equal(handle, captured.Matches[0].Event.ResourceHandle);
    }

    [Fact]
    public async Task StateMachine_CrossDomainService_UsesSemanticEventMatchLlm_WithoutLeakingSecrets()
    {
        var clock = new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
        using var harness = CreateStateMachineHarness(
            () => clock,
            llmResponder: (caller, _) => caller == "StateMachine.EventMatch"
                ? """{"matched_event_ids": ["ev-cross"], "confidence": 0.92, "reason": "same tenso service across .com and .jp"}"""
                : """{"mode": "ignore"}""");

        ContextEventMatchDecision? captured = null;
        harness.Agent.ContextEventMatchHandler = (d, _) => { captured = d; return Task.FromResult(true); };

        // Page is on tenso.jp while email came from support@tenso.com (different registrable domain!)
        await harness.Agent.HandleBrowserPageSignalAsync(
            "conv-1",
            new CachedBrowserPageSignal(
                Url: "https://www.tenso.jp/login/confirm",
                Title: "ログイン確認 | tenso転送サービス",
                InputCount: 1,
                Labels: new[] { "認証コード" },
                Headings: new[] { "ログイン確認" },
                Excerpt: "",
                CapturedAt: clock));

        clock = clock.AddSeconds(15);
        var handle = harness.Vault.StoreOtp("558899", "tenso.com", "tenso.com", "m-cross");
        var handled = await harness.Agent.HandleContextEventAsync(new StateMachineContextEvent(
            "ev-cross", AutonomyPolicyService.ActionFillOtp, "m-cross", "tenso.com", "tenso.com",
            "tenso 登入驗證碼", "tenso 寄來登入驗證碼", handle, clock));

        Assert.True(handled);
        Assert.NotNull(captured);
        Assert.Equal(ContextEventMatchKind.SingleMatch, captured!.Kind);
        Assert.Equal(0.92, captured.Matches[0].Confidence, 2);
        Assert.Contains("StateMachine.EventMatch", harness.Gateway.RecordedCallers);

        // CRITICAL SECURITY CHECK: EventMatch LLM prompt MUST contain NEITHER plaintext OTP NOR vault handle!
        var matchPrompt = Assert.Single(harness.Gateway.RecordedPrompts, p => p.Caller == "StateMachine.EventMatch").UserText;
        Assert.DoesNotContain("558899", matchPrompt);
        Assert.DoesNotContain(handle, matchPrompt);
    }

    [Fact]
    public async Task StateMachine_UnrelatedOrZeroInputPage_DoesNotMatch()
    {
        var clock = new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
        using var harness = CreateStateMachineHarness(
            () => clock,
            llmResponder: (caller, _) => caller == "StateMachine.EventMatch"
                ? """{"matched_event_ids": [], "confidence": 0.0, "reason": "different services"}"""
                : """{"mode": "ignore"}""");

        // 1) Zero-input browsing page -> skips EventMatch LLM completely (0 calls)
        await harness.Agent.HandleBrowserPageSignalAsync(
            "conv-1",
            new CachedBrowserPageSignal("https://www.youtube.com/watch?v=123", "YouTube", 0, Array.Empty<string>(), Array.Empty<string>(), "", clock));

        var h0 = harness.Vault.StoreOtp("112233", "tenso.com", "tenso", "m-yt");
        var handledYt = await harness.Agent.HandleContextEventAsync(new StateMachineContextEvent(
            "ev-yt", AutonomyPolicyService.ActionFillOtp, "m-yt", "tenso.com", "tenso", "tenso", "驗證碼", h0, clock));
        Assert.False(handledYt);
        Assert.DoesNotContain("StateMachine.EventMatch", harness.Gateway.RecordedCallers);

        // 2) Unrelated page with input -> LLM rejects -> no match
        clock = clock.AddSeconds(5);
        await harness.Agent.HandleBrowserPageSignalAsync(
            "conv-1",
            new CachedBrowserPageSignal("https://login.bank-a.com/otp", "Bank A Verification", 1, new[] { "Code" }, Array.Empty<string>(), "", clock));

        var h1 = harness.Vault.StoreOtp("445566", "shop-b.com", "Shop B", "m-other");
        var handledOther = await harness.Agent.HandleContextEventAsync(new StateMachineContextEvent(
            "ev-other", AutonomyPolicyService.ActionFillOtp, "m-other", "shop-b.com", "Shop B", "Shop B", "Verification", h1, clock));
        Assert.False(handledOther);
    }

    [Fact]
    public async Task StateMachine_MultipleMatchingEvents_ReturnsMultipleMatchesNeverSingleAutoFill()
    {
        var clock = new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
        using var harness = CreateStateMachineHarness(() => clock);
        ContextEventMatchDecision? captured = null;
        harness.Agent.ContextEventMatchHandler = (d, _) => { captured = d; return Task.FromResult(true); };

        var h1 = harness.Vault.StoreOtp("111111", "tenso.com", "tenso", "m1");
        await harness.Agent.HandleContextEventAsync(new StateMachineContextEvent(
            "ev-1", AutonomyPolicyService.ActionFillOtp, "m1", "tenso.com", "tenso", "OTP 1", "驗證碼", h1, clock));

        clock = clock.AddSeconds(15);
        var h2 = harness.Vault.StoreOtp("222222", "tenso.com", "tenso", "m2");
        await harness.Agent.HandleContextEventAsync(new StateMachineContextEvent(
            "ev-2", AutonomyPolicyService.ActionFillOtp, "m2", "tenso.com", "tenso", "OTP 2", "驗證碼", h2, clock));

        clock = clock.AddSeconds(5);
        await harness.Agent.HandleBrowserPageSignalAsync(
            "conv-1",
            new CachedBrowserPageSignal("https://login.tenso.com/verify", "tenso Verify", 1, new[] { "Code" }, Array.Empty<string>(), "", clock));

        Assert.NotNull(captured);
        Assert.Equal(ContextEventMatchKind.MultipleMatches, captured!.Kind);
        Assert.Equal(2, captured.Matches.Count);
    }

    [Fact]
    public async Task StateMachine_OutsideThreeMinuteWindow_DoesNotMatch()
    {
        var clock = new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
        using var harness = CreateStateMachineHarness(() => clock);
        ContextEventMatchDecision? captured = null;
        harness.Agent.ContextEventMatchHandler = (d, _) => { captured = d; return Task.FromResult(true); };

        var h1 = harness.Vault.StoreOtp("654321", "tenso.com", "tenso", "m-old");
        await harness.Agent.HandleContextEventAsync(new StateMachineContextEvent(
            "ev-old", AutonomyPolicyService.ActionFillOtp, "m-old", "tenso.com", "tenso", "OTP", "驗證碼", h1, clock));

        clock = clock.AddMinutes(4);
        await harness.Agent.HandleBrowserPageSignalAsync(
            "conv-1",
            new CachedBrowserPageSignal("https://login.tenso.com/verify", "tenso Verify", 1, new[] { "Code" }, Array.Empty<string>(), "", clock));

        Assert.Null(captured);
    }

    [Fact]
    public async Task StateMachine_FirstVisitProbe_SilentPrepare_SavesHotRuleAndExecutesSilentlyWithZeroPopup()
    {
        var clock = new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
        using var harness = CreateStateMachineHarness(
            () => clock,
            llmResponder: (caller, _) => caller == "StateMachine.FirstProbe"
                ? """{"mode":"silent_prepare","proposal":"","skill":"manage_mail","args":{"action":"sync","source":"all","boost_seconds":90}}"""
                : """{"mode":"ignore"}""");

        int proposalCount = 0;
        harness.Agent.ProposalRequested += _ => proposalCount++;

        var url = "https://www.tenso.com/cht/login/confirm";
        var key = StateMachineAgent.BuildTriggerKey(url, "msedge", "登入確認 | tenso");
        var sig = new CachedBrowserPageSignal(url, "登入確認 | tenso", 1, new[] { "驗證碼" }, new[] { "登入確認" }, "請輸入驗證碼", clock);
        var target = new ExternalWindowTarget(IntPtr.Zero, 1, "msedge", sig.Title, clock);

        await harness.Agent.TryFirstVisitProbeAsync("conv-1", key, target, url, sig, CancellationToken.None, skipDwellForTest: true);

        // Zero popup card raised, manage_mail sync ran silently, and rule is saved as Hot for 0-LLM replay next time!
        Assert.Equal(0, proposalCount);
        Assert.Equal(1, harness.FakeMailSkill.Executions);
        var saved = await harness.Store.GetAutoStateRuleAsync(key);
        Assert.NotNull(saved);
        Assert.Equal("Hot", saved!.Status);
        Assert.True(saved.PredictorCounter >= 2);
        Assert.Equal("silent_prepare_manage_mail", saved.StateName);
    }

    [Fact]
    public async Task StateMachine_MigrateLegacyRules_DeletesStaleBrowserWinAndColdMailSyncRules()
    {
        var clock = new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
        using var harness = CreateStateMachineHarness(() => clock);

        await harness.Store.UpsertAutoStateRuleAsync(new AutoStateRuleRecord("win:msedge|登入確認", "first_visit_manage_mail", "[]", 1, 0, 0, "Cold", clock));
        await harness.Store.UpsertAutoStateRuleAsync(new AutoStateRuleRecord("url:www.tenso.com/*/login/confirm", "first_visit_manage_mail", "[]", 1, 0, 0, "Cold", clock));
        await harness.Store.UpsertAutoStateRuleAsync(new AutoStateRuleRecord("url:forms.company.com/apply", "first_visit_browser_form", "[]", 2, 3, 0, "Hot", clock));

        await harness.Agent.MigrateLegacyRulesAsync();

        Assert.Null(await harness.Store.GetAutoStateRuleAsync("win:msedge|登入確認"));
        Assert.Null(await harness.Store.GetAutoStateRuleAsync("url:www.tenso.com/*/login/confirm"));
        Assert.NotNull(await harness.Store.GetAutoStateRuleAsync("url:forms.company.com/apply"));
    }

    [Fact]
    public async Task AutonomyPolicy_Defaults_3xL1Promotion_And_UndoDemotionLoggedToLearningEvents()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"aisec_test_{Guid.NewGuid():N}.db");
        try
        {
            var cfg = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:ConnectionString"] = $"Data Source={tempDb}"
                })
                .Build();
            var store = new SqliteStore(cfg);
            var policy = new AutonomyPolicyService(store);

            // Default: >= 0.85 -> L2, < 0.85 -> L1
            Assert.Equal(2, await policy.ResolveEffectiveLevelAsync("fill_otp", "tenso.com", 0.90));
            Assert.Equal(1, await policy.ResolveEffectiveLevelAsync("fill_otp", "tenso.com", 0.70));

            // 3 L1 acceptances trigger one-time promotion offer on the 3rd acceptance
            var a1 = await policy.RecordAcceptedAsync("fill_otp", "pchome.com.tw", atLevel: 1);
            var a2 = await policy.RecordAcceptedAsync("fill_otp", "pchome.com.tw", atLevel: 1);
            var a3 = await policy.RecordAcceptedAsync("fill_otp", "pchome.com.tw", atLevel: 1);
            Assert.False(a1.ShouldOfferPromotion);
            Assert.False(a2.ShouldOfferPromotion);
            Assert.True(a3.ShouldOfferPromotion);

            // Promote L1 -> L2 and verify LearningEvents entry
            await policy.PromoteToL2Async("fill_otp", "pchome.com.tw");
            Assert.Equal(2, await policy.ResolveEffectiveLevelAsync("fill_otp", "pchome.com.tw", 0.70));

            // Undo on L2 demotes L2 -> L1 and logs demoted event
            var afterUndo = await policy.RecordRejectedOrUndoneAsync("fill_otp", "pchome.com.tw", 2, "user clicked undo");
            Assert.Equal(1, afterUndo);
            Assert.Equal(1, await policy.ResolveEffectiveLevelAsync("fill_otp", "pchome.com.tw", 0.95));

            // Reject on L1 demotes L1 -> L0
            var afterReject = await policy.RecordRejectedOrUndoneAsync("fill_otp", "pchome.com.tw", 1, "user rejected L1");
            Assert.Equal(0, afterReject);
            Assert.Equal(0, await policy.ResolveEffectiveLevelAsync("fill_otp", "pchome.com.tw", 1.00));

            var events = await store.GetRecentLearningEventsAsync(10);
            Assert.Contains(events, e => e.Learner == "AutonomyPolicy" && e.EventType == "promoted" && e.BeforeState == "L1" && e.AfterState == "L2");
            Assert.Contains(events, e => e.Learner == "AutonomyPolicy" && e.EventType == "demoted" && e.BeforeState == "L2" && e.AfterState == "L1");
            Assert.Contains(events, e => e.Learner == "AutonomyPolicy" && e.EventType == "demoted" && e.BeforeState == "L1" && e.AfterState == "L0");
        }
        finally
        {
            CleanupSqlite(tempDb);
        }
    }

    [Fact]
    public async Task L2AutoFill_DomainMismatch_RejectsFill_AndHandleRemainsUsable()
    {
        var clock = new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);
        var vault = new SecretVaultService(() => clock);
        var handle = vault.StoreOtp("482910", "pchome.com.tw", "PChome", "m-pc");

        var page = new ActivePageContext(
            Url: "https://member.pchome.com.tw/login/otp",
            Host: "member.pchome.com.tw",
            RegistrableDomain: "pchome.com.tw",
            Title: "PChome 24h 登入驗證",
            Headings: new[] { "輸入驗證碼" },
            Labels: new[] { "驗證碼" },
            InputCount: 1,
            HasOneTimeCodeInput: true,
            HasFocusedInput: true,
            ObservedAtUtc: clock);

        var browserWindow = new ExternalWindowTarget(
            Hwnd: (nint)1001, ProcessId: 42, ProcessName: "chrome",
            Title: "PChome 24h 登入驗證 - Google Chrome", CapturedAt: clock);

        string? capturedExpectedDomain = null;
        bool? capturedRequireOtp = null;

        var mismatchRes = await SecretaryController.TryExecuteL2AutoFillAsync(
            vault,
            page,
            handle,
            browserWindow,
            (code, expectedDomain, requireOtpField, _) =>
            {
                capturedExpectedDomain = expectedDomain;
                capturedRequireOtp = requireOtpField;
                return Task.FromResult<BrowserOtpFillResult?>(
                    new BrowserOtpFillResult(Filled: false, Url: "https://www.google.com/", Title: "Google", Error: "domain_mismatch"));
            },
            CancellationToken.None);

        Assert.Equal("pchome.com.tw", capturedExpectedDomain);
        Assert.True(capturedRequireOtp);
        Assert.Equal(SecretaryController.L2OtpFillOutcome.DowngradedToL1, mismatchRes.Outcome);
        Assert.Equal("domain_mismatch", mismatchRes.Reason);

        Assert.True(vault.TryResolve(handle, out var stillAliveCode, out _));
        Assert.Equal("482910", stillAliveCode);

        var okRes = await SecretaryController.TryExecuteL2AutoFillAsync(
            vault,
            page,
            handle,
            browserWindow,
            (code, expectedDomain, requireOtpField, _) =>
                Task.FromResult<BrowserOtpFillResult?>(
                    new BrowserOtpFillResult(Filled: true, Title: page.Title, Url: page.Url, Mode: "sole_input")),
            CancellationToken.None);

        Assert.Equal(SecretaryController.L2OtpFillOutcome.Filled, okRes.Outcome);
        Assert.False(vault.TryResolve(handle, out _, out _));
        Assert.False(vault.TryConsume(handle, out _, out _));
    }

    [Fact]
    public async Task FailedFill_DoesNotConsumeHandle_UntilFillActuallySucceeds()
    {
        var clock = new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);
        var vault = new SecretVaultService(() => clock);
        var handle = vault.StoreOtp("135883", "tenso.com", "tenso", "m-tenso");

        var page = new ActivePageContext(
            Url: "https://login.tenso.com/verify",
            Host: "login.tenso.com",
            RegistrableDomain: "tenso.com",
            Title: "tenso Verify",
            Headings: Array.Empty<string>(),
            Labels: new[] { "Code" },
            InputCount: 1,
            HasOneTimeCodeInput: false,
            HasFocusedInput: true,
            ObservedAtUtc: clock);

        var browserWindow = new ExternalWindowTarget(
            Hwnd: (nint)1002, ProcessId: 43, ProcessName: "msedge",
            Title: "tenso Verify - Microsoft Edge", CapturedAt: clock);

        var timeoutRes = await SecretaryController.TryExecuteL2AutoFillAsync(
            vault, page, handle, browserWindow,
            (_, _, _, _) => Task.FromResult<BrowserOtpFillResult?>(null),
            CancellationToken.None);
        Assert.Equal(SecretaryController.L2OtpFillOutcome.DowngradedToL1, timeoutRes.Outcome);
        Assert.True(vault.TryResolve(handle, out var codeAfterTimeout, out _));
        Assert.Equal("135883", codeAfterTimeout);

        var ambiguousRes = await SecretaryController.TryExecuteL2AutoFillAsync(
            vault, page, handle, browserWindow,
            (_, _, _, _) => Task.FromResult<BrowserOtpFillResult?>(new BrowserOtpFillResult(false, Error: "ambiguous_fields")),
            CancellationToken.None);
        Assert.Equal(SecretaryController.L2OtpFillOutcome.DowngradedToL1, ambiguousRes.Outcome);
        Assert.Equal("ambiguous_fields", ambiguousRes.Reason);
        Assert.True(vault.TryResolve(handle, out var codeAfterFail, out _));
        Assert.Equal("135883", codeAfterFail);

        Assert.True(vault.MarkConsumed(handle));
        Assert.False(vault.MarkConsumed(handle));
        Assert.False(vault.TryResolve(handle, out _, out _));
        Assert.Contains(handle, vault.RedactKnownSecrets("Delayed log: 135883"));
        Assert.DoesNotContain("135883", vault.RedactKnownSecrets("Delayed log: 135883"));
    }

    [Fact]
    public async Task L2AutoFill_NeverInvokesUiaFallback_AndBlocksNonBrowserOrIncognitoTargets()
    {
        var clock = new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);
        var vault = new SecretVaultService(() => clock);
        var handle = vault.StoreOtp("654321", "tenso.com", "tenso", "m-tenso-2");

        var page = new ActivePageContext(
            Url: "https://login.tenso.com/verify",
            Host: "login.tenso.com",
            RegistrableDomain: "tenso.com",
            Title: "tenso - Two-Step Verification",
            Headings: Array.Empty<string>(),
            Labels: new[] { "Code" },
            InputCount: 1,
            HasOneTimeCodeInput: false,
            HasFocusedInput: true,
            ObservedAtUtc: clock);

        int extensionCalls = 0;

        var lineWin = new ExternalWindowTarget((nint)2001, 101, "LINE", "LINE - 小明", clock);
        var lineRes = await SecretaryController.TryExecuteL2AutoFillAsync(
            vault, page, handle, lineWin,
            (_, _, _, _) => { extensionCalls++; return Task.FromResult<BrowserOtpFillResult?>(new BrowserOtpFillResult(true)); },
            CancellationToken.None);
        Assert.Equal(SecretaryController.L2OtpFillOutcome.DowngradedToL1, lineRes.Outcome);
        Assert.Equal("window_mismatch", lineRes.Reason);
        Assert.Equal(0, extensionCalls);
        Assert.True(vault.TryResolve(handle, out _, out _));

        var notepadWin = new ExternalWindowTarget((nint)2002, 102, "notepad", "未命名 - 記事本", clock);
        var notepadRes = await SecretaryController.TryExecuteL2AutoFillAsync(
            vault, page, handle, notepadWin,
            (_, _, _, _) => { extensionCalls++; return Task.FromResult<BrowserOtpFillResult?>(new BrowserOtpFillResult(true)); },
            CancellationToken.None);
        Assert.Equal(SecretaryController.L2OtpFillOutcome.DowngradedToL1, notepadRes.Outcome);
        Assert.Equal(0, extensionCalls);

        var settingsWin = new ExternalWindowTarget((nint)2003, 103, "chrome", "Settings - Google Chrome", clock);
        var incognitoWin = new ExternalWindowTarget((nint)2004, 104, "chrome", "New Incognito Tab - Google Chrome", clock);
        Assert.False(SecretaryController.IsExternalWindowMatchingPage(settingsWin, page));
        Assert.False(SecretaryController.IsExternalWindowMatchingPage(incognitoWin, page));

        var onlyChatApps = new[] { lineWin, notepadWin };
        Assert.Null(ActivityMonitorService.SelectOtpUiaTarget(onlyChatApps, serviceHint: "驗證碼已就緒（tenso）", expectedDomain: "tenso.com", senderName: "tenso"));

        var unrelatedChrome = new ExternalWindowTarget((nint)2005, 105, "chrome", "YouTube - Google Chrome", clock.AddSeconds(-30));
        Assert.Null(ActivityMonitorService.SelectOtpUiaTarget(new[] { lineWin, unrelatedChrome }, serviceHint: "tenso", expectedDomain: "tenso.com", senderName: "tenso"));

        var matchingBrowser = new ExternalWindowTarget((nint)2006, 106, "firefox", "tenso - Two-Step Verification - Mozilla Firefox", clock);
        Assert.Equal(matchingBrowser, ActivityMonitorService.SelectOtpUiaTarget(new[] { lineWin, matchingBrowser }, serviceHint: null, expectedDomain: "tenso.com", senderName: "tenso"));
    }

    [Fact]
    public void SecretVault_HandlesAlwaysContainHexLetter_AndBareSuffixIsDetected()
    {
        var vault = new SecretVaultService();
        for (int i = 0; i < 40; i++)
        {
            var h = vault.StoreOtp($"{100000 + i}", "tenso.com", "tenso", $"m-{i}");
            Assert.True(SecretVaultService.IsOtpHandle(h));
            Assert.Contains(h[4..], c => c is >= 'a' and <= 'f');
            Assert.True(vault.TryMatchHandleOrBareSuffix(h, out var m1));
            Assert.Equal(h, m1);
            Assert.True(vault.TryMatchHandleOrBareSuffix(h[4..], out var m2));
            Assert.Equal(h, m2);
        }
        Assert.False(vault.TryMatchHandleOrBareSuffix("777665", out _));
    }

    [Fact]
    public void DoubleVaultProtection_StoreOtpAndVaultAndRedactTriage_NeverReplacesRealDigitsOrLeaksHandle()
    {
        var vault = new SecretVaultService();
        var rawMail = new MailItem(
            Id: "gmail:tenso-repro",
            Subject: "[tenso轉送] 登入驗證碼通知",
            SenderName: "tenso.com",
            SenderEmail: "support@tenso.com",
            ReceivedAt: DateTimeOffset.UtcNow,
            Importance: MailImportance.High,
            Snippet: "驗證碼 304891 ※驗證碼有效期限為10分鐘。",
            Body: "驗證碼\n\n304891\n\n※驗證碼有效期限為10分鐘。",
            IsRead: false,
            ThreadId: "th-tenso");

        var realHandle = vault.StoreOtp("304891", "tenso.com", rawMail.SenderName, rawMail.Id);
        var bareSuffix = realHandle[4..];

        // StoreOtp MUST NOT double-vault bare suffix or handle as a new secret!
        Assert.Equal(realHandle, vault.StoreOtp(bareSuffix, "tenso.com", rawMail.SenderName, rawMail.Id));
        Assert.Equal(realHandle, vault.StoreOtp(realHandle, "tenso.com", rawMail.SenderName, rawMail.Id));

        var badTriage = new TriageResult(
            Urgent: true,
            Title: "tenso 登入驗證碼",
            Summary: $"tenso 寄來登入驗證碼 {realHandle}，有效期限 10 分鐘。",
            ReRemindMinutes: 10,
            Actions: new List<NotificationButtonOption>
            {
                new("fill_otp", "代填驗證碼", bareSuffix, IsPrimary: true),
                new("copy_otp", "複製驗證碼", bareSuffix, IsPrimary: false),
                new("view_mail", "查看郵件", null, IsPrimary: false)
            });

        var (_, safeSummary, safeActions, primaryHandle) = MailSyncService.VaultAndRedactTriage(vault, rawMail, badTriage);

        Assert.Equal(realHandle, primaryHandle);
        var fillBtn = Assert.Single(safeActions!, a => a.Type == "fill_otp");
        Assert.Equal(realHandle, fillBtn.Prompt);
        Assert.True(vault.TryResolve(fillBtn.Prompt, out var resolvedDigits, out _));
        Assert.Equal("304891", resolvedDigits);
        Assert.NotEqual(bareSuffix, resolvedDigits);

        Assert.DoesNotContain("304891", safeSummary);
        Assert.DoesNotContain("otp#", safeSummary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(bareSuffix, safeSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("登入驗證碼", safeSummary);
    }

    [Fact]
    public async Task BrowserPageSignal_FiresSingleUnifiedChannel_AndUniformlySuppressesBrowserUiaWindowFocus()
    {
        var now = DateTimeOffset.UtcNow;
        var pageSignal = new CachedBrowserPageSignal(
            Url: "https://www.tenso.com/cht/login/confirm",
            Title: "登入確認 | tenso轉送服務",
            InputCount: 1,
            Labels: new[] { "驗證碼" },
            Headings: new[] { "登入確認" },
            Excerpt: "請輸入驗證碼",
            CapturedAt: now.AddSeconds(-2),
            HasOneTimeCodeInput: false,
            HasFocusedInput: true);

        // 1) Browser UIA WindowFocused MUST be suppressed uniformly for ALL pages with a fresh extension signal
        Assert.True(ActivityMonitorService.ShouldSuppressBrowserUiaWindowFocus(
            pageSignal,
            "登入確認 | tenso轉送服務 和其他 3 個頁面 - 個人 - Microsoft Edge",
            now));

        // Same title within 15s is also suppressed
        Assert.True(ActivityMonitorService.ShouldSuppressBrowserUiaWindowFocus(
            pageSignal with { CapturedAt = now.AddSeconds(-10) },
            "登入確認 | tenso轉送服務 - Microsoft Edge",
            now));

        // Stale signal (>15s) does NOT suppress
        Assert.False(ActivityMonitorService.ShouldSuppressBrowserUiaWindowFocus(
            pageSignal with { CapturedAt = now.AddSeconds(-30) },
            "其他頁面 - Microsoft Edge",
            now));

        // 2) HandleBrowserPageSignalAsync fires ONLY BrowserPageSignalReceived, NEVER duplicate WindowFocused
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = "Data Source=:memory:" })
            .Build();
        var settings = new AppSettingsService(cfg);
        var localizer = new UiLocalizer(settings, new SqliteStore(cfg), new FakeGateway(), NullLogger<UiLocalizer>.Instance);
        using var monitor = new ActivityMonitorService(localizer, NullLogger<ActivityMonitorService>.Instance);

        int pageSignalCount = 0;
        int windowFocusCount = 0;
        monitor.BrowserPageSignalReceived += _ => pageSignalCount++;
        monitor.WindowFocused += (_, _) => windowFocusCount++;

        await monitor.HandleBrowserPageSignalAsync(new BrowserPageSignal(
            Url: "https://www.tenso.com/cht/login/confirm",
            Title: "登入確認 | tenso轉送服務",
            InputCount: 1,
            Labels: new[] { "驗證碼" },
            Headings: new[] { "登入確認" },
            Excerpt: "驗證碼"));

        Assert.Equal(1, pageSignalCount);
        Assert.Equal(0, windowFocusCount);
    }

    [Theory]
    [InlineData("sync_mail", "sync")]
    [InlineData("get_mail_detail", "detail")]
    [InlineData("get_gmail_detail", "detail")]
    [InlineData("read_gmail", "list")]
    [InlineData("manage_mail", "list")]
    public void ManageMailSkill_InfersLegacyAliasDefaultAction(string requestedAlias, string expectedAction)
    {
        Assert.Equal(expectedAction, ManageMailSkill.InferDefaultAction(requestedAlias));
    }

    private static StateMachineHarness CreateStateMachineHarness(
        Func<DateTimeOffset> utcNow,
        Func<string, string, string>? llmResponder = null)
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"aisec_sm_test_{Guid.NewGuid():N}.db");
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = $"Data Source={tempDb}"
            })
            .Build();

        var store = new SqliteStore(cfg);
        var settings = new AppSettingsService(cfg);
        var gateway = new FakeGateway(llmResponder);
        var localizer = new UiLocalizer(settings, store, gateway, NullLogger<UiLocalizer>.Instance);
        var fakeMailSkill = new FakeManageMailSkill();
        var registry = new SkillRegistry(new IBasicSkill[] { fakeMailSkill }, store, localizer, NullLogger<SkillRegistry>.Instance);
        var guard = new GuardService(cfg, new GuardAuditStore(), new InteractionGateway(), settings, localizer, NullLogger<GuardService>.Instance);
        guard.BindSkillCatalog(registry);
        var customSkills = new CustomSkillService(store, new SandboxService(cfg), guard, settings, localizer, new EmptyServiceProvider(), NullLogger<CustomSkillService>.Instance);
        var worker = new WorkerAgent(customSkills, guard, registry, localizer, NullLogger<WorkerAgent>.Instance);
        var presence = new UserPresenceService(settings, NullLogger<UserPresenceService>.Instance, utcNow, () => TimeSpan.Zero, () => false);
        var vault = new SecretVaultService(utcNow);
        var blackboard = new AgentBlackboard();
        var bridge = new BrowserFormBridgeService();

        var agent = new StateMachineAgent(
            store, worker, guard, gateway, bridge, blackboard, presence, vault,
            NullLogger<StateMachineAgent>.Instance, utcNow);

        return new StateMachineHarness(tempDb, store, vault, gateway, presence, fakeMailSkill, agent);
    }

    private sealed class StateMachineHarness : IDisposable
    {
        public string TempDb { get; }
        public SqliteStore Store { get; }
        public SecretVaultService Vault { get; }
        public FakeGateway Gateway { get; }
        public UserPresenceService Presence { get; }
        public FakeManageMailSkill FakeMailSkill { get; }
        public StateMachineAgent Agent { get; }

        public StateMachineHarness(
            string tempDb,
            SqliteStore store,
            SecretVaultService vault,
            FakeGateway gateway,
            UserPresenceService presence,
            FakeManageMailSkill fakeMailSkill,
            StateMachineAgent agent)
        {
            TempDb = tempDb;
            Store = store;
            Vault = vault;
            Gateway = gateway;
            Presence = presence;
            FakeMailSkill = fakeMailSkill;
            Agent = agent;
        }

        public void Dispose()
        {
            Agent.Dispose();
            Presence.Dispose();
            CleanupSqlite(TempDb);
        }
    }

    private static void CleanupSqlite(string dbPath)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        try { if (File.Exists(dbPath + "-wal")) File.Delete(dbPath + "-wal"); } catch { }
        try { if (File.Exists(dbPath + "-shm")) File.Delete(dbPath + "-shm"); } catch { }
    }

    private sealed class FakeManageMailSkill : IBasicSkill
    {
        public int Executions { get; private set; }
        public string Name => "manage_mail";
        public string DisplayDescription => "Fake Mail Sync";
        public string MainAgentSpec => "";
        public string PlannerSpec => "";

        public Task ExecuteAsync(SkillExecutionContext ctx)
        {
            Executions++;
            ctx.Observations.Add("Synced 0 emails.");
            ctx.CompletedSteps.Add("manage_mail:sync:all");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGateway : IModelGateway
    {
        private readonly Func<string, string, string>? _responder;
        public List<string> RecordedCallers { get; } = new();
        public List<(string Caller, string UserText)> RecordedPrompts { get; } = new();

        public FakeGateway(Func<string, string, string>? responder = null) => _responder = responder;

        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ModelMessage> messages, LlmCallOptions options, CancellationToken ct = default)
        {
            var userText = messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
            RecordedCallers.Add(options.Agent);
            RecordedPrompts.Add((options.Agent, userText));
            var content = _responder?.Invoke(options.Agent, userText) ?? """{"mode":"ignore"}""";
            return Task.FromResult(new ModelResponse(content, 100));
        }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
