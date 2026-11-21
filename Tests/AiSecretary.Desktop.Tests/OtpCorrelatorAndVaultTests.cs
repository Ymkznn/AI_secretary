using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Services;
using Microsoft.Extensions.Configuration;
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
        Assert.Equal(expected, OtpExtractor.ExtractRegistrableDomain(input));
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

        // Redaction replaces plaintext with handle
        var redacted = vault.RedactKnownSecrets("tenso 登入驗證碼 135883，有效期限 10 分鐘");
        Assert.DoesNotContain("135883", redacted);
        Assert.Contains(handle, redacted);

        // First consume succeeds and returns real code
        Assert.True(vault.TryConsume(handle, out var secret, out var meta));
        Assert.Equal("135883", secret);
        Assert.Equal("tenso.com", meta?.SenderDomain);

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

    [Fact]
    public void OtpExtractor_ExtractsValidCodes_AndIgnoresYearsOrNoContext()
    {
        var otpMail = new MailItem(
            "m1", "tenso 登入確認", "tenso", "noreply@mail.tenso.com",
            DateTimeOffset.UtcNow, MailImportance.High, "驗證碼為 135883", "您的登入驗證碼：135883，10分鐘內有效", false, "t1");
        Assert.True(OtpExtractor.TryExtractFromMail(otpMail, out var code));
        Assert.Equal("135883", code);

        var nonOtpMail = new MailItem(
            "m2", "2026 年度報告", "HR", "hr@company.com",
            DateTimeOffset.UtcNow, MailImportance.Normal, "預算 $123456", "2026 年報告已上傳，總金額 $123456。", false, "t2");
        Assert.False(OtpExtractor.TryExtractFromMail(nonOtpMail, out _));
    }

    [Fact]
    public void OtpSituationDetector_PageFirst_ThenMail_ReachesL2Confidence()
    {
        var clock = new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);
        var vault = new SecretVaultService(() => clock);
        var detector = new OtpSituationDetector(vault, () => clock);

        // Step 1: User opens OTP page first
        var first = detector.OnPageSignal(
            "https://member.pchome.com.tw/login/otp",
            "PChome 24h 登入驗證",
            new[] { "輸入驗證碼" },
            otpFieldPresent: true,
            otpFieldFocused: true,
            observedAtUtc: clock);
        Assert.Null(first);

        // Step 2: 25 seconds later, OTP mail arrives from auth.pchome.com.tw
        clock = clock.AddSeconds(25);
        var handle = vault.StoreOtp("482910", "pchome.com.tw", "PChome", "m-pc");
        var decision = detector.OnOtpMailEvent(new OtpMailEvent(
            "m-pc", "auth.pchome.com.tw", "pchome.com.tw", "PChome", "登入驗證碼", clock, handle));

        Assert.NotNull(decision);
        Assert.Equal(OtpDecisionKind.SingleMatch, decision!.Kind);
        Assert.Single(decision.Candidates);
        Assert.True(decision.Candidates[0].Confidence >= OtpSituationDetector.AutoFillL2Threshold,
            $"Expected confidence >= 0.85, got {decision.Candidates[0].Confidence} ({decision.Candidates[0].Reason})");
        Assert.Equal(handle, decision.Candidates[0].Mail.Handle);
    }

    [Fact]
    public void OtpSituationDetector_MailFirst_ThenPage_MatchesCorrelatedOtp()
    {
        var clock = new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);
        var vault = new SecretVaultService(() => clock);
        var detector = new OtpSituationDetector(vault, () => clock);

        // Step 1: Mail arrives first
        var handle = vault.StoreOtp("771234", "tenso.com", "tenso", "m-tenso");
        var first = detector.OnOtpMailEvent(new OtpMailEvent(
            "m-tenso", "mail.tenso.com", "tenso.com", "tenso", "Login verification code", clock, handle));
        Assert.Null(first);

        // Step 2: 40 seconds later, user switches to tenso OTP page
        clock = clock.AddSeconds(40);
        var decision = detector.OnPageSignal(
            "https://login.tenso.com/verify",
            "tenso - Two-Step Verification",
            new[] { "Enter verification code" },
            otpFieldPresent: true,
            otpFieldFocused: false,
            observedAtUtc: clock);

        Assert.NotNull(decision);
        Assert.Equal(OtpDecisionKind.SingleMatch, decision!.Kind);
        Assert.True(decision.Candidates[0].Confidence >= OtpSituationDetector.AutoFillL2Threshold);
    }

    [Fact]
    public void OtpSituationDetector_UnrelatedDomainAndTitle_DoesNotMatch()
    {
        var clock = new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);
        var vault = new SecretVaultService(() => clock);
        var detector = new OtpSituationDetector(vault, () => clock);

        detector.OnPageSignal(
            "https://login.bank-a.com/otp",
            "Bank A Verification",
            Array.Empty<string>(),
            otpFieldPresent: true,
            otpFieldFocused: true,
            observedAtUtc: clock);

        clock = clock.AddSeconds(10);
        var handle = vault.StoreOtp("112233", "shop-b.com", "Shop B", "m-other");
        var decision = detector.OnOtpMailEvent(new OtpMailEvent(
            "m-other", "noreply.shop-b.com", "shop-b.com", "Shop B", "Verification", clock, handle));

        Assert.Null(decision);
    }

    [Fact]
    public void OtpSituationDetector_MultipleMatchingOtps_ReturnsMultipleMatchesNeverSingleAutoFill()
    {
        var clock = new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);
        var vault = new SecretVaultService(() => clock);
        var detector = new OtpSituationDetector(vault, () => clock);

        var h1 = vault.StoreOtp("111111", "tenso.com", "tenso", "m1");
        detector.OnOtpMailEvent(new OtpMailEvent("m1", "mail.tenso.com", "tenso.com", "tenso", "OTP 1", clock, h1));

        clock = clock.AddSeconds(15);
        var h2 = vault.StoreOtp("222222", "tenso.com", "tenso", "m2");
        detector.OnOtpMailEvent(new OtpMailEvent("m2", "mail.tenso.com", "tenso.com", "tenso", "OTP 2", clock, h2));

        clock = clock.AddSeconds(5);
        var decision = detector.OnPageSignal(
            "https://login.tenso.com/verify",
            "tenso Verify",
            Array.Empty<string>(),
            otpFieldPresent: true,
            otpFieldFocused: true,
            observedAtUtc: clock);

        Assert.NotNull(decision);
        Assert.Equal(OtpDecisionKind.MultipleMatches, decision!.Kind);
        Assert.Equal(2, decision.Candidates.Count);
    }

    [Fact]
    public void OtpSituationDetector_OutsideThreeMinuteWindow_DoesNotMatch()
    {
        var clock = new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);
        var vault = new SecretVaultService(() => clock);
        var detector = new OtpSituationDetector(vault, () => clock);

        var h1 = vault.StoreOtp("654321", "tenso.com", "tenso", "m-old");
        detector.OnOtpMailEvent(new OtpMailEvent("m-old", "mail.tenso.com", "tenso.com", "tenso", "OTP", clock, h1));

        // 4 minutes later -> outside ±3 min window
        clock = clock.AddMinutes(4);
        var decision = detector.OnPageSignal(
            "https://login.tenso.com/verify",
            "tenso Verify",
            Array.Empty<string>(),
            otpFieldPresent: true,
            otpFieldFocused: true,
            observedAtUtc: clock);

        Assert.Null(decision);
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
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
            try { if (File.Exists(tempDb + "-wal")) File.Delete(tempDb + "-wal"); } catch { }
            try { if (File.Exists(tempDb + "-shm")) File.Delete(tempDb + "-shm"); } catch { }
        }
    }

    [Fact]
    public async Task L2AutoFill_DomainMismatch_RejectsFill_AndHandleRemainsUsable()
    {
        var clock = new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);
        var vault = new SecretVaultService(() => clock);
        var handle = vault.StoreOtp("482910", "pchome.com.tw", "PChome", "m-pc");

        var page = new OtpPageSignal(
            Url: "https://member.pchome.com.tw/login/otp",
            Host: "member.pchome.com.tw",
            RegistrableDomain: "pchome.com.tw",
            Title: "PChome 24h 登入驗證",
            Headings: new[] { "輸入驗證碼" },
            OtpFieldPresent: true,
            OtpFieldFocused: true,
            ObservedAtUtc: clock);

        var browserWindow = new ExternalWindowTarget(
            Hwnd: (nint)1001, ProcessId: 42, ProcessName: "chrome",
            Title: "PChome 24h 登入驗證 - Google Chrome", CapturedAt: clock);

        string? capturedExpectedDomain = null;
        bool? capturedRequireOtp = null;

        // Simulate extension rejecting due to active tab domain mismatch (e.g. tab switched right before fill)
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

        // Handle MUST still be resolvable & usable after domain mismatch!
        Assert.True(vault.TryResolve(handle, out var stillAliveCode, out _));
        Assert.Equal("482910", stillAliveCode);

        // Once user switches back to matching domain, fill succeeds and THEN consumes the handle.
        var okRes = await SecretaryController.TryExecuteL2AutoFillAsync(
            vault,
            page,
            handle,
            browserWindow,
            (code, expectedDomain, requireOtpField, _) =>
                Task.FromResult<BrowserOtpFillResult?>(
                    new BrowserOtpFillResult(Filled: true, Title: page.Title, Url: page.Url, Mode: "single")),
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

        var page = new OtpPageSignal(
            Url: "https://login.tenso.com/verify",
            Host: "login.tenso.com",
            RegistrableDomain: "tenso.com",
            Title: "tenso Verify",
            Headings: Array.Empty<string>(),
            OtpFieldPresent: true,
            OtpFieldFocused: true,
            ObservedAtUtc: clock);

        var browserWindow = new ExternalWindowTarget(
            Hwnd: (nint)1002, ProcessId: 43, ProcessName: "msedge",
            Title: "tenso Verify - Microsoft Edge", CapturedAt: clock);

        // 1) Extension timeout (returns null) -> handle NOT consumed
        var timeoutRes = await SecretaryController.TryExecuteL2AutoFillAsync(
            vault, page, handle, browserWindow,
            (_, _, _, _) => Task.FromResult<BrowserOtpFillResult?>(null),
            CancellationToken.None);
        Assert.Equal(SecretaryController.L2OtpFillOutcome.DowngradedToL1, timeoutRes.Outcome);
        Assert.True(vault.TryResolve(handle, out var codeAfterTimeout, out _));
        Assert.Equal("135883", codeAfterTimeout);

        // 2) Extension reports no_otp_field (same domain, navigated away from OTP page) -> handle NOT consumed
        var noFieldRes = await SecretaryController.TryExecuteL2AutoFillAsync(
            vault, page, handle, browserWindow,
            (_, _, _, _) => Task.FromResult<BrowserOtpFillResult?>(new BrowserOtpFillResult(false, Error: "no_otp_field")),
            CancellationToken.None);
        Assert.Equal(SecretaryController.L2OtpFillOutcome.DowngradedToL1, noFieldRes.Outcome);
        Assert.Equal("no_otp_field", noFieldRes.Reason);
        Assert.True(vault.TryResolve(handle, out var codeAfterNoField, out _));
        Assert.Equal("135883", codeAfterNoField);

        // 3) Explicitly marking consumed after a successful fill/copy invalidates the handle while preserving redaction tombstone
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

        var page = new OtpPageSignal(
            Url: "https://login.tenso.com/verify",
            Host: "login.tenso.com",
            RegistrableDomain: "tenso.com",
            Title: "tenso - Two-Step Verification",
            Headings: Array.Empty<string>(),
            OtpFieldPresent: true,
            OtpFieldFocused: true,
            ObservedAtUtc: clock);

        int extensionCalls = 0;

        // Case 1: User switched to LINE before OTP email arrived -> C# pre-check downgrades to L1 immediately (0 extension calls, 0 UIA calls)
        var lineWin = new ExternalWindowTarget((nint)2001, 101, "LINE", "LINE - 小明", clock);
        var lineRes = await SecretaryController.TryExecuteL2AutoFillAsync(
            vault, page, handle, lineWin,
            (_, _, _, _) => { extensionCalls++; return Task.FromResult<BrowserOtpFillResult?>(new BrowserOtpFillResult(true)); },
            CancellationToken.None);
        Assert.Equal(SecretaryController.L2OtpFillOutcome.DowngradedToL1, lineRes.Outcome);
        Assert.Equal("window_mismatch", lineRes.Reason);
        Assert.Equal(0, extensionCalls);
        Assert.True(vault.TryResolve(handle, out _, out _));

        // Case 2: User switched to Notepad -> downgraded to L1, 0 extension calls
        var notepadWin = new ExternalWindowTarget((nint)2002, 102, "notepad", "未命名 - 記事本", clock);
        var notepadRes = await SecretaryController.TryExecuteL2AutoFillAsync(
            vault, page, handle, notepadWin,
            (_, _, _, _) => { extensionCalls++; return Task.FromResult<BrowserOtpFillResult?>(new BrowserOtpFillResult(true)); },
            CancellationToken.None);
        Assert.Equal(SecretaryController.L2OtpFillOutcome.DowngradedToL1, notepadRes.Outcome);
        Assert.Equal(0, extensionCalls);

        // Case 3: User switched to chrome://settings or Incognito tab -> browser title no longer matches -> downgraded to L1
        var settingsWin = new ExternalWindowTarget((nint)2003, 103, "chrome", "Settings - Google Chrome", clock);
        var incognitoWin = new ExternalWindowTarget((nint)2004, 104, "chrome", "New Incognito Tab - Google Chrome", clock);
        Assert.False(SecretaryController.IsExternalWindowMatchingOtpPage(settingsWin, page));
        Assert.False(SecretaryController.IsExternalWindowMatchingOtpPage(incognitoWin, page));

        // Case 4: Even when L1/manual UIA target selector runs, LINE/Notepad/Discord are NEVER blindly picked!
        var onlyChatApps = new[] { lineWin, notepadWin };
        Assert.Null(ActivityMonitorService.SelectOtpUiaTarget(onlyChatApps, serviceHint: "驗證碼已就緒（tenso）", expectedDomain: "tenso.com", senderName: "tenso"));

        // If a non-browser foreground app is active AND Chrome is in the background on an unrelated tab, do NOT blindly hijack the unrelated Chrome tab
        var unrelatedChrome = new ExternalWindowTarget((nint)2005, 105, "chrome", "YouTube - Google Chrome", clock.AddSeconds(-30));
        Assert.Null(ActivityMonitorService.SelectOtpUiaTarget(new[] { lineWin, unrelatedChrome }, serviceHint: "tenso", expectedDomain: "tenso.com", senderName: "tenso"));

        // Matching native app or matching browser IS allowed on L1 UIA fallback
        var matchingBrowser = new ExternalWindowTarget((nint)2006, 106, "firefox", "tenso - Two-Step Verification - Mozilla Firefox", clock);
        Assert.Equal(matchingBrowser, ActivityMonitorService.SelectOtpUiaTarget(new[] { lineWin, matchingBrowser }, serviceHint: null, expectedDomain: "tenso.com", senderName: "tenso"));
    }

    [Fact]
    public void MailRedactionBeforeTriage_ReplacesVaultedOtpWithHandleInSubjectSnippetAndBody()
    {
        var clock = new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);
        var vault = new SecretVaultService(() => clock);
        var rawMail = new MailItem(
            Id: "m-redact",
            Subject: "您的登入驗證碼 839201",
            SenderName: "tenso",
            SenderEmail: "noreply@mail.tenso.com",
            ReceivedAt: clock,
            Importance: MailImportance.High,
            Snippet: "驗證碼為 839201，請於 10 分鐘內輸入",
            Body: "您好，您的登入驗證碼是 839201。",
            IsRead: false,
            ThreadId: "t-1");

        Assert.True(OtpExtractor.TryExtractFromMail(rawMail, out var extracted));
        var handle = vault.StoreOtp(extracted, "tenso.com", rawMail.SenderName, rawMail.Id);

        var safeMail = MailSyncService.RedactMailForTriage(vault, rawMail);
        Assert.DoesNotContain("839201", safeMail.Subject);
        Assert.DoesNotContain("839201", safeMail.Snippet);
        Assert.DoesNotContain("839201", safeMail.Body);
        Assert.Contains(handle, safeMail.Subject);
        Assert.Contains(handle, safeMail.Snippet);
        Assert.Contains(handle, safeMail.Body);
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
        // Real 6-digit numeric OTP must NOT be confused with a handle suffix
        Assert.False(vault.TryMatchHandleOrBareSuffix("777665", out _));
    }

    [Theory]
    [InlineData("sync_mail", "sync")]
    [InlineData("get_mail_detail", "detail")]
    [InlineData("get_gmail_detail", "detail")]
    [InlineData("read_gmail", "list")]
    [InlineData("manage_mail", "list")]
    public void ManageMailSkill_InfersLegacyAliasDefaultAction(string requestedAlias, string expectedAction)
    {
        Assert.Equal(expectedAction, Skills.ManageMailSkill.InferDefaultAction(requestedAlias));
    }
}
