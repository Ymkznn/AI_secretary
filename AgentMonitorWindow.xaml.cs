using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Services;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using UiAppearance = Wpf.Ui.Controls.ControlAppearance;
using UiButton = Wpf.Ui.Controls.Button;

namespace AiSecretary.Desktop;

public partial class AgentMonitorWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly SecretaryController _controller;
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
        var runs = _controller.GetAgentRuns();
        var schedules = await _controller.GetPendingSchedulesAsync();
        var calendarEvents = await _controller.GetCalendarEventsAsync();
        var pinned = _controller.GetPinnedReminders();
        StopAllBtn.IsEnabled = _controller.HasRunningTasks;

        var cardBg = BrushFrom(_isDark ? "#2B2B2B" : "#FFFFFF");
        var cardBorder = BrushFrom(_isDark ? "#3D3D3D" : "#E0E0E0");

        RunsPanel.Children.Add(new TextBlock
        {
            Text = $"已排程的背景任務／置頂提醒（{schedules.Count + pinned.Count}）",
            FontWeight = FontWeights.SemiBold,
            FontSize = 13.5,
            Margin = new Thickness(2, 0, 0, 8)
        });

        if (schedules.Count == 0 && pinned.Count == 0)
        {
            RunsPanel.Children.Add(new TextBlock
            {
                Text = "目前沒有待執行的排程或置頂提醒。",
                Opacity = 0.65,
                Margin = new Thickness(4, 0, 4, 12)
            });
        }
        else
        {
            foreach (var p in pinned)
            {
                var pcard = new Border
                {
                    Background = cardBg,
                    BorderBrush = BrushFrom(_isDark ? "#60A5FA" : "#F59E0B"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 10, 12, 10),
                    Margin = new Thickness(0, 0, 0, 8)
                };
                var dock = new DockPanel { LastChildFill = true };
                var dismissBtn = new UiButton
                {
                    Content = "關閉",
                    Appearance = UiAppearance.Danger,
                    Padding = new Thickness(10, 4, 10, 4),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                };
                DockPanel.SetDock(dismissBtn, Dock.Right);
                var pid = p.Id;
                dismissBtn.Click += async (_, _) => await _controller.DismissPinnedReminderAsync(pid);
                dock.Children.Add(dismissBtn);

                var info = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
                var freq = p.ReRemindMinutes > 0 ? $"每 {p.ReRemindMinutes} 分鐘於氣泡提醒" : "僅置頂不重複";
                info.Children.Add(new TextBlock
                {
                    Text = $"📌 {p.Title}（置頂提醒・{freq}）",
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });
                info.Children.Add(new TextBlock
                {
                    Text = p.Summary,
                    FontSize = 12,
                    Opacity = 0.72,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                });
                dock.Children.Add(info);
                pcard.Child = dock;
                RunsPanel.Children.Add(pcard);
            }

            foreach (var s in schedules)
            {
                var scard = new Border
                {
                    Background = cardBg,
                    BorderBrush = cardBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 10, 12, 10),
                    Margin = new Thickness(0, 0, 0, 8)
                };
                var dock = new DockPanel { LastChildFill = true };
                var delBtn = new UiButton
                {
                    Content = "刪除",
                    Appearance = UiAppearance.Danger,
                    Padding = new Thickness(10, 4, 10, 4),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                };
                DockPanel.SetDock(delBtn, Dock.Right);
                var sid = s.ReminderId;
                delBtn.Click += async (_, _) => await _controller.DeleteScheduleAsync(sid);
                dock.Children.Add(delBtn);

                var info = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
                var freq = s.IntervalMinutes > 0 ? $"每 {s.IntervalMinutes} 分鐘" : "單次";
                var wdTag = s.WorkdaysOnly ? "・僅工作日" : "";
                var skillTag = string.IsNullOrWhiteSpace(s.SkillName) ? "提醒" : $"Skill: {s.SkillName}";
                info.Children.Add(new TextBlock
                {
                    Text = $"{s.Title}（{freq}{wdTag}・{skillTag}）",
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });
                info.Children.Add(new TextBlock
                {
                    Text = $"下次執行：{s.RemindAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}｜{s.Message}",
                    FontSize = 12,
                    Opacity = 0.72,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                });
                dock.Children.Add(info);
                scard.Child = dock;
                RunsPanel.Children.Add(scard);
            }
        }

        RunsPanel.Children.Add(new TextBlock
        {
            Text = $"行程表（請假 / 會議 / 週期行程）（{calendarEvents.Count}）",
            FontWeight = FontWeights.SemiBold,
            FontSize = 13.5,
            Margin = new Thickness(2, 8, 0, 8)
        });

        if (calendarEvents.Count == 0)
        {
            RunsPanel.Children.Add(new TextBlock
            {
                Text = "目前沒有已登錄的行事曆行程。",
                Opacity = 0.65,
                Margin = new Thickness(4, 0, 4, 12)
            });
        }
        else
        {
            foreach (var ev in calendarEvents)
            {
                var ecard = new Border
                {
                    Background = cardBg,
                    BorderBrush = cardBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 10, 12, 10),
                    Margin = new Thickness(0, 0, 0, 8)
                };
                var dock = new DockPanel { LastChildFill = true };
                var delBtn = new UiButton
                {
                    Content = "刪除",
                    Appearance = UiAppearance.Danger,
                    Padding = new Thickness(10, 4, 10, 4),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                };
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
                    Opacity = 0.72,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                });
                dock.Children.Add(info);
                ecard.Child = dock;
                RunsPanel.Children.Add(ecard);
            }
        }

        RunsPanel.Children.Add(new TextBlock
        {
            Text = "近期 Agent 執行紀錄",
            FontWeight = FontWeights.SemiBold,
            FontSize = 13.5,
            Margin = new Thickness(2, 8, 0, 8)
        });

        if (runs.Count == 0)
        {
            RunsPanel.Children.Add(new TextBlock
            {
                Text = "目前沒有執行中或近期的 Agent 任務。",
                Opacity = 0.65,
                Margin = new Thickness(4, 2, 4, 8)
            });
        }
        else foreach (var run in runs)
        {
            var isRunning = run.MainState == "Running" || run.PlannerState == "Running" || run.WorkerState == "Running";

            var card = new Border
            {
                Background = cardBg,
                BorderBrush = isRunning ? BrushFrom("#3B82F6") : cardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 10)
            };

            var stack = new StackPanel();

            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            if (isRunning)
            {
                var killBtn = new UiButton
                {
                    Content = "強制終止",
                    Appearance = UiAppearance.Danger,
                    Padding = new Thickness(10, 4, 10, 4),
                    FontSize = 12
                };
                var tid = run.TaskId;
                killBtn.Click += (_, _) => _controller.StopTask(tid);
                DockPanel.SetDock(killBtn, Dock.Right);
                top.Children.Add(killBtn);
            }

            top.Children.Add(new TextBlock
            {
                Text = $"{run.StartedAt:HH:mm:ss}  {run.Goal}",
                FontWeight = FontWeights.SemiBold,
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
                Opacity = 0.72
            });

            card.Child = stack;
            RunsPanel.Children.Add(card);
        }

        // Learned Rules ("我學到的事") + Recent Learning Ledger
        var learnedRules = await _controller.GetLearnedStateRulesAsync();
        var learningEvents = await _controller.GetRecentLearningEventsAsync(12);

        RunsPanel.Children.Add(new TextBlock
        {
            Text = $"我學到的事（視窗／網頁自動化規則：{learnedRules.Count}）",
            FontWeight = FontWeights.SemiBold,
            FontSize = 13.5,
            Margin = new Thickness(2, 10, 0, 8)
        });

        if (learnedRules.Count == 0)
        {
            RunsPanel.Children.Add(new TextBlock
            {
                Text = "尚未學到任何視窗／網頁自動化規則。",
                Opacity = 0.65,
                Margin = new Thickness(4, 0, 4, 10)
            });
        }
        else
        {
            foreach (var r in learnedRules.Take(15))
            {
                var rcard = new Border
                {
                    Background = cardBg,
                    BorderBrush = cardBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0, 0, 0, 6)
                };
                var dock = new DockPanel { LastChildFill = true };
                var forgetBtn = new UiButton
                {
                    Content = "忘掉它",
                    Appearance = UiAppearance.Danger,
                    Padding = new Thickness(10, 3, 10, 3),
                    FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center
                };
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
                RunsPanel.Children.Add(rcard);
            }
        }

        if (learningEvents.Count > 0)
        {
            RunsPanel.Children.Add(new TextBlock
            {
                Text = $"近期自主學習紀錄（最新 {learningEvents.Count} 筆）",
                FontWeight = FontWeights.SemiBold,
                FontSize = 13.5,
                Margin = new Thickness(2, 8, 0, 6)
            });
            foreach (var ev in learningEvents)
            {
                var trans = string.IsNullOrWhiteSpace(ev.BeforeState) && string.IsNullOrWhiteSpace(ev.AfterState)
                    ? ""
                    : $" ({ev.BeforeState} → {ev.AfterState})";
                RunsPanel.Children.Add(new TextBlock
                {
                    Text = $"• {ev.TimestampUtc.ToLocalTime():HH:mm:ss} [{ev.Learner}] {ev.EventType}{trans} — {ev.SubjectKey}" +
                           (string.IsNullOrWhiteSpace(ev.Evidence) ? "" : $"｜{ev.Evidence}"),
                    FontSize = 11.5,
                    Opacity = 0.78,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(4, 0, 4, 3)
                });
            }
        }
    }

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

    private void StopAllBtn_Click(object sender, RoutedEventArgs e) => _controller.StopAllTasks();

    private static SolidColorBrush BrushFrom(string hex)
        => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
}
