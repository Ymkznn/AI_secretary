using System.Runtime.InteropServices;
using AiSecretary.Desktop.Models;
using Microsoft.Win32;

namespace AiSecretary.Desktop.Services;

public enum PresenceState
{
    Active,
    SilentIdle,
    LockedOrAway
}

public enum WelcomeBackReason
{
    ScreenUnlocked,
    SystemResumed,
    DisplayWoke,
    IdleReturnedNoAudio,
    LongIdleReturnedWithAudio
}

public sealed record WelcomeBackEvent(
    WelcomeBackReason Reason,
    TimeSpan AwayDuration,
    bool AudioWasPlaying,
    DateTimeOffset ReturnedAtUtc);

public sealed record PresenceSnapshot(
    PresenceState State,
    bool IsSilentForTokens,
    bool IsSessionLocked,
    bool IsAudioPlaying,
    TimeSpan IdleDuration,
    TimeSpan? AwayDuration);

/// <summary>
/// Native Windows presence &amp; AFK monitor (zero global hooks, zero camera, zero audio recording).
/// Decouples token-saving silence from welcome-back greetings:
/// - Token-saving silence activates immediately on screen lock/sleep/display-off, OR after <c>AfkSilenceMinutes</c>
///   (default 15m) of no input REGARDLESS of whether video/YouTube is playing.
/// - Welcome-back greetings require real away duration (<c>AfkWelcomeBackMinMinutes</c>, default 25m) so short office
///   screen-locks (e.g. bathroom/water breaks) silently resume without nagging, and active audio/video uses a longer
///   threshold (<c>AfkAudioWelcomeBackMinutes</c>, default 60m) to avoid false greetings while watching streams/anime.
/// </summary>
public sealed class UserPresenceService : IDisposable
{
    private static readonly TimeSpan WelcomeBackCooldown = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan ActiveInputResetThreshold = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RecentAudioHoldWindow = TimeSpan.FromMinutes(2);

    private readonly AppSettingsService _settings;
    private readonly ILogger<UserPresenceService>? _logger;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan> _readIdleDuration;
    private readonly Func<bool> _readAudioPlaying;

    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;
    private bool _systemEventsHooked;
    private int _disposed;

    private PresenceState _state = PresenceState.Active;
    private bool _isSessionLocked;
    private bool _isSuspended;
    private bool _isDisplayOff;
    private bool _lastSampledAudioPlaying;
    private DateTimeOffset? _awayStartedUtc;
    private DateTimeOffset? _lastAudioHeardAtUtc;
    private DateTimeOffset _lastWelcomeBackUtc = DateTimeOffset.MinValue;
    private TimeSpan _lastIdleDuration = TimeSpan.Zero;

    public event Action<WelcomeBackEvent>? WelcomeBackDetected;
    public event Action<PresenceSnapshot>? PresenceChanged;
    public event Action? ReturnedFromSilence;

    public UserPresenceService(AppSettingsService settings, ILogger<UserPresenceService> logger)
        : this(settings, logger, () => DateTimeOffset.UtcNow, ReadWindowsIdleDuration, ReadWindowsAudioPeakPlaying)
    {
    }

    internal UserPresenceService(
        AppSettingsService settings,
        ILogger<UserPresenceService>? logger,
        Func<DateTimeOffset> utcNow,
        Func<TimeSpan> readIdleDuration,
        Func<bool> readAudioPlaying)
    {
        _settings = settings;
        _logger = logger;
        _utcNow = utcNow;
        _readIdleDuration = readIdleDuration;
        _readAudioPlaying = readAudioPlaying;
    }

    public bool IsSessionLocked
    {
        get { lock (_lock) return _isSessionLocked; }
    }

    public bool IsSilentForTokens
    {
        get
        {
            if (!_settings.Current.Scheduler.AfkSilenceEnabled)
                return false;
            lock (_lock)
                return _state is PresenceState.SilentIdle or PresenceState.LockedOrAway;
        }
    }

