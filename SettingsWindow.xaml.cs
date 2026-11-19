using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Services;
using CheckBox = System.Windows.Controls.CheckBox;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;

namespace AiSecretary.Desktop;

public partial class SettingsWindow : Window
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
        InitializeComponent();
        _controller = controller;
        _onPickImageAsync = onPickImageAsync;
        _onResetImageAsync = onResetImageAsync;
        _onConnectGmailAsync = onConnectGmailAsync;

        SandboxPathBox.Text = _controller.SandboxRootPath;

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
            FadeSecondsBox.Text = (await _controller.GetOutputFadeSecondsAsync()).ToString();

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
            ProactiveMinBox.Text = proMin.ToString();
            ProactiveMaxBox.Text = proMax.ToString();

            ApplyTheme(mode);
            RefreshTrustedPathsList();
            await PopulateSkillsAsync();
            _initializing = false;
        };

        _controller.ThemeChanged += mode => Dispatcher.InvokeAsync(async () =>
        {
            ApplyTheme(mode);
            await PopulateSkillsAsync();
        });
        _controller.SkillsChanged += () => Dispatcher.InvokeAsync(PopulateSkillsAsync);
        _controller.TrustedPathsChanged += () => Dispatcher.InvokeAsync(RefreshTrustedPathsList);
    }

    private void ApplyTheme(string mode)
    {
        _isDark = ThemeHelper.IsDarkEffective(mode);
        Win11BackdropHelper.Apply(this, _isDark);

        var winBg = BrushFrom(_isDark ? "#202020" : "#F3F3F3");
        var panelBg = BrushFrom(_isDark ? "#2B2B2B" : "#FFFFFF");
        var cardBg = BrushFrom(_isDark ? "#252525" : "#FAFAFA");
        var fg = BrushFrom(_isDark ? "#F5F5F5" : "#1A1A1A");
        var border = BrushFrom(_isDark ? "#3D3D3D" : "#E0E0E0");
        var btnBg = BrushFrom(_isDark ? "#353535" : "#FFFFFF");
        var inputBg = BrushFrom(_isDark ? "#1E1E1E" : "#FFFFFF");

        Background = winBg;
        Foreground = fg;
        MainTabs.Background = panelBg;
        MainTabs.BorderBrush = border;
        MainTabs.Foreground = fg;

        foreach (var card in new[] { SimpleLlmCard, ComplexLlmCard, ReplyLangCard })
        {
            card.Background = cardBg;
            card.BorderBrush = border;
        }

        foreach (var gb in new[] { ThemeGroupBox, AppearanceGroupBox, SandboxGroupBox, TrustedPathsGroupBox })
        {
            gb.Foreground = fg;
            gb.BorderBrush = border;
        }

        foreach (var tb in new[]
        {
            SandboxLabel,
            ThemeLabel, FadeLabel, FadeHintText, AnimFpsLabel, ProactiveSepText, ProactiveUnitText,
            LlmHintText, SimpleLlmTitle, SimpleEpLbl, SimpleKeyLbl, SimpleModelLbl,
            ComplexLlmTitle, ComplexEpLbl, ComplexKeyLbl, ComplexModelLbl, ReplyLangLbl
        })
        {
            tb.Foreground = fg;
        }
        RunAtStartupCheck.Foreground = fg;
        ProactiveChatCheck.Foreground = fg;

        foreach (var box in new[] { SandboxPathBox, FadeSecondsBox, SimpleEpBox, SimpleModelBox, ComplexEpBox, ComplexModelBox, ReplyLangBox })
        {
            box.Background = inputBg;
            box.Foreground = fg;
            box.CaretBrush = fg;
            box.BorderBrush = border;
        }

        foreach (var pbox in new[] { SimpleKeyBox, ComplexKeyBox })
        {
            pbox.Background = inputBg;
            pbox.Foreground = fg;
            pbox.CaretBrush = fg;
            pbox.BorderBrush = border;
        }

        foreach (var tab in new[] { TabGeneral, TabTrustedPaths, TabLlm, TabSkills })
        {
            tab.Background = panelBg;
            tab.Foreground = fg;
            tab.BorderBrush = border;
        }

        foreach (var btn in new[] { OpenSandboxBtn, AddTrustedPathBtn, EditTrustedPathBtn, DeleteTrustedPathBtn, ChangeImgBtn, ResetImgBtn, ClearImageHistoryBtn, ClearChatHistoryBtn, CloseBtn })
        {
            btn.Background = btnBg;
            btn.Foreground = fg;
            btn.BorderBrush = border;
        }

        TrustedPathsList.Background = inputBg;
        TrustedPathsList.Foreground = fg;
        TrustedPathsList.BorderBrush = border;

        var headerStyle = new Style(typeof(GridViewColumnHeader));
        headerStyle.Setters.Add(new Setter(BackgroundProperty, cardBg));
        headerStyle.Setters.Add(new Setter(ForegroundProperty, fg));
        headerStyle.Setters.Add(new Setter(BorderBrushProperty, border));
        headerStyle.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
        headerStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(8, 5, 8, 5)));
        headerStyle.Setters.Add(new Setter(HorizontalContentAlignmentProperty, System.Windows.HorizontalAlignment.Left));

        var headerBorder = new FrameworkElementFactory(typeof(Border));
        headerBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        headerBorder.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
        headerBorder.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
        headerBorder.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(HorizontalContentAlignmentProperty));
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        headerBorder.AppendChild(presenter);
        headerStyle.Setters.Add(new Setter(TemplateProperty, new ControlTemplate(typeof(GridViewColumnHeader)) { VisualTree = headerBorder }));

        if (TrustedPathsList.View is GridView gv)
            gv.ColumnHeaderContainerStyle = headerStyle;
        TrustedPathColumn.HeaderContainerStyle = headerStyle;

        SaveLlmBtn.Background = BrushFrom("#0078D4");
        SaveLlmBtn.Foreground = System.Windows.Media.Brushes.White;
        SaveLlmBtn.BorderBrush = BrushFrom("#0078D4");
    }

    private async Task PopulateSkillsAsync()
    {
        SkillsPanel.Children.Clear();
        var skills = await _controller.GetAllSkillsAsync();
        var fg = BrushFrom(_isDark ? "#F5F5F5" : "#1A1A1A");
        var btnBg = BrushFrom(_isDark ? "#353535" : "#FFFFFF");
        var border = BrushFrom(_isDark ? "#454545" : "#D4D4D4");

        var builtIn = skills.Where(s => !s.IsCustom).ToList();
        var custom = skills.Where(s => s.IsCustom).ToList();

        UIElement BuildSkillRow(SkillMenuEntry s)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 6) };

            if (s.Name.Equals("manage_mail", StringComparison.OrdinalIgnoreCase))
            {
                var authBtn = new Button
                {
                    Content = _controller.IsGmailAuthorized ? "已連接帳號" : "連接帳號",
                    Padding = new Thickness(10, 3, 10, 3),
                    Margin = new Thickness(6, 0, 0, 0),
                    FontSize = 11.5,
                    Background = btnBg,
                    Foreground = fg,
                    BorderBrush = border
                };
                authBtn.Click += async (_, _) =>
                {
                    await _onConnectGmailAsync();
                    await PopulateSkillsAsync();
                };
                DockPanel.SetDock(authBtn, Dock.Right);
                row.Children.Add(authBtn);
            }

            if (s.IsCustom)
            {
                var delBtn = new Button
                {
                    Content = "刪除",
                    Padding = new Thickness(10, 3, 10, 3),
                    Margin = new Thickness(6, 0, 0, 0),
                    FontSize = 11.5,
                    Background = BrushFrom(_isDark ? "#5C1B1B" : "#FEE2E2"),
                    Foreground = BrushFrom(_isDark ? "#FCA5A5" : "#DC2626"),
                    BorderBrush = BrushFrom(_isDark ? "#7F1D1D" : "#FCA5A5")
                };
                var name = s.Name;
                delBtn.Click += async (_, _) =>
                {
                    if (MessageBox.Show(this, $"確定要刪除自訂技能「{name}」？", "刪除確認", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    {
                        await _controller.DeleteCustomSkillAsync(name);
                    }
                };
                DockPanel.SetDock(delBtn, Dock.Right);
                row.Children.Add(delBtn);
            }

            var cb = new CheckBox
            {
                IsChecked = s.IsEnabled,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = fg,
                Content = new TextBlock
                {
                    Text = $"{s.Name} — {s.Description}",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = fg
                }
            };
            var skillName = s.Name;
            cb.Checked += async (_, _) => await _controller.SetSkillEnabledAsync(skillName, true);
            cb.Unchecked += async (_, _) => await _controller.SetSkillEnabledAsync(skillName, false);

            row.Children.Add(cb);
            return row;
        }

        Expander BuildCategoryExpander(string title, IReadOnlyList<SkillMenuEntry> items, string emptyText)
        {
            var stack = new StackPanel { Margin = new Thickness(14, 6, 4, 6) };
            if (items.Count == 0)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = emptyText,
                    Foreground = fg,
                    Opacity = 0.65,
                    Margin = new Thickness(0, 2, 0, 4)
                });
            }
            else
            {
                foreach (var item in items)
                    stack.Children.Add(BuildSkillRow(item));
            }

            return new Expander
            {
                Header = new TextBlock
                {
                    Text = $"{title}（{items.Count}）",
                    FontWeight = FontWeights.SemiBold,
                    Foreground = fg
                },
                IsExpanded = true,
                Foreground = fg,
                Margin = new Thickness(0, 2, 0, 8),
                Content = stack
            };
        }

        SkillsPanel.Children.Add(BuildCategoryExpander("預設 Skills", builtIn, "（無預設技能）"));
        SkillsPanel.Children.Add(BuildCategoryExpander("自訂 Skills", custom, "（目前尚無自訂技能）"));
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

    private async void ProactiveChatBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => await SaveProactiveChatAsync();

    private async Task SaveProactiveChatAsync()
    {
        if (_initializing) return;
        if (int.TryParse(ProactiveMinBox.Text.Trim(), out var min) &&
            int.TryParse(ProactiveMaxBox.Text.Trim(), out var max))
        {
            await _controller.SaveProactiveChatSettingsAsync(ProactiveChatCheck.IsChecked == true, min, max);
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

    private async void FadeSecondsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing) return;
        if (int.TryParse(FadeSecondsBox.Text.Trim(), out var sec) && sec >= 0)
        {
            await _controller.SetOutputFadeSecondsAsync(sec);
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

    private void TrustedPathsList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var w = TrustedPathsList.ActualWidth - 6;
        if (w > 120)
            TrustedPathColumn.Width = w;
    }

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
        var winBg = BrushFrom(_isDark ? "#202020" : "#F3F3F3");
        var inputBg = BrushFrom(_isDark ? "#1E1E1E" : "#FFFFFF");
        var fg = BrushFrom(_isDark ? "#F5F5F5" : "#1A1A1A");
        var border = BrushFrom(_isDark ? "#3D3D3D" : "#E0E0E0");
        var btnBg = BrushFrom(_isDark ? "#353535" : "#FFFFFF");

        var dlg = new Window
        {
            Title = title,
            Owner = this,
            Width = 460,
            Height = 165,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = winBg,
            Foreground = fg,
            FontFamily = FontFamily,
            FontSize = FontSize
        };
        dlg.Loaded += (_, _) => Win11BackdropHelper.Apply(dlg, _isDark);

        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var lbl = new TextBlock { Text = "資料夾路徑（包含其所有子資料夾）：", Foreground = fg, Margin = new Thickness(0, 0, 0, 6) };
        Grid.SetRow(lbl, 0);
        root.Children.Add(lbl);

        var row = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tb = new System.Windows.Controls.TextBox
        {
            Text = initialPath,
            Background = inputBg,
            Foreground = fg,
            CaretBrush = fg,
            BorderBrush = border,
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, 0, 8, 0)
        };
        var browseBtn = new Button { Content = "瀏覽...", Padding = new Thickness(12, 5, 12, 5), Background = btnBg, Foreground = fg, BorderBrush = border };
        browseBtn.Click += (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = "選擇信任資料夾" };
            if (!string.IsNullOrWhiteSpace(tb.Text) && System.IO.Directory.Exists(tb.Text))
                picker.InitialDirectory = tb.Text;
            if (picker.ShowDialog(dlg) == true)
                tb.Text = picker.FolderName;
        };
        Grid.SetColumn(tb, 0);
        Grid.SetColumn(browseBtn, 1);
        row.Children.Add(tb);
        row.Children.Add(browseBtn);
        Grid.SetRow(row, 1);
        root.Children.Add(row);

        var btns = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        var okBtn = new Button { Content = "確定", Width = 76, Margin = new Thickness(0, 0, 8, 0), IsDefault = true, Background = BrushFrom("#0078D4"), Foreground = System.Windows.Media.Brushes.White, BorderBrush = BrushFrom("#0078D4") };
        var cancelBtn = new Button { Content = "取消", Width = 76, IsCancel = true, Background = btnBg, Foreground = fg, BorderBrush = border };
        string? result = null;
        okBtn.Click += (_, _) =>
        {
            result = tb.Text.Trim();
            dlg.DialogResult = true;
            dlg.Close();
        };
        btns.Children.Add(okBtn);
        btns.Children.Add(cancelBtn);
        Grid.SetRow(btns, 2);
        root.Children.Add(btns);

        dlg.Content = root;
        tb.Focus();
        tb.SelectAll();
        return dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(result) ? result : null;
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

    private static SolidColorBrush BrushFrom(string hex)
        => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
}
