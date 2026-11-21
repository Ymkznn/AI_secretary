using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Services;
using Brushes = System.Windows.Media.Brushes;
using RichTextBox = System.Windows.Controls.RichTextBox;
using TextBox = System.Windows.Controls.TextBox;
using UiAppearance = Wpf.Ui.Controls.ControlAppearance;

namespace AiSecretary.Desktop;

public partial class ChatHistoryWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly SecretaryController _controller;
    private bool _isDark;
    private bool _scrollOnNextRefresh = true;

    public ChatHistoryWindow(SecretaryController controller)
    {
        FluentThemeHelper.EnsureSystemAccent();
        InitializeComponent();
        _controller = controller;

        _isDark = ThemeHelper.IsDarkEffective("System");
        FluentThemeHelper.Apply(this, _isDark);
        SourceInitialized += (_, _) => FluentThemeHelper.Apply(this, _isDark);

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
        FluentThemeHelper.Apply(this, _isDark);
        UpdateSendStopButton();
    }

    private void UpdateSendStopButton()
    {
        if (_controller.IsMainTurnActive)
        {
            SendStopBtn.Content = "停止";
            SendStopBtn.Appearance = UiAppearance.Danger;
            HeaderSubtitle.Text = "AI 秘書處理中…";
        }
        else
        {
            SendStopBtn.Content = "傳送";
            SendStopBtn.Appearance = UiAppearance.Primary;
            HeaderSubtitle.Text = _controller.HasRunningTasks
                ? "背景任務執行中（可繼續對話）"
                : "即時同步桌面寵物對話";
        }
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
                Opacity = 0.65,
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
                FontSize = 11,
                Opacity = 0.55,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 6, 2)
            });

            var bubble = new Border
            {
                Background = Brush(_isDark ? "#0F6CBD" : "#0078D4"),
                CornerRadius = new CornerRadius(12, 12, 4, 12),
                Padding = new Thickness(12, 8, 12, 8),
                MaxWidth = 360,
                Child = new TextBox
                {
                    Text = msg.Content,
                    IsReadOnly = true,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Foreground = Brushes.White,
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
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Opacity = 0.7,
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
                : (_isDark ? "#2B2B2B" : "#FFFFFF")),
            BorderBrush = Brush(isNotification
                ? (_isDark ? "#3B82F6" : "#BFDBFE")
                : (_isDark ? "#3D3D3D" : "#E0E0E0")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12, 12, 12, 4),
            Padding = new Thickness(12, 8, 12, 8),
            MaxWidth = 380,
            Child = contentElement
        };

        bubbleRow.Children.Add(leftBubble);
        bubbleRow.Children.Add(new TextBlock
        {
            Text = timeText,
            FontSize = 11,
            Opacity = 0.55,
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
        var text = ChatInputBox.Text.Trim();
        if (text.Equals("/kill magent", StringComparison.OrdinalIgnoreCase))
        {
            ChatInputBox.Text = string.Empty;
            await _controller.KillMainAgentAsync();
            UpdateSendStopButton();
            return;
        }

        if (_controller.IsMainTurnActive)
        {
            _controller.StopAllTasks();
            UpdateSendStopButton();
            return;
        }

        if (string.IsNullOrWhiteSpace(text)) return;

        ChatInputBox.Text = string.Empty;
        _scrollOnNextRefresh = true;
        await _controller.SendUserPromptAsync(text);
    }

    private void ScrollBottomBtn_Click(object sender, RoutedEventArgs e)
        => ChatScroll.ScrollToEnd();

    private static SolidColorBrush Brush(string hex) =>
        (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
}
