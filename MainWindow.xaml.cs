using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Services;
using Forms = System.Windows.Forms;
using Brushes = System.Windows.Media.Brushes;
using Clipboard = System.Windows.Clipboard;
using DataObject = System.Windows.DataObject;
using DataObjectPastingEventArgs = System.Windows.DataObjectPastingEventArgs;
using LinearGradientBrush = System.Windows.Media.LinearGradientBrush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using PointCollection = System.Windows.Media.PointCollection;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace AiSecretary.Desktop;

public partial class MainWindow : Window
{
    private readonly SecretaryController _controller;
    private readonly SandboxService _sandbox;
    private readonly DispatcherTimer _blinkTimer;
    private readonly DispatcherTimer _fadeTimer;
    private readonly DispatcherTimer _pinnedCollapseTimer;
    private readonly DispatcherTimer _hoverIntentTimer;
    private Forms.NotifyIcon? _trayIcon;
    private Icon? _trayIconHandle;
    private SettingsWindow? _settingsWindow;
    private AgentMonitorWindow? _monitorWindow;
    private ChatHistoryWindow? _chatHistoryWindow;
    private Window? _infoWindow;

    private int _fadeSeconds = 5;
    private bool _isReplying;
    private bool _isDark;
    private bool _isHoverLocked;
    private bool _hasOutputContent;
    private PinnedItemKind? _expandedPinnedKind;
    private readonly Dictionary<PinnedItemKind, int> _lastPinnedCounts = new();
    private readonly HashSet<PinnedItemKind> _flashingKinds = new();
    private string? _lastCompletedMsgId;
    private string? _lastAutoSizedMsgId;
    private bool _userResizedBubble;
    private bool _suppressPinnedDuringAlertBubble;
    private bool _forceShowProcessingBubble;
    private Action? _cancelPendingPrompt;
    private string? _pendingPromptKey;
    private bool HasPendingPrompt => _cancelPendingPrompt is not null;

    private static string PromptKey(string title, string message) => title + "\n" + message;

    private bool IsVisiblePromptStillPending()
        => _pendingPromptKey is { } key
           && _controller.GetPendingInteractions().Any(r => PromptKey(r.Title, r.Message) == key);
    private string _currentOutputMarkdown = "";
    private readonly List<string> _attachedFiles = new();
    private bool _isDraggingPet;
    private Point _dragStartDip;
    private Point _windowStartPos;
    private bool _movedDuringPress;

    public MainWindow(SecretaryController controller, SandboxService sandbox)
    {
        InitializeComponent();
        _controller = controller;
        _sandbox = sandbox;

        DataObject.AddPastingHandler(InputBox, InputBox_Pasting);
        InputBox.CommandBindings.Add(new CommandBinding(
            ApplicationCommands.Paste,
            (s, e) =>
            {
                if (TryPasteImageOrFilesFromClipboard())
                {
                    e.Handled = true;
                    return;
                }
                InputBox.Paste();
            },
            (s, e) =>
            {
                e.CanExecute = Clipboard.ContainsText() || Clipboard.ContainsImage() || Clipboard.ContainsFileDropList();
                e.Handled = true;
            }));

        _controller.ConfirmActionHandler = (title, message) =>
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.InvokeAsync(() => ShowInlineConfirmation(title, message, tcs));
            return tcs.Task;
        };

        _controller.ConfirmPathPermissionHandler = (title, message, targetPath) =>
        {
            var tcs = new TaskCompletionSource<PathPermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.InvokeAsync(() => ShowInlinePathPermission(title, message, targetPath, tcs));
            return tcs.Task;
        };

        _controller.ConfirmCommandApprovalHandler = (title, message) =>
        {
            var tcs = new TaskCompletionSource<CommandApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.InvokeAsync(() => ShowInlineCommandApproval(title, message, tcs));
            return tcs.Task;
        };