    public PresenceSnapshot GetSnapshot()
    {
        var now = _utcNow();
        var silenceEnabled = _settings.Current.Scheduler.AfkSilenceEnabled;
        lock (_lock)
        {
            var isSilent = silenceEnabled && _state is PresenceState.SilentIdle or PresenceState.LockedOrAway;
            TimeSpan? away = _awayStartedUtc.HasValue ? now - _awayStartedUtc.Value : null;
            if (away.HasValue && away.Value < TimeSpan.Zero) away = TimeSpan.Zero;
            return new PresenceSnapshot(_state, isSilent, _isSessionLocked, _lastSampledAudioPlaying, _lastIdleDuration, away);
        }
    }

    public string FormatStatusLabel()
    {
        var snap = GetSnapshot();
        if (!snap.IsSilentForTokens)
            return "在席";

        var mins = Math.Max(0, (int)Math.Floor((snap.AwayDuration ?? snap.IdleDuration).TotalMinutes));
        if (snap.IsSessionLocked)
            return mins > 0 ? $"鎖屏靜默（{mins} 分鐘）" : "鎖屏靜默";
        if (snap.State == PresenceState.LockedOrAway)
            return mins > 0 ? $"離席省 Token（{mins} 分鐘）" : "離席省 Token";
        return snap.IsAudioPlaying
            ? $"播片／靜默省 Token（{mins} 分鐘）"
            : $"中離靜默省 Token（{mins} 分鐘）";
    }

