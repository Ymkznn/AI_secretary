using System.Text.Json;
using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Agent.Providers;
using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiSecretary.Desktop.Tests;

public sealed class EmptyReplyAndStopReasonTests
{
    private sealed class ThrowingModelGateway : IModelGateway
    {
        public int Calls { get; private set; }
        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ModelMessage> messages, LlmCallOptions options, CancellationToken ct = default)
        {
            Calls++;
            throw new InvalidOperationException("Fallback error message must NEVER call an LLM!");
        }
    }

    private static (SqliteStore Store, UiLocalizer Localizer, ThrowingModelGateway Model, string DbPath) CreateTestHarness()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"aisec_stopreason_{Guid.NewGuid():N}.db");
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = $"Data Source={tempDb}"
            })
            .Build();
        var store = new SqliteStore(cfg);
        var settings = new AppSettingsService(cfg);
        var model = new ThrowingModelGateway();
        var localizer = new UiLocalizer(settings, store, model, NullLogger<UiLocalizer>.Instance);
        return (store, localizer, model, tempDb);
    }

    private static void CleanupDb(string tempDb)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        try { if (File.Exists(tempDb + "-wal")) File.Delete(tempDb + "-wal"); } catch { }
        try { if (File.Exists(tempDb + "-shm")) File.Delete(tempDb + "-shm"); } catch { }
    }

    [Fact]
    public void ProviderAdapters_ExtractStopReason_AcrossAnthropicOpenAiAndGemini()
    {
        var cfg = new ConfigurationBuilder().Build();
        var anthropic = new AnthropicProviderAdapter(new AppSettingsService(cfg));
        var openai = new OpenAiCompatibleProviderAdapter();
        var gemini = new GeminiProviderAdapter();

        using (var d1 = JsonDocument.Parse("""{"stop_reason":"refusal","content":[],"usage":{"input_tokens":100,"output_tokens":664}}"""))
        {
            Assert.Equal("refusal", anthropic.ExtractStopReason(d1.RootElement));
            Assert.Equal("", anthropic.ExtractResponseText(d1.RootElement));
        }

        using (var d2 = JsonDocument.Parse("""{"choices":[{"finish_reason":"content_filter","message":{"content":""}}]}"""))
        {
            Assert.Equal("content_filter", openai.ExtractStopReason(d2.RootElement));
            Assert.Equal("", openai.ExtractResponseText(d2.RootElement));
        }

        using (var d3 = JsonDocument.Parse("""{"candidates":[{"finishReason":"SAFETY"}]}"""))
        {
            Assert.Equal("SAFETY", gemini.ExtractStopReason(d3.RootElement));
            Assert.Equal("", gemini.ExtractResponseText(d3.RootElement));
        }

        using (var d4 = JsonDocument.Parse("""{"promptFeedback":{"blockReason":"PROHIBITED_CONTENT"}}"""))
        {
            Assert.Equal("PROHIBITED_CONTENT", gemini.ExtractStopReason(d4.RootElement));
        }
    }

    [Fact]
    public async Task RefusalWithEmptyContent_PersistsClearExplanationInChatHistory_WithZeroExtraLlmCalls()
    {
        var (store, localizer, throwingModel, tempDb) = CreateTestHarness();
        try
        {
            const string convId = "conv-refusal-test";
            const string taskId = "task-refusal-1";

            // 1) Simulate model returning stop_reason="refusal" and empty Content (despite 664 billed output tokens)
            var refusalResp = new ModelResponse(
                Content: "",
                EstimatedTokens: 19700 + 664,
                InputTokens: 4,
                OutputTokens: 664,
                CacheTokens: 0,
                CacheWriteTokens: 19700,
                StopReason: "refusal");

            // Log into LlmCallLogs and verify StopReason + RawTextLength are persisted accurately
            await store.InsertLlmCallLogAsync(new LlmCallLogRecord(
                Id: 0,
                TimestampUtc: DateTimeOffset.UtcNow,
                Agent: "MainAgent.FinalReply",
                Tier: "Simple",
                Model: "claude-sonnet-5-5",
                CachePolicy: "Conversation",
                Priority: "Interactive",
                UncachedInput: 4,
                CacheWrite: 19700,
                CacheRead: 0,
                Output: 664,
                LatencyMs: 7800,
                Succeeded: true,
                Error: null,
                StopReason: refusalResp.StopReason,
                RawTextLength: refusalResp.Content.Length));

            var logs = await store.GetRecentLlmCallLogsAsync(5);
            Assert.Single(logs);
            Assert.Equal("refusal", logs[0].StopReason);
            Assert.Equal(0, logs[0].RawTextLength);

            // 2) Resolve final reply -> produces deterministic Chinese refusal explanation with ZERO extra LLM calls
            var (cleanReply, choices, ttsText, isTaskDone, usedFallback) =
                MainAgent.ResolveFinalReplyOrFallback(refusalResp, localizer);

            Assert.True(usedFallback);
            Assert.True(isTaskDone);
            Assert.Empty(choices);
            Assert.Null(ttsText);
            Assert.Equal("這題被模型的安全機制擋下了，可以換個方式描述，例如只問觀念。", cleanReply);
            Assert.Equal(0, throwingModel.Calls);

            // 3) Persist via background report handler and confirm chat history shows the explanation (never stuck!)
            var now = DateTimeOffset.UtcNow;
            var userMsg = new ChatMessageRecord
            {
                ConversationId = convId,
                Role = "user",
                Content = "這題為啥要加 SameSite=None",
                CreatedAt = now,
                RevealedAt = now
            };
            var ackMsg = new ChatMessageRecord
            {
                ConversationId = convId,
                Role = "assistant",
                Content = "正在規劃步驟...",
                TaskId = taskId,
                ReplyToMessageId = userMsg.Id,
                CreatedAt = now.AddMilliseconds(5),
                RevealedAt = now.AddMilliseconds(5)
            };
            await store.UpsertChatMessageAsync(userMsg);
            await store.UpsertChatMessageAsync(ackMsg);

            await SecretaryController.PersistBackgroundReportOrFallbackAsync(
                store, convId, taskId, userMsg.Id, ackMsg, cleanReply, null, localizer, CancellationToken.None);

            var history = await store.GetChatMessagesAsync(convId, 20);
            Assert.Contains(history, m => m.Role == "assistant" && m.Content.Contains("這題被模型的安全機制擋下了"));
        }
        finally
        {
            CleanupDb(tempDb);
        }
    }

    [Fact]
    public async Task MaxTokensTagOnlyAndEmptyBgReport_AllProduceClearNonEmptyUserMessages()
    {
        var (store, localizer, throwingModel, tempDb) = CreateTestHarness();
        try
        {
            // max_tokens with empty text
            var maxTokRes = MainAgent.ResolveFinalReplyOrFallback(
                new ModelResponse("", 4096, OutputTokens: 4096, StopReason: "max_tokens"), localizer);
            Assert.Equal("回覆太長被截斷了。", maxTokRes.CleanReply);
            Assert.True(maxTokRes.UsedFallback);

            // Raw content contained ONLY strippable tags -> becomes empty after stripping -> generic fallback
            var tagOnlyRes = MainAgent.ResolveFinalReplyOrFallback(
                new ModelResponse("[TTS_TEXT: hi]\n[TASK_STATUS: done]", 50, OutputTokens: 12, StopReason: "end_turn"), localizer);
            Assert.Equal("這次沒有產生回覆，請再試一次。", tagOnlyRes.CleanReply);
            Assert.True(tagOnlyRes.UsedFallback);

            // Last-line-of-defense in SecretaryController: even if bgReportText is null/empty,
            // "正在規劃步驟..." placeholder is replaced in-place with clear fallback text!
            const string convId = "conv-bg-fallback";
            var now = DateTimeOffset.UtcNow;
            var userMsg = new ChatMessageRecord
            {
                ConversationId = convId,
                Role = "user",
                Content = "測試問題",
                CreatedAt = now,
                RevealedAt = now
            };
            var ackMsg = new ChatMessageRecord
            {
                ConversationId = convId,
                Role = "assistant",
                Content = "正在規劃步驟...",
                TaskId = "t-empty",
                ReplyToMessageId = userMsg.Id,
                CreatedAt = now.AddMilliseconds(5),
                RevealedAt = now.AddMilliseconds(5)
            };
            await store.UpsertChatMessageAsync(userMsg);
            await store.UpsertChatMessageAsync(ackMsg);

            await SecretaryController.PersistBackgroundReportOrFallbackAsync(
                store, convId, "t-empty", userMsg.Id, ackMsg, bgReportText: "", bgSources: null, localizer, CancellationToken.None);

            var history = await store.GetChatMessagesAsync(convId, 20);
            Assert.DoesNotContain(history, m => m.Content == "正在規劃步驟...");
            Assert.Contains(history, m => m.Role == "assistant" && m.Content == "這次沒有產生回覆，請再試一次。");
            Assert.Equal(0, throwingModel.Calls);
        }
        finally
        {
            CleanupDb(tempDb);
        }
    }
}
