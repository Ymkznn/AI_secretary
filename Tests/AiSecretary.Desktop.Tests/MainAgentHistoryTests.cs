using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Models;
using Xunit;

namespace AiSecretary.Desktop.Tests;

public sealed class MainAgentHistoryTests
{
    [Fact]
    public void BuildHistoryModelMessages_AfterManyConsecutiveProactiveMessages_PreservesProactiveContext()
    {
        var convId = $"conv-proactive-{Guid.NewGuid():N}";
        var t0 = new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);
        var records = new List<ChatMessageRecord>
        {
            new() { ConversationId = convId, Role = "user", Content = "幫我開一下表單", CreatedAt = t0, RevealedAt = t0 }
        };

        // Simulate 10 consecutive assistant / proactive messages while the user was away or silent
        for (int i = 1; i <= 9; i++)
        {
            var ts = t0.AddMinutes(i * 15);
            records.Add(new ChatMessageRecord
            {
                ConversationId = convId,
                Role = "assistant",
                Content = $"主動關懷第 {i} 則：TODO.md 看完了嗎？",
                CreatedAt = ts,
                RevealedAt = ts
            });
        }

        var osuTs = t0.AddMinutes(150);
        records.Add(new ChatMessageRecord
        {
            ConversationId = convId,
            Role = "assistant",
            Content = "欸，現在換成在玩 osu! 了呀，佐奈有點好奇你打得怎麼樣喔。",
            CreatedAt = osuTs,
            RevealedAt = osuTs
        });

        var replyTs = osuTs.AddSeconds(25);
        var userImg = "[Attachment: C:\\sandbox\\paste_osu.png]";
        records.Add(new ChatMessageRecord
        {
            ConversationId = convId,
            Role = "user",
            Content = userImg,
            CreatedAt = replyTs,
            RevealedAt = replyTs
        });

        var history = MainAgent.BuildHistoryModelMessages(records, excludeTrailingUserPrompt: userImg, maxMessages: 20);

        Assert.NotEmpty(history);
        Assert.Equal("user", history[0].Role);
        Assert.Contains(history, m => m.Role == "assistant" && m.Content.Contains("佐奈有點好奇你打得怎麼樣喔"));
    }

    [Fact]
    public void BuildHistoryModelMessages_OnlyProactiveAssistantMessages_SynthesizesUserOpenerInsteadOfDroppingAll()
    {
        var convId = $"conv-proactive-only-{Guid.NewGuid():N}";
        var t0 = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        var records = new List<ChatMessageRecord>
        {
            new() { ConversationId = convId, Role = "assistant", Content = "佐奈有點好奇你打得怎麼樣喔。", CreatedAt = t0, RevealedAt = t0 },
            new() { ConversationId = convId, Role = "user", Content = "你看這張圖", CreatedAt = t0.AddSeconds(20), RevealedAt = t0.AddSeconds(20) }
        };

        var history = MainAgent.BuildHistoryModelMessages(records, excludeTrailingUserPrompt: "你看這張圖", maxMessages: 20);

        Assert.Equal(2, history.Count);
        Assert.Equal("user", history[0].Role);
        Assert.Equal("assistant", history[1].Role);
        Assert.Contains("佐奈有點好奇你打得怎麼樣喔", history[1].Content);
    }
}
