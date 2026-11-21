using AiSecretary.Desktop.Services;
using Xunit;

namespace AiSecretary.Desktop.Tests;

public sealed class AttentionContextTests
{
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
