using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Services;
using Brushes = System.Windows.Media.Brushes;
using RichTextBox = System.Windows.Controls.RichTextBox;
using TextBox = System.Windows.Controls.TextBox;

namespace AiSecretary.Desktop;

public partial class ChatHistoryWindow : Window
{
    private readonly SecretaryController _controller;
    private bool _isDark;
    private bool _scrollOnNextRefresh = true;

    public ChatHistoryWindow(SecretaryController controller)
    {
        InitializeComponent();
        _controller = controller;

        _controller.StateChanged += OnStateChanged;
        _controller.ThemeChanged += OnThemeChanged;

        Loaded += async (_, _) =>
        {
            ApplyTheme(await _controller.GetThemeModeAsync());
            await RefreshMessagesAsync(scrollToBottom: true);
            ChatInputBox.Focus();
        };

        Closed += (_, _) =>
        {
            _controller.StateChanged -= OnStateChanged;
            _controller.ThemeChanged -= OnThemeChanged;
        };
    }

    private void OnStateChanged() =>
        Dispatcher.BeginInvoke(async () => await RefreshMessagesAsync(scrollToBottom: _scrollOnNextRefresh));

    private void OnThemeChanged(string mode) =>
        Dispatcher.BeginInvoke(async () =>
        {
            ApplyTheme(mode);
            await RefreshMessagesAsync(scrollToBottom: false);
        });

    private void ApplyTheme(string mode)
    {
        _isDark = ThemeHelper.IsDarkEffective(mode);
        Win11BackdropHelper.Apply(this, _isDark);

        Background = Brush(_isDark ? "#0F172A" : "#E8EEF5");
        HeaderBorder.Background = Brush(_isDark ? "#1E293B" : "#FFFFFF");
        HeaderBorder.BorderBrush = Brush(_isDark ? "#334155" : "#CBD5E1");
        HeaderTitle.Foreground = Brush(_isDark ? "#F8FAFC" : "#0F172A");
        HeaderSubtitle.Foreground = Brush(_isDark ? "#94A3B8" : "#64748B");

        InputBorder.Background = Brush(_isDark ? "#1E293B" : "#FFFFFF");
        InputBorder.BorderBrush = Brush(_isDark ? "#334155" : "#CBD5E1");
        InputBoxFrame.Background = Brush(_isDark ? "#0F172A" : "#F8FAFC");
        InputBoxFrame.BorderBrush = Brush(_isDark ? "#334155" : "#CBD5E1");
        ChatInputBox.Foreground = Brush(_isDark ? "#F8FAFC" : "#0F172A");
        ChatInputBox.CaretBrush = Brush(_isDark ? "#F8FAFC" : "#0F172A");

        ScrollBottomBtn.Background = Brush(_isDark ? "#334155" : "#E2E8F0");
        ScrollBottomBtn.Foreground = Brush(_isDark ? "#F8FAFC" : "#0F172A");
        ScrollBottomBtn.BorderThickness = new Thickness(0);

        UpdateSendStopButton();
    }

    private void UpdateSendStopButton()
    {
        if (_controller.HasRunningTasks)
        {
            SendStopBtn.Content = "停止";
            SendStopBtn.Background = Brush("#DC2626");
            SendStopBtn.Foreground = Brushes.White;
            HeaderSubtitle.Text = "AI 秘書處理中…";
        }
        else
        {
            SendStopBtn.Content = "傳送";
            SendStopBtn.Background = Brush("#06C755");
            SendStopBtn.Foreground = Brushes.White;
            HeaderSubtitle.Text = "即時同步桌面寵物對話";
        }
        SendStopBtn.BorderThickness = new Thickness(0);
    }

