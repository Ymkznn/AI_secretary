using AiSecretary.Desktop.Services;
using Xunit;

namespace AiSecretary.Desktop.Tests;

public sealed class DeicticDetectorTests
{
    [Theory]
    // Positive cases (Chinese explicit deictic cues)
    [InlineData("幫我看一下這個", "這個")]
    [InlineData("這個是什麼", "這個")]
    [InlineData("幫我摘要這頁", "這頁")]
    [InlineData("這篇在講什麼？", "這篇")]
    [InlineData("翻譯這段", "這段")]
    [InlineData("幫我摘要這封信", "這封")]
    [InlineData("這裡寫錯了嗎", "這裡")]
    [InlineData("上面說的條件是什麼", "上面")]
    [InlineData("幫我看一下這份文件的重點", "這份")]
    [InlineData("畫面上那個錯誤是什麼意思", "畫面上")]
    // Positive cases (English explicit deictic cues)
    [InlineData("Summarize this", "this")]
    [InlineData("What is that?", "that")]
    [InlineData("Explain what's here", "here")]
    [InlineData("Translate this paragraph into Traditional Chinese", "this")]
    // Positive cases (objectless imperative verbs)
    [InlineData("幫我看一下", null)]
    [InlineData("看一下", null)]
    [InlineData("總結一下", null)]
    [InlineData("翻譯一下", null)]
    [InlineData("幫我整理一下吧", null)]
    [InlineData("summarize", null)]
    [InlineData("translate please", null)]
    public void Detect_PositiveCases_TriggersDeixis(string prompt, string? expectedCue)
    {
        var result = DeicticDetector.Detect(prompt);
        Assert.True(result.IsDeictic, $"Expected deictic=true for '{prompt}', got reason={result.Reason}");
        if (expectedCue is not null)
            Assert.Equal(expectedCue, result.MatchedCue);
    }

    [Theory]
    // Negative cases: explicit domain nouns (calendar / mail / schedule / weather / memory)
    [InlineData("幫我看一下明天行程")]
    [InlineData("幫我看一下下週行事曆")]
    [InlineData("幫我看一下信箱")]
    [InlineData("看一下信")]
    [InlineData("幫我看一下信")]
    [InlineData("幫我看一下有沒有新信")]
    [InlineData("幫我看一下排程")]
    [InlineData("幫我看一下待辦提醒")]
    [InlineData("看一下今天台北天氣")]
    [InlineData("幫我查一下 Gmail 未讀郵件")]
    [InlineData("Check my calendar for tomorrow")]
    [InlineData("Summarize my inbox")]
    // Negative cases: URL, file path, attachment, inline quoted / pasted text
    [InlineData("幫我看一下這個 https://en.wikipedia.org/wiki/C_Sharp")]
    [InlineData("翻譯這段：「Hello world from unit test」")]
    [InlineData("Summarize this: \"The quick brown fox jumps over the lazy dog\"")]
    [InlineData("幫我看一下這個檔案 C:\\Users\\test\\report.xlsx")]
    [InlineData("幫我看一下這個 [Attachment: C:\\temp\\doc.pdf]")]
    [InlineData("翻譯一下：\nThis is a pasted paragraph that is already inside the prompt.")]
    // Negative cases: action verb WITH an explicit non-deictic object
    [InlineData("幫我翻譯 Hello world")]
    [InlineData("總結一下 Python 和 Go 的差異")]
    [InlineData("解釋一下什麼是量子糾纏")]
    // Negative cases: temporal & filler idioms using 這個 / this / that
    [InlineData("這個月有幾天？")]
    [InlineData("這個禮拜天氣怎麼樣")]
    [InlineData("What meetings do I have this week?")]
    [InlineData("Thanks for that!")]
    [InlineData("That's great, thank you")]
    // Negative cases: general chat / empty
    [InlineData("你好")]
    [InlineData("1+1等於多少")]
    [InlineData("")]
    public void Detect_NegativeCases_DoesNotTriggerDeixis(string prompt)
    {
        var result = DeicticDetector.Detect(prompt);
        Assert.False(result.IsDeictic, $"Expected deictic=false for '{prompt}', but triggered ({result.Reason}, cue={result.MatchedCue})");
    }

    [Fact]
    public void Detect_WithTargetMailId_DoesNotTriggerDeixis()
    {
        var result = DeicticDetector.Detect("幫我摘要這封信", targetMailId: "mail-123");
        Assert.False(result.IsDeictic);
        Assert.Equal("has_target_mail_id", result.Reason);
    }

    [Theory]
    [InlineData("不是這個，我是說剛剛那封信", true)]
    [InlineData("搞錯了，我是指另一個視窗", true)]
    [InlineData("Not that page, I meant my email", true)]
    [InlineData("謝謝，懂了", false)]
    [InlineData("那明天九點提醒我", false)]
    public void IsUserCorrectionSignal_DetectsCorrectionsAccurately(string prompt, bool expected)
    {
        Assert.Equal(expected, DeicticDetector.IsUserCorrectionSignal(prompt));
    }
}
