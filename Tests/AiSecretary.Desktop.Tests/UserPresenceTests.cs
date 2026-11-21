using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AiSecretary.Desktop.Tests;

public sealed class UserPresenceTests
{
    private static AppSettingsService CreateSettings(
        bool afkSilence = true,
        int silenceMins = 15,
        bool welcomeBack = true,
        int welcomeMinMins = 25,
        int audioWelcomeMins = 60)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scheduler:AfkSilenceEnabled"] = afkSilence.ToString(),
                ["Scheduler:AfkSilenceMinutes"] = silenceMins.ToString(),
                ["Scheduler:AfkWelcomeBackEnabled"] = welcomeBack.ToString(),
                ["Scheduler:AfkWelcomeBackMinMinutes"] = welcomeMinMins.ToString(),
                ["Scheduler:AfkAudioWelcomeBackMinutes"] = audioWelcomeMins.ToString()
            })
            .Build();
        return new AppSettingsService(cfg);
    }

    [Fact]
    public void IdlePast15Min_EvenWithYouTubeAudioPlaying_EntersSilentIdleToSaveTokens()
    {
        var now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        var idle = TimeSpan.FromMinutes(10);
        bool audioPlaying = true;
        var settings = CreateSettings(silenceMins: 15, welcomeMinMins: 25, audioWelcomeMins: 60);
        using var svc = new UserPresenceService(settings, null, () => now, () => idle, () => audioPlaying);

        svc.EvaluateTick();
        Assert.False(svc.IsSilentForTokens);
        Assert.Equal(PresenceState.Active, svc.GetSnapshot().State);

        // At 15 min without mouse/keyboard, silence activates even though YouTube audio is playing
        now = now.AddMinutes(5);
        idle = TimeSpan.FromMinutes(15);
        svc.EvaluateTick();
        Assert.True(svc.IsSilentForTokens);
        Assert.Equal(PresenceState.SilentIdle, svc.GetSnapshot().State);
    }

    [Fact]
    public void ReturningAfter30MinWatchingVideo_DoesNotTriggerFalseWelcomeBack()
    {
        var now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        var idle = TimeSpan.FromMinutes(16);
        bool audioPlaying = true;
        var settings = CreateSettings(silenceMins: 15, welcomeMinMins: 25, audioWelcomeMins: 60);
        using var svc = new UserPresenceService(settings, null, () => now, () => idle, () => audioPlaying);

        WelcomeBackEvent? fired = null;
        svc.WelcomeBackDetected += ev => fired = ev;

        svc.EvaluateTick();
        Assert.True(svc.IsSilentForTokens);

        // Still watching video at 30 min idle
        now = now.AddMinutes(14);
        idle = TimeSpan.FromMinutes(30);
        svc.EvaluateTick();
        Assert.True(svc.IsSilentForTokens);

        // User moves mouse at 31 min while video is still playing -> silent resume, NO welcome-back!
        now = now.AddMinutes(1);
        idle = TimeSpan.Zero;
        svc.EvaluateTick();
        Assert.False(svc.IsSilentForTokens);
        Assert.Null(fired);
    }

    [Fact]
    public void ReturningAfter65MinWithVideoPlaying_TriggersAmbiguousSafeWelcomeBack()
    {
        var now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        var idle = TimeSpan.FromMinutes(16);
        bool audioPlaying = true;
        var settings = CreateSettings(silenceMins: 15, welcomeMinMins: 25, audioWelcomeMins: 60);
        using var svc = new UserPresenceService(settings, null, () => now, () => idle, () => audioPlaying);

        WelcomeBackEvent? fired = null;
        svc.WelcomeBackDetected += ev => fired = ev;

        svc.EvaluateTick();
        now = now.AddMinutes(49);
        idle = TimeSpan.FromMinutes(65);
        svc.EvaluateTick();

        // User moves mouse after 65m continuous zero input with audio
        now = now.AddSeconds(10);
        idle = TimeSpan.Zero;
        svc.EvaluateTick();

        Assert.NotNull(fired);
        Assert.Equal(WelcomeBackReason.LongIdleReturnedWithAudio, fired!.Reason);
        Assert.True(fired.AudioWasPlaying);
        Assert.True(fired.AwayDuration >= TimeSpan.FromMinutes(65));
    }

    [Fact]
    public void VideoEndedEarlyThenSilentFor26Min_RecognizesRealAbsenceOnReturn()
    {
        var now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        var idle = TimeSpan.FromMinutes(16);
        bool audioPlaying = true;
        var settings = CreateSettings(silenceMins: 15, welcomeMinMins: 25, audioWelcomeMins: 60);
        using var svc = new UserPresenceService(settings, null, () => now, () => idle, () => audioPlaying);

        WelcomeBackEvent? fired = null;
        svc.WelcomeBackDetected += ev => fired = ev;

        // Audio playing at minute 16
        svc.EvaluateTick();

        // Video ends; 26 minutes of complete silence pass (total idle = 42m)
        audioPlaying = false;
        now = now.AddMinutes(26);
        idle = TimeSpan.FromMinutes(42);
        svc.EvaluateTick();

        // User returns and moves mouse -> recognized as no-audio real absence!
        now = now.AddSeconds(5);
        idle = TimeSpan.Zero;
        svc.EvaluateTick();

        Assert.NotNull(fired);
        Assert.Equal(WelcomeBackReason.IdleReturnedNoAudio, fired!.Reason);
        Assert.False(fired.AudioWasPlaying);
    }

    [Fact]
    public void OfficeShortScreenLock_SilencesWhileLocked_ButNeverGreetsOnQuickUnlock()
    {
        var now = new DateTimeOffset(2026, 11, 21, 14, 0, 0, TimeSpan.Zero);
        var settings = CreateSettings(silenceMins: 15, welcomeMinMins: 25, audioWelcomeMins: 60);
        using var svc = new UserPresenceService(settings, null, () => now, () => TimeSpan.Zero, () => false);

        WelcomeBackEvent? fired = null;
        svc.WelcomeBackDetected += ev => fired = ev;

        // User locks screen (Win+L) for a 5-minute bathroom break
        svc.OnSessionLockForTest();
        Assert.True(svc.IsSilentForTokens);
        Assert.True(svc.IsSessionLocked);

        // Unlock 5 minutes later -> silently resumes, NO welcome-back!
        now = now.AddMinutes(5);
        svc.OnSessionUnlockForTest();
        Assert.False(svc.IsSilentForTokens);
        Assert.False(svc.IsSessionLocked);
        Assert.Null(fired);
    }

    [Fact]
    public void LongScreenLock_TriggersWelcomeBack_AndRespects60MinCooldown()
    {
        var now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        var settings = CreateSettings(silenceMins: 15, welcomeMinMins: 25, audioWelcomeMins: 60);
        using var svc = new UserPresenceService(settings, null, () => now, () => TimeSpan.Zero, () => false);

        var events = new List<WelcomeBackEvent>();
        svc.WelcomeBackDetected += ev => events.Add(ev);

        // Lunch break: 40 min lock
        svc.OnSessionLockForTest();
        now = now.AddMinutes(40);
        svc.OnSessionUnlockForTest();

        Assert.Single(events);
        Assert.Equal(WelcomeBackReason.ScreenUnlocked, events[0].Reason);
        Assert.Equal(TimeSpan.FromMinutes(40), events[0].AwayDuration);

        // Another 30 min lock right away (within 60 min cooldown) -> suppressed
        now = now.AddMinutes(5);
        svc.OnSessionLockForTest();
        now = now.AddMinutes(30);
        svc.OnSessionUnlockForTest();
        Assert.Single(events);

        // 65 minutes after first greeting -> cooldown elapsed, next long absence triggers again
        now = now.AddMinutes(30);
        svc.OnSessionLockForTest();
        now = now.AddMinutes(35);
        svc.OnSessionUnlockForTest();
        Assert.Equal(2, events.Count);
    }
}
