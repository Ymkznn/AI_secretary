namespace AiSecretary.Desktop.Prompts;

/// <summary>
/// 集中管理 <see cref="Services.TtsService"/> 的語音台詞轉換提示詞。
/// </summary>
public static class TtsPrompts
{
    public static string BuildSpeechPrompt(string lang, bool fullTranslation)
    {
        var jaRule = lang == "ja"
            ? " Convert ALL English words/acronyms into katakana (e.g., dataset->データセット, Colab->コラボ, Gmail->ジーメール, CSV->シーエスブイ, AI->エーアイ) and never output Latin letters."
            : "";
        return fullTranslation
            ? $"Translate or rewrite the reply into natural spoken text in language '{lang}'. Strip all symbols, Markdown, and URLs.{jaRule} Output only the spoken line."
            : $"Summarize the reply or notification into one short natural spoken sentence (under 20 words / 35 chars) in language '{lang}'. Strip all symbols, Markdown, and URLs.{jaRule} Output only the spoken line.";
    }
}
