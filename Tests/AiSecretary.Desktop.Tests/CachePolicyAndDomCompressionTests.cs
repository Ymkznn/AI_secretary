using System.Text.Json;
using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Agent.Providers;
using AiSecretary.Desktop.Services;
using AiSecretary.Desktop.Skills;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AiSecretary.Desktop.Tests;

public sealed class CachePolicyAndDomCompressionTests
{
    private static AnthropicProviderAdapter CreateAdapter()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection().Build();
        return new AnthropicProviderAdapter(new AppSettingsService(cfg));
    }

    private static bool HasCacheControl(JsonElement msgElement)
    {
        if (!msgElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("cache_control", out _))
                return true;
        }
        return false;
    }

    private static bool SystemHasCacheControl(JsonElement root)
    {
        if (!root.TryGetProperty("system", out var sys) || sys.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var block in sys.EnumerateArray())
        {
            if (block.TryGetProperty("cache_control", out _))
                return true;
        }
        return false;
    }

    [Fact]
    public void AnthropicAdapter_PrefixReadOnly_CachesSystemAndRouteTailOnly_NeverTrailingObservations()
    {
        var adapter = CreateAdapter();
        // Exact FinalReply shape: [system, history_user, history_assistant, route_user, route_assistant_json, final_observations_user]
        var messages = new List<ModelMessage>
        {
            new("system", "System persona & skills"),
            new("user", "prev question"),
            new("assistant", "prev answer"),
            new("user", "dynamic route prompt (cached by Route)"),
            new("assistant", """{"action":"call_planner"}"""),
            new("user", "20k one-off worker observations")
        };

        // 1) PrefixReadOnly -> system cached, index n-3 (route_user, idx=2) cached, n-2 & n-1 NOT cached!
        var prefixJson = adapter.BuildRequestJson(messages, "claude-sonnet-5-5", CachePolicy.PrefixReadOnly);
        using (var doc = JsonDocument.Parse(prefixJson))
        {
            Assert.True(SystemHasCacheControl(doc.RootElement));
            var msgs = doc.RootElement.GetProperty("messages");
            Assert.Equal(5, msgs.GetArrayLength());
            Assert.False(HasCacheControl(msgs[0]));
            Assert.False(HasCacheControl(msgs[1]));
            Assert.True(HasCacheControl(msgs[2]));  // exact n-3 route prompt breakpoint
            Assert.False(HasCacheControl(msgs[3])); // routing assistant json: no write
            Assert.False(HasCacheControl(msgs[4])); // one-off observations: no 1.25x CacheWrite!
        }

        // 2) Conversation -> system + n-2 + n-1 cached
        var convJson = adapter.BuildRequestJson(messages, "claude-sonnet-5-5", CachePolicy.Conversation);
        using (var doc = JsonDocument.Parse(convJson))
        {
            Assert.True(SystemHasCacheControl(doc.RootElement));
            var msgs = doc.RootElement.GetProperty("messages");
            Assert.False(HasCacheControl(msgs[2]));
            Assert.True(HasCacheControl(msgs[3]));
            Assert.True(HasCacheControl(msgs[4]));
        }

        // 3) None -> zero cache_control anywhere
        var noneJson = adapter.BuildRequestJson(messages, "claude-sonnet-5-5", CachePolicy.None);
        using (var doc = JsonDocument.Parse(noneJson))
        {
            Assert.False(SystemHasCacheControl(doc.RootElement));
            var msgs = doc.RootElement.GetProperty("messages");
            for (int i = 0; i < msgs.GetArrayLength(); i++)
                Assert.False(HasCacheControl(msgs[i]));
        }
    }

    [Fact]
    public void BrowserFormSkill_FormatDomObservation_DefaultsToTextOnly_AndCapsTotalBudgetWhenHtmlRequested()
    {
        var bigHtml = "<head><meta name='x' content='y'><link rel='stylesheet' href='a.css'></head>" +
                      "<body><script>var a=1;</script><img src='data:image/png;base64," + new string('A', 2000) + "'/>" +
                      "<div class='wrapper' style='color:red'><p>Tracking Status: Delivered</p></div></body>";
        var bigText = "Tracking Status: Delivered\n\n\n\n" + new string('B', 9000);
        var snap = new BrowserDomSnapshot("https://example.com/track", "EMS Track", bigHtml, bigText, Truncated: false);

        // 1) Default (includeHtml = false): text-only, NO HTML section, strictly capped to maxChars
        var textOnly = BrowserFormSkill.FormatDomObservation(snap, maxChars: 2000, includeHtml: false);
        Assert.Contains("--- TEXT ---", textOnly);
        Assert.DoesNotContain("--- HTML (compacted) ---", textOnly);
        Assert.Contains("[truncated]", textOnly);
        Assert.True(textOnly.Length < 2200);

        // 2) Explicit includeHtml = true: strips <head>, <script>, base64 data: URI, style/class noise, and strictly stays within budget
        var withHtml = BrowserFormSkill.FormatDomObservation(snap, maxChars: 3000, includeHtml: true);
        Assert.Contains("--- TEXT ---", withHtml);
        Assert.Contains("--- HTML (compacted) ---", withHtml);
        Assert.DoesNotContain("<head>", withHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script>", withHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:image/png", withHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("style=", withHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<p>Tracking Status: Delivered</p>", withHtml);
        Assert.True(withHtml.Length < 3300);

        // 3) FinalReply stripping removes the entire HTML appendix while preserving TEXT
        var stripped = BrowserFormSkill.StripHtmlSectionForFinalReply(withHtml);
        Assert.Contains("Tracking Status: Delivered", stripped);
        Assert.DoesNotContain("--- HTML (compacted) ---", stripped);
        Assert.DoesNotContain("<p>", stripped);
        Assert.Contains("[HTML omitted in final reply synthesis]", stripped);
    }

    [Fact]
    public void WebFetchSkill_ExtractsPdfBytesViaPdfPig_SupportsPageRange_AndRejectsNonPdfBinary()
    {
        var pdfBytes = BuildMinimalTwoPagePdf("PostgreSQL exploit page one", "Second page follow up");

        // 1) Auto-detects PDF via %PDF- magic header even if Content-Type is octet-stream
        var (okAll, textAll) = WebFetchSkill.ExtractTextFromPayload(pdfBytes, "application/octet-stream");
        Assert.True(okAll);
        Assert.Contains("PDF extracted (2 page(s)", textAll);
        Assert.Contains("--- Page 1 ---", textAll);
        Assert.Contains("PostgreSQL exploit page one", textAll);
        Assert.Contains("--- Page 2 ---", textAll);
        Assert.Contains("Second page follow up", textAll);

        // 2) Respects start_page / end_page pagination
        var (okP2, textP2) = WebFetchSkill.ExtractTextFromPayload(pdfBytes, "application/pdf", startPage: 2, endPage: 2);
        Assert.True(okP2);
        Assert.DoesNotContain("PostgreSQL exploit page one", textP2);
        Assert.Contains("Second page follow up", textP2);

        // 3) Rejects non-PDF binary payloads cleanly instead of emitting UTF-8 gibberish
        var pngHeader = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D };
        var (okBin, binMsg) = WebFetchSkill.ExtractTextFromPayload(pngHeader, "image/png");
        Assert.False(okBin);
        Assert.Contains("binary content", binMsg);
    }

    private static byte[] BuildMinimalTwoPagePdf(string page1Text, string page2Text)
    {
        var sb = new System.Text.StringBuilder();
        var offsets = new List<int>();
        void AddObj(int id, string body)
        {
            offsets.Add(sb.Length);
            sb.Append($"{id} 0 obj\n{body}\nendobj\n");
        }

        sb.Append("%PDF-1.4\n");
        AddObj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        AddObj(2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>");
        AddObj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R /Resources << /Font << /F1 7 0 R >> >> >>");
        AddObj(4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Resources << /Font << /F1 7 0 R >> >> >>");

        var s1 = $"BT /F1 12 Tf 72 720 Td ({page1Text}) Tj ET";
        var s2 = $"BT /F1 12 Tf 72 720 Td ({page2Text}) Tj ET";
        AddObj(5, $"<< /Length {s1.Length} >>\nstream\n{s1}\nendstream");
        AddObj(6, $"<< /Length {s2.Length} >>\nstream\n{s2}\nendstream");
        AddObj(7, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        int xrefOffset = sb.Length;
        sb.Append($"xref\n0 {offsets.Count + 1}\n");
        sb.Append("0000000000 65535 f \n");
        foreach (var off in offsets)
            sb.Append($"{off:D10} 00000 n \n");

        sb.Append($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }
}
