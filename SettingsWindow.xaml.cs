using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Services;
using MessageBox = System.Windows.MessageBox;
using UiAppearance = Wpf.Ui.Controls.ControlAppearance;
using UiButton = Wpf.Ui.Controls.Button;
using UiCardControl = Wpf.Ui.Controls.CardControl;
using UiToggleSwitch = Wpf.Ui.Controls.ToggleSwitch;

namespace AiSecretary.Desktop;

/// <summary>Preferences window rendered with native Windows 11 Fluent styling (WPF-UI: Mica, rounded corners, Fluent controls).</summary>
public partial class SettingsWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly SecretaryController _controller;
    private readonly Func<Task> _onPickImageAsync;
    private readonly Func<Task> _onResetImageAsync;
    private readonly Func<Task> _onConnectGmailAsync;
    private bool _initializing = true;
    private bool _isDark;

    public SettingsWindow(
        SecretaryController controller,
        Func<Task> onPickImageAsync,
        Func<Task> onResetImageAsync,
        Func<Task> onConnectGmailAsync)
    {
        FluentThemeHelper.EnsureSystemAccent();
        InitializeComponent();
        _controller = controller;
        _onPickImageAsync = onPickImageAsync;
        _onResetImageAsync = onResetImageAsync;
        _onConnectGmailAsync = onConnectGmailAsync;

        // Apply the system Fluent theme immediately to avoid a dark->light flash before settings load.
        _isDark = ThemeHelper.IsDarkEffective("System");
        FluentThemeHelper.Apply(this, _isDark);
        SourceInitialized += (_, _) => FluentThemeHelper.Apply(this, _isDark);

        SandboxPathBox.Text = _controller.SandboxRootPath;

        FadeSecondsBox.ValueChanged += async (_, _) => await SaveFadeSecondsAsync();
        ProactiveMinBox.ValueChanged += async (_, _) => await SaveProactiveChatAsync();
        ProactiveMaxBox.ValueChanged += async (_, _) => await SaveProactiveChatAsync();

        Loaded += async (_, _) =>
        {
            var mode = await _controller.GetThemeModeAsync();
            foreach (ComboBoxItem item in ThemeCombo.Items)
            {
                if ((item.Tag as string) == mode)
                {
                    ThemeCombo.SelectedItem = item;
                    break;
                }
            }
            FadeSecondsBox.Value = await _controller.GetOutputFadeSecondsAsync();

            var fps = (await _controller.GetAnimationFpsAsync()).ToString();
            foreach (ComboBoxItem item in AnimFpsCombo.Items)
            {
                if ((item.Tag as string) == fps)
                {
                    AnimFpsCombo.SelectedItem = item;
                    break;
                }
            }

            var llm = await _controller.GetLlmProfilesAsync();
            SimpleEpBox.Text = llm.SimpleEp;
            SimpleKeyBox.Password = llm.SimpleKey;
            SimpleModelBox.Text = llm.SimpleModel;
            ComplexEpBox.Text = llm.ComplexEp;
            ComplexKeyBox.Password = llm.ComplexKey;
            ComplexModelBox.Text = llm.ComplexModel;
            ReplyLangBox.Text = _controller.GetReplyLanguage();
            RunAtStartupCheck.IsChecked = IsRunAtStartupEnabled();
            var (proEnabled, proMin, proMax) = _controller.GetProactiveChatSettings();
            ProactiveChatCheck.IsChecked = proEnabled;
            ProactiveMinBox.Value = proMin;
            ProactiveMaxBox.Value = proMax;

            ApplyTheme(mode);
            RefreshTrustedPathsList();
            await PopulateSkillsAsync();
            _initializing = false;
        };

        _controller.ThemeChanged += mode => Dispatcher.InvokeAsync(() => ApplyTheme(mode));
        _controller.SkillsChanged += () => Dispatcher.InvokeAsync(PopulateSkillsAsync);
        _controller.TrustedPathsChanged += () => Dispatcher.InvokeAsync(RefreshTrustedPathsList);
    }

    private void ApplyTheme(string mode)
    {
        _isDark = ThemeHelper.IsDarkEffective(mode);
        FluentThemeHelper.Apply(this, _isDark);
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageGeneral is null) return;
        if (NavList.SelectedItem is not ListBoxItem item || item.Tag is not string tag) return;

        PageGeneral.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        PageTrustedPaths.Visibility = tag == "paths" ? Visibility.Visible : Visibility.Collapsed;
        PageLlm.Visibility = tag == "llm" ? Visibility.Visible : Visibility.Collapsed;
        PageSkills.Visibility = tag == "skills" ? Visibility.Visible : Visibility.Collapsed;

        PageHeaderTitle.Text = tag switch
        {
            "paths" => "信任路徑",
            "llm" => "LLM",
            "skills" => "Skills",
            _ => "一般"
        };
    }

    private async Task PopulateSkillsAsync()
    {
        SkillsPanel.Children.Clear();
        var skills = await _controller.GetAllSkillsAsync();

        var builtIn = skills.Where(s => !s.IsCustom).ToList();
        var custom = skills.Where(s => s.IsCustom).ToList();

        UIElement BuildSkillCard(SkillMenuEntry s)
        {
            var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            if (string.Equals(s.Capability, "mail", StringComparison.OrdinalIgnoreCase))
            {
                var authBtn = new UiButton
                {
                    Content = _controller.IsGmailAuthorized ? "已連接帳號" : "連接帳號",
                    Appearance = _controller.IsGmailAuthorized ? UiAppearance.Secondary : UiAppearance.Primary,
                    Margin = new Thickness(0, 0, 10, 0)
                };
                authBtn.Click += async (_, _) =>
                {
                    await _onConnectGmailAsync();
                    await PopulateSkillsAsync();
                };
                actions.Children.Add(authBtn);
            }

            if (s.IsCustom)
            {
                var delBtn = new UiButton
                {
                    Content = "刪除",
                    Appearance = UiAppearance.Danger,
                    Margin = new Thickness(0, 0, 10, 0)
                };
                var name = s.Name;
                delBtn.Click += async (_, _) =>
                {
                    if (MessageBox.Show(this, $"確定要刪除自訂技能「{name}」？", "刪除確認", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    {
                        await _controller.DeleteCustomSkillAsync(name);
                    }
                };
                actions.Children.Add(delBtn);
            }

            var toggle = new UiToggleSwitch { IsChecked = s.IsEnabled, VerticalAlignment = VerticalAlignment.Center };
            var skillName = s.Name;
            toggle.Checked += async (_, _) => await _controller.SetSkillEnabledAsync(skillName, true);
            toggle.Unchecked += async (_, _) => await _controller.SetSkillEnabledAsync(skillName, false);
            actions.Children.Add(toggle);

            var header = new StackPanel();
            header.Children.Add(new TextBlock { Text = s.Name, FontWeight = FontWeights.SemiBold });
            if (!string.IsNullOrWhiteSpace(s.Description))
            {
                header.Children.Add(new TextBlock
                {
                    Text = s.Description,
                    FontSize = 12,
                    Opacity = 0.65,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            return new UiCardControl
            {
                Header = header,
                Content = actions,
                Margin = new Thickness(0, 0, 0, 6)
            };
        }

        void AddCategory(string title, IReadOnlyList<SkillMenuEntry> items, string emptyText)
        {
            SkillsPanel.Children.Add(new TextBlock
            {
                Text = $"{title}（{items.Count}）",
                Style = (Style)FindResource("SectionTitle")
            });

            if (items.Count == 0)
            {
                SkillsPanel.Children.Add(new TextBlock
                {
                    Text = emptyText,
                    Opacity = 0.65,
                    Margin = new Thickness(4, 0, 0, 8)
                });
                return;
            }

            foreach (var item in items)
                SkillsPanel.Children.Add(BuildSkillCard(item));
        }

        AddCategory("預設 Skills", builtIn, "（無預設技能）");
        AddCategory("自訂 Skills", custom, "（目前尚無自訂技能）");
    }

    private const string StartupRegKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "AiSecretary.Desktop";

    private static bool IsRunAtStartupEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(StartupRegKey, writable: false);
            return key?.GetValue(StartupValueName) is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch
        {
            return false;
        }
    }

    private void RunAtStartupCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        bool enable = RunAtStartupCheck.IsChecked == true;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(StartupRegKey, writable: true)
                ?? Microsoft.Win32.Registry.CurrentUser.CreateSubKey(StartupRegKey);
            if (key is null) return;

            if (enable)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe) || Path.GetFileName(exe).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
                {
                    var candidate = Path.Combine(AppContext.BaseDirectory, "AiSecretary.Desktop.exe");
                    if (File.Exists(candidate)) exe = candidate;
                }
                if (!string.IsNullOrWhiteSpace(exe))
                    key.SetValue(StartupValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(StartupValueName, throwOnMissingValue: false);
            }
        }
        catch { }
    }

    private async void ProactiveChatSetting_Changed(object sender, RoutedEventArgs e)
        => await SaveProactiveChatAsync();

    private async Task SaveProactiveChatAsync()
    {
        if (_initializing) return;
        if (ProactiveMinBox.Value is double min && ProactiveMaxBox.Value is double max)
        {
            await _controller.SaveProactiveChatSettingsAsync(ProactiveChatCheck.IsChecked == true, (int)min, (int)max);
        }
    }

    private async Task SaveFadeSecondsAsync()
    {
        if (_initializing) return;
        if (FadeSecondsBox.Value is double sec && sec >= 0)
        {
            await _controller.SetOutputFadeSecondsAsync((int)sec);
        }
    }

    private async void SaveLlmBtn_Click(object sender, RoutedEventArgs e)
    {
        await _controller.SaveLlmProfilesAsync(
            SimpleEpBox.Text, SimpleKeyBox.Password, SimpleModelBox.Text,
            ComplexEpBox.Text, ComplexKeyBox.Password, ComplexModelBox.Text);
        await _controller.SaveReplyLanguageAsync(ReplyLangBox.Text);
        LlmSavedText.Visibility = Visibility.Visible;
        await Task.Delay(2000);
        LlmSavedText.Visibility = Visibility.Collapsed;
    }

    private async void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (ThemeCombo.SelectedItem is ComboBoxItem item && item.Tag is string mode)
        {
            await _controller.SetThemeModeAsync(mode);
        }
    }

    private async void AnimFpsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (AnimFpsCombo.SelectedItem is ComboBoxItem item && item.Tag is string raw && int.TryParse(raw, out var fps))
        {
            await _controller.SetAnimationFpsAsync(fps);
        }
    }

    private void OpenSandboxBtn_Click(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo { FileName = _controller.SandboxRootPath, UseShellExecute = true });

    private void RefreshTrustedPathsList()
    {
        var selected = TrustedPathsList.SelectedItem as string;
        TrustedPathsList.Items.Clear();
        foreach (var p in _controller.GetTrustedPaths())
            TrustedPathsList.Items.Add(p);

        if (selected is not null && TrustedPathsList.Items.Contains(selected))
            TrustedPathsList.SelectedItem = selected;

        UpdateTrustedPathButtons();
    }

    private void UpdateTrustedPathButtons()
    {
        bool hasSel = TrustedPathsList.SelectedItem is string;
        EditTrustedPathBtn.IsEnabled = hasSel;
        DeleteTrustedPathBtn.IsEnabled = hasSel;
    }

    private void TrustedPathsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateTrustedPathButtons();

    private async void TrustedPathsList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (TrustedPathsList.SelectedItem is string current)
            await EditTrustedPathAsync(current);
    }

    private async void AddTrustedPathBtn_Click(object sender, RoutedEventArgs e)
    {
        var newPath = ShowTrustedPathEditorDialog("新增信任路徑", "");
        if (string.IsNullOrWhiteSpace(newPath)) return;

        var list = _controller.GetTrustedPaths().ToList();
        list.Add(newPath);
        await _controller.SaveTrustedPathsAsync(list);
    }

    private async void EditTrustedPathBtn_Click(object sender, RoutedEventArgs e)
    {
        if (TrustedPathsList.SelectedItem is string current)
            await EditTrustedPathAsync(current);
    }

    private async Task EditTrustedPathAsync(string current)
    {
        var edited = ShowTrustedPathEditorDialog("編輯信任路徑", current);
        if (string.IsNullOrWhiteSpace(edited)) return;

        var list = _controller.GetTrustedPaths().ToList();
        var idx = list.FindIndex(p => string.Equals(p, current, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
            list[idx] = edited;
        else
            list.Add(edited);

        await _controller.SaveTrustedPathsAsync(list);
    }

    private async void DeleteTrustedPathBtn_Click(object sender, RoutedEventArgs e)
    {
        if (TrustedPathsList.SelectedItem is not string current) return;
        var list = _controller.GetTrustedPaths()
            .Where(p => !string.Equals(p, current, StringComparison.OrdinalIgnoreCase))
            .ToList();
        await _controller.SaveTrustedPathsAsync(list);
    }

    private string? ShowTrustedPathEditorDialog(string title, string initialPath)
    {
        var dlg = new PathEditorDialog(this, title, initialPath, _isDark);
        return dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.ResultPath) ? dlg.ResultPath : null;
    }

    private async void ChangeImgBtn_Click(object sender, RoutedEventArgs e)
        => await _onPickImageAsync();

    private async void ResetImgBtn_Click(object sender, RoutedEventArgs e)
        => await _onResetImageAsync();

    private async void ClearImageHistoryBtn_Click(object sender, RoutedEventArgs e)
    {
        var count = await _controller.ClearImageAttachmentsInHistoryAsync();
        MessageBox.Show(
            count == 0
                ? "資料庫中目前沒有殘留的附件標籤。"
                : $"已清除 {count} 則對話紀錄中的附件標籤（文字紀錄仍完整保留）。",
            "清除完成",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void ClearChatHistoryBtn_Click(object sender, RoutedEventArgs e)
    {
        var firstConfirm = MessageBox.Show(
            "確定要清空所有對話紀錄嗎？\n（已儲存的知識庫記憶、排程與行事曆都會保留，不會刪除。）",
            "確認清空對話紀錄（1/2）",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (firstConfirm != MessageBoxResult.Yes) return;

        var secondConfirm = MessageBox.Show(
            "再次確認：所有對話紀錄刪除後將無法復原！\n真的確定要立即清空嗎？",
            "最後確認（2/2）",
            MessageBoxButton.YesNo,
            MessageBoxImage.Stop,
            MessageBoxResult.No);
        if (secondConfirm != MessageBoxResult.Yes) return;

        var count = await _controller.ClearChatMessagesAsync();
        MessageBox.Show(
            $"已清空 {count} 則對話紀錄（知識庫、排程與行事曆皆已保留）。",
            "清空完成",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}

