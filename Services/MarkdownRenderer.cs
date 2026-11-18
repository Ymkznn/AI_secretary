using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;

namespace AiSecretary.Desktop.Services;

/// <summary>
/// Lightweight Markdown-to-FlowDocument renderer for the desktop pet output bubble.
/// Supports headings, bold, italic, inline code, fenced code blocks, bullet/numbered lists, and links.
/// </summary>
public static class MarkdownRenderer
{
    private static readonly Regex InlinePattern = new(
        @"(\*\*[^*]+\*\*|`[^`]+`|\[[^\]]+\]\([^)]+\)|https?://[^\s\)\]>]+|\*[^*\n]+\*)",
        RegexOptions.Compiled);

    public static FlowDocument Render(string markdown, bool isDark)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontSize = 13.5,
            LineHeight = 20,
            Foreground = BrushFrom(isDark ? "#F1F5F9" : "#0F172A")
        };

        if (string.IsNullOrWhiteSpace(markdown))
            return doc;

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var inCodeBlock = false;
        var codeBuffer = new List<string>();
        var tableBuffer = new List<string>();
        List? currentList = null;

        void FlushList()
        {
            if (currentList is not null)
            {
                doc.Blocks.Add(currentList);
                currentList = null;
            }
        }

        void FlushTable()
        {
            if (tableBuffer.Count > 0)
            {
                doc.Blocks.Add(BuildTable(tableBuffer, isDark));
                tableBuffer.Clear();
            }
        }

        foreach (var rawLine in lines)
        {
            var cleanLine = (!inCodeBlock && rawLine.EndsWith('\\'))
                ? rawLine[..^1]
                : rawLine;
            var trimmed = cleanLine.TrimStart();

            if (trimmed.StartsWith("```"))
            {
                FlushList();
                FlushTable();
                if (!inCodeBlock)
                {
                    inCodeBlock = true;
                    codeBuffer.Clear();
                }
                else
                {
                    inCodeBlock = false;
                    doc.Blocks.Add(BuildCodeBlock(string.Join("\n", codeBuffer), isDark));
                    codeBuffer.Clear();
                }
                continue;
            }

            if (inCodeBlock)
            {
                codeBuffer.Add(rawLine);
                continue;
            }

            if (trimmed.StartsWith('|') && trimmed.IndexOf('|', 1) > 0)
            {
                FlushList();
                tableBuffer.Add(trimmed);
                continue;
            }
            else
            {
                FlushTable();
            }

            if (string.IsNullOrWhiteSpace(rawLine))
            {
                FlushList();
                continue;
            }

            // Headings
            var headingMatch = Regex.Match(trimmed, @"^(#{1,3})\s+(.+)$");
            if (headingMatch.Success)
            {
                FlushList();
                var level = headingMatch.Groups[1].Value.Length;
                var p = new Paragraph
                {
                    Margin = new Thickness(0, 4, 0, 4),
                    FontWeight = FontWeights.SemiBold,
                    FontSize = level switch { 1 => 16, 2 => 15, _ => 14 }
                };
                AppendInlines(p.Inlines, headingMatch.Groups[2].Value, isDark);
                doc.Blocks.Add(p);
                continue;
            }

            // Unordered list
            var ulMatch = Regex.Match(trimmed, @"^[-*•]\s+(.+)$");
            if (ulMatch.Success)
            {
                currentList ??= new List
                {
                    MarkerStyle = TextMarkerStyle.Disc,
                    Margin = new Thickness(0, 2, 0, 4),
                    Padding = new Thickness(16, 0, 0, 0)
                };
                var itemPara = new Paragraph { Margin = new Thickness(0, 1, 0, 2) };
                AppendInlines(itemPara.Inlines, ulMatch.Groups[1].Value, isDark);
                currentList.ListItems.Add(new ListItem(itemPara));
                continue;
            }

            // Ordered list
            var olMatch = Regex.Match(trimmed, @"^\d+[.)]\s+(.+)$");
            if (olMatch.Success)
            {
                if (currentList is null || currentList.MarkerStyle != TextMarkerStyle.Decimal)
                {
                    FlushList();
                    currentList = new List
                    {
                        MarkerStyle = TextMarkerStyle.Decimal,
                        Margin = new Thickness(0, 2, 0, 4),
                        Padding = new Thickness(18, 0, 0, 0)
                    };
                }
                var itemPara = new Paragraph { Margin = new Thickness(0, 1, 0, 2) };
                AppendInlines(itemPara.Inlines, olMatch.Groups[1].Value, isDark);
                currentList.ListItems.Add(new ListItem(itemPara));
                continue;
            }

            FlushList();
            var para = new Paragraph { Margin = new Thickness(0, 2, 0, 6) };
            AppendInlines(para.Inlines, rawLine, isDark);
            doc.Blocks.Add(para);
        }

        if (inCodeBlock && codeBuffer.Count > 0)
            doc.Blocks.Add(BuildCodeBlock(string.Join("\n", codeBuffer), isDark));

        FlushTable();
        FlushList();
        return doc;
    }

    private static Table BuildTable(List<string> rows, bool isDark)
    {
        var parsedRows = new List<string[]>();
        foreach (var r in rows)
        {
            var cells = r.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            // Skip separator row like |---|---|
            if (cells.Length > 0 && cells.All(c => Regex.IsMatch(c, @"^:?-{2,}:?$")))
                continue;
            parsedRows.Add(cells);
        }

        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 4, 0, 8),
            BorderBrush = BrushFrom(isDark ? "#44FFFFFF" : "#330F172A"),
            BorderThickness = new Thickness(0.8)
        };

        if (parsedRows.Count == 0) return table;

        int colCount = parsedRows.Max(r => r.Length);
        for (int i = 0; i < colCount; i++)
            table.Columns.Add(new TableColumn());

        var rowGroup = new TableRowGroup();
        for (int rIdx = 0; rIdx < parsedRows.Count; rIdx++)
        {
            var tr = new TableRow();
            var isHeader = rIdx == 0 && rows.Count > 1;
            if (isHeader)
                tr.Background = BrushFrom(isDark ? "#28FFFFFF" : "#180F172A");

            for (int cIdx = 0; cIdx < colCount; cIdx++)
            {
                var cellText = cIdx < parsedRows[rIdx].Length ? parsedRows[rIdx][cIdx] : "";
                var p = new Paragraph
                {
                    Margin = new Thickness(0),
                    FontSize = 12.5,
                    FontWeight = isHeader ? FontWeights.SemiBold : FontWeights.Normal
                };
                AppendInlines(p.Inlines, cellText, isDark);

                var cell = new TableCell(p)
                {
                    Padding = new Thickness(6, 4, 6, 4),
                    BorderBrush = BrushFrom(isDark ? "#33FFFFFF" : "#220F172A"),
                    BorderThickness = new Thickness(0.5)
                };
                tr.Cells.Add(cell);
            }
            rowGroup.Rows.Add(tr);
        }
        table.RowGroups.Add(rowGroup);
        return table;
    }

    private static Paragraph BuildCodeBlock(string code, bool isDark)
    {
        var p = new Paragraph(new Run(code))
        {
            FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
            FontSize = 12,
            Background = BrushFrom(isDark ? "#550F172A" : "#220F172A"),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 4, 0, 6)
        };
        return p;
    }

    public static void AppendInlines(InlineCollection inlines, string text, bool isDark)
    {
        int pos = 0;
        foreach (Match m in InlinePattern.Matches(text))
        {
            if (m.Index > pos)
                inlines.Add(new Run(text[pos..m.Index]));

            var tok = m.Value;
            if (tok.StartsWith("**") && tok.EndsWith("**") && tok.Length > 4)
            {
                inlines.Add(new Run(tok[2..^2]) { FontWeight = FontWeights.SemiBold });
            }
            else if (tok.StartsWith('`') && tok.EndsWith('`') && tok.Length > 2)
            {
                inlines.Add(new Run($" {tok[1..^1]} ")
                {
                    FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
                    FontSize = 12.5,
                    Background = BrushFrom(isDark ? "#44FFFFFF" : "#180F172A"),
                    Foreground = BrushFrom(isDark ? "#93C5FD" : "#1D4ED8")
                });
            }
            else if (tok.StartsWith('['))
            {
                var lm = Regex.Match(tok, @"^\[([^\]]+)\]\(([^)]+)\)$");
                if (lm.Success)
                {
                    inlines.Add(MakeLink(lm.Groups[1].Value, lm.Groups[2].Value, isDark));
                }
                else
                {
                    inlines.Add(new Run(tok));
                }
            }
            else if (tok.StartsWith("http://") || tok.StartsWith("https://"))
            {
                inlines.Add(MakeLink(tok, tok, isDark));
            }
            else if (tok.StartsWith('*') && tok.EndsWith('*') && tok.Length > 2)
            {
                inlines.Add(new Run(tok[1..^1]) { FontStyle = FontStyles.Italic });
            }
            else
            {
                inlines.Add(new Run(tok));
            }

            pos = m.Index + m.Length;
        }

        if (pos < text.Length)
            inlines.Add(new Run(text[pos..]));
    }

    private static Hyperlink MakeLink(string label, string url, bool isDark)
    {
        var link = new Hyperlink(new Run(label))
        {
            Foreground = BrushFrom(isDark ? "#60A5FA" : "#2563EB"),
            TextDecorations = TextDecorations.Underline
        };
        link.Click += (_, _) =>
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                try { Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true }); }
                catch { }
            }
        };
        return link;
    }

    private static SolidColorBrush BrushFrom(string hex)
        => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
}
