using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Prompts;
using AiSecretary.Desktop.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiSecretary.Desktop.Tests;

public sealed class ProactiveTopicAndBackoffTests
{
    private sealed class StubModelGateway : IModelGateway
    {
        private readonly string _reply;
        public int Calls { get; private set; }
        public StubModelGateway(string reply) => _reply = reply;
        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ModelMessage> messages, LlmCallOptions options, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new ModelResponse(_reply, 50));
        }
    }

    private static (SqliteStore Store, string TempDb) CreateTempStore()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"aisec_proactive_{Guid.NewGuid():N}.db");
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = $"Data Source={tempDb}"
            })
            .Build();
        return (new SqliteStore(cfg), tempDb);
    }

    private static void CleanupDb(string tempDb)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        try { if (File.Exists(tempDb + "-wal")) File.Delete(tempDb + "-wal"); } catch { }
        try { if (File.Exists(tempDb + "-shm")) File.Delete(tempDb + "-shm"); } catch { }
    }

    [Fact]
    public void TopicPicker_NeverRepeatsLastTwoTopicsConsecutively()
    {
        var rng = new Random(42);
        var recent = new List<ProactiveTopicKind>();
        var activity = new ProactiveActivitySnapshot("osu!", "osu! - 水樹奈々 - 嘆きの花", null);
        var customTopic = new KnowledgeFactRecord(
            "f-trivia", "conv-1", "user_pref", "分享冷知識", "偶爾分享有趣冷知識",
            null, "observed", new List<string> { "proactive_topic" },
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "proactive_topic:trivia", "Confirmed", 2);

        for (int i = 0; i < 60; i++)
        {
            var ctx = new ProactiveTopicContext(
                Now: new DateTimeOffset(2026, 11, 21, 22, 0, 0, TimeSpan.FromHours(8)),
                RecentUserMessages24h: new[] { "幫我追蹤 EMS 單號" },
                PendingConfirmations: Array.Empty<string>(),
                RecentBlackboardResults: new[] { "EMS 追蹤: 已到達國際交換局" },
                PinnedReminders: new[] { "11/25 打 HTB Vaccine" },
                UpcomingSchedules: Array.Empty<string>(),
                Activity: activity,
                CustomTopics: new[] { customTopic },
                RecentSharedCustomSummaries: new[] { "章魚有三顆心臟" },
                RecentTopics: recent,
                AwarenessLevel: ProactiveAwarenessLevel.Normal);

            var picked = ProactiveTopicEngine.PickTopic(ctx, rng);

            if (recent.Count >= 1)
                Assert.NotEqual(recent[^1], picked.Topic);
            if (recent.Count >= 2)
                Assert.NotEqual(recent[^2], picked.Topic);

            recent.Add(picked.Topic);
            if (recent.Count > 6) recent.RemoveAt(0);
        }

        // Edge case: only 2 topics have data (TimeAndLife + PersonaSmallTalk) -> still never repeats immediately previous topic
        var minimalRecent = new List<ProactiveTopicKind>();
        for (int i = 0; i < 20; i++)
        {
            var minCtx = new ProactiveTopicContext(
                Now: DateTimeOffset.Now,
                RecentUserMessages24h: Array.Empty<string>(),
                PendingConfirmations: Array.Empty<string>(),
                RecentBlackboardResults: Array.Empty<string>(),
                PinnedReminders: Array.Empty<string>(),
                UpcomingSchedules: Array.Empty<string>(),
                Activity: null,
                CustomTopics: Array.Empty<KnowledgeFactRecord>(),
                RecentSharedCustomSummaries: Array.Empty<string>(),
                RecentTopics: minimalRecent,
                AwarenessLevel: ProactiveAwarenessLevel.Normal);

            var picked = ProactiveTopicEngine.PickTopic(minCtx, rng);
            if (minimalRecent.Count >= 1)
                Assert.NotEqual(minimalRecent[^1], picked.Topic);
            Assert.True(picked.Topic is ProactiveTopicKind.TimeAndLife or ProactiveTopicKind.PersonaSmallTalk);
            minimalRecent.Add(picked.Topic);
        }
    }

    [Fact]
    public void PromptIsolation_OmitsAllWindowInfoWhenActivityNotPicked_AndMasksTitlesForLowAndNormal()
    {
        var sensitiveActivity = new ProactiveActivitySnapshot(
            ProcessName: "osu!",
            Title: "osu! - 水樹奈々 - 嘆きの花 [SecretMap.osz]",
            Url: null);

        // 1) When CurrentActivity is excluded (in RecentTopics) -> ZERO window/process/title info in prompt!
        var nonActCtx = new ProactiveTopicContext(
            Now: new DateTimeOffset(2026, 11, 21, 22, 30, 0, TimeSpan.FromHours(8)),
            RecentUserMessages24h: Array.Empty<string>(),
            PendingConfirmations: Array.Empty<string>(),
            RecentBlackboardResults: Array.Empty<string>(),
            PinnedReminders: Array.Empty<string>(),
            UpcomingSchedules: Array.Empty<string>(),
            Activity: sensitiveActivity,
            CustomTopics: Array.Empty<KnowledgeFactRecord>(),
            RecentSharedCustomSummaries: Array.Empty<string>(),
            RecentTopics: new[] { ProactiveTopicKind.CurrentActivity, ProactiveTopicKind.TimeAndLife },
            AwarenessLevel: ProactiveAwarenessLevel.High);

        var pickedNonAct = ProactiveTopicEngine.PickTopic(nonActCtx, new Random(1));
        Assert.NotEqual(ProactiveTopicKind.CurrentActivity, pickedNonAct.Topic);
        Assert.Null(pickedNonAct.ActivityPayload);

        var promptNonAct = MainAgentPrompts.BuildProactiveUserPrompt(nonActCtx.Now, pickedNonAct, nonActCtx.RecentTopics);
        Assert.DoesNotContain("current_activity:", promptNonAct);
        Assert.DoesNotContain("category=", promptNonAct);
        Assert.DoesNotContain("osu!", promptNonAct, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("嘆きの花", promptNonAct);
        Assert.DoesNotContain("SecretMap", promptNonAct);

        // 2) Low & Normal: coarse category ONLY, NEVER raw song/file title!
        var normalPayload = ProactiveTopicEngine.FormatActivityForPrompt(sensitiveActivity, ProactiveAwarenessLevel.Normal);
        var lowPayload = ProactiveTopicEngine.FormatActivityForPrompt(sensitiveActivity, ProactiveAwarenessLevel.Low);
        Assert.Equal("category=rhythm_game", normalPayload);
        Assert.Equal("category=rhythm_game", lowPayload);
        Assert.DoesNotContain("嘆きの花", normalPayload!);
        Assert.DoesNotContain("SecretMap", normalPayload!);

        // Browser YouTube -> watching_video category on Normal, no video title leaked
        var ytSnap = new ProactiveActivitySnapshot("msedge", "腳踩空力爆打球鯊 - YouTube", "https://www.youtube.com/watch?v=123");
        Assert.Equal("category=watching_video", ProactiveTopicEngine.FormatActivityForPrompt(ytSnap, ProactiveAwarenessLevel.Normal));

        // 3) High: includes full title (for yandere/menhera persona), EXCEPT incognito stays redacted!
        var highPayload = ProactiveTopicEngine.FormatActivityForPrompt(sensitiveActivity, ProactiveAwarenessLevel.High);
        Assert.Contains("rhythm_game", highPayload!);
        Assert.Contains("嘆きの花", highPayload!);

        var incognitoSnap = new ProactiveActivitySnapshot("msedge", "Private Secret Page", "https://secret.example", IsIncognito: true);
        var highIncognito = ProactiveTopicEngine.FormatActivityForPrompt(incognitoSnap, ProactiveAwarenessLevel.High);
        Assert.DoesNotContain("Private Secret Page", highIncognito!);
        Assert.Contains("private", highIncognito!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BackoffCalculator_DoublesOnUnanswered_CapsProperly_SkipDoesNotAdvance_AndInteractionResets()
    {
        // Fixed base 15m, cap 60m -> 15 -> 30 -> 60 -> 60
        Assert.Equal(15, ProactiveBackoffCalculator.ComputeNextIntervalMinutes(15, 15, unansweredCount: 0, backoffCapMinutes: 60));
        Assert.Equal(30, ProactiveBackoffCalculator.ComputeNextIntervalMinutes(15, 15, unansweredCount: 1, backoffCapMinutes: 60));
        Assert.Equal(60, ProactiveBackoffCalculator.ComputeNextIntervalMinutes(15, 15, unansweredCount: 2, backoffCapMinutes: 60));
        Assert.Equal(60, ProactiveBackoffCalculator.ComputeNextIntervalMinutes(15, 15, unansweredCount: 5, backoffCapMinutes: 60));

        // Simulated lifecycle:
        int unanswered = 0;
        // 1st message sent -> unanswered becomes 1 -> next interval 30m
        unanswered++;
        Assert.Equal(30, ProactiveBackoffCalculator.ComputeNextIntervalMinutes(15, 15, unanswered, 60));
        // Next turn returns {"skip": true} -> unanswered stays 1 (NOT incremented!) -> still 30m
        Assert.Equal(30, ProactiveBackoffCalculator.ComputeNextIntervalMinutes(15, 15, unanswered, 60));
        // 2nd message actually sent -> unanswered becomes 2 -> next interval 60m
        unanswered++;
        Assert.Equal(60, ProactiveBackoffCalculator.ComputeNextIntervalMinutes(15, 15, unanswered, 60));
        // Any user interaction resets unanswered to 0 -> back to 15m!
        unanswered = 0;
        Assert.Equal(15, ProactiveBackoffCalculator.ComputeNextIntervalMinutes(15, 15, unanswered, 60));
    }

    [Fact]
    public async Task AwarenessPrecedence_VerbalOverrideBeatsLlmClassification_AndDeletingOverrideRestoresLlm()
    {
        var (store, tempDb) = CreateTempStore();
        try
        {
            const string convId = "conv-awareness-test";
            const string yandereStyle = "You are an obsessively devoted yandere persona who watches every move the user makes.";
            var stubLlm = new StubModelGateway("""{"level": "high"}""");

            // 1) First call with NO verbal override -> classifies once via LLM ("high") and caches by SHA-256
            var prefsEmpty = await store.GetConfirmedProactivePrefsAsync(convId);
            var (lvl1, src1) = await MainAgent.ResolveProactiveAwarenessAsync(
                store, stubLlm, convId, yandereStyle, prefsEmpty, NullLogger.Instance, CancellationToken.None);
            Assert.Equal(ProactiveAwarenessLevel.High, lvl1);
            Assert.Equal("llm", src1);
            Assert.Equal(1, stubLlm.Calls);

            // 2) Second call with same style -> cache hit, 0 additional LLM calls
            var (lvl2, src2) = await MainAgent.ResolveProactiveAwarenessAsync(
                store, stubLlm, convId, yandereStyle, prefsEmpty, NullLogger.Instance, CancellationToken.None);
            Assert.Equal(ProactiveAwarenessLevel.High, lvl2);
            Assert.Equal("llm", src2);
            Assert.Equal(1, stubLlm.Calls);

            // 3) User verbally overrides ("別一直講我在幹嘛") -> stored as Confirmed user_pref with tag proactive_awareness
            var overrideFact = new KnowledgeFactRecord(
                FactId: "fact-override-1",
                ConversationId: convId,
                Category: "user_pref",
                Summary: "減少評論目前視窗（low）",
                Detail: "low - user asked not to comment frequently on active windows",
                Evidence: "user verbal instruction",
                Confidence: "observed",
                Tags: new List<string> { ProactiveTopicEngine.AwarenessTag },
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow,
                MemoryKey: ProactiveTopicEngine.AwarenessTag,
                State: "Confirmed",
                ReinforceCount: 1);
            await store.UpsertFactAsync(overrideFact);

            var prefsWithOverride = await store.GetConfirmedProactivePrefsAsync(convId);
            var (lvl3, src3) = await MainAgent.ResolveProactiveAwarenessAsync(
                store, stubLlm, convId, yandereStyle, prefsWithOverride, NullLogger.Instance, CancellationToken.None);
            Assert.Equal(ProactiveAwarenessLevel.Low, lvl3);
            Assert.Equal("override", src3);
            Assert.Equal(1, stubLlm.Calls); // Still 0 extra LLM calls!

            // 4) User deletes the override fact in "我學到的事" -> automatically reverts to cached LLM classification ("high")
            await store.DeleteFactAsync("fact-override-1");
            var prefsAfterDelete = await store.GetConfirmedProactivePrefsAsync(convId);
            var (lvl4, src4) = await MainAgent.ResolveProactiveAwarenessAsync(
                store, stubLlm, convId, yandereStyle, prefsAfterDelete, NullLogger.Instance, CancellationToken.None);
            Assert.Equal(ProactiveAwarenessLevel.High, lvl4);
            Assert.Equal("llm", src4);
        }
        finally
        {
            CleanupDb(tempDb);
        }
    }

    [Fact]
    public async Task CustomTopicAndOngoingThread_RespectConfirmedOnly_ExcludeAssistantMessages_AndIncludeRecentShares()
    {
        var (store, tempDb) = CreateTempStore();
        try
        {
            const string convId = "conv-pool-test";
            var now = DateTimeOffset.UtcNow;

            // Insert Candidate (one-off joke) vs Confirmed (ongoing new knowledge)
            await store.UpsertFactAsync(new KnowledgeFactRecord(
                "f-joke", convId, "user_pref", "冷笑話", "使用者隨口問過一次冷笑話",
                null, "inferred", new List<string> { "proactive_topic" },
                now, now, "proactive_topic:jokes", "Candidate", 1));
            await store.UpsertFactAsync(new KnowledgeFactRecord(
                "f-know", convId, "user_pref", "分享有趣新知識", "使用者希望偶爾分享新知識",
                null, "observed", new List<string> { "proactive_topic" },
                now, now, "proactive_topic:new_knowledge", "Confirmed", 1));

            var confirmedPrefs = await store.GetConfirmedProactivePrefsAsync(convId);
            var topics = ProactiveTopicEngine.ExtractConfirmedCustomTopics(confirmedPrefs);
            Assert.Single(topics);
            Assert.Equal("f-know", topics[0].FactId);

            // Verify recent 5 shared summaries are embedded in CustomUserTopic prompt with DO NOT repeat warning
            var selectedCustom = new SelectedProactiveTopic(
                Topic: ProactiveTopicKind.CustomUserTopic,
                ActivityPayload: null,
                SelectedCustomTopic: topics[0],
                RecentSharedSummaries: new[] { "事實1", "事實2", "事實3", "事實4", "事實5" },
                OngoingThreadItems: Array.Empty<string>(),
                ReminderItems: Array.Empty<string>());
            var customPrompt = MainAgentPrompts.BuildProactiveUserPrompt(now, selectedCustom, new[] { ProactiveTopicKind.TimeAndLife });
            Assert.Contains("recent_shared_items (DO NOT repeat", customPrompt);
            Assert.Contains("事實1", customPrompt);
            Assert.Contains("事實5", customPrompt);

            // Verify GetRecentUserOnlyMessagesAsync excludes ALL assistant/proactive messages and returns max 3 from last 24h
            await store.UpsertChatMessageAsync(new ChatMessageRecord { ConversationId = convId, Role = "user", Content = "48小時前舊訊息", CreatedAt = now.AddHours(-48), RevealedAt = now.AddHours(-48) });
            await store.UpsertChatMessageAsync(new ChatMessageRecord { ConversationId = convId, Role = "assistant", Content = "TODO.md 還在看呀", TaskId = "proactive", CreatedAt = now.AddMinutes(-30), RevealedAt = now.AddMinutes(-30) });
            await store.UpsertChatMessageAsync(new ChatMessageRecord { ConversationId = convId, Role = "user", Content = "使用者訊息A", CreatedAt = now.AddMinutes(-25), RevealedAt = now.AddMinutes(-25) });
            await store.UpsertChatMessageAsync(new ChatMessageRecord { ConversationId = convId, Role = "assistant", Content = "一般助理回覆", CreatedAt = now.AddMinutes(-24), RevealedAt = now.AddMinutes(-24) });
            await store.UpsertChatMessageAsync(new ChatMessageRecord { ConversationId = convId, Role = "user", Content = "使用者訊息B", CreatedAt = now.AddMinutes(-10), RevealedAt = now.AddMinutes(-10) });

            var userOnly = await store.GetRecentUserOnlyMessagesAsync(convId, now.AddHours(-24), 3);
            Assert.Equal(2, userOnly.Count);
            Assert.Equal("使用者訊息A", userOnly[0]);
            Assert.Equal("使用者訊息B", userOnly[1]);
            Assert.DoesNotContain(userOnly, s => s.Contains("TODO.md") || s.Contains("一般助理回覆"));
        }
        finally
        {
            CleanupDb(tempDb);
        }
    }
}
