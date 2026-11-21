using System.Text.RegularExpressions;

namespace AiSecretary.Desktop.Services;

public sealed record DeixisDetectionResult(
    bool IsDeictic,
    string Reason,
    string? MatchedCue = null);

/// <summary>
/// Pure deterministic, zero-LLM detector for screen/window deictic references
/// ("這個", "這頁", "這段", "this", "that", objectless "幫我看一下" / "翻譯一下").
/// Strictly favors precision over recall (prefer missing a borderline case over false-triggering).
/// </summary>
public static class DeicticDetector
{
    private static readonly Regex AttachmentRegex = new(@"\[Attachment:", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UrlOrPathRegex = new(@"(https?://\S+|www\.\S+|[A-Za-z]:\\[^\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex InlineQuotedTextRegex = new(@"(「[^」]{4,}」|『[^』]{4,}』|""[^""]{4,}""|'[^']{4,}'|`[^`]{4,}`)", RegexOptions.Compiled);
    private static readonly Regex MultilinePayloadRegex = new(@"[\r\n].{12,}", RegexOptions.Singleline | RegexOptions.Compiled);

    // Explicit non-screen domain keywords that indicate a specific tool/domain rather than the active external window.
    private static readonly Regex DomainExclusionRegex = new(
        @"(行事曆|行程|日程|會議|約會|排程|提醒|鬧鐘|待辦|信箱|收件匣|新信|未讀信|未讀郵件|寄件|天氣|氣溫|降雨|幾點|星期幾|禮拜幾|記憶|偏好|監控|資料夾|\b(?:calendar|schedule|reminder|reminders|meeting|meetings|todo|inbox|gmail|outlook|weather|watcher|folder)\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Temporal / filler idioms using "這個" / "this" / "that" that do NOT point to the screen.
    private static readonly Regex NonDeicticIdiomRegex = new(
        @"(這個(?:月|禮拜|星期|週|週末|季度|季節|年代|時代|時間)|這(?:禮拜|星期|週|個月|幾天)|(?:this\s+(?:week|month|year|morning|afternoon|evening|night|weekend|time|monday|tuesday|wednesday|thursday|friday|saturday|sunday))|(?:that'?s\s+(?:great|fine|ok|okay|cool|all|good|nice|awesome|right|true|enough))|(?:thanks?\s+for\s+that))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Positive Cue 1: explicit Chinese or English deictic terms pointing to current screen/page/selection.
    private static readonly Regex ChineseDeicticCueRegex = new(
        @"(這個|這頁|這篇|這段|這封|這裡|這份|這張|這行|這句|這題|這東西|上面|螢幕上|畫面上|眼前|目前這|現在這)",
        RegexOptions.Compiled);

    private static readonly Regex EnglishDeicticCueRegex = new(
        @"\b(this|that|here)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Positive Cue 2: imperative action verbs with NO explicit target object after them.
    private static readonly Regex ChineseObjectlessActionRegex = new(
        @"^(?:請)?(?:幫我|幫忙|幫)?(?:看一下|看看|看|總結一下|總結|摘要一下|摘要|翻譯一下|翻譯|解釋一下|解釋|整理一下|整理|分析一下|分析|檢查一下|檢查|讀一下|讀|填一下|填寫一下|填)(?:吧|喔|哦|嗎|一下|好了|啦|呢)?[？?！!。.,，\s]*$",
        RegexOptions.Compiled);

    private static readonly Regex EnglishObjectlessActionRegex = new(
        @"^(?:please\s+)?(?:can\s+you\s+)?(?:check|look|summarize|summary|translate|explain|analyze|review|read|fill)(?:\s+(?:it|out))?(?:\s+please)?[\s?!.]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 90-second implicit user correction detector (for PredictionLogs telemetry).
    private static readonly Regex UserCorrectionRegex = new(
        @"(不是這個|不是這頁|不是這篇|不是這段|不是那個|不是那頁|不是要看|不要看螢幕|不要看網頁|我是說|我是指|搞錯|猜錯|看錯|說錯了|不對，我是|wrong\s+(?:page|window|tab|one)|not\s+that|i\s+meant|no,\s*i\s+mean)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static DeixisDetectionResult Detect(string? userPrompt, string? targetMailId = null)
    {
        var text = (userPrompt ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text))
            return new DeixisDetectionResult(false, "empty");

        if (!string.IsNullOrWhiteSpace(targetMailId))
            return new DeixisDetectionResult(false, "has_target_mail_id");

        if (AttachmentRegex.IsMatch(text))
            return new DeixisDetectionResult(false, "has_attachment");

        if (UrlOrPathRegex.IsMatch(text))
            return new DeixisDetectionResult(false, "has_url_or_path");

        if (InlineQuotedTextRegex.IsMatch(text) || MultilinePayloadRegex.IsMatch(text))
            return new DeixisDetectionResult(false, "has_inline_quoted_or_pasted_text");

        if (DomainExclusionRegex.IsMatch(text))
            return new DeixisDetectionResult(false, "explicit_domain_keyword");

        // Strip known non-deictic time/filler idioms before checking explicit deictic cues
        var scrubbed = NonDeicticIdiomRegex.Replace(text, " ").Trim();

        var zhCue = ChineseDeicticCueRegex.Match(scrubbed);
        if (zhCue.Success)
            return new DeixisDetectionResult(true, "chinese_deictic_cue", zhCue.Value);

        var enCue = EnglishDeicticCueRegex.Match(scrubbed);
        if (enCue.Success)
            return new DeixisDetectionResult(true, "english_deictic_cue", enCue.Value.ToLowerInvariant());

        if (ChineseObjectlessActionRegex.IsMatch(text))
            return new DeixisDetectionResult(true, "chinese_objectless_action", text);

        if (EnglishObjectlessActionRegex.IsMatch(text))
            return new DeixisDetectionResult(true, "english_objectless_action", text.ToLowerInvariant());

        return new DeixisDetectionResult(false, "no_deictic_signal");
    }

    public static bool IsUserCorrectionSignal(string? userPrompt)
    {
        var text = (userPrompt ?? "").Trim();
        return !string.IsNullOrWhiteSpace(text) && UserCorrectionRegex.IsMatch(text);
    }
}
