using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AiSecretary.Desktop.Agent;

namespace AiSecretary.Desktop.Services;

/// <summary>
/// Extracts text/binary payloads from [Attachment: path] markers in user prompts (PDF via PdfPig, DOCX, PPTX, XLSX, text files, and images).
/// </summary>
public static class AttachmentExtractor
{
    public static readonly Regex AttachmentPattern = new(@"\[Attachment:\s*([^\]]+)\]", RegexOptions.Compiled);
    private static readonly Regex SlideIndexRegex = new(@"^ppt/slides/slide(\d+)\.xml$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SheetIndexRegex = new(@"^xl/worksheets/sheet(\d+)\.xml$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool HasImageAttachment(string text)
    {
        foreach (Match m in AttachmentPattern.Matches(text))
        {
            var path = m.Groups[1].Value.Trim();
            if (!File.Exists(path)) continue;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".pptx")
                return true;
        }
        return false;
    }

    public static IReadOnlyList<ModelMessage> StripImagesToPlaceholder(IReadOnlyList<ModelMessage> messages)
    {
        var result = new List<ModelMessage>(messages.Count);
        foreach (var msg in messages)
        {
            if (msg.Role != "user" || !HasImageAttachment(msg.Content))
            {
                result.Add(msg);
                continue;
            }

            var rewritten = AttachmentPattern.Replace(msg.Content, match =>
            {
                var path = match.Groups[1].Value.Trim();
                if (!File.Exists(path)) return match.Value;
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".webp"))
                    return match.Value;

                var name = Path.GetFileName(path);
                return $"[Image attachment: {name} (direct image input not supported by current model)]";
            });

