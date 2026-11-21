using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Services;
using Brush = System.Windows.Media.Brush;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using UiAppearance = Wpf.Ui.Controls.ControlAppearance;
using UiButton = Wpf.Ui.Controls.Button;

namespace AiSecretary.Desktop;

public partial class AgentMonitorWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly SecretaryController _controller;
    private readonly Dictionary<string, bool> _sectionExpanded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Expander> _sectionExpanders = new(StringComparer.Ordinal);
    private bool _isDark;

    public AgentMonitorWindow(SecretaryController controller)
    {
        FluentThemeHelper.EnsureSystemAccent();
        InitializeComponent();
        _controller = controller;

        _isDark = ThemeHelper.IsDarkEffective("System");
        FluentThemeHelper.Apply(this, _isDark);
        SourceInitialized += (_, _) => FluentThemeHelper.Apply(this, _isDark);

        Loaded += async (_, _) =>
        {
            ApplyTheme(await _controller.GetThemeModeAsync());
            await RefreshRunsAsync();
        };

        _controller.AgentsChanged += () => Dispatcher.InvokeAsync(async () => await RefreshRunsAsync());
        _controller.PinnedRemindersChanged += () => Dispatcher.InvokeAsync(async () => await RefreshRunsAsync());
        _controller.WatcherTriggered += (_, _) => Dispatcher.InvokeAsync(async () => await RefreshRunsAsync());
        _controller.ThemeChanged += mode => Dispatcher.InvokeAsync(async () =>
        {
            ApplyTheme(mode);
            await RefreshRunsAsync();
        });
    }

    private void ApplyTheme(string mode)
    {
        _isDark = ThemeHelper.IsDarkEffective(mode);
        FluentThemeHelper.Apply(this, _isDark);
    }

    private async Task RefreshRunsAsync()
    {
        RunsPanel.Children.Clear();
        _sectionExpanders.Clear();

        var runs = _controller.GetAgentRuns();
        var schedules = await _controller.GetPendingSchedulesAsync();
        var watchers = await _controller.GetWatchersAsync();
        var calendarEvents = await _controller.GetCalendarEventsAsync();
        var pinned = _controller.GetPinnedReminders();
        var learnedRules = await _controller.GetLearnedStateRulesAsync();
        var autonomyPolicies = await _controller.GetAutonomyPoliciesAsync();
        var learningEvents = await _controller.GetRecentLearningEventsAsync(15);

        StopAllBtn.IsEnabled = _controller.HasRunningTasks;

        var runningRuns = runs.Where(IsRunActive).ToList();
        var finishedRuns = runs.Where(r => !IsRunActive(r)).ToList();

        var cardBg = BrushFrom(_isDark ? "#2B2B2B" : "#FFFFFF");
        var cardBorder = BrushFrom(_isDark ? "#3D3D3D" : "#E0E0E0");

        // 1) 執行中任務（Running）
        AddCollapsibleSection("running", "⚡ 執行中任務", runningRuns.Count, defaultExpanded: runningRuns.Count > 0, body =>
        {
            if (runningRuns.Count == 0)
            {
                body.Children.Add(MakeEmptyNote("目前沒有正在執行的 Agent 任務。"));
                return;
            }
            foreach (var run in runningRuns)
                body.Children.Add(BuildRunCard(run, cardBg, cardBorder, isRunning: true));
        });

        // 2) 置頂提醒與待確認（Pinned & Confirmations）
        AddCollapsibleSection("pinned", "📌 置頂提醒與待確認", pinned.Count, defaultExpanded: pinned.Count > 0, body =>
        {
            if (pinned.Count == 0)
            {
                body.Children.Add(MakeEmptyNote("目前沒有置頂提醒或待確認項目。"));
                return;
            }
            foreach (var p in pinned)
            {
                var kindTag = p.Kind switch
                {
                    PinnedItemKind.Confirmation => "待確認",
                    PinnedItemKind.Schedule => "排程提醒",
                    _ => "通知"
                };
                var freq = p.ReRemindMinutes > 0 ? $"每 {p.ReRemindMinutes} 分鐘氣泡提醒" : "僅置頂不重複";
                var pcard = MakeCard(cardBg, BrushFrom(_isDark ? "#60A5FA" : "#F59E0B"));
                var dock = new DockPanel { LastChildFill = true };
                var dismissBtn = MakeActionBtn("關閉", UiAppearance.Danger);
                DockPanel.SetDock(dismissBtn, Dock.Right);
                var pid = p.Id;
                dismissBtn.Click += async (_, _) => await _controller.DismissPinnedReminderAsync(pid);
                dock.Children.Add(dismissBtn);

                var info = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
                info.Children.Add(new TextBlock
                {
                    Text = $"[{kindTag}] {p.Title}（{freq}）",
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });
                info.Children.Add(new TextBlock
                {
                    Text = p.Summary,
                    FontSize = 12,
                    Opacity = 0.74,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                });
                dock.Children.Add(info);
                pcard.Child = dock;
                body.Children.Add(pcard);
            }
        });

        // 3) 定期與單次排程（Scheduled Skills & Reminders）
        AddCollapsibleSection("schedules", "⏰ 定期與單次排程", schedules.Count, defaultExpanded: schedules.Count > 0, body =>
        {
            if (schedules.Count == 0)
            {
                body.Children.Add(MakeEmptyNote("目前沒有待執行的排程任務。"));
                return;
            }
            foreach (var s in schedules)
            {
                var scard = MakeCard(cardBg, cardBorder);
                var dock = new DockPanel { LastChildFill = true };
                var delBtn = MakeActionBtn("刪除", UiAppearance.Danger);
                DockPanel.SetDock(delBtn, Dock.Right);
                var sid = s.ReminderId;
                delBtn.Click += async (_, _) => await _controller.DeleteScheduleAsync(sid);
                dock.Children.Add(delBtn);

                var info = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
                var freq = s.IntervalMinutes > 0 ? $"每 {s.IntervalMinutes} 分鐘" : "單次";
                var wdTag = s.WorkdaysOnly ? "・僅工作日" : "";
                var typeTag = string.IsNullOrWhiteSpace(s.SkillName) ? "一般提醒" : $"技能排程：{s.SkillName}";
                info.Children.Add(new TextBlock
                {
                    Text = $"{s.Title}（{freq}{wdTag}・{typeTag}）",
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });
                info.Children.Add(new TextBlock
                {
                    Text = $"下次執行：{s.RemindAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}｜{s.Message}",
                    FontSize = 12,
                    Opacity = 0.74,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                });
                dock.Children.Add(info);
                scard.Child = dock;
                body.Children.Add(scard);
            }
        });

        // 4) 網頁常駐監控（Standing Watchers）
        AddCollapsibleSection("watchers", "👁️ 網頁常駐監控（Watchers）", watchers.Count, defaultExpanded: watchers.Count > 0, body =>
        {
            if (watchers.Count == 0)
            {
                body.Children.Add(MakeEmptyNote("目前沒有常駐網頁監控（可用對話要求追蹤包裹、價格或網頁變動）。"));
                return;
            }
            foreach (var w in watchers)
            {
                var isActive = string.Equals(w.Status, "Active", StringComparison.OrdinalIgnoreCase);
                var statusBorder = w.Status switch
                {
                    "Active" => BrushFrom(_isDark ? "#22C55E" : "#16A34A"),
                    "NeedsAuth" => BrushFrom("#F59E0B"),
                    _ => cardBorder
                };
                var wcard = MakeCard(cardBg, statusBorder);
                var dock = new DockPanel { LastChildFill = true };

                var btnStack = new StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var wid = w.WatcherId;

                var checkNowBtn = MakeActionBtn("立即檢查", UiAppearance.Secondary);
                checkNowBtn.Margin = new Thickness(0, 0, 6, 0);
                checkNowBtn.Click += async (_, _) =>
                {
                    checkNowBtn.IsEnabled = false;
                    checkNowBtn.Content = "檢查中…";
                    try { await _controller.CheckWatcherNowAsync(wid); }
                    finally { checkNowBtn.IsEnabled = true; }
                };
                btnStack.Children.Add(checkNowBtn);

                var toggleBtn = MakeActionBtn(isActive ? "暫停" : "恢復", UiAppearance.Secondary);
                toggleBtn.Margin = new Thickness(0, 0, 6, 0);
                toggleBtn.Click += async (_, _) => await _controller.SetWatcherStatusAsync(wid, isActive ? "Paused" : "Active");
                btnStack.Children.Add(toggleBtn);

                var delBtn = MakeActionBtn("刪除", UiAppearance.Danger);
                delBtn.Click += async (_, _) => await _controller.DeleteWatcherAsync(wid);
                btnStack.Children.Add(delBtn);

                DockPanel.SetDock(btnStack, Dock.Right);
                dock.Children.Add(btnStack);

                var statusLabel = w.Status switch
                {
                    "Active" => "監控中",
                    "Paused" => "已暫停",
                    "NeedsAuth" => "需重新登入",
                    _ => w.Status
                };
                var intervalLabel = w.IntervalSeconds >= 3600
                    ? $"每 {w.IntervalSeconds / 3600.0:0.#} 小時"
                    : $"每 {Math.Max(1, w.IntervalSeconds / 60)} 分鐘";
                var condLabel = string.IsNullOrWhiteSpace(w.ThresholdValue)
                    ? w.ConditionType
                    : $"{w.ConditionType} {w.ThresholdValue}";
                var nextStr = w.NextCheckAt.HasValue ? w.NextCheckAt.Value.ToLocalTime().ToString("MM-dd HH:mm") : "排程中";
                var lastStr = w.LastCheckedAt.HasValue ? w.LastCheckedAt.Value.ToLocalTime().ToString("MM-dd HH:mm") : "尚未檢查";

                var info = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
                info.Children.Add(new TextBlock
                {
                    Text = $"{w.Title}（{statusLabel}・{intervalLabel}・{condLabel}）",
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });
                info.Children.Add(new TextBlock
                {
                    Text = $"上次：{lastStr}｜下次：{nextStr}｜{w.Url}",
                    FontSize = 11.5,
                    Opacity = 0.72,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                });
                info.Children.Add(new TextBlock
                {
                    Text = $"快照：{StandingWatcherService.FormatSnapshotPreview(w.LastSnapshot, 120)}",
                    FontSize = 11.5,
                    Opacity = 0.62,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                });
                dock.Children.Add(info);
                wcard.Child = dock;
                body.Children.Add(wcard);
            }
        });

        // 5) 行事曆行程（請假 / 會議 / 週期行程）
        AddCollapsibleSection("calendar", "📅 行事曆行程（請假 / 會議 / 週期）", calendarEvents.Count, defaultExpanded: calendarEvents.Count > 0, body =>
        {
            if (calendarEvents.Count == 0)
            {
                body.Children.Add(MakeEmptyNote("目前沒有已登錄的行事曆行程。"));
                return;
            }
            foreach (var ev in calendarEvents)
            {
                var ecard = MakeCard(cardBg, cardBorder);
                var dock = new DockPanel { LastChildFill = true };
                var delBtn = MakeActionBtn("刪除", UiAppearance.Danger);
                DockPanel.SetDock(delBtn, Dock.Right);
                var eid = ev.EventId;
                delBtn.Click += async (_, _) => await _controller.DeleteCalendarEventAsync(eid);
                dock.Children.Add(delBtn);

                var info = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
                var recTag = string.IsNullOrWhiteSpace(ev.Recurrence) || ev.Recurrence == "none" ? "" : $"・週期: {ev.Recurrence}";
                info.Children.Add(new TextBlock
                {
                    Text = $"[{ev.Category}] {ev.Title}{recTag}",
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });
                var timeStr = ev.AllDay
                    ? $"{ev.StartAt.ToLocalTime():yyyy-MM-dd} ~ {ev.EndAt.ToLocalTime():yyyy-MM-dd}（全天）"
                    : $"{ev.StartAt.ToLocalTime():yyyy-MM-dd HH:mm} ~ {ev.EndAt.ToLocalTime():yyyy-MM-dd HH:mm}";
                info.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(ev.Notes) ? timeStr : $"{timeStr}｜{ev.Notes}",
                    FontSize = 12,
                    Opacity = 0.74,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                });
                dock.Children.Add(info);
                ecard.Child = dock;
                body.Children.Add(ecard);
            }
        });

        // 6) 近期 Agent 執行紀錄（已完成 / 已終止）
        AddCollapsibleSection("recent_runs", "📋 近期 Agent 執行紀錄", finishedRuns.Count, defaultExpanded: finishedRuns.Count > 0, body =>
        {
            if (finishedRuns.Count == 0)
            {
                body.Children.Add(MakeEmptyNote("目前沒有已完成的 Agent 執行紀錄。"));
                return;
            }
            foreach (var run in finishedRuns)
                body.Children.Add(BuildRunCard(run, cardBg, cardBorder, isRunning: false));
        });

        // 7) 我學到的事 — 視窗／網頁自動化規則（StateMachine）
        AddCollapsibleSection("learned_rules", "🧠 我學到的事（視窗／網頁自動化規則）", learnedRules.Count, defaultExpanded: false, body =>
        {
            if (learnedRules.Count == 0)
            {
                body.Children.Add(MakeEmptyNote("尚未學到任何視窗／網頁自動化規則。"));
                return;
            }
            foreach (var r in learnedRules.Take(20))
            {
                var rcard = MakeCard(cardBg, cardBorder, compact: true);
                var dock = new DockPanel { LastChildFill = true };
                var forgetBtn = MakeActionBtn("忘掉它", UiAppearance.Danger, compact: true);
                var keyCopy = r.TriggerKey;
                forgetBtn.Click += async (_, _) => await _controller.ForgetStateRuleAsync(keyCopy);
                DockPanel.SetDock(forgetBtn, Dock.Right);
                dock.Children.Add(forgetBtn);

                var statusLabel = r.Status switch
                {
                    "Hot" => "自動執行 (Hot)",
                    "Cold" => "待確認 (Cold)",
                    "Muted" => "已靜音 (Muted)",
                    "Ignored" => "暫時略過 (Ignored)",
                    _ => r.Status
                };
                var info = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
                info.Children.Add(new TextBlock
                {
                    Text = $"{r.TriggerKey}  →  {statusLabel}",
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });
                info.Children.Add(new TextBlock
                {
                    Text = $"動作：{r.StateName}｜命中 {r.HitCount} / 失敗 {r.MissCount}｜上次：{r.LastSeenAt.ToLocalTime():MM-dd HH:mm}",
                    FontSize = 11.5,
                    Opacity = 0.72,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                });
                dock.Children.Add(info);
                rcard.Child = dock;
                body.Children.Add(rcard);
            }
        });

        // 8) 驗證碼與動作自治等級（Autonomy Policies）
        AddCollapsibleSection("autonomy_policies", "🛡️ 驗證碼與動作自治等級（Autonomy）", autonomyPolicies.Count, defaultExpanded: false, body =>
        {
            if (autonomyPolicies.Count == 0)
            {
                body.Children.Add(MakeEmptyNote("目前各網站皆採用預設自治策略（同網域高信心 L2 自動填入＋10 秒復原；低信心 L1 詢問）。"));
                return;
            }
            foreach (var pol in autonomyPolicies)
            {
                var pcard = MakeCard(cardBg, cardBorder, compact: true);
                var dock = new DockPanel { LastChildFill = true };
                var resetBtn = MakeActionBtn("重設預設", UiAppearance.Secondary, compact: true);
                var ac = pol.ActionClass;
                var dom = pol.Domain;
                resetBtn.Click += async (_, _) => await _controller.DeleteAutonomyPolicyAsync(ac, dom);
                DockPanel.SetDock(resetBtn, Dock.Right);
                dock.Children.Add(resetBtn);

                var lvlText = pol.Level switch
                {
                    0 => "L0 已停用",
                    1 => "L1 先詢問",
                    2 => "L2 自動執行（附復原）",
                    _ => $"L{pol.Level}"
                };
                var info = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
                info.Children.Add(new TextBlock
                {
                    Text = $"{pol.Domain}（{pol.ActionClass}） → {lvlText}",
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });
                info.Children.Add(new TextBlock
                {
                    Text = $"接受 {pol.Accepted} 次 / 拒絕或復原 {pol.Rejected} 次｜更新：{pol.UpdatedAt.ToLocalTime():MM-dd HH:mm}",
                    FontSize = 11.5,
                    Opacity = 0.72,
                    Margin = new Thickness(0, 2, 0, 0)
                });
                dock.Children.Add(info);
                pcard.Child = dock;
                body.Children.Add(pcard);
            }
        });

        // 9) 近期自主學習紀錄（Learning Events）
        AddCollapsibleSection("learning_events", "📜 近期自主學習紀錄", learningEvents.Count, defaultExpanded: false, body =>
        {
            if (learningEvents.Count == 0)
            {
                body.Children.Add(MakeEmptyNote("目前沒有自主學習或策略調整紀錄。"));
                return;
            }
            foreach (var ev in learningEvents)
            {
                var trans = string.IsNullOrWhiteSpace(ev.BeforeState) && string.IsNullOrWhiteSpace(ev.AfterState)
                    ? ""
                    : $" ({ev.BeforeState} → {ev.AfterState})";
                body.Children.Add(new TextBlock
                {
                    Text = $"• {ev.TimestampUtc.ToLocalTime():MM-dd HH:mm:ss} [{ev.Learner}] {ev.EventType}{trans} — {ev.SubjectKey}" +
                           (string.IsNullOrWhiteSpace(ev.Evidence) ? "" : $"｜{ev.Evidence}"),
                    FontSize = 11.5,
                    Opacity = 0.78,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(4, 1, 4, 3)
                });
            }
        });
    }

    private void AddCollapsibleSection(
        string key,
        string title,
        int count,
        bool defaultExpanded,
        Action<StackPanel> populate)
    {
        var isExpanded = _sectionExpanded.TryGetValue(key, out var saved) ? saved : defaultExpanded;
        var contentPanel = new StackPanel { Margin = new Thickness(2, 6, 2, 2) };
        populate(contentPanel);

        var headerPanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        headerPanel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13.5,
            VerticalAlignment = VerticalAlignment.Center
        });
        headerPanel.Children.Add(new Border
        {
            Background = BrushFrom(count > 0
                ? (_isDark ? "#1E3A8A" : "#DBEAFE")
                : (_isDark ? "#333333" : "#E5E7EB")),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(7, 1, 7, 1),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = count.ToString(),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushFrom(count > 0
                    ? (_isDark ? "#93C5FD" : "#1D4ED8")
                    : (_isDark ? "#A3A3A3" : "#6B7280"))
            }
        });

        var expander = new Expander
        {
            Header = headerPanel,
            Content = contentPanel,
            IsExpanded = isExpanded,
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(8, 6, 8, 8),
            Background = BrushFrom(_isDark ? "#202020" : "#F8FAFC"),
            BorderBrush = BrushFrom(_isDark ? "#343434" : "#E2E8F0"),
            BorderThickness = new Thickness(1)
        };
        expander.Expanded += (_, _) => _sectionExpanded[key] = true;
        expander.Collapsed += (_, _) => _sectionExpanded[key] = false;

        _sectionExpanders[key] = expander;
        RunsPanel.Children.Add(expander);
    }

    private Border BuildRunCard(ActiveAgentRun run, Brush cardBg, Brush cardBorder, bool isRunning)
    {
        var card = MakeCard(cardBg, isRunning ? BrushFrom("#3B82F6") : cardBorder);
        var stack = new StackPanel();

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        if (isRunning)
        {
            var killBtn = MakeActionBtn("強制終止", UiAppearance.Danger);
            var tid = run.TaskId;
            killBtn.Click += (_, _) => _controller.StopTask(tid);
            DockPanel.SetDock(killBtn, Dock.Right);
            top.Children.Add(killBtn);
        }

        top.Children.Add(new TextBlock
        {
            Text = $"{run.StartedAt:HH:mm:ss}  {run.Goal}",
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });
        stack.Children.Add(top);

        var graph = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 2, 0, 6)
        };
        graph.Children.Add(MakeNode("MainAgent", run.MainState));
        graph.Children.Add(MakeArrow(run.PlannerState != "Pending"));
        graph.Children.Add(MakeNode("Planner", run.PlannerState));
        graph.Children.Add(MakeArrow(run.WorkerState != "Pending"));
        graph.Children.Add(MakeNode("Worker / Skills", run.WorkerState));
        stack.Children.Add(graph);

        stack.Children.Add(new TextBlock
        {
            Text = $"狀態：{run.CurrentStep}",
            FontSize = 12,
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap
        });

        card.Child = stack;
        return card;
    }

    private static bool IsRunActive(ActiveAgentRun run)
        => run.MainState == "Running" || run.PlannerState == "Running" || run.WorkerState == "Running";

    private static Border MakeCard(Brush bg, Brush border, bool compact = false) => new()
    {
        Background = bg,
        BorderBrush = border,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = compact ? new Thickness(12, 8, 12, 8) : new Thickness(12, 10, 12, 10),
        Margin = new Thickness(0, 0, 0, compact ? 6 : 8)
    };

    private static UiButton MakeActionBtn(string label, UiAppearance appearance, bool compact = false) => new()
    {
        Content = label,
        Appearance = appearance,
        Padding = compact ? new Thickness(10, 3, 10, 3) : new Thickness(10, 4, 10, 4),
        FontSize = compact ? 11.5 : 12,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static TextBlock MakeEmptyNote(string text) => new()
    {
        Text = text,
        Opacity = 0.65,
        FontSize = 12,
        Margin = new Thickness(4, 2, 4, 4),
        TextWrapping = TextWrapping.Wrap
    };

    private Border MakeNode(string title, string state)
    {
        var (bg, border, fg, label) = state switch
        {
            "Running" => (_isDark ? "#1E3A8A" : "#DBEAFE", "#3B82F6", _isDark ? "#93C5FD" : "#1D4ED8", "運作中"),
            "Done" => (_isDark ? "#14532D" : "#DCFCE7", "#22C55E", _isDark ? "#86EFAC" : "#15803D", "完成"),
            "Stopped" => (_isDark ? "#7F1D1D" : "#FEE2E2", "#EF4444", _isDark ? "#FCA5A5" : "#B91C1C", "已終止"),
            _ => (_isDark ? "#202020" : "#F3F3F3", _isDark ? "#454545" : "#CBD5E1", _isDark ? "#A3A3A3" : "#64748B", "待命")
        };

        return new Border
        {
            Background = BrushFrom(bg),
            BorderBrush = BrushFrom(border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6),
            MinWidth = 112,
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 12, Foreground = BrushFrom(fg), HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = label, FontSize = 11, Foreground = BrushFrom(fg), HorizontalAlignment = HorizontalAlignment.Center }
                }
            }
        };
    }

    private TextBlock MakeArrow(bool active) => new()
    {
        Text = " ──► ",
        FontWeight = FontWeights.Bold,
        FontSize = 13,
        Foreground = BrushFrom(active ? "#3B82F6" : (_isDark ? "#525252" : "#94A3B8")),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(2, 0, 2, 0)
    };

    private void ExpandAllBtn_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (key, exp) in _sectionExpanders)
        {
            _sectionExpanded[key] = true;
            exp.IsExpanded = true;
        }
    }

    private void CollapseAllBtn_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (key, exp) in _sectionExpanders)
        {
            _sectionExpanded[key] = false;
            exp.IsExpanded = false;
        }
    }

    private void StopAllBtn_Click(object sender, RoutedEventArgs e) => _controller.StopAllTasks();

    private static SolidColorBrush BrushFrom(string hex)
        => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
}
