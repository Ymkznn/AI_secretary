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
}
