using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Services;
using Clipboard = System.Windows.Clipboard;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace AiSecretary.Desktop;

/// <summary>
/// Clean, readable Win11 Fluent email viewer (dark/light theme synced, formatted paragraphs,
/// clickable shortened links with full URL tooltips, quote styling, copy &amp; open-in-browser).
/// </summary>
public partial class MailDetailWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly MailItem _mail;
    private readonly string _formattedBody;
    private readonly string? _webUrl;
    private bool _isDark;

    public MailDetailWindow(MailItem mail, string themeMode)
    {
        FluentThemeHelper.EnsureSystemAccent();
        InitializeComponent();
        _mail = mail;
        _formattedBody = MailBodyFormatter.FormatForDisplay(mail.Body);
        if (string.IsNullOrWhiteSpace(_formattedBody))
            _formattedBody = MailBodyFormatter.NormalizeReadablePlainText(mail.Snippet);

        _webUrl = BuildWebMailUrl(mail);

        _isDark = ThemeHelper.IsDarkEffective(themeMode);
        FluentThemeHelper.Apply(this, _isDark);
        SourceInitialized += (_, _) => FluentThemeHelper.Apply(this, _isDark);

        Title = string.IsNullOrWhiteSpace(mail.Subject) ? "郵件詳情" : mail.Subject;
        SubjectBox.Text = string.IsNullOrWhiteSpace(mail.Subject) ? "(無主旨)" : mail.Subject.Trim();

        var senderDisplay = string.IsNullOrWhiteSpace(mail.SenderName) ||
                            string.Equals(mail.SenderName, mail.SenderEmail, StringComparison.OrdinalIgnoreCase)
            ? mail.SenderEmail
            : $"{mail.SenderName} <{mail.SenderEmail}>";
        SenderText.Text = $"寄件者：{senderDisplay}";

        var impLabel = mail.Importance == MailImportance.High ? " ｜ 🔴 重要" : "";
        var srcLabel = mail.Id.StartsWith("gmail:", StringComparison.OrdinalIgnoreCase) ? "Gmail" : "郵件";
        MetaText.Text = $"{srcLabel} ｜ {mail.ReceivedAt.ToLocalTime():yyyy/MM/dd (ddd) HH:mm}{impLabel}";

        if (!string.IsNullOrWhiteSpace(_webUrl))
            OpenBrowserBtn.Visibility = Visibility.Visible;

        ApplyVisualTheme();
        RenderBodyDocument();
    }

    private static string? BuildWebMailUrl(MailItem mail)
    {
        if (mail.Id.StartsWith("gmail:", StringComparison.OrdinalIgnoreCase))
        {
            var rawId = mail.Id["gmail:".Length..].Trim();
            if (!string.IsNullOrWhiteSpace(rawId))
                return $"https://mail.google.com/mail/u/0/#all/{Uri.EscapeDataString(rawId)}";
        }
        return null;
    }

    private void ApplyVisualTheme()
    {
        var cardBg = BrushFrom(_isDark ? "#262626" : "#FFFFFF");
        var cardBorder = BrushFrom(_isDark ? "#3A3A3A" : "#E2E8F0");
        var fg = BrushFrom(_isDark ? "#F1F5F9" : "#0F172A");

        HeaderCard.Background = cardBg;
        HeaderCard.BorderBrush = cardBorder;
        BodyCard.Background = cardBg;
        BodyCard.BorderBrush = cardBorder;

        SubjectBox.Foreground = fg;
        SenderText.Foreground = fg;
        MetaText.Foreground = fg;
    }

    private void RenderBodyDocument()
    {
        var fg = BrushFrom(_isDark ? "#E5E7EB" : "#1E293B");
        var mutedFg = BrushFrom(_isDark ? "#9CA3AF" : "#64748B");
        var linkFg = BrushFrom(_isDark ? "#60A5FA" : "#2563EB");
        var quoteBorder = BrushFrom(_isDark ? "#475569" : "#CBD5E1");
        var dividerFg = BrushFrom(_isDark ? "#4B5563" : "#CBD5E1");

        var doc = new FlowDocument
        {
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Microsoft JhengHei UI, Segoe UI"),
            FontSize = 14,
            Foreground = fg,
            PagePadding = new Thickness(18, 16, 18, 20),
            TextAlignment = TextAlignment.Left
        };

        if (string.IsNullOrWhiteSpace(_formattedBody))
        {
            doc.Blocks.Add(new Paragraph(new Run("（此郵件沒有文字內容）"))
            {
                Foreground = mutedFg,
                FontStyle = FontStyles.Italic
            });
            BodyViewer.Document = doc;
            return;
        }

        // Split into paragraphs separated by blank lines so spacing breathes naturally.
        var paragraphs = Regex.Split(_formattedBody, @"\n{2,}");
        foreach (var rawPara in paragraphs)
        {
            var blockText = rawPara.Trim();
            if (string.IsNullOrEmpty(blockText)) continue;

            var lines = blockText.Split('\n');
            bool isDividerOnly = lines.Length == 1 && Regex.IsMatch(blockText, @"^[─\-_=]{6,}$");
            if (isDividerOnly)
            {
                doc.Blocks.Add(new Paragraph(new Run("────────────────────────────────────"))
                {
                    Foreground = dividerFg,
                    Margin = new Thickness(0, 6, 0, 12),
                    FontSize = 11
                });
                continue;
            }

            bool isQuoted = lines.All(l => l.TrimStart().StartsWith('>'));
            var para = new Paragraph
            {
                LineHeight = 24,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Margin = new Thickness(0, 0, 0, 12)
            };

            if (isQuoted)
            {
                para.Foreground = mutedFg;
                para.Padding = new Thickness(10, 2, 0, 2);
                para.BorderBrush = quoteBorder;
                para.BorderThickness = new Thickness(3, 0, 0, 0);
            }

            for (int i = 0; i < lines.Length; i++)
            {
                var line = isQuoted ? lines[i].TrimStart().TrimStart('>').TrimStart() : lines[i];
                AppendInlinesWithLinks(para.Inlines, line, linkFg);
                if (i < lines.Length - 1)
                    para.Inlines.Add(new LineBreak());
            }

            doc.Blocks.Add(para);
        }

        BodyViewer.Document = doc;
    }

    private static void AppendInlinesWithLinks(InlineCollection inlines, string text, System.Windows.Media.Brush linkBrush)
    {
        var matches = MailBodyFormatter.FindUrls(text);
        if (matches.Count == 0)
        {
            inlines.Add(new Run(text));
            return;
        }

        int cursor = 0;
        foreach (Match m in matches)
        {
            if (m.Index > cursor)
                inlines.Add(new Run(text[cursor..m.Index]));

            var rawUrl = m.Value.TrimEnd('.', ',', ';', ':', ')', ']', '>', '"', '\'');
            var trailing = m.Value[rawUrl.Length..];

            if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                var display = MailBodyFormatter.FormatDisplayUrl(rawUrl, 58);
                var link = new Hyperlink(new Run($"🔗 {display}"))
                {
                    NavigateUri = uri,
                    Foreground = linkBrush,
                    ToolTip = rawUrl
                };
                link.Click += (_, _) => OpenUrlInDefaultBrowser(rawUrl);
                inlines.Add(link);
            }
            else
            {
                inlines.Add(new Run(rawUrl));
            }

            if (!string.IsNullOrEmpty(trailing))
                inlines.Add(new Run(trailing));

            cursor = m.Index + m.Length;
        }

        if (cursor < text.Length)
            inlines.Add(new Run(text[cursor..]));
    }

    private static void OpenUrlInDefaultBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private async void CopyBodyBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_formattedBody);
            CopyBodyBtn.Content = "已複製 ✓";
            await Task.Delay(1400);
            CopyBodyBtn.Content = "複製內文";
        }
        catch { }
    }

    private void OpenBrowserBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_webUrl))
            OpenUrlInDefaultBrowser(_webUrl);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
    }

    private static SolidColorBrush BrushFrom(string hex)
        => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
}
