namespace AiSecretary.Desktop.Models;

/// <summary>
/// Centralized strongly-typed application settings bound from appsettings.json and user_settings.json.
/// </summary>
public sealed class AppSettings
{
    public LlmRuntimeSettings Llm { get; set; } = new();
    public CustomSkillSettings CustomSkill { get; set; } = new();
    public TtsSettings Tts { get; set; } = new();
    public SchedulerSettings Scheduler { get; set; } = new();
}

public sealed class LlmRuntimeSettings
{
    public int ContextWindowTokens { get; set; } = 128000;
    public double SessionRotateRatio { get; set; } = 0.25;
    public int? MaxTokensPerSession { get; set; }
    public string AnthropicVersion { get; set; } = "2023-06-01";
    public string ReplyLanguage { get; set; } = "繁體中文";
}

public sealed class CustomSkillSettings
{
    public int MaxArgLength { get; set; } = 500;
    public int TimeoutSeconds { get; set; } = 60;
    public string ShellExecutable { get; set; } = "powershell.exe";
}

public sealed class SchedulerSettings
{
    public int TickSeconds { get; set; } = 15;
    public int ProactiveIdleMinutes { get; set; } = 20;
    public int ProactiveIdleMinMinutes { get; set; } = 12;
    public int ProactiveIdleMaxMinutes { get; set; } = 30;
    public int ProactiveBackoffMaxMinutes { get; set; } = 60;
    public bool ProactiveEnabledWithStyle { get; set; } = true;
    public bool AfkSilenceEnabled { get; set; } = true;
    public int AfkSilenceMinutes { get; set; } = 15;
    public bool AfkWelcomeBackEnabled { get; set; } = true;
    public int AfkWelcomeBackMinMinutes { get; set; } = 25;
    public int AfkAudioWelcomeBackMinutes { get; set; } = 60;
}

public sealed class TtsSettings
{
    public bool Enabled { get; set; } = true;
    public bool AutoStartServer { get; set; } = true;
    public string SoVitsRoot { get; set; } = "";
    public string ChatMode { get; set; } = "smart";
    public string DefaultProfile { get; set; } = "default";
    public bool ReloadWeightsOnConnect { get; set; } = false;
    public string ApiBaseUrl { get; set; } = "http://127.0.0.1:9880";
    public string GptWeightsPath { get; set; } = "";
    public string SovitsWeightsPath { get; set; } = "";
    public string RefAudioPath { get; set; } = "";
    public string PromptText { get; set; } = "";
    public string[] AuxRefAudioPaths { get; set; } = Array.Empty<string>();
    public string PromptLang { get; set; } = "zh";
    public string TextLang { get; set; } = "zh";
    public string TextSplitMethod { get; set; } = "cut3";
    public double FragmentInterval { get; set; } = 0.35;
    public int TopK { get; set; } = 4;
    public double TopP { get; set; } = 0.85;
    public double Temperature { get; set; } = 0.7;
    public double SpeedFactor { get; set; } = 1.0;
    public string? VoiceCue { get; set; }
    public string? SpeechPrompt { get; set; }
    public Dictionary<string, string> PronunciationMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, TtsVoiceProfileSettings> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class TtsVoiceProfileSettings
{
    public string? DisplayName { get; set; }
    public string? StylePromptPath { get; set; }
    public string? GptWeightsPath { get; set; }
    public string? SovitsWeightsPath { get; set; }
    public string? RefAudioPath { get; set; }
    public string? PromptText { get; set; }
    public string[]? AuxRefAudioPaths { get; set; }
    public string? PromptLang { get; set; }
    public string? TextLang { get; set; }
    public string? VoiceCue { get; set; }
    public string? SpeechPrompt { get; set; }
    public string? TestPhrase { get; set; }
    public Dictionary<string, string>? PronunciationMap { get; set; }
}
