using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Services;
using Xunit;

namespace AiSecretary.Desktop.Tests;

public sealed class AttentionContextTests
{
    [Theory]
    [InlineData("這個方案可以嗎？")]
    [InlineData("上面第二點再講清楚一點")]
    public void ShouldSkipForRecentChat_WhenAssistantJustRepliedAndNoWindowSwitch_SkipsInjection(string prompt)
    {
        // Confirm the detector itself recognizes the deictic cue, while behavioral suppression skips screen injection
        Assert.True(DeicticDetector.Detect(prompt).IsDeictic);

        var now = DateTimeOffset.UtcNow;
        var externalCapturedAt = now.AddSeconds(-40);
        var recent = new List<ChatMessageRecord>
        {
            new()
            {
                Role = "assistant",
                Content = "以下是三個方案：1. A方案 2. B方案 3. C方案",
                CreatedAt = now.AddSeconds(-20),
                RevealedAt = now.AddSeconds(-15),
                IsStreaming = false
            }
        };

        Assert.True(AttentionContextService.ShouldSkipForRecentChat(externalCapturedAt, recent, now));
    }

    [Fact]
    public void ShouldSkipForRecentChat_WhenUserSwitchedToExternalWindowAfterReply_AllowsInjection()
    {
        var now = DateTimeOffset.UtcNow;
        var recent = new List<ChatMessageRecord>
        {
            new()
            {
                Role = "assistant",
                Content = "好的，沒問題。",
                CreatedAt = now.AddSeconds(-30),
                RevealedAt = now.AddSeconds(-25),
                IsStreaming = false
            }
        };
        // User switched back to Chrome/Word 5 seconds ago (after assistant replied 25s ago)
        var externalCapturedAt = now.AddSeconds(-5);

        Assert.False(AttentionContextService.ShouldSkipForRecentChat(externalCapturedAt, recent, now));
    }

    [Fact]
    public void ShouldSkipForRecentChat_WhenAssistantReplyOlderThan3Minutes_DoesNotSuppress()
    {
        var now = DateTimeOffset.UtcNow;
        var recent = new List<ChatMessageRecord>
        {
            new()
            {
                Role = "assistant",
                Content = "五分鐘前的對話",
                CreatedAt = now.AddMinutes(-5),
                RevealedAt = now.AddMinutes(-5),
                IsStreaming = false
            }
        };
        var externalCapturedAt = now.AddMinutes(-6);

        // Older than 3 minutes -> does not suppress as recent chat (will fall through to stale >2m disambiguation)
        Assert.False(AttentionContextService.ShouldSkipForRecentChat(externalCapturedAt, recent, now));
    }
    [Fact]
    public void FormatFreshBlock_StaysWithinTokenBudgetCap()
    {
        var longSelection = new string('A', 900);
        var longExcerpt = new string('B', 900);
        var headings = new[] { "Heading One", "Heading Two", "Heading Three", "Heading Four", "Heading Five" };
        var labels = new[] { "Email", "Name", "Department", "Remarks" };

        var block = AttentionContextService.FormatFreshBlock(
            source: "browser",
            ageSec: 8,
            confidence: "high",
            processName: "chrome",
            title: "C# - Wikipedia",
            url: "https://en.wikipedia.org/wiki/C_Sharp_(programming_language)",
            selectionText: longSelection,
            focusedLabel: null,
            headings: headings,
            formLabels: labels,
            excerpt: longExcerpt);

        Assert.StartsWith("<attention_context source=\"browser\" age_sec=\"8\" confidence=\"high\">", block);
        Assert.EndsWith("</attention_context>", block);
        Assert.True(block.Length <= AttentionContextService.MaxBlockChars, $"Block length {block.Length} exceeded cap {AttentionContextService.MaxBlockChars}");
        Assert.Contains("selected_text:", block);
        Assert.Contains("headings:", block);
    }

    [Fact]
    public void FormatStaleBlock_MarksLowConfidenceAndListsCandidates()
    {
        var candidates = new[] { "Chrome 上的〈Wikipedia〉", "剛才的〈專案通知〉", "其他（我直接說）" };
        var block = AttentionContextService.FormatStaleBlock(
            source: "browser",
            ageSec: 310,
            processName: "chrome",
            title: "Wikipedia",
            candidates: candidates);

        Assert.Contains("confidence=\"low\"", block);
        Assert.Contains("stale=\"true\"", block);
        Assert.Contains("age_sec=\"310\"", block);
        Assert.Contains("Do NOT guess blindly", block);
        Assert.Contains("Chrome 上的〈Wikipedia〉", block);
        Assert.Contains("剛才的〈專案通知〉", block);
    }
}