        _controller.InteractionAdded += _req => Dispatcher.InvokeAsync(RefreshPinnedReminders);
        _controller.InteractionResolved += _id => Dispatcher.InvokeAsync(() =>
        {
            // Only dismiss the visible prompt if *its own* request was resolved elsewhere (API / ❓ pill).
            // Previously any resolution dismissed whatever prompt was on screen, so a confirmation that popped up
            // right after the user answered another one was immediately cancelled and treated as "deny".
            if (_cancelPendingPrompt is { } cancel && !IsVisiblePromptStillPending())
            {
                _cancelPendingPrompt = null;
                _pendingPromptKey = null;
                cancel();
                OutputActionsPanel.Children.Clear();
                OutputActionsPanel.Visibility = Visibility.Collapsed;
                _ = RefreshOutputAsync();
            }
            RefreshPinnedReminders();
        });
        _controller.StateChanged += () => Dispatcher.InvokeAsync(RefreshOutputAsync);
        _controller.PinnedRemindersChanged += () => Dispatcher.InvokeAsync(RefreshPinnedReminders);
        _controller.ThemeChanged += mode => Dispatcher.InvokeAsync(() =>
        {
            ApplyTheme(mode);
            RefreshPinnedReminders();
        });
        _controller.FadeSettingsChanged += sec => Dispatcher.InvokeAsync(() =>
        {
            _fadeSeconds = sec;
            RestartFadeTimerIfNeeded();
        });
        _controller.AnimationFpsChanged += fps => Dispatcher.InvokeAsync(() => ApplyAnimationFps(fps));
        _controller.ProactiveAlert += (title, body) => Dispatcher.InvokeAsync(() =>
        {
            if (!IsVisible && _trayIcon is not null)
            {
                var snippet = body.Length > 180 ? body[..180] + "…" : body;
                _trayIcon.ShowBalloonTip(5000, title, snippet, Forms.ToolTipIcon.Info);
            }
        });

        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.2) };
        _blinkTimer.Tick += async (_, _) => await BlinkOnceAsync();

        _fadeTimer = new DispatcherTimer();
        _fadeTimer.Tick += (_, _) =>
        {
            _fadeTimer.Stop();
            FadeOutBubble();
        };

        _pinnedCollapseTimer = new DispatcherTimer();
        _pinnedCollapseTimer.Tick += (_, _) =>
        {
            _pinnedCollapseTimer.Stop();
            CollapsePinnedToPill();
        };

        _hoverIntentTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _hoverIntentTimer.Tick += (_, _) =>
        {
            _hoverIntentTimer.Stop();
            if (OutputContainer.IsMouseOver || PetAvatar.IsMouseOver)
            {
                _isHoverLocked = true;
                _fadeTimer.Stop();
                _pinnedCollapseTimer.Stop();
                if (OutputContainer.Visibility == Visibility.Visible)
                {
                    OutputBorder.BeginAnimation(OpacityProperty, null);
                    OutputBorder.Opacity = 1;
                    BubbleTail.BeginAnimation(OpacityProperty, null);
                    BubbleTail.Opacity = 1;
                }
            }
        };

        InitializeTrayIcon();

        Loaded += async (_, _) =>
        {
            await RestoreWindowPositionAsync();
            UpdateBubbleSide();
            _fadeSeconds = await _controller.GetOutputFadeSecondsAsync();
            ApplyTheme(await _controller.GetThemeModeAsync());
            ApplyAnimationFps(await _controller.GetAnimationFpsAsync());
            await LoadPetImageAsync();
            RefreshPinnedReminders();
            var existingMsgs = await _controller.GetMessagesAsync();
            _lastCompletedMsgId = existingMsgs.LastOrDefault(m => m.Role is "assistant" or "notification")?.Id;
            OutputBorder.Visibility = Visibility.Collapsed;
            UpdateOutputContainerVisibility();
            UpdateSendButtonState();
        };

        LocationChanged += (_, _) => UpdateBubbleSide();

        Closed += (_, _) =>
        {
            _blinkTimer.Stop();
            _fadeTimer.Stop();
            _pinnedCollapseTimer.Stop();
            _hoverIntentTimer.Stop();
            _settingsWindow?.Close();
            _monitorWindow?.Close();
            _chatHistoryWindow?.Close();
            _infoWindow?.Close();
            if (_trayIcon is not null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
            _trayIconHandle?.Dispose();
            System.Windows.Application.Current.Shutdown();
        };
    }

    private void ApplyAnimationFps(int fps)
    {
        var sb = (Storyboard)FindResource("FloatAnimation");
        sb.Stop(this);
        if (fps <= 0)
        {
            PetTransform.Y = 0;
            _blinkTimer.Stop();
            return;
        }

        Timeline.SetDesiredFrameRate(sb, fps);
        sb.Begin(this, true);
        if (!_blinkTimer.IsEnabled)
            _blinkTimer.Start();
    }

    private void InitializeTrayIcon()
    {
        _trayIconHandle = CreateTrayIcon();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("對話紀錄", null, (_, _) => Dispatcher.Invoke(OpenChatHistoryWindow));
        menu.Items.Add("資訊", null, (_, _) => Dispatcher.Invoke(async () => await OpenInfoWindowAsync()));
        menu.Items.Add("詳細顯示", null, (_, _) => Dispatcher.Invoke(OpenMonitorWindow));
        menu.Items.Add("設定", null, (_, _) => Dispatcher.Invoke(OpenSettingsWindow));
        menu.Items.Add("最小化", null, (_, _) => Dispatcher.Invoke(Hide));
        menu.Items.Add("結束", null, (_, _) => Dispatcher.Invoke(Close));

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _trayIconHandle,
            Text = "AI 秘書",
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        _trayIcon.BalloonTipClicked += (_, _) => Dispatcher.Invoke(RestoreFromTray);
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        InputBox.Focus();
    }

    private Rect GetCurrentScreenWorkAreaDip()
    {
        var centerDip = new Point(Left + Width / 2.0, Top + Height / 2.0);
        var source = PresentationSource.FromVisual(this);
        var toDevice = source?.CompositionTarget?.TransformToDevice ?? System.Windows.Media.Matrix.Identity;
        var fromDevice = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;

        var centerPx = toDevice.Transform(centerDip);
        var screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)Math.Round(centerPx.X), (int)Math.Round(centerPx.Y)));
        var wa = screen.WorkingArea;

        var topLeftDip = fromDevice.Transform(new Point(wa.Left, wa.Top));
        var bottomRightDip = fromDevice.Transform(new Point(wa.Right, wa.Bottom));
        return new Rect(topLeftDip, bottomRightDip);
    }

    private async Task RestoreWindowPositionAsync()
    {
        var saved = await _controller.GetWindowPositionAsync();
        if (saved is { } pos)
        {
            Left = pos.Left;
            Top = pos.Top;
            var wa = GetCurrentScreenWorkAreaDip();
            Left = Math.Clamp(Left, wa.Left - Width / 2.0 + 40, wa.Right - Width / 2.0 - 40);
            Top = Math.Clamp(Top, wa.Top, Math.Max(wa.Top, wa.Bottom - Height));
            return;
        }
        PositionBottomRight();
    }

    private void PositionBottomRight()
    {
        var workArea = GetCurrentScreenWorkAreaDip();
        Left = workArea.Right - Width / 2.0 - 190;
        Top = Math.Max(workArea.Top, workArea.Bottom - Height - 12);
    }

    private void UpdateBubbleSide()
    {
        var workArea = GetCurrentScreenWorkAreaDip();
        var screenCenterX = workArea.Left + workArea.Width / 2.0;
        var windowCenterX = Left + Width / 2.0;
        var onRightHalf = windowCenterX >= screenCenterX;

        if (onRightHalf)
        {
            Grid.SetColumn(OutputContainer, 0);
            OutputContainer.HorizontalAlignment = HorizontalAlignment.Right;
            OutputContainer.Margin = new Thickness(0, 0, 6, 4);
            OutputBorder.HorizontalAlignment = HorizontalAlignment.Right;
            OutputBorder.RenderTransformOrigin = new Point(1, 0);
            PinnedRemindersContainer.HorizontalAlignment = HorizontalAlignment.Right;
            LeftResizeThumb.Visibility = Visibility.Visible;
            TopLeftResizeThumb.Visibility = Visibility.Visible;
            RightResizeThumb.Visibility = Visibility.Collapsed;
            TopRightResizeThumb.Visibility = Visibility.Collapsed;

            PinnedLeftResizeThumb.Visibility = Visibility.Visible;
            PinnedTopLeftResizeThumb.Visibility = Visibility.Visible;
            PinnedRightResizeThumb.Visibility = Visibility.Collapsed;
            PinnedTopRightResizeThumb.Visibility = Visibility.Collapsed;

            BubbleTail.HorizontalAlignment = HorizontalAlignment.Right;
            BubbleTail.Margin = new Thickness(0, -1, 14, 0);
            BubbleTail.Points = new PointCollection { new Point(0, 0), new Point(14, 0), new Point(22, 10) };

            PinnedPillsBar.HorizontalAlignment = HorizontalAlignment.Right;
            PinnedExpandedHost.RenderTransformOrigin = new Point(1, 1);
            PinnedRemindersPanel.RenderTransformOrigin = new Point(1, 1);
        }
        else
        {
            Grid.SetColumn(OutputContainer, 2);
            OutputContainer.HorizontalAlignment = HorizontalAlignment.Left;
            OutputContainer.Margin = new Thickness(6, 0, 0, 4);
            OutputBorder.HorizontalAlignment = HorizontalAlignment.Left;
            OutputBorder.RenderTransformOrigin = new Point(0, 0);
            PinnedRemindersContainer.HorizontalAlignment = HorizontalAlignment.Left;
            LeftResizeThumb.Visibility = Visibility.Collapsed;
            TopLeftResizeThumb.Visibility = Visibility.Collapsed;
            RightResizeThumb.Visibility = Visibility.Visible;
            TopRightResizeThumb.Visibility = Visibility.Visible;

            PinnedLeftResizeThumb.Visibility = Visibility.Collapsed;
            PinnedTopLeftResizeThumb.Visibility = Visibility.Collapsed;
            PinnedRightResizeThumb.Visibility = Visibility.Visible;
            PinnedTopRightResizeThumb.Visibility = Visibility.Visible;

            BubbleTail.HorizontalAlignment = HorizontalAlignment.Left;
            BubbleTail.Margin = new Thickness(14, -1, 0, 0);
            BubbleTail.Points = new PointCollection { new Point(0, 0), new Point(14, 0), new Point(-8, 10) };

            PinnedPillsBar.HorizontalAlignment = HorizontalAlignment.Left;
            PinnedExpandedHost.RenderTransformOrigin = new Point(0, 1);
            PinnedRemindersPanel.RenderTransformOrigin = new Point(0, 1);
        }
    }

    private void ApplyTheme(string mode)
    {
        _isDark = ThemeHelper.IsDarkEffective(mode);
        if (_isDark)
        {
            OutputBorder.Background = new LinearGradientBrush(
                ColorFrom("#CC1E293B"), ColorFrom("#B80F172A"), new Point(0, 0), new Point(1, 1));
            OutputBorder.BorderBrush = BrushFrom("#55FFFFFF");
            OutputRichText.Foreground = BrushFrom("#F1F5F9");
            OutputStatusText.Foreground = BrushFrom("#60A5FA");
            BubbleTail.Fill = BrushFrom("#C40F172A");
            BubbleTail.Stroke = BrushFrom("#55FFFFFF");

            InputBorder.Background = BrushFrom("#CC1E293B");
            InputBorder.BorderBrush = BrushFrom("#55FFFFFF");
            InputBox.Foreground = BrushFrom("#F1F5F9");
            InputBox.CaretBrush = BrushFrom("#F8FAFC");
            InputPlaceholder.Foreground = BrushFrom("#94A3B8");
            AttachBtn.Foreground = BrushFrom("#CBD5E1");
            CloseBubbleBtn.Foreground = BrushFrom("#94A3B8");
        }
        else
        {
            OutputBorder.Background = new LinearGradientBrush(
                ColorFrom("#CCFFFFFF"), ColorFrom("#B3F1F5F9"), new Point(0, 0), new Point(1, 1));
            OutputBorder.BorderBrush = BrushFrom("#AAFFFFFF");
            OutputRichText.Foreground = BrushFrom("#0F172A");
            OutputStatusText.Foreground = BrushFrom("#2563EB");
            BubbleTail.Fill = BrushFrom("#CCF1F5F9");
            BubbleTail.Stroke = BrushFrom("#AAFFFFFF");

            InputBorder.Background = BrushFrom("#B8FFFFFF");
            InputBorder.BorderBrush = BrushFrom("#AAFFFFFF");
            InputBox.Foreground = BrushFrom("#0F172A");
            InputBox.CaretBrush = BrushFrom("#0F172A");
            InputPlaceholder.Foreground = BrushFrom("#64748B");
            AttachBtn.Foreground = BrushFrom("#475569");
            CloseBubbleBtn.Foreground = BrushFrom("#64748B");
        }

        if (!string.IsNullOrWhiteSpace(_currentOutputMarkdown))
            OutputRichText.Document = MarkdownRenderer.Render(_currentOutputMarkdown, _isDark);
    }

    private void UpdateOutputContainerVisibility()
    {
        bool anyPinned = PinnedRemindersContainer.Visibility == Visibility.Visible;
        bool hasBubble = OutputBorder.Visibility == Visibility.Visible;
        BubbleTail.Visibility = OutputBorder.Visibility;
        OutputContainer.Visibility = (anyPinned || hasBubble) ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string GetPinnedIcon(PinnedItemKind kind) => kind switch
    {
        PinnedItemKind.Schedule => "📅",
        PinnedItemKind.Confirmation => "❓",
        _ => "📌"
    };

    private static (string BgLight, string BorderLight, string BgDark, string BorderDark) GetPinnedColors(PinnedItemKind kind) => kind switch
    {
        PinnedItemKind.Schedule => ("#EBF0FDF4", "#10B981", "#E61E293B", "#34D399"),
        PinnedItemKind.Confirmation => ("#EBF5F3FF", "#8B5CF6", "#E61E293B", "#A78BFA"),
        _ => ("#EBFFFBEB", "#F59E0B", "#E61E293B", "#60A5FA")
    };

    private static void SetPillFlash(Button pill, bool shouldFlash)
    {
        if (!shouldFlash || pill.Visibility != Visibility.Visible)
        {
            if (pill.Effect is System.Windows.Media.Effects.DropShadowEffect existingGlow)
                existingGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, null);
            pill.Effect = null;
            return;
        }

        if (pill.Effect is System.Windows.Media.Effects.DropShadowEffect)
            return;

        var glow = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Color.FromRgb(250, 204, 21), // subtle warm yellow
            BlurRadius = 12,
            ShadowDepth = 0,
            Opacity = 0.08
        };
        pill.Effect = glow;

        var pulse = new DoubleAnimation(0.08, 0.72, TimeSpan.FromMilliseconds(480))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        glow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, pulse);
    }

    private void StylePinnedPill(Button pill, TextBlock textBlock, PinnedItemKind kind, int count)
    {
        if (count <= 0)
        {
            pill.Visibility = Visibility.Collapsed;
            return;
        }

        pill.Visibility = Visibility.Visible;
        var isActive = _expandedPinnedKind == kind;
        var (bgL, bdL, bgD, bdD) = GetPinnedColors(kind);
        pill.Background = BrushFrom(_isDark ? bgD : bgL);
        pill.BorderBrush = BrushFrom(isActive ? (_isDark ? "#F8FAFC" : "#0F172A") : (_isDark ? bdD : bdL));
        textBlock.Foreground = BrushFrom(_isDark ? "#F8FAFC" : "#0F172A");
        textBlock.Text = count > 1 ? $"{GetPinnedIcon(kind)} {count}" : GetPinnedIcon(kind);
    }

    private static bool TrySetClipboardText(string text)
    {
        try { Clipboard.SetText(text); return true; } catch { return false; }
    }

    private async Task DismissPinnedForNotificationAsync(NotificationActionPayload n)
    {
        var matches = _controller.GetPinnedReminders()
            .Where(p => p.MailNotification is not null &&
                        (p.MailNotification.NotificationId == n.NotificationId ||
                         (!string.IsNullOrEmpty(n.MailId) && p.MailNotification.MailId == n.MailId)))
            .Select(p => p.Id)
            .ToList();
        foreach (var id in matches)
            await _controller.DismissPinnedReminderAsync(id);
    }

    private void RenderNotificationActions(
        NotificationActionPayload n,
        string? pinnedId,
        Action<string, bool, Func<Task>> addAction)
    {
        if (n.Actions is { Count: > 0 })
        {
            foreach (var btn in n.Actions)
            {
                switch (btn.Type)
                {
                    case "copy_otp":
                        if (!string.IsNullOrWhiteSpace(btn.Prompt))
                        {
                            var code = btn.Prompt;
                            addAction(btn.Label, btn.IsPrimary, async () =>
                            {
                                if (TrySetClipboardText(code))
                                {
                                    if (pinnedId is not null)
                                        await _controller.DismissPinnedReminderAsync(pinnedId);
                                    else
                                        await DismissPinnedForNotificationAsync(n);
                                    OutputStatusText.Text = await _controller.LocalizeAsync("OtpCopiedStatus", "Copied code {0}!", code);
                                }
                            });
                        }
                        break;
                    case "fill_otp":
                        if (!string.IsNullOrWhiteSpace(btn.Prompt))
                        {
                            var code = btn.Prompt;
                            var titleHint = n.Title;
                            addAction(btn.Label, btn.IsPrimary, async () =>
                            {
                                TrySetClipboardText(code);
                                try
                                {
                                    var (ok, msg) = await _controller.FillOtpInExternalWindowAsync(code, titleHint);
                                    if (ok)
                                    {
                                        if (pinnedId is not null)
                                            await _controller.DismissPinnedReminderAsync(pinnedId);
                                        else
                                            await DismissPinnedForNotificationAsync(n);
                                    }
                                    OutputStatusText.Text = msg;
                                }
                                catch (Exception ex)
                                {
                                    OutputStatusText.Text = ex.Message;
                                }
                            });
                        }
                        break;
                    case "view_mail":
                        addAction(btn.Label, btn.IsPrimary, () => ShowMailDialogAsync(n.MailId));
                        break;
                    case "draft_reply":
                        addAction(btn.Label, btn.IsPrimary, async () =>
                        {
                            if (pinnedId is not null)
                                await _controller.DismissPinnedReminderAsync(pinnedId);
                            await _controller.DraftReplyFromNotificationAsync(n);
                        });
                        break;
                    case "snooze":
                        if (n.Status == "Unread" || pinnedId is not null)
                        {
                            addAction(btn.Label, btn.IsPrimary, async () =>
                            {
                                if (pinnedId is not null)
                                    await _controller.DismissPinnedReminderAsync(pinnedId);
                                await _controller.SnoozeNotificationAsync(n.NotificationId);
                            });
                        }
                        break;
                    case "ask_ai":
                        if (!string.IsNullOrWhiteSpace(btn.Prompt))
                        {
                            var p = btn.Prompt;
                            addAction(btn.Label, btn.IsPrimary, async () =>
                            {
                                if (pinnedId is not null)
                                    await _controller.DismissPinnedReminderAsync(pinnedId);
                                await _controller.SendUserPromptAsync(p, n.MailId);
                            });
                        }
                        break;
                }
            }
        }
    }

    private void RefreshPinnedReminders()
    {
        PinnedRemindersPanel.Children.Clear();
        var items = _controller.GetPinnedReminders();
        var pendingInteractions = _controller.GetPendingInteractions();

        var notifCount = items.Count(i => i.Kind == PinnedItemKind.Notification);
        var schedCount = items.Count(i => i.Kind == PinnedItemKind.Schedule);
        var confirmCount = items.Count(i => i.Kind == PinnedItemKind.Confirmation) + pendingInteractions.Count;

        PinnedItemKind? newlyAddedKind = null;
        foreach (var (kind, cnt) in new[]
        {
            (PinnedItemKind.Confirmation, confirmCount),
            (PinnedItemKind.Schedule, schedCount),
            (PinnedItemKind.Notification, notifCount)
        })
        {
            _lastPinnedCounts.TryGetValue(kind, out var prev);
            if (cnt > prev)
            {
                _flashingKinds.Add(kind);
                newlyAddedKind ??= kind;
            }
            if (cnt == 0)
                _flashingKinds.Remove(kind);
            _lastPinnedCounts[kind] = cnt;
        }

        bool hasBubbleVisible = OutputBorder.Visibility == Visibility.Visible;
        if (newlyAddedKind is not null && !hasBubbleVisible && !_isReplying && !HasPendingPrompt && !_controller.HasRunningTasks)
        {
            // When no bubble is open and no active turn is running, show the speech bubble first and shrink into the pill later.
            _suppressPinnedDuringAlertBubble = true;
            _expandedPinnedKind = null;
        }
        else if (_controller.HasRunningTasks || _isReplying || HasPendingPrompt)
        {
            // When a conversation turn or confirmation is active, never suppress the pinned pill bar so notifications stay visible as pills without hijacking the bubble.
            _suppressPinnedDuringAlertBubble = false;
        }

        if (_expandedPinnedKind is PinnedItemKind active &&
            items.All(i => i.Kind != active) &&
            !(active == PinnedItemKind.Confirmation && pendingInteractions.Count > 0))
            _expandedPinnedKind = null;

        if (_expandedPinnedKind is PinnedItemKind openKind)
            _flashingKinds.Remove(openKind);

        StylePinnedPill(PinnedNotifPill, PinnedNotifPillText, PinnedItemKind.Notification, notifCount);
        StylePinnedPill(PinnedSchedulePill, PinnedSchedulePillText, PinnedItemKind.Schedule, schedCount);
        StylePinnedPill(PinnedConfirmPill, PinnedConfirmPillText, PinnedItemKind.Confirmation, confirmCount);

        SetPillFlash(PinnedNotifPill, _flashingKinds.Contains(PinnedItemKind.Notification));
        SetPillFlash(PinnedSchedulePill, _flashingKinds.Contains(PinnedItemKind.Schedule));
        SetPillFlash(PinnedConfirmPill, _flashingKinds.Contains(PinnedItemKind.Confirmation) || (pendingInteractions.Count > 0 && _expandedPinnedKind != PinnedItemKind.Confirmation));

        if (_expandedPinnedKind is PinnedItemKind expandedKind)
        {
            var (bgL, bdL, bgD, bdD) = GetPinnedColors(expandedKind);
            var icon = GetPinnedIcon(expandedKind);

            foreach (var item in items.Where(i => i.Kind == expandedKind))
            {
                var card = new Border
                {
                    CornerRadius = new CornerRadius(12),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 0, 0, 5),
                    Background = BrushFrom(_isDark ? bgD : bgL),
                    BorderBrush = BrushFrom(_isDark ? bdD : bdL),
                    Effect = (System.Windows.Media.Effects.Effect)FindResource("GlassShadow")
                };

                var stack = new StackPanel();
                var topDock = new DockPanel { LastChildFill = true };

                var closeBtn = new Button
                {
                    Content = "✕",
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Width = 20,
                    Height = 20,
                    Padding = new Thickness(0),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = BrushFrom(_isDark ? "#CBD5E1" : "#64748B"),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Top,
                    ToolTip = "最小化"
                };
                DockPanel.SetDock(closeBtn, Dock.Right);
                var id = item.Id;
                closeBtn.Click += (_, _) => CollapsePinnedToPill(force: true);
                topDock.Children.Add(closeBtn);

                var titleTb = new TextBlock
                {
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = BrushFrom(_isDark ? "#F8FAFC" : "#0F172A"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 6, 0)
                };
                MarkdownRenderer.AppendInlines(titleTb.Inlines, $"{icon} {item.Title}", _isDark);
                topDock.Children.Add(titleTb);
                stack.Children.Add(topDock);

                if (!string.IsNullOrWhiteSpace(item.Summary))
                {
                    var summaryTb = new TextBlock
                    {
                        FontSize = 12,
                        Foreground = BrushFrom(_isDark ? "#CBD5E1" : "#334155"),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 3, 0, 0)
                    };
                    MarkdownRenderer.AppendInlines(summaryTb.Inlines, item.Summary, _isDark);
                    stack.Children.Add(summaryTb);
                }

                if (item.MailNotification is not null)
                {
                    var n = item.MailNotification;
                    var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                    RenderNotificationActions(n, id, (label, isPrimary, handler) =>
                    {
                        var b = MakeActionButton(label, isPrimary);
                        b.Click += async (_, _) => await handler();
                        actions.Children.Add(b);
                    });
                    var dismissBtn = MakeActionButton("已知（結束）", actions.Children.Count == 0);
                    dismissBtn.Click += async (_, _) => await _controller.DismissPinnedReminderAsync(id);
                    actions.Children.Add(dismissBtn);
                    stack.Children.Add(actions);
                }
                else if (item.Kind == PinnedItemKind.Confirmation)
                {
                    var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                    var choices = item.Choices is { Count: > 0 }
                        ? item.Choices
                        : new[] { "繼續處理" };
                    var logicalTaskId = item.LogicalTaskId;
                    for (int i = 0; i < choices.Count; i++)
                    {
                        var chosenText = choices[i];
                        var choiceBtn = MakeActionButton(chosenText, i == 0);
                        choiceBtn.Click += async (_, _) =>
                        {
                            _controller.ClearChoices();
                            CollapsePinnedToPill(force: true);
                            await _controller.SendUserPromptAsync(chosenText, replyToLogicalTaskId: logicalTaskId);
                        };
                        actions.Children.Add(choiceBtn);
                    }
                    var dismissBtn = MakeActionButton("放棄", false);
                    dismissBtn.Click += async (_, _) => await _controller.DismissPinnedReminderAsync(id);
                    actions.Children.Add(dismissBtn);
                    stack.Children.Add(actions);
                }
                else
                {
                    var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                    var dismissBtn = MakeActionButton("已知（結束）", true);
                    dismissBtn.Click += async (_, _) => await _controller.DismissPinnedReminderAsync(id);
                    actions.Children.Add(dismissBtn);
                    stack.Children.Add(actions);
                }

                card.Child = stack;
                PinnedRemindersPanel.Children.Add(card);
            }

            if (expandedKind == PinnedItemKind.Confirmation)
            {
                var (cBgL, cBdL, cBgD, cBdD) = GetPinnedColors(PinnedItemKind.Confirmation);
                foreach (var req in pendingInteractions)
                {
                    var card = new Border
                    {
                        CornerRadius = new CornerRadius(12),
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(10, 8, 10, 8),
                        Margin = new Thickness(0, 0, 0, 5),
                        Background = BrushFrom(_isDark ? cBgD : cBgL),
                        BorderBrush = BrushFrom(_isDark ? cBdD : cBdL),
                        Effect = (System.Windows.Media.Effects.Effect)FindResource("GlassShadow")
                    };

                    var stack = new StackPanel();
                    var topDock = new DockPanel { LastChildFill = true };

                    var closeBtn = new Button
                    {
                        Content = "✕",
                        FontSize = 11,
                        FontWeight = FontWeights.Bold,
                        Width = 20,
                        Height = 20,
                        Padding = new Thickness(0),
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        Foreground = BrushFrom(_isDark ? "#CBD5E1" : "#64748B"),
                        Cursor = System.Windows.Input.Cursors.Hand,
                        VerticalAlignment = VerticalAlignment.Top,
                        ToolTip = "最小化"
                    };
                    DockPanel.SetDock(closeBtn, Dock.Right);
                    var reqId = req.RequestId;
                    var reqKind = req.Kind;
                    closeBtn.Click += (_, _) => CollapsePinnedToPill(force: true);
                    topDock.Children.Add(closeBtn);

                    var titleTb = new TextBlock
                    {
                        FontSize = 12.5,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = BrushFrom(_isDark ? "#F8FAFC" : "#0F172A"),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 6, 0)
                    };
                    MarkdownRenderer.AppendInlines(titleTb.Inlines, $"❓ {req.Title}", _isDark);
                    topDock.Children.Add(titleTb);
                    stack.Children.Add(topDock);

                    var bodyText = req.Message;
                    if (req.Kind == "path_permission" && !string.IsNullOrWhiteSpace(req.TargetPath))
                        bodyText = $"{req.Message}\n`{req.TargetPath}`";
                    if (!string.IsNullOrWhiteSpace(bodyText))
                    {
                        var bodyTb = new TextBlock
                        {
                            FontSize = 12,
                            Foreground = BrushFrom(_isDark ? "#CBD5E1" : "#334155"),
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 3, 0, 0)
                        };
                        MarkdownRenderer.AppendInlines(bodyTb.Inlines, bodyText, _isDark);
                        stack.Children.Add(bodyTb);
                    }

                    var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                    void AddChoice(string label, bool isPrimary, Action onClick)
                    {
                        var b = MakeActionButton(label, isPrimary);
                        b.Click += (_, _) => onClick();
                        actions.Children.Add(b);
                    }

                    if (req.Kind == "path_permission")
                    {
                        AddChoice("永久信任", true, () => _controller.ResolvePathPermission(reqId, PathPermissionDecision.TrustPermanently));
                        AddChoice("信任一次", false, () => _controller.ResolvePathPermission(reqId, PathPermissionDecision.AllowOnce));
                        AddChoice("拒絕", false, () => _controller.ResolvePathPermission(reqId, PathPermissionDecision.Deny));
                    }
                    else if (req.Kind == "command_approval")
                    {
                        AddChoice("同意本次", true, () => _controller.ResolveCommandApproval(reqId, CommandApprovalDecision.AllowOnce));
                        AddChoice("本任務內允許", false, () => _controller.ResolveCommandApproval(reqId, CommandApprovalDecision.AllowForTask));
                        AddChoice("拒絕", false, () => _controller.ResolveCommandApproval(reqId, CommandApprovalDecision.Deny));
                    }
                    else
                    {
                        AddChoice("同意", true, () => _controller.ResolveConfirmation(reqId, true));
                        AddChoice("拒絕", false, () => _controller.ResolveConfirmation(reqId, false));
                    }

                    stack.Children.Add(actions);
                    card.Child = stack;
                    PinnedRemindersPanel.Children.Add(card);
                }
            }
        }

        if ((items.Count == 0 && pendingInteractions.Count == 0) || _suppressPinnedDuringAlertBubble)
        {
            _pinnedCollapseTimer.Stop();
            _expandedPinnedKind = null;
            PinnedRemindersContainer.Visibility = Visibility.Collapsed;
            PinnedExpandedHost.Visibility = Visibility.Collapsed;
            PinnedPillsBar.Visibility = Visibility.Collapsed;
        }
        else
        {
            PinnedRemindersContainer.Visibility = Visibility.Visible;
            PinnedPillsBar.Visibility = Visibility.Visible;

            if (_expandedPinnedKind is not null && PinnedRemindersPanel.Children.Count > 0)
            {
                ExpandPinnedFromPill(animate: newlyAddedKind is not null);
            }
            else
            {
                PinnedExpandedHost.Visibility = Visibility.Collapsed;
            }
        }

        UpdateOutputContainerVisibility();
    }

    private void ExpandPinnedFromPill(bool animate = true)
    {
        if (PinnedRemindersPanel.Children.Count == 0) return;

        PinnedExpandedHost.BeginAnimation(OpacityProperty, null);
        PinnedPanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PinnedPanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        PinnedExpandedHost.Visibility = Visibility.Visible;

        if (animate)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            PinnedExpandedHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
            PinnedPanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.82, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            PinnedPanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.82, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        }
        else
        {
            PinnedExpandedHost.Opacity = 1;
            PinnedPanelScale.ScaleX = 1;
            PinnedPanelScale.ScaleY = 1;
        }

        RestartPinnedCollapseTimer();
    }

    private void CollapsePinnedToPill(bool force = false)
    {
        if (_expandedPinnedKind is null || (!force && _isHoverLocked))
            return;

        _pinnedCollapseTimer.Stop();
        _expandedPinnedKind = null;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var fadeOut = new DoubleAnimation(PinnedExpandedHost.Opacity, 0, TimeSpan.FromMilliseconds(200));
        var scaleX = new DoubleAnimation(1, 0.72, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease };
        var scaleY = new DoubleAnimation(1, 0.72, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease };

        fadeOut.Completed += (_, _) =>
        {
            if (_expandedPinnedKind is not null) return;
            PinnedExpandedHost.Visibility = Visibility.Collapsed;
            RefreshPinnedReminders();
        };

        PinnedExpandedHost.BeginAnimation(OpacityProperty, fadeOut);
        PinnedPanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        PinnedPanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
    }

    private void RestartPinnedCollapseTimer()
    {
        _pinnedCollapseTimer.Stop();
        if (_expandedPinnedKind is not null && !_isHoverLocked && PinnedRemindersPanel.Children.Count > 0)
        {
            var secs = _fadeSeconds > 0 ? _fadeSeconds : 6;
            _pinnedCollapseTimer.Interval = TimeSpan.FromSeconds(secs);
            _pinnedCollapseTimer.Start();
        }
    }

    private void TogglePinnedKind(PinnedItemKind kind)
    {
        if (_expandedPinnedKind == kind)
        {
            CollapsePinnedToPill(force: true);
            return;
        }

        _flashingKinds.Remove(kind);
        _expandedPinnedKind = kind;
        RefreshPinnedReminders();
    }

    private void PinnedNotifPill_Click(object sender, RoutedEventArgs e) => TogglePinnedKind(PinnedItemKind.Notification);
    private void PinnedSchedulePill_Click(object sender, RoutedEventArgs e) => TogglePinnedKind(PinnedItemKind.Schedule);
    private void PinnedConfirmPill_Click(object sender, RoutedEventArgs e) => TogglePinnedKind(PinnedItemKind.Confirmation);

    private void ShowOutputBubbleWithPopIn(bool resetOnlyIfVisible = false)
    {
        if (!_hasOutputContent) return;

        if (resetOnlyIfVisible && OutputBorder.Visibility == Visibility.Visible && OutputBorder.Opacity > 0.85)
        {
            RestartFadeTimerIfNeeded();
            return;
        }

        _fadeTimer.Stop();
        OutputBorder.BeginAnimation(OpacityProperty, null);
        BubbleTail.BeginAnimation(OpacityProperty, null);
        BubbleTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        BubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        BubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        BubbleShrinkTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        BubbleScale.ScaleX = 1;
        BubbleScale.ScaleY = 1;
        BubbleShrinkTranslate.Y = 0;

        OutputBorder.Visibility = Visibility.Visible;
        BubbleTail.Visibility = Visibility.Visible;
        UpdateOutputContainerVisibility();

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
        var fadeInTail = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
        var slideUp = new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        OutputBorder.BeginAnimation(OpacityProperty, fadeIn);
        BubbleTail.BeginAnimation(OpacityProperty, fadeInTail);
        BubbleTranslate.BeginAnimation(TranslateTransform.YProperty, slideUp);

        RestartFadeTimerIfNeeded();
    }

    private void FadeOutBubble()
    {
        if (OutputBorder.Visibility != Visibility.Visible || _isReplying) return;

        bool shrinkToPill = _suppressPinnedDuringAlertBubble || _controller.GetPinnedReminders().Count > 0 || _controller.GetPendingInteractions().Count > 0;
        var dur = TimeSpan.FromMilliseconds(shrinkToPill ? 240 : 350);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var anim = new DoubleAnimation(OutputBorder.Opacity, 0, dur);
        var tailAnim = new DoubleAnimation(BubbleTail.Opacity, 0, TimeSpan.FromMilliseconds(160));
        anim.Completed += (_, _) =>
        {
            if (!_isReplying && OutputBorder.Opacity <= 0.01)
            {
                OutputBorder.Visibility = Visibility.Collapsed;
                BubbleTail.Visibility = Visibility.Collapsed;
                BubbleScale.ScaleX = 1;
                BubbleScale.ScaleY = 1;
                BubbleShrinkTranslate.Y = 0;
                if (_suppressPinnedDuringAlertBubble)
                {
                    _suppressPinnedDuringAlertBubble = false;
                    RefreshPinnedReminders();
                    var popEase = new CubicEase { EasingMode = EasingMode.EaseOut };
                    PinnedPillScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.65, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = popEase });
                    PinnedPillScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.65, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = popEase });
                }
                UpdateOutputContainerVisibility();
            }
        };
        OutputBorder.BeginAnimation(OpacityProperty, anim);
        BubbleTail.BeginAnimation(OpacityProperty, tailAnim);
        if (shrinkToPill)
        {
            BubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.32, dur) { EasingFunction = ease });
            BubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.28, dur) { EasingFunction = ease });
            BubbleShrinkTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -24, dur) { EasingFunction = ease });
        }
    }

    private void RestartFadeTimerIfNeeded()
    {
        _fadeTimer.Stop();
        if (!HasPendingPrompt && !_isReplying && !_isHoverLocked && _fadeSeconds > 0 && OutputBorder.Visibility == Visibility.Visible)
        {
            _fadeTimer.Interval = TimeSpan.FromSeconds(_fadeSeconds);
            _fadeTimer.Start();
        }
    }

    private void HoverTarget_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _hoverIntentTimer.Stop();
        _hoverIntentTimer.Start();
    }

    private void HoverTarget_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (OutputContainer.IsMouseOver || PetAvatar.IsMouseOver) return;
        _hoverIntentTimer.Stop();
        if (_isHoverLocked)
        {
            _isHoverLocked = false;
            RestartFadeTimerIfNeeded();
            RestartPinnedCollapseTimer();
        }
    }

    private void TopResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        => ResizeBubbleHeight(-e.VerticalChange);

    private void LeftResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        => ResizeBubbleWidth(-e.HorizontalChange);

    private void RightResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        => ResizeBubbleWidth(e.HorizontalChange);

    private void TopLeftResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeBubbleWidth(-e.HorizontalChange);
        ResizeBubbleHeight(-e.VerticalChange);
    }

    private void TopRightResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeBubbleWidth(e.HorizontalChange);
        ResizeBubbleHeight(-e.VerticalChange);
    }

    private void ResizeBubbleHeight(double delta)
    {
        _userResizedBubble = true;
        var current = double.IsNaN(OutputBorder.Height) ? OutputBorder.ActualHeight : OutputBorder.Height;
        var next = Math.Clamp(current + delta, 60, 660);
        OutputBorder.MaxHeight = next;
        OutputBorder.Height = next;
    }

    private void ResizeBubbleWidth(double delta)
    {
        _userResizedBubble = true;
        var current = double.IsNaN(OutputBorder.Width) ? OutputBorder.ActualWidth : OutputBorder.Width;
        OutputBorder.Width = Math.Clamp(current + delta, 180, 630);
    }

    private void PinnedTopResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        => ResizePinnedHeight(-e.VerticalChange);

    private void PinnedLeftResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        => ResizePinnedWidth(-e.HorizontalChange);

    private void PinnedRightResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        => ResizePinnedWidth(e.HorizontalChange);

    private void PinnedTopLeftResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizePinnedWidth(-e.HorizontalChange);
        ResizePinnedHeight(-e.VerticalChange);
    }

    private void PinnedTopRightResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizePinnedWidth(e.HorizontalChange);
        ResizePinnedHeight(-e.VerticalChange);
    }

    private void ResizePinnedHeight(double delta)
    {
        var current = double.IsNaN(PinnedScroll.Height) ? PinnedScroll.ActualHeight : PinnedScroll.Height;
        if (current <= 0) current = 220;
        var next = Math.Clamp(current + delta, 100, 520);
        PinnedScroll.MaxHeight = next;
        PinnedScroll.Height = double.NaN;
    }

    private void ResizePinnedWidth(double delta)
    {
        var current = double.IsNaN(PinnedExpandedHost.Width) ? PinnedExpandedHost.ActualWidth : PinnedExpandedHost.Width;
        PinnedExpandedHost.Width = Math.Clamp(current + delta, 220, 630);
    }

    private void CloseBubbleBtn_Click(object sender, RoutedEventArgs e)
    {
        // If there is a pending permission request, keep it alive in the ❓ pill instead of forcing Deny when closing the bubble.
        // Cancelling (not denying) keeps the request alive in the ❓ pill and lets the next queued prompt show.
        if (_cancelPendingPrompt is { } cancel)
        {
            _cancelPendingPrompt = null;
            _pendingPromptKey = null;
            cancel();
        }

        _fadeTimer.Stop();
        if (_suppressPinnedDuringAlertBubble || _controller.GetPinnedReminders().Count > 0 || _controller.GetPendingInteractions().Count > 0)
        {
            FadeOutBubble();
            return;
        }
        OutputBorder.BeginAnimation(OpacityProperty, null);
        BubbleTail.BeginAnimation(OpacityProperty, null);
        OutputBorder.Visibility = Visibility.Collapsed;
        UpdateOutputContainerVisibility();
    }

    private void ShowInlineConfirmation(string title, string message, TaskCompletionSource<bool> tcs)
    {
        if (tcs.Task.IsCompleted) return;
        if (_cancelPendingPrompt is { } prevCancel)
        {
            _cancelPendingPrompt = null;
            _pendingPromptKey = null;
            prevCancel();
        }

        // Dismissing/replacing this bubble without an explicit choice must NOT count as "deny";
        // cancelling keeps the request pending in the ❓ pill so it can still be answered.
        _pendingPromptKey = PromptKey(title, message);
        _cancelPendingPrompt = () => tcs.TrySetCanceled();
        void Resolve(bool approved)
        {
            _cancelPendingPrompt = null;
            _pendingPromptKey = null;
            tcs.TrySetResult(approved);
            _ = RefreshOutputAsync();
        }

        _fadeTimer.Stop();
        _hasOutputContent = true;

        OutputStatusText.Text = "等待您的確認…";
        OutputStatusText.Visibility = Visibility.Visible;

        _currentOutputMarkdown = $"**{title}**\n\n{message}";
        OutputRichText.Visibility = Visibility.Visible;
        OutputRichText.Document = MarkdownRenderer.Render(_currentOutputMarkdown, _isDark);

        OutputActionsPanel.Children.Clear();
        AddAction("同意", true, () => { Resolve(true); return Task.CompletedTask; });
        AddAction("拒絕", false, () => { Resolve(false); return Task.CompletedTask; });
        OutputActionsPanel.Visibility = Visibility.Visible;

        AdjustBubbleSizeForContent($"confirm_{Guid.NewGuid():N}", _currentOutputMarkdown, 2);
        ShowOutputBubbleWithPopIn(resetOnlyIfVisible: true);
    }

    private void ShowInlineCommandApproval(string title, string message, TaskCompletionSource<CommandApprovalDecision> tcs)
    {
        if (tcs.Task.IsCompleted) return;
        if (_cancelPendingPrompt is { } prevCancel)
        {
            _cancelPendingPrompt = null;
            _pendingPromptKey = null;
            prevCancel();
        }

        // Dismissing/replacing this bubble without an explicit choice must NOT count as "deny";
        // cancelling keeps the request pending in the ❓ pill so it can still be answered.
        _pendingPromptKey = PromptKey(title, message);
        _cancelPendingPrompt = () => tcs.TrySetCanceled();
        void Resolve(CommandApprovalDecision decision)
        {
            _cancelPendingPrompt = null;
            _pendingPromptKey = null;
            tcs.TrySetResult(decision);
            _ = RefreshOutputAsync();
        }

        _fadeTimer.Stop();
        _hasOutputContent = true;

        OutputStatusText.Text = "等待您的確認…";
        OutputStatusText.Visibility = Visibility.Visible;

        _currentOutputMarkdown = $"**{title}**\n\n{message}";
        OutputRichText.Visibility = Visibility.Visible;
        OutputRichText.Document = MarkdownRenderer.Render(_currentOutputMarkdown, _isDark);

        OutputActionsPanel.Children.Clear();
        AddAction("同意本次", true, () => { Resolve(CommandApprovalDecision.AllowOnce); return Task.CompletedTask; });
        AddAction("本任務內允許", false, () => { Resolve(CommandApprovalDecision.AllowForTask); return Task.CompletedTask; });
        AddAction("拒絕", false, () => { Resolve(CommandApprovalDecision.Deny); return Task.CompletedTask; });
        OutputActionsPanel.Visibility = Visibility.Visible;

        AdjustBubbleSizeForContent($"cmd_confirm_{Guid.NewGuid():N}", _currentOutputMarkdown, 3);
        ShowOutputBubbleWithPopIn(resetOnlyIfVisible: true);
    }

    private void ShowInlinePathPermission(string title, string message, string targetPath, TaskCompletionSource<PathPermissionDecision> tcs)
    {
        if (tcs.Task.IsCompleted) return;
        if (_cancelPendingPrompt is { } prevCancel)
        {
            _cancelPendingPrompt = null;
            _pendingPromptKey = null;
            prevCancel();
        }

        // Dismissing/replacing this bubble without an explicit choice must NOT count as "deny";
        // cancelling keeps the request pending in the ❓ pill so it can still be answered.
        _pendingPromptKey = PromptKey(title, message);
        _cancelPendingPrompt = () => tcs.TrySetCanceled();
        void Resolve(PathPermissionDecision decision)
        {
            _cancelPendingPrompt = null;
            _pendingPromptKey = null;
            tcs.TrySetResult(decision);
            _ = RefreshOutputAsync();
        }

        _fadeTimer.Stop();
        _hasOutputContent = true;

        OutputStatusText.Text = "等待您的路徑授權…";
        OutputStatusText.Visibility = Visibility.Visible;

        var trustFolder = SandboxService.NormalizeFolderForTrust(targetPath);
        _currentOutputMarkdown = $"**{title}**\n\n{message}\n\n`{targetPath}`\n（若選「永久信任」，將信任 `{trustFolder}` 及其所有子資料夾）";
        OutputRichText.Visibility = Visibility.Visible;
        OutputRichText.Document = MarkdownRenderer.Render(_currentOutputMarkdown, _isDark);

        OutputActionsPanel.Children.Clear();
        AddAction("永久信任", true, () => { Resolve(PathPermissionDecision.TrustPermanently); return Task.CompletedTask; });
        AddAction("信任一次", false, () => { Resolve(PathPermissionDecision.AllowOnce); return Task.CompletedTask; });
        AddAction("拒絕", false, () => { Resolve(PathPermissionDecision.Deny); return Task.CompletedTask; });
        OutputActionsPanel.Visibility = Visibility.Visible;

        AdjustBubbleSizeForContent($"confirm_{Guid.NewGuid():N}", _currentOutputMarkdown, 3);
        ShowOutputBubbleWithPopIn(resetOnlyIfVisible: true);
    }

    private void AdjustBubbleSizeForContent(string messageId, string markdown, int actionCount)
    {
        if (_lastAutoSizedMsgId != messageId)
        {
            _lastAutoSizedMsgId = messageId;
            _userResizedBubble = false;
        }
        if (_userResizedBubble) return;

        var text = markdown ?? "";
        var lines = text.Split('\n');
        var maxLineLen = lines.Length == 0 ? 0 : lines.Max(l => l.Trim().Length);
        var totalLen = text.Trim().Length;

        bool hasMarkdown = text.Contains("```", StringComparison.Ordinal)
            || text.Contains("**", StringComparison.Ordinal)
            || lines.Any(l =>
            {
                var t = l.TrimStart();
                return t.StartsWith('#')
                    || t.StartsWith("|", StringComparison.Ordinal)
                    || t.StartsWith("- ", StringComparison.Ordinal)
                    || t.StartsWith("* ", StringComparison.Ordinal)
                    || t.StartsWith("> ", StringComparison.Ordinal)
                    || (t.Length > 2 && char.IsDigit(t[0]) && (t[1] == '.' || (t.Length > 3 && char.IsDigit(t[1]) && t[2] == '.')));
            });

        double targetWidth;
        if (hasMarkdown)
        {
            targetWidth = 620;
        }
        else if (totalLen == 0)
        {
            targetWidth = 230;
        }
        else if (totalLen <= 28 && lines.Length <= 2 && actionCount == 0)
        {
            targetWidth = 220;
        }
        else if (totalLen <= 90 && lines.Length <= 4 && maxLineLen <= 36 && actionCount <= 1)
        {
            targetWidth = 300;
        }
        else if (totalLen <= 240 && lines.Length <= 8 && maxLineLen <= 55)
        {
            targetWidth = 380;
        }
        else
        {
            targetWidth = 480;
        }

        OutputBorder.Width = Math.Clamp(targetWidth, 220, 620);
        OutputBorder.Height = double.NaN;
        OutputBorder.MaxHeight = 420;
    }

    private async Task BlinkOnceAsync()
    {
        if (VectorMascot.Visibility != Visibility.Visible) return;
        LeftEye.Height = 2;
        RightEye.Height = 2;
        LeftEye.SetValue(Canvas.TopProperty, 51.0);
        RightEye.SetValue(Canvas.TopProperty, 51.0);
        await Task.Delay(120);
        LeftEye.Height = 14;
        RightEye.Height = 14;
        LeftEye.SetValue(Canvas.TopProperty, 45.0);
        RightEye.SetValue(Canvas.TopProperty, 45.0);
    }

    private async Task LoadPetImageAsync()
    {
        var path = await _controller.GetPetImagePathAsync();
        if (!string.IsNullOrWhiteSpace(path))
            ApplyCustomPetImage(path);
    }

    private void ApplyCustomPetImage(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();

            CustomPetImage.Source = bmp;
            CustomPetImage.Visibility = Visibility.Visible;
            VectorMascot.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"無法載入圖片：{ex.Message}", "更換圖片", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task ResetPetImageAsync()
    {
        CustomPetImage.Source = null;
        CustomPetImage.Visibility = Visibility.Collapsed;
        VectorMascot.Visibility = Visibility.Visible;
        await _controller.SetPetImagePathAsync("");
    }

    private void OpenSettingsWindow()
    {
        if (_settingsWindow is not null && _settingsWindow.IsVisible)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(
            _controller,
            PickAndApplyPetImageAsync,
            ResetPetImageAsync,
            ConnectGmailAsync);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void OpenMonitorWindow()
    {
        if (_monitorWindow is not null && _monitorWindow.IsVisible)
        {
            _monitorWindow.Activate();
            return;
        }

        _monitorWindow = new AgentMonitorWindow(_controller);
        _monitorWindow.Closed += (_, _) => _monitorWindow = null;
        _monitorWindow.Show();
    }

    private void OpenChatHistoryWindow()
    {
        if (_chatHistoryWindow is not null && _chatHistoryWindow.IsVisible)
        {
            _chatHistoryWindow.Activate();
            return;
        }

        _chatHistoryWindow = new ChatHistoryWindow(_controller);
        _chatHistoryWindow.Closed += (_, _) => _chatHistoryWindow = null;
        _chatHistoryWindow.Show();
    }

    private async Task ConnectGmailAsync()
    {
        try
        {
            if (!_controller.HasGmailClientCredentials)
            {
                if (!PromptForGmailClientCredentials())
                    return;
            }

            var ok = await _controller.AuthorizeGmailAsync();
            if (!ok)
                MessageBox.Show("Gmail 授權未完成或已取消。", "Gmail 授權", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Gmail 授權提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool PromptForGmailClientCredentials()
    {
        var dlg = new Window
        {
            Title = "設定 Google OAuth 用戶端",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            Background = Brushes.White
        };

        var stack = new StackPanel { Margin = new Thickness(16) };
        stack.Children.Add(new TextBlock
        {
            Text = "請輸入 Google OAuth Client ID（或選擇下載的 credentials.json）以開啟網頁授權：",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });

        var idBox = new TextBox { Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 0, 8) };
        var secretBox = new TextBox { Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 0, 12) };
        stack.Children.Add(new TextBlock { Text = "Client ID：", FontSize = 11.5 });
        stack.Children.Add(idBox);
        stack.Children.Add(new TextBlock { Text = "Client Secret（若為桌面版憑證）：", FontSize = 11.5 });
        stack.Children.Add(secretBox);

        var row = new DockPanel { LastChildFill = false };
        var fileBtn = MakeActionButton("匯入 JSON…", false);
        fileBtn.Click += (_, _) =>
        {
            var ofd = new OpenFileDialog { Filter = "JSON 檔案|*.json" };
            if (ofd.ShowDialog() == true)
            {
                File.Copy(ofd.FileName, Path.Combine(AppPaths.UserDataRoot, "gmail_credentials.json"), overwrite: true);
                dlg.DialogResult = true;
                dlg.Close();
            }
        };
        row.Children.Add(fileBtn);

        var okBtn = MakeActionButton("前往 Google 授權", true);
        DockPanel.SetDock(okBtn, Dock.Right);
        okBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(idBox.Text)) return;
            _controller.SaveGmailCredentials(idBox.Text, secretBox.Text);
            dlg.DialogResult = true;
            dlg.Close();
        };
        row.Children.Add(okBtn);
        stack.Children.Add(row);

        dlg.Content = stack;
        return dlg.ShowDialog() == true;
    }

    private async Task RefreshOutputAsync()
    {
        if (HasPendingPrompt)
            return;

        var messages = await _controller.GetMessagesAsync();
        OutputActionsPanel.Children.Clear();
        OutputActionsPanel.Visibility = Visibility.Collapsed;

        var streamingAssistant = messages.LastOrDefault(m => m.Role == "assistant" && m.IsStreaming);
        var latest = (_controller.IsMainTurnActive && streamingAssistant is not null)
            ? streamingAssistant
            : _controller.HasRunningTasks
                ? (messages.LastOrDefault(m => m.Role == "assistant") ?? messages.LastOrDefault(m => m.Role is "assistant" or "notification"))
                : messages.LastOrDefault(m => m.Role is "assistant" or "notification");
        if (latest is null)
        {
            _hasOutputContent = false;
            _isReplying = false;
            OutputBorder.Visibility = Visibility.Collapsed;
            UpdateOutputContainerVisibility();
            UpdateSendButtonState();
            return;
        }

        _hasOutputContent = true;
        _isReplying = _controller.IsMainTurnActive && latest.IsStreaming;
        UpdateSendButtonState();

        if (latest.IsStreaming && !string.IsNullOrWhiteSpace(latest.StatusText))
        {
            OutputStatusText.Text = latest.StatusText;
            OutputStatusText.Visibility = Visibility.Visible;
        }
        else
        {
            OutputStatusText.Visibility = Visibility.Collapsed;
        }

        if (latest.Role == "notification" && latest.Notification is not null)
        {
            var n = latest.Notification;
            _currentOutputMarkdown = $"**{n.Title}**\n\n{n.Summary}";
            OutputRichText.Visibility = Visibility.Visible;
            OutputRichText.Document = MarkdownRenderer.Render(_currentOutputMarkdown, _isDark);

            RenderNotificationActions(n, pinnedId: null, AddAction);
            if (OutputActionsPanel.Children.Count > 0)
                OutputActionsPanel.Visibility = Visibility.Visible;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(latest.Content))
            {
                _currentOutputMarkdown = "";
                OutputRichText.Document = new System.Windows.Documents.FlowDocument();
                OutputRichText.Visibility = Visibility.Collapsed;
            }
            else
            {
                _currentOutputMarkdown = latest.Content;
                OutputRichText.Visibility = Visibility.Visible;
                OutputRichText.Document = MarkdownRenderer.Render(_currentOutputMarkdown, _isDark);
            }

            if (latest.Sources.Count > 0)
            {
                foreach (var src in latest.Sources.Take(3))
                {
                    var mid = src.MailId;
                    AddAction($"[{Truncate(src.Label, 18)}]", false, async () => await ShowMailDialogAsync(mid));
                }
                OutputActionsPanel.Visibility = Visibility.Visible;
            }

            if (!latest.IsStreaming)
            {
                var choices = _controller.GetChoicesForMessage(latest.Id);
                var choiceLogicalTaskId = _controller.GetChoicesLogicalTaskIdForMessage(latest.Id);
                for (int i = 0; i < choices.Count; i++)
                {
                    var chosenText = choices[i];
                    AddAction(chosenText, i == 0 && latest.Sources.Count == 0, async () =>
                    {
                        _controller.ClearChoices();
                        await _controller.SendUserPromptAsync(chosenText, replyToLogicalTaskId: choiceLogicalTaskId);
                    });
                }
                if (choices.Count > 0)
                    OutputActionsPanel.Visibility = Visibility.Visible;
            }
        }

        AdjustBubbleSizeForContent(latest.Id, _currentOutputMarkdown, OutputActionsPanel.Children.Count);

        if (latest.IsStreaming)
        {
            _fadeTimer.Stop();
            if (_forceShowProcessingBubble)
            {
                _forceShowProcessingBubble = false;
                if (OutputBorder.Visibility != Visibility.Visible || OutputBorder.Opacity < 0.85)
                    ShowOutputBubbleWithPopIn();
            }
            else if (OutputContainer.Visibility != Visibility.Visible)
            {
                ShowOutputBubbleWithPopIn();
            }
        }
        else if (_lastCompletedMsgId != latest.Id)
        {
            _lastCompletedMsgId = latest.Id;
            ShowOutputBubbleWithPopIn();
        }

        OutputScroll.ScrollToEnd();
    }

    private void UpdateSendButtonState()
    {
        if (_isReplying)
        {
            SendBtn.Content = "■";
            SendBtn.IsEnabled = true;
            SendBtn.Background = BrushFrom("#DC2626");
            SendBtn.Foreground = Brushes.White;
            SendBtn.ToolTip = "停止回覆";
        }
        else if (string.IsNullOrWhiteSpace(InputBox.Text) && _attachedFiles.Count == 0)
        {
            SendBtn.Content = "↑";
            SendBtn.IsEnabled = false;
            SendBtn.Background = BrushFrom("#5594A3B8");
            SendBtn.Foreground = BrushFrom("#AAFFFFFF");
            SendBtn.ToolTip = null;
        }
        else
        {
            SendBtn.Content = "↑";
            SendBtn.IsEnabled = true;
            SendBtn.Background = BrushFrom("#2563EB");
            SendBtn.Foreground = Brushes.White;
            SendBtn.ToolTip = "送出";
        }
    }

    private void AddAction(string label, bool isPrimary, Func<Task> onClick)
    {
        var btn = MakeActionButton(label, isPrimary);
        btn.Click += async (_, _) => await onClick();
        OutputActionsPanel.Children.Add(btn);
    }

    private async Task ShowMailDialogAsync(string mailId)
    {
        MailItem? mail;
        try
        {
            mail = await _controller.GetMailDetailAsync(mailId);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"讀取郵件失敗：{ex.Message}", "郵件詳情", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (mail is null)
        {
            MessageBox.Show("找不到該封郵件內容。", "郵件詳情", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new Window
        {
            Title = mail.Subject,
            Width = 540,
            Height = 440,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = Topmost,
            Background = Brushes.White
        };

        var grid = new Grid { Margin = new Thickness(18) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var meta = new TextBlock
        {
            Text = $"寄件者：{mail.SenderName} <{mail.SenderEmail}>\n時間：{mail.ReceivedAt.ToLocalTime():yyyy-MM-dd HH:mm}",
            FontSize = 13,
            Foreground = BrushFrom("#64748B"),
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(meta, 0);
        grid.Children.Add(meta);

        var bodyBox = new TextBox
        {
            Text = mail.Body,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(10),
            Background = BrushFrom("#F8FAFC"),
            BorderBrush = BrushFrom("#E2E8F0"),
            FontSize = 13.5
        };
        Grid.SetRow(bodyBox, 1);
        grid.Children.Add(bodyBox);

        dlg.Content = grid;
        dlg.ShowDialog();
    }

    private void DragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
            _ = _controller.SaveWindowPositionAsync(Left, Top);
        }
    }

    private Point ScreenPixelsToDip(Point screenPx)
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is not null)
        {
            return source.CompositionTarget.TransformFromDevice.Transform(screenPx);
        }
        return screenPx;
    }

    private void Pet_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _isDraggingPet = false;
            ShowOutputBubbleWithPopIn(resetOnlyIfVisible: true);
            return;
        }

        _isDraggingPet = true;
        _movedDuringPress = false;
        _dragStartDip = ScreenPixelsToDip(PointToScreen(e.GetPosition(this)));
        _windowStartPos = new Point(Left, Top);
        PetAvatar.CaptureMouse();
    }

    private void Pet_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDraggingPet || e.LeftButton != MouseButtonState.Pressed) return;
        var currentDip = ScreenPixelsToDip(PointToScreen(e.GetPosition(this)));
        var dx = currentDip.X - _dragStartDip.X;
        var dy = currentDip.Y - _dragStartDip.Y;
        if (Math.Abs(dx) > 3 || Math.Abs(dy) > 3)
        {
            _movedDuringPress = true;
            Left = _windowStartPos.X + dx;
            Top = _windowStartPos.Y + dy;
        }
    }

    private void Pet_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingPet) return;
        _isDraggingPet = false;
        PetAvatar.ReleaseMouseCapture();

        if (!_movedDuringPress)
        {
            InputBox.Focus();
        }
        else
        {
            _ = _controller.SaveWindowPositionAsync(Left, Top);
        }
    }

    private async void MenuInfo_Click(object sender, RoutedEventArgs e)
        => await OpenInfoWindowAsync();

    private async Task OpenInfoWindowAsync()
    {
        if (_infoWindow is not null && _infoWindow.IsVisible)
        {
            _infoWindow.Activate();
            return;
        }

        var (model, endpoint, inTok, outTok, cacheTok) = await _controller.GetModelAndUsageInfoAsync();
        var branchStats = await _controller.GetBranchTelemetryAsync();
        var dark = ThemeHelper.IsDarkEffective(await _controller.GetThemeModeAsync());

        var win = new Window
        {
            Title = "AI 秘書 — 資訊",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            Background = BrushFrom(dark ? "#0F172A" : "#F8FAFC"),
            Foreground = BrushFrom(dark ? "#F1F5F9" : "#0F172A")
        };
        _infoWindow = win;
        win.Closed += (_, _) => _infoWindow = null;

        var panel = new StackPanel { Margin = new Thickness(18) };
        void AddRow(string label, string value, bool bold = false)
        {
            var dp = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            dp.Children.Add(new TextBlock
            {
                Text = label,
                Width = 115,
                Foreground = BrushFrom(dark ? "#94A3B8" : "#64748B"),
                FontSize = 12.5
            });
            dp.Children.Add(new TextBlock
            {
                Text = value,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = BrushFrom(dark ? "#F1F5F9" : "#0F172A"),
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(dp);
        }

        AddRow("AI 模型：", model, bold: true);
        AddRow("端點：", endpoint);
        panel.Children.Add(new Separator { Margin = new Thickness(0, 4, 0, 10) });
        AddRow("累計輸入 Token：", $"{inTok:N0}");
        AddRow("累計輸出 Token：", $"{outTok:N0}");
        AddRow("累計快取 Token：", $"{cacheTok:N0}");
        AddRow("總計 Token：", $"{(inTok + outTok + cacheTok):N0}", bold: true);
        panel.Children.Add(new Separator { Margin = new Thickness(0, 4, 0, 10) });
        AddRow("Workflow 快取：", $"Hot {branchStats.HotWorkflows} / Cold {branchStats.ColdWorkflows} (共 {branchStats.TotalWorkflows})");
        AddRow("分支預測命中：", $"{branchStats.Hits} / {branchStats.Predictions} ({branchStats.HitRatePercent:0.0}%)");
        AddRow("預測失敗 (Miss)：", $"{branchStats.Mispredictions}");
        AddRow("估計節省 Token：", $"{branchStats.EstimatedTokensSaved:N0}", bold: true);

        var closeBtn = MakeActionButton("關閉", false);
        closeBtn.HorizontalAlignment = HorizontalAlignment.Right;
        closeBtn.Margin = new Thickness(0, 6, 0, 0);
        closeBtn.Background = BrushFrom(dark ? "#334155" : "#FFFFFF");
        closeBtn.Foreground = BrushFrom(dark ? "#F1F5F9" : "#0F172A");
        closeBtn.BorderBrush = BrushFrom(dark ? "#475569" : "#CBD5E1");
        closeBtn.Click += (_, _) => win.Close();
        panel.Children.Add(closeBtn);

        win.Content = panel;
        win.Loaded += (_, _) => Win11BackdropHelper.Apply(win, dark);
        win.Show();
    }

    private void MenuChatHistory_Click(object sender, RoutedEventArgs e) => OpenChatHistoryWindow();

    private void PetContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        var (enabled, profile, mode) = _controller.GetTtsSettings();
        MenuTtsEnabled.IsChecked = enabled;

        int startIdx = MenuVoiceRoot.Items.IndexOf(MenuVoiceProfilesStartSeparator) + 1;
        int endIdx = MenuVoiceRoot.Items.IndexOf(MenuVoiceProfilesEndSeparator);
        for (int i = endIdx - 1; i >= startIdx; i--)
            MenuVoiceRoot.Items.RemoveAt(i);

        var voices = _controller.GetAvailableVoiceProfiles();
        for (int i = 0; i < voices.Count; i++)
        {
            var (key, displayName) = voices[i];
            var vItem = new MenuItem
            {
                Header = displayName,
                Tag = key,
                IsCheckable = true,
                IsChecked = string.Equals(profile, key, StringComparison.OrdinalIgnoreCase)
            };
            vItem.Click += MenuVoiceProfile_Click;
            MenuVoiceRoot.Items.Insert(startIdx + i, vItem);
        }

        MenuTtsModeSmart.IsChecked = mode == "smart";
        MenuTtsModeSummary.IsChecked = mode == "summary";
        MenuTtsModeFull.IsChecked = mode == "full";
        MenuTtsModeNotify.IsChecked = mode == "notify_only";

        MenuStyleRoot.Items.Clear();
        var activePath = _controller.GetActiveStylePath();

        var defaultItem = new MenuItem
        {
            Header = "預設（標準秘書）",
            IsCheckable = true,
            IsChecked = string.IsNullOrWhiteSpace(activePath)
        };
        defaultItem.Click += async (_, _) => await _controller.SetActiveStylePathAsync(null);
        MenuStyleRoot.Items.Add(defaultItem);

        var styles = _controller.GetAvailableStyles();
        if (styles.Count > 0)
        {
            MenuStyleRoot.Items.Add(new Separator());
            foreach (var st in styles)
            {
                var path = st.FilePath;
                var item = new MenuItem
                {
                    Header = st.DisplayName,
                    IsCheckable = true,
                    IsChecked = string.Equals(activePath, path, StringComparison.OrdinalIgnoreCase)
                };
                item.Click += async (_, _) => await _controller.SetActiveStylePathAsync(path);
                MenuStyleRoot.Items.Add(item);
            }
        }

        MenuStyleRoot.Items.Add(new Separator());
        var importItem = new MenuItem { Header = "匯入語氣檔 (.md)..." };
        importItem.Click += async (_, _) =>
        {
            var dlg = new OpenFileDialog
            {
                Title = "選擇語氣設定檔 (.md)",
                Filter = "Markdown 檔案 (*.md)|*.md|所有檔案 (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                await _controller.ImportAndActivateStyleAsync(dlg.FileName);
            }
        };
        MenuStyleRoot.Items.Add(importItem);
    }

    private async void MenuTtsEnabled_Click(object sender, RoutedEventArgs e)
    {
        var (_, profile, mode) = _controller.GetTtsSettings();
        await _controller.SaveTtsSettingsAsync(MenuTtsEnabled.IsChecked, profile, mode);
    }

    private async void MenuVoiceProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string profile })
        {
            var (enabled, _, mode) = _controller.GetTtsSettings();
            await _controller.SaveTtsSettingsAsync(enabled, profile, mode);
        }
    }

    private async void MenuTtsMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string mode })
        {
            var (enabled, profile, _) = _controller.GetTtsSettings();
            await _controller.SaveTtsSettingsAsync(enabled, profile, mode);
        }
    }

    private void MenuTtsTest_Click(object sender, RoutedEventArgs e)
    {
        _controller.TestTtsVoice();
    }

    private async void MenuImportVoiceZip_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "匯入語音包 (.zip)",
            Filter = "語音包壓縮檔 (*.zip)|*.zip"
        };
        if (dlg.ShowDialog() != true) return;

        var (ok, msg) = await _controller.ImportVoicePackageAsync(dlg.FileName);
        if (!ok)
        {
            MessageBox.Show(this, msg, "匯入語音包失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        OutputStatusText.Text = msg;
    }

    private void MenuDetails_Click(object sender, RoutedEventArgs e) => OpenMonitorWindow();

    private void MenuSettings_Click(object sender, RoutedEventArgs e) => OpenSettingsWindow();

    private async void MenuOpenDedicatedBrowser_Click(object sender, RoutedEventArgs e)
    {
        var (ok, msg) = await _controller.OpenDedicatedBrowserAsync();
        if (!ok)
        {
            MessageBox.Show(msg, "專用瀏覽器", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void InputBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (TryPasteImageOrFilesFromClipboard())
            e.CancelCommand();
    }

    private bool TryPasteImageOrFilesFromClipboard()
    {
        if (Clipboard.ContainsImage())
        {
            try
            {
                var img = Clipboard.GetImage();
                if (img is not null)
                {
                    var fileName = $"paste_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
                    var path = Path.Combine(_sandbox.RootPath, fileName);
                    using (var fs = File.Create(path))
                    {
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(img));
                        encoder.Save(fs);
                    }
                    _attachedFiles.Add(path);
                    RefreshAttachmentChips();
                    UpdateSendButtonState();
                    return true;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"貼上圖片失敗：{ex.Message}", "貼上圖片", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        if (Clipboard.ContainsFileDropList())
        {
            var files = Clipboard.GetFileDropList();
            if (files.Count > 0)
            {
                foreach (string? f in files)
                {
                    if (!string.IsNullOrWhiteSpace(f) && !_attachedFiles.Contains(f, StringComparer.OrdinalIgnoreCase))
                        _attachedFiles.Add(f);
                }
                RefreshAttachmentChips();
                UpdateSendButtonState();
                return true;
            }
        }

        return false;
    }

    private void AttachBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "選擇附件",
            Multiselect = true,
            Filter = "所有檔案|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            foreach (var f in dlg.FileNames)
            {
                if (!_attachedFiles.Contains(f, StringComparer.OrdinalIgnoreCase))
                    _attachedFiles.Add(f);
            }
            RefreshAttachmentChips();
            UpdateSendButtonState();
            InputBox.Focus();
        }
    }

    private void RefreshAttachmentChips()
    {
        AttachmentPanel.Children.Clear();
        AttachmentPanel.Visibility = _attachedFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var file in _attachedFiles.ToList())
        {
            var chip = new Border
            {
                Background = BrushFrom("#E2E8F0"),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 2, 6, 2),
                Margin = new Thickness(0, 0, 6, 2)
            };
            var sp = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            sp.Children.Add(new TextBlock
            {
                Text = $"📎 {Truncate(Path.GetFileName(file), 20)}",
                FontSize = 11.5,
                Foreground = BrushFrom("#1E293B"),
                VerticalAlignment = VerticalAlignment.Center
            });
            var rm = new Button
            {
                Content = "×",
                FontSize = 11,
                Margin = new Thickness(4, 0, 0, 0),
                Padding = new Thickness(2, 0, 2, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = BrushFrom("#64748B")
            };
            var captured = file;
            rm.Click += (_, _) =>
            {
                _attachedFiles.Remove(captured);
                RefreshAttachmentChips();
                UpdateSendButtonState();
            };
            sp.Children.Add(rm);
            chip.Child = sp;
            AttachmentPanel.Children.Add(chip);
        }
    }

    private async Task PickAndApplyPetImageAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = "選擇桌面寵物圖片",
            Filter = "圖片檔案|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|所有檔案|*.*"
        };

        if (dlg.ShowDialog() == true)
        {
            ApplyCustomPetImage(dlg.FileName);
            await _controller.SetPetImagePathAsync(dlg.FileName);
        }
    }

    private void MenuMinimize_Click(object sender, RoutedEventArgs e) => Hide();

    private void MenuExit_Click(object sender, RoutedEventArgs e) => Close();

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        InputPlaceholder.Visibility = string.IsNullOrEmpty(InputBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        UpdateSendButtonState();
    }

    private async void SendBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isReplying)
        {
            _controller.StopAllTasks();
            return;
        }
        await SubmitInputAsync();
    }

    private async void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            var raw = InputBox.Text.Trim();
            if (raw.Equals("/kill magent", StringComparison.OrdinalIgnoreCase))
            {
                InputBox.Clear();
                await _controller.KillMainAgentAsync();
                return;
            }
            if (_isReplying || (string.IsNullOrWhiteSpace(InputBox.Text) && _attachedFiles.Count == 0))
                return;
            await SubmitInputAsync();
        }
    }

    private async Task SubmitInputAsync()
    {
        var text = InputBox.Text.Trim();
        if (text.Equals("/kill magent", StringComparison.OrdinalIgnoreCase))
        {
            InputBox.Clear();
            await _controller.KillMainAgentAsync();
            return;
        }
        if (string.IsNullOrWhiteSpace(text) && _attachedFiles.Count == 0) return;

        if (_attachedFiles.Count > 0)
        {
            var filesNote = string.Join("\n", _attachedFiles.Select(f => $"[Attachment: {f}]"));
            text = string.IsNullOrWhiteSpace(text) ? filesNote : $"{text}\n{filesNote}";
            _attachedFiles.Clear();
            RefreshAttachmentChips();
        }

        InputBox.Clear();
        UpdateSendButtonState();
        _forceShowProcessingBubble = true;
        await _controller.SendUserPromptAsync(text);
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    private Button MakeActionButton(string label, bool isPrimary)
    {
        var bg = isPrimary
            ? BrushFrom("#2563EB")
            : BrushFrom(_isDark ? "#22FFFFFF" : "#120F172A");
        var hoverBg = isPrimary
            ? BrushFrom("#1D4ED8")
            : BrushFrom(_isDark ? "#38FFFFFF" : "#220F172A");
        var fg = isPrimary
            ? Brushes.White
            : BrushFrom(_isDark ? "#E2E8F0" : "#1E293B");
        var border = isPrimary
            ? BrushFrom("#3B82F6")
            : BrushFrom(_isDark ? "#44FFFFFF" : "#220F172A");

        var btn = new Button
        {
            Content = new TextBlock
            {
                Text = label,
                TextWrapping = TextWrapping.Wrap
            },
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 4),
            FontSize = 11.5,
            Background = bg,
            Foreground = fg,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand
        };

        var template = new ControlTemplate(typeof(Button));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.Name = "PillBd";
        borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        borderFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
        borderFactory.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
        borderFactory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));

        var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        contentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        borderFactory.AppendChild(contentFactory);
        template.VisualTree = borderFactory;

        var hoverTrigger = new Trigger { Property = IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(BackgroundProperty, hoverBg));
        template.Triggers.Add(hoverTrigger);

        btn.Template = template;
        return btn;
    }

    private static SolidColorBrush BrushFrom(string hex)
        => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

    private static Color ColorFrom(string hex)
        => (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!;

    private static Icon CreateTrayIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(System.Drawing.Color.Transparent);

        using var bg = new SolidBrush(System.Drawing.Color.FromArgb(59, 130, 246));
        g.FillEllipse(bg, 2, 2, 28, 28);

        using var face = new SolidBrush(System.Drawing.Color.White);
        g.FillEllipse(face, 6, 7, 20, 18);

        using var eye = new SolidBrush(System.Drawing.Color.FromArgb(30, 41, 59));
        g.FillEllipse(eye, 10, 13, 4, 5);
        g.FillEllipse(eye, 18, 13, 4, 5);

        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }
}