    public void Start()
    {
        if (_loopTask is not null) return;
        try
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _systemEventsHooked = true;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "SystemEvents subscription skipped");
        }
        _loopTask = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    public void NotifyDisplayPowerState(bool isOn)
    {
        if (isOn)
            HandleResumeSignal(WelcomeBackReason.DisplayWoke, clearLock: false, clearSuspend: false, clearDisplayOff: true);
        else
            HandleAwaySignal(setLock: false, setSuspend: false, setDisplayOff: true);
    }

    internal void OnSessionLockForTest() => HandleAwaySignal(setLock: true, setSuspend: false, setDisplayOff: false);
    internal void OnSessionUnlockForTest() => HandleResumeSignal(WelcomeBackReason.ScreenUnlocked, clearLock: true, clearSuspend: false, clearDisplayOff: false);
    internal void OnSystemSuspendForTest() => HandleAwaySignal(setLock: false, setSuspend: true, setDisplayOff: false);
    internal void OnSystemResumeForTest() => HandleResumeSignal(WelcomeBackReason.SystemResumed, clearLock: false, clearSuspend: true, clearDisplayOff: false);

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
            HandleAwaySignal(setLock: true, setSuspend: false, setDisplayOff: false);
        else if (e.Reason == SessionSwitchReason.SessionUnlock)
            HandleResumeSignal(WelcomeBackReason.ScreenUnlocked, clearLock: true, clearSuspend: false, clearDisplayOff: false);
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
            HandleAwaySignal(setLock: false, setSuspend: true, setDisplayOff: false);
        else if (e.Mode == PowerModes.Resume)
            HandleResumeSignal(WelcomeBackReason.SystemResumed, clearLock: false, clearSuspend: true, clearDisplayOff: false);
    }

    private void HandleAwaySignal(bool setLock, bool setSuspend, bool setDisplayOff)
    {
        var now = _utcNow();
        PresenceSnapshot? changedSnap = null;
        lock (_lock)
        {
            var prev = _state;
            if (setLock) _isSessionLocked = true;
            if (setSuspend) _isSuspended = true;
            if (setDisplayOff) _isDisplayOff = true;
            _awayStartedUtc ??= now;
            _state = PresenceState.LockedOrAway;
            if (prev != _state)
                changedSnap = BuildSnapshotLocked(now);
        }
        if (changedSnap is not null)
            PresenceChanged?.Invoke(changedSnap);
    }

    private void HandleResumeSignal(WelcomeBackReason reason, bool clearLock, bool clearSuspend, bool clearDisplayOff)
    {
        var now = _utcNow();
        var sched = _settings.Current.Scheduler;
        var minAway = TimeSpan.FromMinutes(Math.Clamp(sched.AfkWelcomeBackMinMinutes, 5, 240));

        WelcomeBackEvent? wb = null;
        PresenceSnapshot? changedSnap = null;
        bool returnedFromSilence = false;

        lock (_lock)
        {
            var wasSilent = _state is PresenceState.SilentIdle or PresenceState.LockedOrAway;
            if (clearLock) _isSessionLocked = false;
            if (clearSuspend) _isSuspended = false;
            if (clearDisplayOff) _isDisplayOff = false;

            // Still locked/suspended/display-off on another flag -> remain LockedOrAway
            if (_isSessionLocked || _isSuspended || _isDisplayOff)
                return;

            var awayDuration = _awayStartedUtc.HasValue ? now - _awayStartedUtc.Value : TimeSpan.Zero;
            if (awayDuration < TimeSpan.Zero) awayDuration = TimeSpan.Zero;

            _state = PresenceState.Active;
            _awayStartedUtc = null;
            _lastAudioHeardAtUtc = null;
            _lastIdleDuration = TimeSpan.Zero;
            returnedFromSilence = wasSilent;

            if (wasSilent)
                changedSnap = BuildSnapshotLocked(now);

            if (sched.AfkWelcomeBackEnabled &&
                awayDuration >= minAway &&
                (now - _lastWelcomeBackUtc) >= WelcomeBackCooldown)
            {
                _lastWelcomeBackUtc = now;
                wb = new WelcomeBackEvent(reason, awayDuration, AudioWasPlaying: false, now);
            }
        }

        if (changedSnap is not null)
            PresenceChanged?.Invoke(changedSnap);
        if (returnedFromSilence)
            ReturnedFromSilence?.Invoke();
        if (wb is not null)
            WelcomeBackDetected?.Invoke(wb);
    }

    internal void EvaluateTick()
    {
        var now = _utcNow();
        var idle = _readIdleDuration();
        var sched = _settings.Current.Scheduler;
        var silenceThreshold = TimeSpan.FromMinutes(Math.Clamp(sched.AfkSilenceMinutes, 2, 180));
        var noAudioWelcomeMin = TimeSpan.FromMinutes(Math.Clamp(sched.AfkWelcomeBackMinMinutes, 5, 240));
        var audioWelcomeMin = TimeSpan.FromMinutes(Math.Clamp(sched.AfkAudioWelcomeBackMinutes, Math.Max(15, sched.AfkWelcomeBackMinMinutes), 360));

        // Only query Core Audio COM peak meter when user has been idle >= 1 minute (0 overhead during active work).
        bool audioPlaying = idle >= TimeSpan.FromMinutes(1) && _readAudioPlaying();

        WelcomeBackEvent? wb = null;
        PresenceSnapshot? changedSnap = null;
        bool returnedFromSilence = false;

        lock (_lock)
        {
            _lastIdleDuration = idle;
            _lastSampledAudioPlaying = audioPlaying;
            if (audioPlaying)
                _lastAudioHeardAtUtc = now;

            if (_isSessionLocked || _isSuspended || _isDisplayOff)
            {
                if (_state != PresenceState.LockedOrAway)
                {
                    _state = PresenceState.LockedOrAway;
                    _awayStartedUtc ??= now;
                    changedSnap = BuildSnapshotLocked(now);
                }
                return;
            }

            if (idle >= silenceThreshold)
            {
                var estimatedIdleStart = now - idle;
                if (!_awayStartedUtc.HasValue || estimatedIdleStart < _awayStartedUtc.Value)
                    _awayStartedUtc = estimatedIdleStart;

                if (_state != PresenceState.SilentIdle)
                {
                    _state = PresenceState.SilentIdle;
                    changedSnap = BuildSnapshotLocked(now);
                }
                return;
            }

            // User actively moved mouse or pressed a key after being in SilentIdle.
            if (_state == PresenceState.SilentIdle && idle < ActiveInputResetThreshold)
            {
                var awayDuration = _awayStartedUtc.HasValue ? now - _awayStartedUtc.Value : TimeSpan.Zero;
                if (awayDuration < TimeSpan.Zero) awayDuration = TimeSpan.Zero;

                bool audioRecentlyActive = audioPlaying ||
                    (_lastAudioHeardAtUtc.HasValue && (now - _lastAudioHeardAtUtc.Value) <= RecentAudioHoldWindow);

                // Even if a short video played early on, if it ended >= noAudioWelcomeMin ago, treat as no-audio absence.
                var silentWithoutAudio = _lastAudioHeardAtUtc.HasValue
                    ? now - _lastAudioHeardAtUtc.Value
                    : awayDuration;

                _state = PresenceState.Active;
                _awayStartedUtc = null;
                _lastAudioHeardAtUtc = null;
                returnedFromSilence = true;
                changedSnap = BuildSnapshotLocked(now);

                if (sched.AfkWelcomeBackEnabled && (now - _lastWelcomeBackUtc) >= WelcomeBackCooldown)
                {
                    if (!audioRecentlyActive && awayDuration >= noAudioWelcomeMin && silentWithoutAudio >= noAudioWelcomeMin)
                    {
                        _lastWelcomeBackUtc = now;
                        wb = new WelcomeBackEvent(WelcomeBackReason.IdleReturnedNoAudio, awayDuration, AudioWasPlaying: false, now);
                    }
                    else if (audioRecentlyActive && awayDuration >= audioWelcomeMin)
                    {
                        _lastWelcomeBackUtc = now;
                        wb = new WelcomeBackEvent(WelcomeBackReason.LongIdleReturnedWithAudio, awayDuration, AudioWasPlaying: true, now);
                    }
                }
            }
            else if (_state == PresenceState.Active && idle < ActiveInputResetThreshold)
            {
                _lastAudioHeardAtUtc = null;
            }
        }

        if (changedSnap is not null)
            PresenceChanged?.Invoke(changedSnap);
        if (returnedFromSilence)
            ReturnedFromSilence?.Invoke();
        if (wb is not null)
            WelcomeBackDetected?.Invoke(wb);
    }

    private PresenceSnapshot BuildSnapshotLocked(DateTimeOffset now)
    {
        var silenceEnabled = _settings.Current.Scheduler.AfkSilenceEnabled;
        var isSilent = silenceEnabled && _state is PresenceState.SilentIdle or PresenceState.LockedOrAway;
        TimeSpan? away = _awayStartedUtc.HasValue ? now - _awayStartedUtc.Value : null;
        if (away.HasValue && away.Value < TimeSpan.Zero) away = TimeSpan.Zero;
        return new PresenceSnapshot(_state, isSilent, _isSessionLocked, _lastSampledAudioPlaying, _lastIdleDuration, away);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                EvaluateTick();
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "UserPresenceService poll tick error");
            }
        }
    }

    private static TimeSpan ReadWindowsIdleDuration()
    {
        try
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref lii))
                return TimeSpan.Zero;
            uint elapsedMs = unchecked((uint)Environment.TickCount - lii.dwTime);
            return TimeSpan.FromMilliseconds(elapsedMs);
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Queries Windows Core Audio default render endpoint peak level (scalar float only; never records audio).
    /// Returns true when any app (Edge/Chrome YouTube, Better-Youtube, mpv, Spotify, Discord, etc.) outputs sound.
    /// </summary>
    private static bool ReadWindowsAudioPeakPlaying()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioMeterInformation? meter = null;
        try
        {
            var type = Type.GetTypeFromCLSID(ClsidMMDeviceEnumerator);
            if (type is null) return false;
            enumerator = Activator.CreateInstance(type) as IMMDeviceEnumerator;
            if (enumerator is null) return false;

            // eRender = 0, eMultimedia = 1
            int hr = enumerator.GetDefaultAudioEndpoint(0, 1, out device);
            if (hr != 0 || device is null) return false;

            var iidMeter = IidIAudioMeterInformation;
            hr = device.Activate(ref iidMeter, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out var obj);
            if (hr != 0 || obj is not IAudioMeterInformation m) return false;
            meter = m;

            hr = meter.GetPeakValue(out float peak);
            return hr == 0 && peak > 0.002f;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (meter is not null) try { Marshal.ReleaseComObject(meter); } catch { }
            if (device is not null) try { Marshal.ReleaseComObject(device); } catch { }
            if (enumerator is not null) try { Marshal.ReleaseComObject(enumerator); } catch { }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_systemEventsHooked)
        {
            try
            {
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            }
            catch { }
        }
        try { _cts.Cancel(); } catch { }
        _cts.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    private static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidIAudioMeterInformation = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr ppDevices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [ComImport]
    [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig]
        int GetPeakValue(out float pfPeak);
    }
}