            result.Add(new ModelMessage(msg.Role, rewritten));
        }
        return result;
    }

    public static string EnrichTextWithDocumentAttachments(string text)
    {
        var matches = AttachmentPattern.Matches(text);
        if (matches.Count == 0) return text;

        var sb = new StringBuilder(text);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in matches)
        {
            var path = m.Groups[1].Value.Trim();
            if (!seen.Add(path) || !File.Exists(path)) continue;

            var ext = Path.GetExtension(path).ToLowerInvariant();
            string? extracted = null;
            try
            {
                if (ext is ".txt" or ".md" or ".csv" or ".json" or ".log" or ".xml" or ".yaml" or ".yml" or ".cs" or ".py" or ".js" or ".ts" or ".html" or ".htm")
                {
                    var info = new FileInfo(path);
                    if (info.Length <= 2 * 1024 * 1024)
                        extracted = File.ReadAllText(path);
                }
                else if (ext == ".docx")
                {
                    var info = new FileInfo(path);
                    if (info.Length <= 15 * 1024 * 1024)
                        extracted = ExtractDocxText(path);
                }
                else if (ext == ".pptx")
                {
                    var info = new FileInfo(path);
                    if (info.Length <= 40 * 1024 * 1024)
                        extracted = ExtractPptxText(path);
                }
                else if (ext == ".xlsx")
                {
                    var info = new FileInfo(path);
                    if (info.Length <= 25 * 1024 * 1024)
                        extracted = ExtractXlsxText(path);
                }
                else if (ext == ".pdf")
                {
                    var info = new FileInfo(path);
                    if (info.Length <= 25 * 1024 * 1024)
                        extracted = ExtractPdfText(path);
                }
            }
            catch
            {
                // Ignore unreadable files
            }

            if (!string.IsNullOrWhiteSpace(extracted))
            {
                if (extracted.Length > 150000)
                    extracted = extracted[..150000] + "\n...(truncated)";
                sb.AppendLine();
                sb.AppendLine($"<attached_document name=\"{Path.GetFileName(path)}\">");
                sb.AppendLine(extracted);
                sb.AppendLine("</attached_document>");
            }
        }
        return sb.ToString();
    }

    public static List<(string Mime, string Base64)> ExtractBinaryAttachments(string text, bool includePdf)
    {
        var list = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AttachmentPattern.Matches(text))
        {
            var path = m.Groups[1].Value.Trim();
            if (!seen.Add(path) || !File.Exists(path)) continue;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".pptx")
            {
                try
                {
                    foreach (var img in ExtractPptxContentImages(path, maxImagesPerDeck: 6))
                    {
                        if (list.Count >= 12) break;
                        list.Add(img);
                    }
                }
                catch { }
                continue;
            }

            var mime = ext switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".pdf" when includePdf => "application/pdf",
                _ => null
            };
            if (mime is null) continue;
            try
            {
                var info = new FileInfo(path);
                if (info.Length > 15 * 1024 * 1024) continue;
                var b64 = Convert.ToBase64String(File.ReadAllBytes(path));
                list.Add((mime, b64));
            }
            catch { }
        }
        return list;
    }

    private static string ExtractPdfText(string path)
    {
        using var doc = UglyToad.PdfPig.PdfDocument.Open(path);
        var sb = new StringBuilder();
        foreach (var page in doc.GetPages())
        {
            var pageText = page.Text;
            if (!string.IsNullOrWhiteSpace(pageText))
            {
                sb.AppendLine($"--- Page {page.Number} ---");
                sb.AppendLine(pageText);
            }
            if (sb.Length > 155000) break;
        }
        return sb.ToString();
    }

    private static string ExtractDocxText(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml");
        if (entry is null) return "";
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var lines = doc.Descendants(w + "p")
            .Select(p => string.Concat(p.Descendants(w + "t").Select(t => t.Value)))
            .Where(s => !string.IsNullOrWhiteSpace(s));
        return string.Join("\n", lines);
    }

    public static string ExtractPptxText(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";

        var slides = zip.Entries
            .Select(e => (Entry: e, Match: SlideIndexRegex.Match(e.FullName)))
            .Where(x => x.Match.Success)
            .Select(x => (x.Entry, Index: int.TryParse(x.Match.Groups[1].Value, out var idx) ? idx : 0))
            .OrderBy(x => x.Index)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"Presentation: {Path.GetFileName(path)} (TotalSlides={slides.Count})");

        foreach (var (slideEntry, idx) in slides)
        {
            sb.AppendLine($"--- Slide {idx} ---");
            try
            {
                using var stream = slideEntry.Open();
                var doc = XDocument.Load(stream);

                // Extract tables formatted with pipe separators first, plus general paragraphs
                foreach (var tbl in doc.Descendants(a + "tbl"))
                {
                    sb.AppendLine("  [Table]");
                    foreach (var tr in tbl.Elements(a + "tr"))
                    {
                        var cells = tr.Elements(a + "tc")
                            .Select(tc => string.Join(" ", tc.Descendants(a + "p")
                                .Select(p => string.Concat(p.Descendants(a + "t").Select(t => t.Value)).Trim())
                                .Where(s => !string.IsNullOrWhiteSpace(s))))
                            .ToList();
                        if (cells.Any(c => !string.IsNullOrWhiteSpace(c)))
                            sb.AppendLine($"    | {string.Join(" | ", cells)} |");
                    }
                }

                // Paragraphs outside of tables (shapes, grouped shapes, SmartArt text bodies)
                foreach (var p in doc.Descendants(a + "p"))
                {
                    if (p.Ancestors(a + "tbl").Any()) continue;
                    var line = string.Concat(p.Descendants(a + "t").Select(t => t.Value)).Trim();
                    if (!string.IsNullOrWhiteSpace(line))
                        sb.AppendLine($"  {line}");
                }
            }
            catch { }

            // Check slide relationships for SmartArt diagrams or embedded media images
            var relsEntry = zip.GetEntry($"ppt/slides/_rels/slide{idx}.xml.rels");
            if (relsEntry is not null)
            {
                try
                {
                    using var relStream = relsEntry.Open();
                    var relDoc = XDocument.Load(relStream);
                    foreach (var rel in relDoc.Descendants().Where(e => e.Name.LocalName == "Relationship"))
                    {
                        var target = rel.Attribute("Target")?.Value ?? "";
                        if (target.Contains("diagrams/data", StringComparison.OrdinalIgnoreCase))
                        {
                            var cleanTarget = "ppt/" + target.TrimStart('.', '/');
                            var diagEntry = zip.GetEntry(cleanTarget);
                            if (diagEntry is not null)
                            {
                                using var diagStream = diagEntry.Open();
                                var diagDoc = XDocument.Load(diagStream);
                                var diagTexts = diagDoc.Descendants(a + "p")
                                    .Select(p => string.Concat(p.Descendants(a + "t").Select(t => t.Value)).Trim())
                                    .Where(s => !string.IsNullOrWhiteSpace(s))
                                    .Distinct()
                                    .ToList();
                                if (diagTexts.Count > 0)
                                    sb.AppendLine($"  [SmartArt] {string.Join(" / ", diagTexts)}");
                            }
                        }
                        else if (target.Contains("media/", StringComparison.OrdinalIgnoreCase))
                        {
                            var mediaName = Path.GetFileName(target);
                            sb.AppendLine($"  [Embedded Image on Slide {idx}: {mediaName}]");
                        }
                    }
                }
                catch { }
            }

            // Speaker notes
            var notesEntry = zip.GetEntry($"ppt/notesSlides/notesSlide{idx}.xml");
            if (notesEntry is not null)
            {
                try
                {
                    using var notesStream = notesEntry.Open();
                    var notesDoc = XDocument.Load(notesStream);
                    var noteLines = notesDoc.Descendants(a + "p")
                        .Select(p => string.Concat(p.Descendants(a + "t").Select(t => t.Value)).Trim())
                        .Where(s => !string.IsNullOrWhiteSpace(s) && !int.TryParse(s, out _))
                        .ToList();
                    if (noteLines.Count > 0)
                        sb.AppendLine($"  [Notes] {string.Join(" / ", noteLines)}");
                }
                catch { }
            }
        }

        return sb.ToString();
    }

    private static List<(string Mime, string Base64)> ExtractPptxContentImages(string path, int maxImagesPerDeck)
    {
        var list = new List<(string Mime, string Base64)>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        var slides = zip.Entries
            .Select(e => (Entry: e, Match: SlideIndexRegex.Match(e.FullName)))
            .Where(x => x.Match.Success)
            .Select(x => int.TryParse(x.Match.Groups[1].Value, out var idx) ? idx : 0)
            .OrderBy(idx => idx)
            .ToList();

        var seenMedia = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Count how many slides reference each media file so we skip template backgrounds/logos repeated on >=3 slides
        var mediaRefCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var slideMediaMap = new List<(int SlideIdx, string MediaEntryPath)>();

        foreach (var idx in slides)
        {
            var relsEntry = zip.GetEntry($"ppt/slides/_rels/slide{idx}.xml.rels");
            if (relsEntry is null) continue;
            try
            {
                using var relStream = relsEntry.Open();
                var relDoc = XDocument.Load(relStream);
                foreach (var rel in relDoc.Descendants().Where(e => e.Name.LocalName == "Relationship"))
                {
                    var target = rel.Attribute("Target")?.Value ?? "";
                    if (!target.Contains("media/", StringComparison.OrdinalIgnoreCase)) continue;
                    var entryPath = "ppt/media/" + Path.GetFileName(target);
                    slideMediaMap.Add((idx, entryPath));
                    mediaRefCount[entryPath] = mediaRefCount.TryGetValue(entryPath, out var c) ? c + 1 : 1;
                }
            }
            catch { }
        }

        foreach (var (_, entryPath) in slideMediaMap)
        {
            if (list.Count >= maxImagesPerDeck) break;
            if (!seenMedia.Add(entryPath)) continue;
            // Skip repeated template backgrounds or logos that appear on 3+ slides when deck has >= 3 slides
            if (slides.Count >= 3 && mediaRefCount.TryGetValue(entryPath, out var refs) && refs >= 3)
                continue;

            var mediaEntry = zip.GetEntry(entryPath);
            if (mediaEntry is null || mediaEntry.Length < 4096 || mediaEntry.Length > 5 * 1024 * 1024)
                continue;

            var ext = Path.GetExtension(entryPath).ToLowerInvariant();
            var mime = ext switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => null
            };
            if (mime is null) continue;

            using var ms = new MemoryStream();
            using (var s = mediaEntry.Open())
            {
                s.CopyTo(ms);
            }
            list.Add((mime, Convert.ToBase64String(ms.ToArray())));
        }

        return list;
    }

    public static string ExtractXlsxText(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        XNamespace ss = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        var sharedStrings = new List<string>();
        var sstEntry = zip.GetEntry("xl/sharedStrings.xml");
        if (sstEntry is not null)
        {
            try
            {
                using var sstStream = sstEntry.Open();
                var sstDoc = XDocument.Load(sstStream);
                foreach (var si in sstDoc.Descendants(ss + "si"))
                {
                    sharedStrings.Add(string.Concat(si.Descendants(ss + "t").Select(t => t.Value)));
                }
            }
            catch { }
        }

        var sheetNames = new Dictionary<int, string>();
        var wbEntry = zip.GetEntry("xl/workbook.xml");
        if (wbEntry is not null)
        {
            try
            {
                using var wbStream = wbEntry.Open();
                var wbDoc = XDocument.Load(wbStream);
                int idx = 1;
                foreach (var sh in wbDoc.Descendants(ss + "sheet"))
                {
                    var name = sh.Attribute("name")?.Value ?? $"Sheet{idx}";
                    sheetNames[idx++] = name;
                }
            }
            catch { }
        }

        var sheets = zip.Entries
            .Select(e => (Entry: e, Match: SheetIndexRegex.Match(e.FullName)))
            .Where(x => x.Match.Success)
            .Select(x => (x.Entry, Index: int.TryParse(x.Match.Groups[1].Value, out var i) ? i : 0))
            .OrderBy(x => x.Index)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"Workbook: {Path.GetFileName(path)} (Sheets={sheets.Count})");

        foreach (var (entry, idx) in sheets)
        {
            var title = sheetNames.TryGetValue(idx, out var nm) ? nm : $"Sheet{idx}";
            sb.AppendLine($"--- Sheet {idx}: {title} ---");
            try
            {
                using var stream = entry.Open();
                var doc = XDocument.Load(stream);
                int rowCount = 0;
                foreach (var row in doc.Descendants(ss + "row"))
                {
                    var cells = new List<string>();
                    foreach (var c in row.Elements(ss + "c"))
                    {
                        var rAttr = c.Attribute("r")?.Value ?? "";
                        var tAttr = c.Attribute("t")?.Value ?? "";
                        var vVal = c.Element(ss + "v")?.Value ?? "";
                        string textVal = vVal;
                        if (tAttr == "s" && int.TryParse(vVal, out var sIdx) && sIdx >= 0 && sIdx < sharedStrings.Count)
                            textVal = sharedStrings[sIdx];
                        else if (tAttr == "inlineStr")
                            textVal = string.Concat(c.Descendants(ss + "t").Select(t => t.Value));

                        if (!string.IsNullOrWhiteSpace(textVal))
                            cells.Add($"{rAttr}={textVal}");
                    }
                    if (cells.Count > 0)
                    {
                        sb.AppendLine("  " + string.Join(" | ", cells));
                        if (++rowCount >= 200)
                        {
                            sb.AppendLine("  ...(sheet rows truncated)");
                            break;
                        }
                    }
                }
            }
            catch { }
        }

        return sb.ToString();
    }
}
