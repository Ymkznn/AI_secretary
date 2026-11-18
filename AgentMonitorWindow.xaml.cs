using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Services;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AiSecretary.Desktop;

public partial class AgentMonitorWindow : Window
{
    private readonly SecretaryController _controller;
    private bool _isDark;

    public AgentMonitorWindow(SecretaryController controller)
    {
        InitializeComponent();
        _controller = controller;

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
        Win11BackdropHelper.Apply(this, _isDark);
        Background = BrushFrom(_isDark ? "#202020" : "#F3F3F3");
        Foreground = BrushFrom(_isDark ? "#F1F5F9" : "#0F172A");
        HeaderText.Foreground = Foreground;
        StopAllBtn.Background = BrushFrom(_isDark ? "#7F1D1D" : "#FEE2E2");
        StopAllBtn.Foreground = BrushFrom(_isDark ? "#FCA5A5" : "#B91C1C");
        StopAllBtn.BorderBrush = BrushFrom(_isDark ? "#991B1B" : "#FCA5A5");
    }

    private async Task RefreshRunsAsync()
    {
        RunsPanel.Children.Clear();
        var runs = _controller.GetAgentRuns();
        var schedules = await _controller.GetPendingSchedulesAsync();
        var calendarEvents = await _controller.GetCalendarEventsAsync();
        var pinned = _controller.GetPinnedReminders();
        StopAllBtn.IsEnabled = _controller.HasRunningTasks;

        RunsPanel.Children.Add(new TextBlock
        {
            Text = $"已排程的背景任務／置頂提醒（{schedules.Count + pinned.Count}）",
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Margin = new Thickness(2, 0, 0, 8),
            Foreground = Foreground
        });

        if (schedules.Count == 0 && pinned.Count == 0)
        {
            RunsPanel.Children.Add(new TextBlock
            {
                Text = "目前沒有待執行的排程或置頂提醒。",
                Opacity = 0.7,
                Margin = new Thickness(4, 0, 4, 12),
                Foreground = Foreground
            });
        }
        else
        {
            foreach (var p in pinned)
            {
                var pcard = new Border
                {
                    Background = BrushFrom(_isDark ? "#1E293B" : "#FFFFFF"),
                    BorderBrush = BrushFrom(_isDark ? "#60A5FA" : "#F59E0B"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 0, 0, 8)
                };
                var dock = new DockPanel { LastChildFill = true };
                var dismissBtn = new Button
                {
                    Content = "關閉",
                    Padding = new Thickness(8, 2, 8, 2),
                    FontSize = 11.5,
                    Background = BrushFrom(_isDark ? "#7F1D1D" : "#FEE2E2"),
                    Foreground = BrushFrom(_isDark ? "#FCA5A5" : "#B91C1C"),
                    BorderBrush = BrushFrom(_isDark ? "#991B1B" : "#FCA5A5"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                DockPanel.SetDock(dismissBtn, Dock.Right);
                var pid = p.Id;
                dismissBtn.Click += async (_, _) => await _controller.DismissPinnedReminderAsync(pid);
                dock.Children.Add(dismissBtn);

                var info = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
                var freq = p.ReRemindMinutes > 0 ? $"每 {p.ReRemindMinutes} 分鐘於氣泡提醒" : "僅置頂不重複";
                info.Children.Add(new TextBlock
                {
                    Text = $"📌 {p.Title}（置頂提醒・{freq}）",
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Foreground,
                    TextWrapping = TextWrapping.Wrap
                });
                info.Children.Add(new TextBlock
                {
                    Text = p.Summary,
                    FontSize = 11.5,
                    Opacity = 0.8,
                    Foreground = Foreground,
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
                    Background = BrushFrom(_isDark ? "#1E293B" : "#FFFFFF"),
                    BorderBrush = BrushFrom(_isDark ? "#334155" : "#E2E8F0"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 0, 0, 8)
                };
                var dock = new DockPanel { LastChildFill = true };
                var delBtn = new Button
                {
                    Content = "刪除",
                    Padding = new Thickness(8, 2, 8, 2),
                    FontSize = 11.5,
                    Background = BrushFrom(_isDark ? "#7F1D1D" : "#FEE2E2"),
                    Foreground = BrushFrom(_isDark ? "#FCA5A5" : "#B91C1C"),
                    BorderBrush = BrushFrom(_isDark ? "#991B1B" : "#FCA5A5"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                DockPanel.SetDock(delBtn, Dock.Right);
                var sid = s.ReminderId;
                delBtn.Click += async (_, _) => await _controller.DeleteScheduleAsync(sid);
                dock.Children.Add(delBtn);

                var info = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
                var freq = s.IntervalMinutes > 0 ? $"每 {s.IntervalMinutes} 分鐘" : "單次";
                var wdTag = s.WorkdaysOnly ? "・僅工作日" : "";
                var skillTag = string.IsNullOrWhiteSpace(s.SkillName) ? "提醒" : $"Skill: {s.SkillName}";
                info.Children.Add(new TextBlock
                {
                    Text = $"{s.Title}（{freq}{wdTag}・{skillTag}）",
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Foreground,
                    TextWrapping = TextWrapping.Wrap
                });
                info.Children.Add(new TextBlock
                {
                    Text = $"下次執行：{s.RemindAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}｜{s.Message}",
                    FontSize = 11.5,
                    Opacity = 0.8,
                    Foreground = Foreground,
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
            FontSize = 13,
            Margin = new Thickness(2, 6, 0, 8),
            Foreground = Foreground
        });

        if (calendarEvents.Count == 0)
        {
            RunsPanel.Children.Add(new TextBlock
            {
                Text = "目前沒有已登錄的行事曆行程。",
                Opacity = 0.7,
                Margin = new Thickness(4, 0, 4, 12),
                Foreground = Foreground
            });
        }
        else
        {
            foreach (var ev in calendarEvents)
            {
                var ecard = new Border
                {
                    Background = BrushFrom(_isDark ? "#1E293B" : "#FFFFFF"),
                    BorderBrush = BrushFrom(_isDark ? "#334155" : "#E2E8F0"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 0, 0, 8)
                };
                var dock = new DockPanel { LastChildFill = true };
                var delBtn = new Button
                {
                    Content = "刪除",
                    Padding = new Thickness(8, 2, 8, 2),
                    FontSize = 11.5,
                    Background = BrushFrom(_isDark ? "#7F1D1D" : "#FEE2E2"),
                    Foreground = BrushFrom(_isDark ? "#FCA5A5" : "#B91C1C"),
                    BorderBrush = BrushFrom(_isDark ? "#991B1B" : "#FCA5A5"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                DockPanel.SetDock(delBtn, Dock.Right);
                var eid = ev.EventId;
                delBtn.Click += async (_, _) => await _controller.DeleteCalendarEventAsync(eid);
                dock.Children.Add(delBtn);

                var info = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
                var recTag = string.IsNullOrWhiteSpace(ev.Recurrence) || ev.Recurrence == "none" ? "" : $"・週期: {ev.Recurrence}";
                info.Children.Add(new TextBlock
                {
                    Text = $"[{ev.Category}] {ev.Title}{recTag}",
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Foreground,
                    TextWrapping = TextWrapping.Wrap
                });
                var timeStr = ev.AllDay
                    ? $"{ev.StartAt.ToLocalTime():yyyy-MM-dd} ~ {ev.EndAt.ToLocalTime():yyyy-MM-dd}（全天）"
                    : $"{ev.StartAt.ToLocalTime():yyyy-MM-dd HH:mm} ~ {ev.EndAt.ToLocalTime():yyyy-MM-dd HH:mm}";
                info.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(ev.Notes) ? timeStr : $"{timeStr}｜{ev.Notes}",
                    FontSize = 11.5,
                    Opacity = 0.8,
                    Foreground = Foreground,
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
            FontSize = 13,
            Margin = new Thickness(2, 6, 0, 8),
            Foreground = Foreground
        });

        if (runs.Count == 0)
        {
            RunsPanel.Children.Add(new TextBlock
            {
                Text = "目前沒有執行中或近期的 Agent 任務。",
                Opacity = 0.7,
                Margin = new Thickness(4, 2, 4, 0),
                Foreground = Foreground
            });
            return;
        }

        foreach (var run in runs)
        {
            var isRunning = run.MainState == "Running" || run.PlannerState == "Running" || run.WorkerState == "Running";

            var card = new Border
            {
                Background = BrushFrom(_isDark ? "#1E293B" : "#FFFFFF"),
                BorderBrush = BrushFrom(isRunning ? "#3B82F6" : (_isDark ? "#334155" : "#E2E8F0")),
                BorderThickness = new Thickness(1.2),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 10)
            };

            var stack = new StackPanel();

            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            if (isRunning)
            {
                var killBtn = new Button
                {
                    Content = "強制終止",
                    Padding = new Thickness(10, 3, 10, 3),
                    FontSize = 11.5,
                    Background = BrushFrom(_isDark ? "#7F1D1D" : "#FEE2E2"),
                    Foreground = BrushFrom(_isDark ? "#FCA5A5" : "#B91C1C"),
                    BorderBrush = BrushFrom(_isDark ? "#991B1B" : "#FCA5A5")
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
                Foreground = Foreground,
                VerticalAlignment = VerticalAlignment.Center
            });
            stack.Children.Add(top);

            // Connection Graph: MainAgent ──► Planner ──► Worker
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
                FontSize = 11.5,
                Foreground = BrushFrom(_isDark ? "#94A3B8" : "#475569")
            });

            card.Child = stack;
            RunsPanel.Children.Add(card);
        }
    }

    private Border MakeNode(string title, string state)
    {
        var (bg, border, fg, label) = state switch
        {
            "Running" => (_isDark ? "#1E3A8A" : "#DBEAFE", "#3B82F6", _isDark ? "#93C5FD" : "#1D4ED8", "運作中"),
            "Done" => (_isDark ? "#14532D" : "#DCFCE7", "#22C55E", _isDark ? "#86EFAC" : "#15803D", "完成"),
            "Stopped" => (_isDark ? "#7F1D1D" : "#FEE2E2", "#EF4444", _isDark ? "#FCA5A5" : "#B91C1C", "已終止"),
            _ => (_isDark ? "#0F172A" : "#F1F5F9", _isDark ? "#475569" : "#CBD5E1", _isDark ? "#94A3B8" : "#64748B", "待命")
        };

        return new Border
        {
            Background = BrushFrom(bg),
            BorderBrush = BrushFrom(border),
            BorderThickness = new Thickness(1.2),
            CornerRadius = new CornerRadius(8),
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
        Foreground = BrushFrom(active ? "#3B82F6" : (_isDark ? "#475569" : "#94A3B8")),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(2, 0, 2, 0)
    };

    private void StopAllBtn_Click(object sender, RoutedEventArgs e) => _controller.StopAllTasks();

    private static SolidColorBrush BrushFrom(string hex)
        => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
}
