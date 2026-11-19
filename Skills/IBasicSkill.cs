using System.Text.Json;
using AiSecretary.Desktop.Models;

namespace AiSecretary.Desktop.Skills;

/// <summary>
/// 單步技能執行所需的上下文與參數輔助工具。
/// </summary>
public sealed class SkillExecutionContext
{
    public string SkillName { get; init; } = "";
    public required string ConversationId { get; init; }
    public required string UserPrompt { get; init; }
    public required JsonElement Args { get; init; }
    public string? TargetMailId { get; init; }
    public required Func<string, string, Task<bool>> ConfirmAsync { get; init; }
    public required Func<string, Task> OnStatusAsync { get; init; }
    public Func<string, string, object?[], Task<string>>? LocalizeAsync { get; init; }
    public required List<MailSourceRef> Sources { get; init; }
    public required List<string> Observations { get; init; }
    public required List<string> CompletedSteps { get; init; }
    public CancellationToken Ct { get; init; }

    /// <summary>依目前 ReplyLanguage 非同步取得本地化介面／提示文字。</summary>
    public Task<string> TextAsync(string key, string englishTemplate, params object?[] args)
        => LocalizeAsync is not null
            ? LocalizeAsync(key, englishTemplate, args)
            : Task.FromResult(args.Length == 0 ? englishTemplate : string.Format(englishTemplate, args));

    /// <summary>取得由 SQLite 傳入的執行期技能名稱（若未設定則退回技能預設名稱）。</summary>
    public string EffectiveName(string fallback)
        => string.IsNullOrWhiteSpace(SkillName) ? fallback : SkillName;

    /// <summary>安全讀取 JSON 物件中的單一字串或純量參數。</summary>
    public string? GetArg(string name)
    {
        if (Args.ValueKind != JsonValueKind.Object || !Args.TryGetProperty(name, out var val))
            return null;
        return val.ValueKind switch
        {
            JsonValueKind.String => val.GetString(),
            JsonValueKind.Number => val.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    /// <summary>安全讀取 JSON 物件中的字串陣列參數（相容逗號或空白分隔字串）。</summary>
    public List<string> GetStringArrayArg(string name)
    {
        var list = new List<string>();
        if (Args.ValueKind != JsonValueKind.Object || !Args.TryGetProperty(name, out var val))
            return list;
        if (val.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in val.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        list.Add(s.Trim());
                }
            }
        }
        else if (val.ValueKind == JsonValueKind.String)
        {
            var raw = val.GetString();
            if (!string.IsNullOrWhiteSpace(raw))
                list.AddRange(raw.Split(new[] { ',', '、', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        return list;
    }
}

/// <summary>
/// 內建技能的統一介面契約，由 <see cref="SkillRegistry"/> 於啟動時自動掃描並同步至 SQLite 技能目錄。
/// </summary>
public interface IBasicSkill
{
    string Name { get; }
    string DisplayDescription { get; }
    string MainAgentSpec { get; }
    string PlannerSpec { get; }
    string StatusText => $"Running {DisplayDescription}...";
    string? Capability => null;
    bool IsSilentPoller => false;
    bool IsDangerousByDefault => false;
    Task ExecuteAsync(SkillExecutionContext ctx);
}