    private async Task RefreshMessagesAsync(bool scrollToBottom)
    {
        UpdateSendStopButton();

        var nearBottom = ChatScroll.ScrollableHeight <= 0 ||
                         (ChatScroll.VerticalOffset >= ChatScroll.ScrollableHeight - 40);

        var messages = await _controller.GetMessagesAsync();
        MessagesPanel.Children.Clear();

        if (messages.Count == 0)
        {
            MessagesPanel.Children.Add(new TextBlock
            {
                Text = "尚無對話紀錄，在下方輸入訊息即可開始對話。",
                Foreground = Brush(_isDark ? "#94A3B8" : "#64748B"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Margin = new Thickness(0, 32, 0, 0)
            });
            return;
        }

        foreach (var msg in messages)
        {
            MessagesPanel.Children.Add(BuildMessageRow(msg));
        }

        if (scrollToBottom || nearBottom)
        {
            _scrollOnNextRefresh = false;
            _ = Dispatcher.BeginInvoke(() => ChatScroll.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private UIElement BuildMessageRow(ChatMessageRecord msg)
    {
        bool isUser = string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase);
        bool isNotification = string.Equals(msg.Role, "notification", StringComparison.OrdinalIgnoreCase);
        var timeText = msg.CreatedAt.ToLocalTime().ToString("HH:mm");

        var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };

        if (isUser)
        {
            var stack = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };

            stack.Children.Add(new TextBlock
            {
                Text = timeText,
                FontSize = 10.5,
                Foreground = Brush(_isDark ? "#64748B" : "#64748B"),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 6, 2)
            });

            var bubble = new Border
            {
                Background = Brush(_isDark ? "#16A34A" : "#8DE055"),
                CornerRadius = new CornerRadius(14, 14, 4, 14),
                Padding = new Thickness(12, 8, 12, 8),
                MaxWidth = 340,
                Child = new TextBox
                {
                    Text = msg.Content,
                    IsReadOnly = true,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Foreground = Brush(_isDark ? "#F8FAFC" : "#0F172A"),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                    Padding = new Thickness(0)
                }
            };
            stack.Children.Add(bubble);
            row.Children.Add(stack);
            return row;
        }

        // Assistant or Notification (left-aligned)
        var leftContainer = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };

        leftContainer.Children.Add(new TextBlock
        {
            Text = isNotification ? "AI 秘書・主動通知" : "AI 秘書",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(_isDark ? "#94A3B8" : "#475569"),
            Margin = new Thickness(4, 0, 0, 3)
        });

        var bubbleRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };

        var bodyText = !string.IsNullOrWhiteSpace(msg.Content)
            ? msg.Content
            : (msg.StatusText ?? "處理中…");

        var contentElement = new RichTextBox
        {
            Document = MarkdownRenderer.Render(bodyText, _isDark),
            IsReadOnly = true,
            IsDocumentEnabled = true,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            FontSize = 13
        };

        var leftBubble = new Border
        {
            Background = Brush(isNotification
                ? (_isDark ? "#1E3A5F" : "#EFF6FF")
                : (_isDark ? "#1E293B" : "#FFFFFF")),
            BorderBrush = Brush(isNotification
                ? (_isDark ? "#3B82F6" : "#BFDBFE")
                : (_isDark ? "#334155" : "#CBD5E1")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14, 14, 14, 4),
            Padding = new Thickness(12, 8, 12, 8),
            MaxWidth = 360,
            Child = contentElement
        };

        bubbleRow.Children.Add(leftBubble);
        bubbleRow.Children.Add(new TextBlock
        {
            Text = timeText,
            FontSize = 10.5,
            Foreground = Brush(_isDark ? "#64748B" : "#64748B"),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(6, 0, 0, 2)
        });

        leftContainer.Children.Add(bubbleRow);
        row.Children.Add(leftContainer);
        return row;
    }

    private async void SendStopBtn_Click(object sender, RoutedEventArgs e) => await SubmitOrStopAsync();

    private async void ChatInputBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            await SubmitOrStopAsync();
        }
    }

    private async Task SubmitOrStopAsync()
    {
        if (_controller.HasRunningTasks)
        {
            _controller.StopAllTasks();
            UpdateSendStopButton();
            return;
        }

        var text = ChatInputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        ChatInputBox.Text = string.Empty;
        _scrollOnNextRefresh = true;
        await _controller.SendUserPromptAsync(text);
    }

    private void ScrollBottomBtn_Click(object sender, RoutedEventArgs e)
    {
        ChatScroll.ScrollToEnd();
    }

    private static SolidColorBrush Brush(string hex) =>
        (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
}
