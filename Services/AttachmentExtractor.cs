using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AiSecretary.Desktop.Agent;

namespace AiSecretary.Desktop.Services;

/// <summary>
/// Extracts text/binary payloads from [附件：path] markers in user prompts (PDF via PdfPig, DOCX, text files, and images).
/// </summary>
public static class AttachmentExtractor
{
    public static readonly Regex AttachmentPattern = new(@"\[附件：([^\]]+)\]", RegexOptions.Compiled);

    public static bool HasImageAttachment(string text)
    {
        foreach (Match m in AttachmentPattern.Matches(text))
        {
            var path = m.Groups[1].Value.Trim();
            if (!File.Exists(path)) continue;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp")
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
                    if (info.Length <= 10 * 1024 * 1024)
                        extracted = ExtractDocxText(path);
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
        foreach (Match m in AttachmentPattern.Matches(text))
        {
            var path = m.Groups[1].Value.Trim();
            if (!File.Exists(path)) continue;
            var ext = Path.GetExtension(path).ToLowerInvariant();
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
        using var zip = ZipFile.OpenRead(path);
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
}
