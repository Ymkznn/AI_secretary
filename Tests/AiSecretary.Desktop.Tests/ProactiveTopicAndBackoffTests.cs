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
        private readonly Queue<string> _replies;
        public int Calls { get; private set; }
        public StubModelGateway(params string[] replies) => _replies = new Queue<string>(replies);
        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ModelMessage> messages, LlmCallOptions options, CancellationToken ct = default)
        {
            Calls++;
            var next = _replies.Count > 1 ? _replies.Dequeue() : _replies.Peek();
            return Task.FromResult(new ModelResponse(next, 50));
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
    public void TopicPicker_LowersWeightAfterBeingPicked_InsteadOfHardBanning()
    {
        var activity = new ProactiveActivitySnapshot("osu!", "osu! - 水樹奈々 - 嘆きの花", null);
        var customTopic = new KnowledgeFactRecord(
            "f-trivia", "conv-1", "user_pref", "分享冷知識", "偶爾分享有趣冷知識",
            null, "observed", new List<string> { "proactive_topic" },
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "proactive_topic:trivia", "Confirmed", 2);

        ProactiveTopicContext MakeCtx(IReadOnlyList<ProactiveTopicKind> recent) => new(
            Now: new DateTimeOffset(2026, 11, 21, 22, 0, 0, TimeSpan.FromHours(8)),
            RecentConversationTurns24h: new[] { "[user] 幫我追蹤 EMS 單號", "[assistant] 已開始追蹤 EMS 單號" },
            PendingConfirmations: Array.Empty<string>(),
            RecentBlackboardResults: new[] { "EMS 追蹤: 已到達國際交換局" },
            PinnedReminders: new[] { "11/25 打 HTB Vaccine" },
            UpcomingSchedules: Array.Empty<string>(),
            Activity: activity,
            CustomTopics: new[] { customTopic },
            RecentSharedCustomSummaries: new[] { "章魚有三顆心臟" },
            RecentTopics: recent,
            AwarenessLevel: ProactiveAwarenessLevel.Normal);

        // Fresh weights: CurrentActivity is 25%
        var fresh = ProactiveTopicEngine.ComputeTopicWeights(MakeCtx(Array.Empty<ProactiveTopicKind>()));
        Assert.Equal(0.25, fresh[ProactiveTopicKind.CurrentActivity], precision: 4);

        // Immediately after CurrentActivity is picked -> weight is sharply lowered (> 0, NOT hard-banned!)
        var afterOne = ProactiveTopicEngine.ComputeTopicWeights(MakeCtx(new[] { ProactiveTopicKind.CurrentActivity }));
        Assert.True(afterOne[ProactiveTopicKind.CurrentActivity] > 0.0, "Topic must still be selectable (soft decay, not hard-banned)");
        Assert.True(afterOne[ProactiveTopicKind.CurrentActivity] < fresh[ProactiveTopicKind.CurrentActivity] * 0.35);

        // Two turns after CurrentActivity was picked -> partially recovered, still lower than fresh
        var afterTwo = ProactiveTopicEngine.ComputeTopicWeights(MakeCtx(new[] { ProactiveTopicKind.CurrentActivity, ProactiveTopicKind.TimeAndLife }));
        Assert.True(afterTwo[ProactiveTopicKind.CurrentActivity] > afterOne[ProactiveTopicKind.CurrentActivity]);
        Assert.True(afterTwo[ProactiveTopicKind.CurrentActivity] < fresh[ProactiveTopicKind.CurrentActivity]);
    }

    [Fact]
    public void PromptIsolation_OmitsAllWindowInfoWhenActivityNotPicked_AndPassesTitleDirectlyWhenPicked()
    {
        var activity = new ProactiveActivitySnapshot(
            ProcessName: "osu!",
            Title: "osu! - 水樹奈々 - 嘆きの花",
            Url: null);

        // 1) When a non-activity topic is selected -> ZERO window/process/title info in prompt!
        var selectedNonAct = new SelectedProactiveTopic(
            Topic: ProactiveTopicKind.PersonaSmallTalk,
            ActivityPayload: null,
            SelectedCustomTopic: null,
            RecentSharedSummaries: Array.Empty<string>(),
            OngoingThreadItems: Array.Empty<string>(),
            ReminderItems: Array.Empty<string>());

        var promptNonAct = MainAgentPrompts.BuildProactiveUserPrompt(
            new DateTimeOffset(2026, 11, 21, 22, 30, 0, TimeSpan.FromHours(8)),
            selectedNonAct,
            new[] { ProactiveTopicKind.CurrentActivity });
        Assert.DoesNotContain("current_activity:", promptNonAct);
        Assert.DoesNotContain("osu!", promptNonAct, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("嘆きの花", promptNonAct);

        // 2) When CurrentActivity IS picked -> passes process + title directly
        var normalPayload = ProactiveTopicEngine.FormatActivityForPrompt(activity, ProactiveAwarenessLevel.Normal);
        Assert.Equal("osu!: osu! - 水樹奈々 - 嘆きの花", normalPayload);

        // 3) Incognito / private browsing stays redacted
        var incognitoSnap = new ProactiveActivitySnapshot("msedge", "Private Secret Page", "https://secret.example", IsIncognito: true);
        var incognitoPayload = ProactiveTopicEngine.FormatActivityForPrompt(incognitoSnap, ProactiveAwarenessLevel.High);
        Assert.DoesNotContain("Private Secret Page", incognitoPayload!);
        Assert.Contains("private browsing", incognitoPayload!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AwarenessPrecedence_IsBoundPerStyleFile_BeatsLlm_SurvivesMdEdit_AndRevertsWhenDeleted()
    {
        var (store, tempDb) = CreateTempStore();
        try
        {
            const string convId = "conv-awareness-test";
            const string sanaScope = "sana_style_prompt";
            const string ruiScope = "rui_style_prompt";
            const string sanaMdV1 = "You are an obsessively devoted yandere persona who watches every move the user makes.";
            const string sanaMdV2 = "You are an edited gentle persona who still cares warmly.";
            const string ruiMd = "You are a deeply possessive, vigilant persona.";

            var stubLlm = new StubModelGateway("""{"level": "high"}""", """{"level": "high"}""", """{"level": "normal"}""");

            // 1) Classify sana V1 via LLM -> "high" (cached by SHA-256)
            var prefsEmpty = await store.GetConfirmedProactivePrefsAsync(convId);
            var (lvl1, src1) = await MainAgent.ResolveProactiveAwarenessAsync(
                store, stubLlm, convId, sanaScope, sanaMdV1, prefsEmpty, NullLogger.Instance, CancellationToken.None);
            Assert.Equal(ProactiveAwarenessLevel.High, lvl1);
            Assert.Equal("llm", src1);
            Assert.Equal(1, stubLlm.Calls);

            // 2) Verbal override bound specifically to sana_style_prompt -> "low"
            var sanaOverride = new KnowledgeFactRecord(
                FactId: "fact-override-sana",
                ConversationId: convId,
                Category: "user_pref",
                Summary: "佐奈減少看視窗（low）",
                Detail: "low - user asked not to comment frequently on active windows",
                Evidence: "user verbal instruction",
                Confidence: "observed",
                Tags: new List<string> { ProactiveTopicEngine.AwarenessTag, ProactiveTopicEngine.BuildStyleScopeTag(sanaScope) },
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow,
                MemoryKey: ProactiveTopicEngine.BuildAwarenessMemoryKey(sanaScope),
                State: "Confirmed",
                ReinforceCount: 1);
            await store.UpsertFactAsync(sanaOverride);

            var prefsWithSanaOverride = await store.GetConfirmedProactivePrefsAsync(convId);

            // 2a) For sana: verbal override beats LLM classification ("low")
            var (sanaOverridden, srcSana) = await MainAgent.ResolveProactiveAwarenessAsync(
                store, stubLlm, convId, sanaScope, sanaMdV1, prefsWithSanaOverride, NullLogger.Instance, CancellationToken.None);
            Assert.Equal(ProactiveAwarenessLevel.Low, sanaOverridden);
            Assert.Equal("override", srcSana);

            // 2b) For rui (DIFFERENT persona file): sana's override MUST NOT affect rui!
            var (ruiLevel, srcRui) = await MainAgent.ResolveProactiveAwarenessAsync(
                store, stubLlm, convId, ruiScope, ruiMd, prefsWithSanaOverride, NullLogger.Instance, CancellationToken.None);
            Assert.Equal(ProactiveAwarenessLevel.High, ruiLevel);
            Assert.Equal("llm", srcRui);
            Assert.Equal(2, stubLlm.Calls);

            // 3) User edits sana_style_prompt.md (sanaMdV2) while override still exists -> override stays intact!
            var (sanaEditedStillOverridden, srcEdited) = await MainAgent.ResolveProactiveAwarenessAsync(
                store, stubLlm, convId, sanaScope, sanaMdV2, prefsWithSanaOverride, NullLogger.Instance, CancellationToken.None);
            Assert.Equal(ProactiveAwarenessLevel.Low, sanaEditedStillOverridden);
            Assert.Equal("override", srcEdited);
            Assert.Equal(2, stubLlm.Calls); // Didn't waste an LLM call while override is active

            // 4) User deletes sana's override -> automatically classifies the edited sanaMdV2 ("normal")!
            await store.DeleteFactAsync("fact-override-sana");
            var prefsAfterDelete = await store.GetConfirmedProactivePrefsAsync(convId);
            var (sanaReverted, srcReverted) = await MainAgent.ResolveProactiveAwarenessAsync(
                store, stubLlm, convId, sanaScope, sanaMdV2, prefsAfterDelete, NullLogger.Instance, CancellationToken.None);
            Assert.Equal(ProactiveAwarenessLevel.Normal, sanaReverted);
            Assert.Equal("llm", srcReverted);
            Assert.Equal(3, stubLlm.Calls);
        }
        finally
        {
            CleanupDb(tempDb);
        }
    }

    [Fact]
    public async Task CustomTopicCrudAndOngoingTurns_KeepCompletedAssistantReplies_ExcludeProactiveSelfCheckIns()
    {
        var (store, tempDb) = CreateTempStore();
        try
        {
            const string convId = "conv-pool-test";
            var now = DateTimeOffset.UtcNow;

            // 1) Candidate (one-off joke) vs Confirmed (ongoing new knowledge)
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

            // Edit + verify updated + Delete custom topic (full CRUD)
            Assert.True(await store.UpdateFactAsync("f-know", "分享資安冷知識", "偏資安或網路冷知識", null, null));
            var afterEdit = await store.GetAllKnowledgeFactsAsync(convId);
            Assert.Contains(afterEdit, f => f.FactId == "f-know" && f.Summary == "分享資安冷知識");

            // Recent 5 shared summaries embedded in CustomUserTopic prompt with DO NOT repeat warning
            var selectedCustom = new SelectedProactiveTopic(
                Topic: ProactiveTopicKind.CustomUserTopic,
                ActivityPayload: null,
                SelectedCustomTopic: afterEdit.First(f => f.FactId == "f-know"),
                RecentSharedSummaries: new[] { "事實1", "事實2", "事實3", "事實4", "事實5" },
                OngoingThreadItems: Array.Empty<string>(),
                ReminderItems: Array.Empty<string>());
            var customPrompt = MainAgentPrompts.BuildProactiveUserPrompt(now, selectedCustom, new[] { ProactiveTopicKind.TimeAndLife });
            Assert.Contains("recent_shared_items (DO NOT repeat", customPrompt);
            Assert.Contains("事實1", customPrompt);
            Assert.Contains("事實5", customPrompt);

            Assert.True(await store.DeleteFactAsync("f-know"));
            Assert.Empty(ProactiveTopicEngine.ExtractConfirmedCustomTopics(await store.GetConfirmedProactivePrefsAsync(convId)));

            // 2) GetRecentNonProactiveChatTurnsAsync keeps user turns + assistant replies with ReplyToMessageId,
            //    excluding both legacy (TaskId=null, ReplyToMessageId=null) and new (TaskId="proactive") proactive messages!
            var userAsk = new ChatMessageRecord
            {
                ConversationId = convId,
                Role = "user",
                Content = "下週四前要打掉 HTB Vaccine 記得提醒我",
                CreatedAt = now.AddMinutes(-25),
                RevealedAt = now.AddMinutes(-25)
            };
            await store.UpsertChatMessageAsync(new ChatMessageRecord { ConversationId = convId, Role = "user", Content = "48小時前舊訊息", CreatedAt = now.AddHours(-48), RevealedAt = now.AddHours(-48) });
            await store.UpsertChatMessageAsync(new ChatMessageRecord { ConversationId = convId, Role = "assistant", Content = "舊版未標記：TODO.md 還在看呀", TaskId = null, ReplyToMessageId = null, CreatedAt = now.AddMinutes(-30), RevealedAt = now.AddMinutes(-30) });
            await store.UpsertChatMessageAsync(userAsk);
            await store.UpsertChatMessageAsync(new ChatMessageRecord { ConversationId = convId, Role = "assistant", Content = "設好囉，11/25 晚上八點提醒你打 HTB Vaccine！", TaskId = "task-123", ReplyToMessageId = userAsk.Id, CreatedAt = now.AddMinutes(-24), RevealedAt = now.AddMinutes(-24) });
            await store.UpsertChatMessageAsync(new ChatMessageRecord { ConversationId = convId, Role = "assistant", Content = "又在看 TODO.md 喔", TaskId = "proactive", ReplyToMessageId = null, CreatedAt = now.AddMinutes(-10), RevealedAt = now.AddMinutes(-10) });

            var turns = await store.GetRecentNonProactiveChatTurnsAsync(convId, now.AddHours(-24), 6);
            Assert.Equal(2, turns.Count);
            Assert.Contains("下週四前要打掉 HTB Vaccine", turns[0]);
            Assert.Contains("設好囉，11/25 晚上八點提醒你打 HTB Vaccine", turns[1]);
            Assert.DoesNotContain(turns, s => s.Contains("TODO.md"));
        }
        finally
        {
            CleanupDb(tempDb);
        }
    }

    [Fact]
    public async Task GetRecentNonProactiveChatTurns_ExcludesAssistantMessagesWithNullReplyToMessageId()
    {
        var (store, tempDb) = CreateTempStore();
        try
        {
            const string convId = "conv-replyto-filter";
            var now = DateTimeOffset.UtcNow;

            var userMsg = new ChatMessageRecord
            {
                ConversationId = convId,
                Role = "user",
                Content = "幫我查一下 EMS 進度",
                CreatedAt = now.AddMinutes(-20),
                RevealedAt = now.AddMinutes(-20)
            };
            await store.UpsertChatMessageAsync(userMsg);

            // Real assistant reply linked via ReplyToMessageId -> MUST be included
            await store.UpsertChatMessageAsync(new ChatMessageRecord
            {
                ConversationId = convId,
                Role = "assistant",
                Content = "查到了，包裹已經到東京國際交換局。",
                TaskId = "t-ems",
                ReplyToMessageId = userMsg.Id,
                CreatedAt = now.AddMinutes(-19),
                RevealedAt = now.AddMinutes(-19)
            });

            // Unsolicited assistant messages with ReplyToMessageId = null (legacy proactive, scheduled push, proposal) -> MUST be excluded
            await store.UpsertChatMessageAsync(new ChatMessageRecord
            {
                ConversationId = convId,
                Role = "assistant",
                Content = "TODO.md 還在看呀",
                TaskId = null,
                ReplyToMessageId = null,
                CreatedAt = now.AddMinutes(-15),
                RevealedAt = now.AddMinutes(-15)
            });
            await store.UpsertChatMessageAsync(new ChatMessageRecord
            {
                ConversationId = convId,
                Role = "assistant",
                Content = "⏰ 排程提醒：記得喝水",
                TaskId = "sched-99",
                ReplyToMessageId = null,
                CreatedAt = now.AddMinutes(-10),
                RevealedAt = now.AddMinutes(-10)
            });

            var turns = await store.GetRecentNonProactiveChatTurnsAsync(convId, now.AddHours(-24), 6);
            Assert.Equal(2, turns.Count);
            Assert.Contains("幫我查一下 EMS 進度", turns[0]);
            Assert.Contains("包裹已經到東京國際交換局", turns[1]);
            Assert.DoesNotContain(turns, s => s.Contains("TODO.md") || s.Contains("排程提醒"));
        }
        finally
        {
            CleanupDb(tempDb);
        }
    }

    [Fact]
    public async Task ProactiveAntiRepeatBlocklist_AndUpcomingScheduleFilter_PreventRamenLoopsAndSkillLeaks()
    {
        var (store, tempDb) = CreateTempStore();
        try
        {
            const string convId = "conv-antirepeat";
            var now = DateTimeOffset.UtcNow;

            var userMsg = new ChatMessageRecord { ConversationId = convId, Role = "user", Content = "那個解決ㄌ", CreatedAt = now.AddMinutes(-90), RevealedAt = now.AddMinutes(-90) };
            await store.UpsertChatMessageAsync(userMsg);
            await store.UpsertChatMessageAsync(new ChatMessageRecord { ConversationId = convId, Role = "assistant", Content = "太好了！SameSite 解決了", ReplyToMessageId = userMsg.Id, CreatedAt = now.AddMinutes(-89), RevealedAt = now.AddMinutes(-89) });
            await store.UpsertChatMessageAsync(new ChatMessageRecord { ConversationId = convId, Role = "assistant", Content = "欸，都已經快凌晨一點了，佐奈突然超想吃熱呼呼的拉麵耶……", TaskId = "proactive", CreatedAt = now.AddMinutes(-75), RevealedAt = now.AddMinutes(-75) });

            var snippets = await store.GetRecentProactiveSnippetsAsync(convId, now.AddHours(-12), 6);
            Assert.Single(snippets);
            Assert.Contains("熱呼呼的拉麵", snippets[0]);
            Assert.DoesNotContain(snippets, s => s.Contains("SameSite 解決了"));

            var selected = new SelectedProactiveTopic(
                ProactiveTopicKind.PersonaSmallTalk, null, null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
            var prompt = MainAgentPrompts.BuildProactiveUserPrompt(now, selected, new[] { ProactiveTopicKind.TimeAndLife }, snippets);
            Assert.Contains("already_said_recently (NEGATIVE BLOCKLIST", prompt);
            Assert.Contains("熱呼呼的拉麵", prompt);

            // Background polling skills (SkillName != null) like "新郵件監控" must NEVER be surfaced as user reminders!
            var reminders = new List<ReminderRecord>
            {
                new("r1", convId, "新郵件監控", "sync mail", null, now.AddMinutes(1), "Pending", IntervalMinutes: 1, SkillName: "sync_mail"),
                new("r2", convId, "打掉 HTB Vaccine 機器", "remember HTB", null, now.AddHours(5), "Pending", IntervalMinutes: 0, SkillName: null),
                new("r3", convId, "下週例會", "next week", null, now.AddDays(5), "Pending", IntervalMinutes: 0, SkillName: null)
            };

            var upcoming = ProactiveTopicEngine.FilterUserUpcomingSchedules(reminders, now.AddHours(24));
            Assert.Single(upcoming);
            Assert.Contains("打掉 HTB Vaccine 機器", upcoming[0]);
            Assert.DoesNotContain(upcoming, s => s.Contains("新郵件監控"));
        }
        finally
        {
            CleanupDb(tempDb);
        }
    }
}
