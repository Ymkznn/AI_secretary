using System.Text.Json;
using System.Text.RegularExpressions;
using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Services;

namespace AiSecretary.Desktop.Agent;

/// <summary>
/// 管理獨立於長期 UI 對話紀錄之外的短期模型 Session。
/// 依模型 Context Window × 可設定百分比（預設 25%，兼顧省 Token）計算輪替上限，
/// 並支援從模型 Context Overflow 錯誤訊息動態學習實際視窗大小，同時保留固定上限覆寫彈性。
/// </summary>
public sealed class ContextSessionManager
{
    private static readonly Regex[] OverflowWindowPatterns =
    {
        new(@"maximum context length is (\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"context (?:window|length) (?:of|is) (\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"max(?:imum)?[ _-]?tokens?[^0-9]{0,20}(\d{4,})", RegexOptions.IgnoreCase | RegexOptions.Compiled)
    };

    private readonly SqliteStore _store;
    private readonly AppSettingsService _settings;
    private readonly ILogger<ContextSessionManager> _logger;
    private int? _learnedWindowTokens;

    public ContextSessionManager(SqliteStore store, AppSettingsService settings, ILogger<ContextSessionManager> logger)
    {
        _store = store;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// 計算單一 Session 的 Token 上限：
    /// 若設定檔明確指定 MaxTokensPerSession 則優先採用；否則依 (有效 ContextWindow × SessionRotateRatio) 計算（最低保護 500）。
    /// </summary>
    private int MaxTokensPerSession
    {
        get
        {
            var llm = _settings.Current.Llm;
            if (llm.MaxTokensPerSession is int explicitCap && explicitCap > 0)
                return Math.Max(500, explicitCap);

            var configuredWindow = Math.Max(1000, llm.ContextWindowTokens);
            var effectiveWindow = _learnedWindowTokens is int lw && lw > 0
                ? Math.Min(lw, configuredWindow)
                : configuredWindow;
            var ratio = Math.Clamp(llm.SessionRotateRatio, 0.10, 0.98);
            return Math.Max(500, (int)Math.Round(effectiveWindow * ratio));
        }
    }

    /// <summary>
    /// 嘗試從 API 溢位錯誤訊息中解析真實 Context Window 並記錄，讓後續自動依百分比縮放上限。
    /// </summary>
    public bool TryLearnWindowFromError(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
            return false;

        foreach (var re in OverflowWindowPatterns)
        {
            var m = re.Match(errorMessage);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var parsed) && parsed >= 1000)
            {
                _learnedWindowTokens = parsed;
                _logger.LogWarning("Learned smaller context window from error: {Window} tokens (limit={Limit})", parsed, MaxTokensPerSession);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 取得目前啟用中的 Session；若已超額或強制換新，則關閉舊 Session 並將交接摘要繼承到新 Session。
    /// </summary>
    public async Task<AgentSessionRecord> GetOrRotateSessionAsync(
        string conversationId,
        string roleName,
        bool forceNewSession = false,
        SessionHandoff? handoffIfRotating = null,
        CancellationToken ct = default)
    {
        var current = await _store.GetActiveSessionAsync(conversationId, roleName, ct);
        if (current is not null && !forceNewSession && current.EstimatedTokens < MaxTokensPerSession)
        {
            return current;
        }

        if (current is not null)
        {
            current.IsActive = false;
            if (handoffIfRotating is not null)
            {
                current.HandoffJson = JsonSerializer.Serialize(handoffIfRotating);
            }
            await _store.SaveSessionAsync(current, ct);
            _logger.LogInformation(
                "Rotated {Role} session {OldId} (tokens={Tokens}/{Max}) for conversation {Conv}",
                roleName, current.SessionId, current.EstimatedTokens, MaxTokensPerSession, conversationId);
        }

        // 若未提供新的交接摘要，沿用上一輪留下的 HandoffJson 以維持任務脈絡
        var next = new AgentSessionRecord
        {
            ConversationId = conversationId,
            RoleName = roleName,
            EstimatedTokens = 0,
            IsActive = true,
            HandoffJson = handoffIfRotating is not null
                ? JsonSerializer.Serialize(handoffIfRotating)
                : current?.HandoffJson ?? "{}"
        };
        await _store.SaveSessionAsync(next, ct);
        return next;
    }

    /// <summary>
    /// 累加本次模型呼叫估算的 Token 數，並可選擇同時更新交接摘要。
    /// </summary>
    public async Task RecordTokensAsync(AgentSessionRecord session, int addedTokens, SessionHandoff? handoff = null, CancellationToken ct = default)
    {
        session.EstimatedTokens += addedTokens;
        if (handoff is not null)
        {
            session.HandoffJson = JsonSerializer.Serialize(handoff);
        }
        await _store.SaveSessionAsync(session, ct);
    }

    /// <summary>
    /// 在任務完成時結束該 Session，並寫入最終交接摘要供後續階段參考。
    /// </summary>
    public async Task CompleteTaskSessionAsync(AgentSessionRecord session, SessionHandoff handoff, CancellationToken ct = default)
    {
        session.IsActive = false;
        session.HandoffJson = JsonSerializer.Serialize(handoff);
        await _store.SaveSessionAsync(session, ct);
    }
}
