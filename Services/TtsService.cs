using System.Diagnostics;
using System.Media;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Prompts;
using Microsoft.Extensions.Configuration;

namespace AiSecretary.Desktop.Services;

/// <summary>
/// Synthesizes Japanese speech via local GPT-SoVITS api_v2.py with smart chat/summary mode and sentence pipelining.
/// </summary>
public sealed class TtsService : IDisposable
{
    private readonly HttpClient _http;
    private readonly IModelGateway _model;
    private readonly AppSettingsService _settings;
    private readonly StyleService _styles;
    private readonly UiLocalizer _localizer;
    private readonly ILogger<TtsService> _logger;
    private readonly Channel<(string Title, string Content, bool IsDirectJapanese, SpeechKind Kind, Action? OnBeforePlay)> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _workerTask;
    private bool _weightsInitialized;
    private bool _pendingWeightReload;
    private bool _disposed;
    private bool? _enabledOverride;
    private string? _chatModeOverride;
    private string _voiceProfile;
    private string _loadedWeightGroup;
    private readonly Dictionary<string, string?> _cachedStylePrompts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _cachedStyleLangs = new(StringComparer.OrdinalIgnoreCase);
    private Process? _serverProcess;

    public TtsService(
        IHttpClientFactory httpFactory,
        IModelGateway model,
        AppSettingsService settings,
        StyleService styles,
        UiLocalizer localizer,
        ILogger<TtsService> logger)
    {
        _http = httpFactory.CreateClient("TtsClient");
        _http.Timeout = TimeSpan.FromSeconds(90);
        _model = model;
        _settings = settings;
        _styles = styles;
        _localizer = localizer;
        _logger = logger;
        var defProf = settings.Current.Tts.DefaultProfile;
        _voiceProfile = string.IsNullOrWhiteSpace(defProf) ? "default" : defProf.Trim().ToLowerInvariant();
        _loadedWeightGroup = GetWeightGroup(_voiceProfile);

        _queue = Channel.CreateBounded<(string, string, bool, SpeechKind, Action?)>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });

        _workerTask = Task.Run(() => ProcessQueueAsync(_cts.Token));
    }

    public bool IsEnabled => _enabledOverride ?? _settings.Current.Tts.Enabled;

    public string ChatMode => (_chatModeOverride ?? _settings.Current.Tts.ChatMode ?? "smart").Trim().ToLowerInvariant();

    public string VoiceProfile => _voiceProfile;

    public string TargetLang
    {
        get
        {
            if (!_cachedStyleLangs.TryGetValue(_voiceProfile, out var profLang))
            {
                var path = GetActiveProfileSettings()?.StylePromptPath
                           ?? FindVoiceAsset(GetWeightGroup(_voiceProfile), "*.md");
                profLang = !string.IsNullOrWhiteSpace(path) ? StyleService.ParseTtsLanguageFromMd(path) : null;
                _cachedStyleLangs[_voiceProfile] = profLang;
            }

            var fromMd = profLang ?? _styles.GetActiveTtsLanguage();
            if (!string.IsNullOrWhiteSpace(fromMd))
                return fromMd.Trim().ToLowerInvariant();

            var fallback = GetProfileSetting("TextLang", _settings.Current.Tts.TextLang);
            return string.IsNullOrWhiteSpace(fallback) ? "ja" : fallback.Trim().ToLowerInvariant();
        }
    }

    public IReadOnlyList<(string Key, string DisplayName)> GetAvailableVoiceProfiles()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _settings.Current.Tts.Profiles)
            result[kv.Key] = string.IsNullOrWhiteSpace(kv.Value.DisplayName) ? kv.Key : kv.Value.DisplayName!;

        foreach (var root in new[]
        {
            Path.Combine(AppPaths.UserDataRoot, "tts", "voices"),
            Path.Combine(AppContext.BaseDirectory, "tts", "voices")
        })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root))
            {
                var key = Path.GetFileName(dir).ToLowerInvariant();
                if (result.ContainsKey(key)) continue;

                var displayName = key;
                var mdFile = Directory.GetFiles(dir, "*.md").FirstOrDefault();
                if (mdFile is not null)
                {
                    try
                    {
                        var firstLine = File.ReadLines(mdFile, Encoding.UTF8).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();
                        if (!string.IsNullOrWhiteSpace(firstLine) && firstLine.StartsWith('#'))
                        {
                            var clean = firstLine.TrimStart('#', ' ').Split('（', '(')[0].Trim();
                            if (!string.IsNullOrWhiteSpace(clean))
                                displayName = clean;
                        }
                    }
                    catch { }
                }
                result[key] = displayName;
            }
        }

        if (result.Count == 0)
            return new[] { ("default", _localizer.TextCached("TtsDefaultVoice", "Default Voice")) };

        return result.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    public string GetTestPhrase()
    {
        var custom = GetActiveProfileSettings()?.TestPhrase ?? _styles.GetActiveTtsTestPhrase();
        if (!string.IsNullOrWhiteSpace(custom))
            return custom;

        return _localizer.TextCached("TtsTestPhrase", "Voice test completed. Ready whenever you are.");
    }

    private TtsVoiceProfileSettings? GetActiveProfileSettings()
    {
        var profiles = _settings.Current.Tts.Profiles;
        if (profiles.TryGetValue(_voiceProfile, out var p)) return p;
        var group = GetWeightGroup(_voiceProfile);
        return profiles.TryGetValue(group, out var g) ? g : null;
    }

    public string? GetActiveStylePrompt()
    {
        if (!IsEnabled) return null;

        if (_cachedStylePrompts.TryGetValue(_voiceProfile, out var cached))
            return cached;

        var path = GetActiveProfileSettings()?.StylePromptPath
                   ?? FindVoiceAsset(GetWeightGroup(_voiceProfile), "*.md");
        var loaded = !string.IsNullOrWhiteSpace(path) ? LoadStylePromptFromMd(path) : null;
        _cachedStylePrompts[_voiceProfile] = loaded;
        return loaded;
    }

    private static string? LoadStylePromptFromMd(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var md = File.ReadAllText(path, Encoding.UTF8);
            var m = Regex.Match(md, @"```(?:text)?\s*\r?\n([\s\S]*?)```", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : md.Trim();
        }
        catch
        {
            return null;
        }
    }

    public void EnsureServerStartedInBackground()
    {
        var tts = _settings.Current.Tts;
        if (!IsEnabled || !tts.AutoStartServer)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                var baseUrl = (tts.ApiBaseUrl ?? "http://127.0.0.1:9880").TrimEnd('/');
                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
                    return;

                if (await IsPortOpenAsync(uri.Host, uri.Port))
                {
                    _logger.LogInformation("GPT-SoVITS already listening on {Host}:{Port}", uri.Host, uri.Port);
                    return;
                }

                var (root, python, apiScript) = ResolveSoVitsServer();
                if (root is null || python is null || apiScript is null)
                {
                    _logger.LogWarning("GPT-SoVITS not found in configured or bundled tts/ directories; skipping auto-start.");
                    return;
                }

                var group = GetWeightGroup(_voiceProfile);
                var yamlName = $"tts_infer_{group}.yaml";
                var yamlPath = new[]
                {
                    Path.Combine(AppPaths.UserDataRoot, "tts", yamlName),
                    Path.Combine(AppContext.BaseDirectory, yamlName),
                    Path.Combine(Directory.GetCurrentDirectory(), yamlName),
                    Path.Combine(root, "GPT_SoVITS", "configs", "tts_infer.yaml")
                }.FirstOrDefault(File.Exists);

                if (yamlPath is null)
                {
                    _logger.LogWarning("TTS config {Yaml} not found; skipping auto-start.", yamlName);
                    return;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = python,
                    Arguments = $"\"{apiScript}\" -a {uri.Host} -p {uri.Port} -c \"{yamlPath}\"",
                    WorkingDirectory = root,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.Environment["PYTHONUTF8"] = "1";
                psi.Environment["PYTHONIOENCODING"] = "utf-8";
                // Cap OpenMP/MKL threads so PyTorch inference doesn't spike all CPU cores while gaming.
                psi.Environment.TryAdd("OMP_NUM_THREADS", "2");
                psi.Environment.TryAdd("MKL_NUM_THREADS", "2");

                _serverProcess = Process.Start(psi);
                if (_serverProcess is not null)
                {
                    try { _serverProcess.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                    _loadedWeightGroup = group;
                    _pendingWeightReload = false;
                    _weightsInitialized = true;
                    _serverProcess.BeginOutputReadLine();
                    _serverProcess.BeginErrorReadLine();
                    _logger.LogInformation("Auto-started GPT-SoVITS server (PID {Pid}, voice={Group})", _serverProcess.Id, group);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to auto-start GPT-SoVITS server.");
            }
        });
    }

    private static async Task<bool> IsPortOpenAsync(string host, int port)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(250);
            await tcp.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Configure(bool enabled, string voiceProfile, string chatMode)
    {
        _enabledOverride = enabled;
        _chatModeOverride = string.IsNullOrWhiteSpace(chatMode) ? "smart" : chatMode.Trim().ToLowerInvariant();

        var defProf = _settings.Current.Tts.DefaultProfile;
        var defaultProfile = string.IsNullOrWhiteSpace(defProf) ? "default" : defProf.Trim().ToLowerInvariant();
        var nextProfile = string.IsNullOrWhiteSpace(voiceProfile) ? defaultProfile : voiceProfile.Trim().ToLowerInvariant();
        var nextGroup = GetWeightGroup(nextProfile);
        if (!string.Equals(_loadedWeightGroup, nextGroup, StringComparison.Ordinal))
        {
            _pendingWeightReload = true;
            _weightsInitialized = false;
        }
        _voiceProfile = nextProfile;
    }

    private static string GetWeightGroup(string profile)
    {
        if (string.IsNullOrWhiteSpace(profile)) return "default";
        var idx = profile.IndexOf('_');
        return idx > 0 ? profile[..idx] : profile;
    }

    public void EnqueueSummarySpeech(string title, string content, Action? onBeforePlay = null, SpeechKind kind = SpeechKind.Notification)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(content) ||
            (ChatMode == "notify_only" &&
             kind is SpeechKind.ChatReply or SpeechKind.Report or SpeechKind.Status))
        {
            SafeInvoke(onBeforePlay);
            return;
        }

        if (!_queue.Writer.TryWrite((title ?? "", content, false, kind, onBeforePlay)))
            SafeInvoke(onBeforePlay);
    }

    public void EnqueueJapaneseSpeech(string japaneseText, Action? onBeforePlay = null)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(japaneseText))
        {
            SafeInvoke(onBeforePlay);
            return;
        }

        if (!_queue.Writer.TryWrite(("", japaneseText, true, SpeechKind.ChatReply, onBeforePlay)))
            SafeInvoke(onBeforePlay);
    }

    private static void SafeInvoke(Action? action)
    {
        if (action is null) return;
        try { action(); } catch { }
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (title, content, isDirect, kind, onBeforePlay) in _queue.Reader.ReadAllAsync(ct))
            {
                bool revealed = false;
                void RevealOnce()
                {
                    if (revealed) return;
                    revealed = true;
                    SafeInvoke(onBeforePlay);
                }

                try
                {
                    var sw = Stopwatch.StartNew();
                    var speechLang = TargetLang;
                    var jaText = isDirect
                        ? CleanSpokenText(content, ChatMode == "full" ? 800 : 240, speechLang == "ja", GetPronunciationMap())
                        : await ToSpeechTextAsync(title, content, kind, ct);
                    var llmMs = sw.ElapsedMilliseconds;

                    if (string.IsNullOrWhiteSpace(jaText))
                    {
                        RevealOnce();
                        continue;
                    }

                    await SynthesizeAndPlayPipelinedAsync(jaText, ct, RevealOnce);
                    _logger.LogInformation("TTS done (isDirect={IsDirect}, llm={LlmMs}ms, total={TotalMs}ms): {Text}",
                        isDirect, llmMs, sw.ElapsedMilliseconds, jaText);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    RevealOnce();
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "TTS synthesis/playback skipped.");
                }
                finally
                {
                    RevealOnce();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private string GetProfileSetting(string key, string fallback = "")
    {
        var tts = _settings.Current.Tts;
        var prof = GetActiveProfileSettings();
        return key switch
        {
            "PromptLang" => prof?.PromptLang ?? tts.PromptLang,
            "TextLang" => prof?.TextLang ?? tts.TextLang,
            "VoiceCue" => prof?.VoiceCue ?? tts.VoiceCue ?? fallback,
            "SpeechPrompt" => prof?.SpeechPrompt ?? tts.SpeechPrompt ?? fallback,
            _ => fallback
        };
    }

    private async Task<string> ToSpeechTextAsync(string title, string content, SpeechKind kind, CancellationToken ct)
    {
        var lang = TargetLang;
        var isChatReply = kind == SpeechKind.ChatReply;
        var mode = ChatMode;
        bool fullTranslation = mode == "full" ||
            (mode == "smart" && isChatReply && IsShortPlainReply(content));

        var trimmed = content.Length > 600 ? content[..600] : content;
        var customPrompt = GetProfileSetting("SpeechPrompt");
        var basePrompt = string.IsNullOrWhiteSpace(customPrompt)
            ? TtsPrompts.BuildSpeechPrompt(lang, fullTranslation)
            : customPrompt;

        var voiceCue = GetProfileSetting("VoiceCue");
        var sysPrompt = string.IsNullOrWhiteSpace(voiceCue) ? basePrompt : $"{voiceCue} {basePrompt}";

        var userPrompt = string.IsNullOrWhiteSpace(title)
            ? trimmed
            : $"Title/Type: {title}\nContent:\n{trimmed}";

        try
        {
            var resp = await _model.CompleteAsync(
                new[]
                {
                    new ModelMessage("system", sysPrompt),
                    new ModelMessage("user", userPrompt)
                },
                ModelTier.Simple,
                ct);

            var speechLang = TargetLang;
            var cleaned = CleanSpokenText(resp.Content, fullTranslation ? 240 : 90, lang == "ja", GetPronunciationMap());
            if (!string.IsNullOrWhiteSpace(cleaned))
                return cleaned;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to convert TTS text via LLM; using fallback phrase.");
        }

        return FallbackPhrase(title, content, lang);
    }

    private static bool IsShortPlainReply(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > 120)
            return false;
        if (content.Contains("```", StringComparison.Ordinal) ||
            content.Contains("|", StringComparison.Ordinal) ||
            content.Contains("\n-", StringComparison.Ordinal) ||
            content.Contains("\n*", StringComparison.Ordinal) ||
            content.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("https://", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private string FallbackPhrase(string title, string content, string lang)
    {
        var combined = $"{title} {content}";
        bool isMail = combined.Contains("mail", StringComparison.OrdinalIgnoreCase) ||
                      combined.Contains(_localizer.TextCached("TtsKeywordMail", "email"), StringComparison.OrdinalIgnoreCase);
        bool isSchedule = combined.Contains("schedule", StringComparison.OrdinalIgnoreCase) ||
                          combined.Contains("reminder", StringComparison.OrdinalIgnoreCase) ||
                          combined.Contains("meeting", StringComparison.OrdinalIgnoreCase) ||
                          combined.Contains(_localizer.TextCached("TtsKeywordSchedule", "reminder"), StringComparison.OrdinalIgnoreCase);

        var kind = isMail ? "mail" : isSchedule ? "schedule" : "done";
        var mdFallback = _styles.GetActiveTtsFallbackPhrase(kind);
        if (!string.IsNullOrWhiteSpace(mdFallback))
            return mdFallback;

        if (lang == "en")
        {
            if (isMail) return "You have a new email notification.";
            if (isSchedule) return "You have an upcoming schedule reminder.";
            return "Done. Please check the screen.";
        }
        if (isMail) return _localizer.TextCached("TtsFallbackMail", "You have a new email notification.");
        if (isSchedule) return _localizer.TextCached("TtsFallbackSchedule", "You have an upcoming schedule reminder.");
        return _localizer.TextCached("TtsFallbackDone", "Done. Please check the screen.");
    }

    private Dictionary<string, string> GetPronunciationMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tts = _settings.Current.Tts;
        foreach (var (k, v) in tts.PronunciationMap)
            map[k] = v;

        var group = GetWeightGroup(_voiceProfile);
        if (tts.Profiles.TryGetValue(group, out var g) && g.PronunciationMap is not null)
        {
            foreach (var (k, v) in g.PronunciationMap)
                map[k] = v;
        }
        if (tts.Profiles.TryGetValue(_voiceProfile, out var p) && p.PronunciationMap is not null)
        {
            foreach (var (k, v) in p.PronunciationMap)
                map[k] = v;
        }
        return map;
    }

    private static string CleanSpokenText(
        string raw,
        int maxChars,
        bool convertEnglishToKatakana = false,
        IReadOnlyDictionary<string, string>? pronunciationMap = null)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var text = Regex.Replace(raw, @"https?://\S+", "");
        text = Regex.Replace(text, @"[`*_#>\[\]()「」『』""'~～♪★☆♡♥✨🔥⚠📌📅❓🔔⏰]", "");
        // Strip remaining surrogate-pair emojis / symbols that can break GPT-SoVITS G2P and cause vocoder noise.
        text = Regex.Replace(text, @"[\uD800-\uDBFF][\uDC00-\uDFFF]", "");
        if (pronunciationMap is { Count: > 0 })
        {
            var keys = pronunciationMap.Keys
                .Where(k => !string.IsNullOrEmpty(k))
                .OrderByDescending(k => k.Length)
                .ToList();
            if (keys.Count > 0)
            {
                var pattern = string.Join("|", keys.Select(Regex.Escape));
                text = Regex.Replace(text, pattern, m => pronunciationMap.TryGetValue(m.Value, out var rep) ? rep : m.Value, RegexOptions.IgnoreCase);
            }
        }
        if (convertEnglishToKatakana)
        {
            text = Regex.Replace(text, @"ね([。！!])", "ねっ$1");
            text = Regex.Replace(text, @"\bdatasets?\b", "データセット", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\bcolab\b", "コラボ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\bgmail\b", "ジーメール", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\bcsv\b", "シーエスブイ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\bpdf\b", "ピーディーエフ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\btts\b", "ティーティーエス", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\bai\b", "エーアイ", RegexOptions.IgnoreCase);
        }
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length > maxChars)
            text = text[..maxChars];
        return text;
    }

    private static List<string> SplitSentences(string text)
    {
        var rawParts = text.Length <= 60
            ? new List<string> { text }
            : Regex.Split(text, @"(?<=[。！？!?])")
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

        // Filter out punctuation-only fragments and merge tiny fragments (<= 3 chars) so GPT-SoVITS never hits EOS-loop static noise.
        var merged = new List<string>();
        foreach (var part in rawParts)
        {
            if (!Regex.IsMatch(part, @"[\p{L}\p{N}]"))
                continue;

            if (merged.Count > 0 && part.Length <= 3)
                merged[^1] = merged[^1] + part;
            else
                merged.Add(part);
        }

        if (merged.Count == 0 && Regex.IsMatch(text, @"[\p{L}\p{N}]"))
            merged.Add(text);
        return merged;
    }

    public async Task SynthesizeAndPlayPipelinedAsync(string japaneseText, CancellationToken ct, Action? onBeforeFirstPlay = null)
    {
        var baseUrl = (_settings.Current.Tts.ApiBaseUrl ?? "http://127.0.0.1:9880").TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https"))
        {
            _logger.LogWarning("Invalid Tts:ApiBaseUrl: {Url}", baseUrl);
            SafeInvoke(onBeforeFirstPlay);
            return;
        }

        await EnsureWeightsLoadedIfRequestedAsync(baseUrl, ct);

        var sentences = SplitSentences(japaneseText);
        Task<byte[]?>? nextTask = SynthesizeWavAsync(baseUrl, sentences[0], ct);

        for (int i = 0; i < sentences.Count; i++)
        {
            if (nextTask is null) break;
            var wavBytes = await nextTask;
            nextTask = (i + 1 < sentences.Count)
                ? SynthesizeWavAsync(baseUrl, sentences[i + 1], ct)
                : null;

            if (i == 0)
                SafeInvoke(onBeforeFirstPlay);

            if (wavBytes is { Length: >= 44 })
            {
                await Task.Run(() =>
                {
                    using var ms = new MemoryStream(wavBytes);
                    using var player = new SoundPlayer(ms);
                    player.PlaySync();
                }, ct);
            }
        }

        SafeInvoke(onBeforeFirstPlay);
    }

    private (string? Root, string? Python, string? ApiScript) ResolveSoVitsServer()
    {
        var candidates = new[]
        {
            _settings.Current.Tts.SoVitsRoot,
            Path.Combine(AppContext.BaseDirectory, "tts", "GPT-SoVITS"),
            Path.Combine(AppPaths.UserDataRoot, "tts", "GPT-SoVITS")
        }
        .Where(p => !string.IsNullOrWhiteSpace(p))
        .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in candidates)
        {
            var api = Path.Combine(root!, "api_v2.py");
            if (!File.Exists(api)) continue;

            var py = new[]
            {
                Path.Combine(root!, "runtime", "python.exe"),
                Path.Combine(root!, "python", "python.exe"),
                Path.Combine(root!, ".venv", "Scripts", "python.exe")
            }.FirstOrDefault(File.Exists);

            if (py is not null)
                return (root, py, api);
        }

        return (null, null, null);
    }

    private string? FindVoiceAsset(string group, string pattern)
    {
        var dirs = new[]
        {
            Path.Combine(AppPaths.UserDataRoot, "tts", "voices", group),
            Path.Combine(AppContext.BaseDirectory, "tts", "voices", group)
        };
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            var match = Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (match is not null) return match;
        }
        return null;
    }

    private (string RefAudio, string PromptText) SelectVoiceStyle()
    {
        var tts = _settings.Current.Tts;
        var prof = GetActiveProfileSettings();
        var group = GetWeightGroup(_voiceProfile);

        var refAudio = prof?.RefAudioPath
                       ?? (string.IsNullOrWhiteSpace(tts.RefAudioPath) ? null : tts.RefAudioPath)
                       ?? FindVoiceAsset(group, "*.wav")
                       ?? "";
        var promptText = prof?.PromptText;
        if (string.IsNullOrWhiteSpace(promptText))
        {
            promptText = tts.PromptText;
            if (string.IsNullOrWhiteSpace(promptText))
            {
                var txtPath = FindVoiceAsset(group, "*.txt");
                if (!string.IsNullOrWhiteSpace(txtPath) && File.Exists(txtPath))
                {
                    try { promptText = File.ReadAllText(txtPath, Encoding.UTF8).Trim(); } catch { }
                }
            }
        }

        return (refAudio, promptText ?? "");
    }

    private async Task<byte[]?> SynthesizeWavAsync(string baseUrl, string sentence, CancellationToken ct)
    {
        var (refAudio, promptText) = SelectVoiceStyle();
        if (string.IsNullOrWhiteSpace(refAudio) || !File.Exists(refAudio))
        {
            _logger.LogDebug("TTS reference audio not configured or missing ({Ref}); skipping synthesis.", refAudio);
            return null;
        }

        var tts = _settings.Current.Tts;
        var prof = GetActiveProfileSettings();
        var promptLang = prof?.PromptLang ?? (string.IsNullOrWhiteSpace(tts.PromptLang) ? TargetLang : TargetLang);
        var textLang = TargetLang;
        var auxRefs = (prof?.AuxRefAudioPaths ?? tts.AuxRefAudioPaths ?? Array.Empty<string>())
            .Where(File.Exists)
            .ToArray();

        var payload = new
        {
            text = sentence,
            text_lang = textLang,
            ref_audio_path = refAudio,
            aux_ref_audio_paths = auxRefs,
            prompt_lang = promptLang,
            prompt_text = promptText,
            text_split_method = tts.TextSplitMethod,
            fragment_interval = tts.FragmentInterval,
            batch_size = 1,
            top_k = tts.TopK,
            top_p = tts.TopP,
            temperature = tts.Temperature,
            speed_factor = tts.SpeedFactor,
            media_type = "wav",
            streaming_mode = false
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/tts")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("GPT-SoVITS /tts returned {Status}: {Body}", (int)resp.StatusCode, err);

            // Self-heal orphaned api_v2.py whose parent stdout/stderr pipe broke after a force-kill.
            if (err.Contains("Errno 22", StringComparison.OrdinalIgnoreCase) &&
                tts.AutoStartServer &&
                await RestartBrokenServerAsync(baseUrl, ct))
            {
                using var retryReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/tts")
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                };
                using var retryResp = await _http.SendAsync(retryReq, HttpCompletionOption.ResponseHeadersRead, ct);
                if (retryResp.IsSuccessStatusCode)
                    return await retryResp.Content.ReadAsByteArrayAsync(ct);
            }
            return null;
        }

        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    private async Task<bool> RestartBrokenServerAsync(string baseUrl, CancellationToken ct)
    {
        try
        {
            KillManagedOrLocalSoVitsServers();

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
                return false;

            await Task.Delay(600, ct);
            EnsureServerStartedInBackground();

            for (int i = 0; i < 40 && !ct.IsCancellationRequested; i++)
            {
                await Task.Delay(500, ct);
                if (await IsPortOpenAsync(uri.Host, uri.Port))
                    return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RestartBrokenServerAsync failed");
        }
        return false;
    }

    private void KillManagedOrLocalSoVitsServers()
    {
        try
        {
            if (_serverProcess is { HasExited: false })
            {
                try { _serverProcess.Kill(entireProcessTree: true); } catch { }
            }
            _serverProcess?.Dispose();
            _serverProcess = null;
        }
        catch { }

        foreach (var p in Process.GetProcessesByName("python"))
        {
            try
            {
                if (p.MainModule?.FileName?.Contains("GPT-SoVITS", StringComparison.OrdinalIgnoreCase) == true)
                    p.Kill(entireProcessTree: true);
            }
            catch { }
            finally { p.Dispose(); }
        }
    }

    private async Task EnsureWeightsLoadedIfRequestedAsync(string baseUrl, CancellationToken ct)
    {
        var tts = _settings.Current.Tts;
        bool shouldReload = _pendingWeightReload ||
                            (!_weightsInitialized && tts.ReloadWeightsOnConnect);
        if (!shouldReload)
        {
            _weightsInitialized = true;
            return;
        }

        string targetGroup = GetWeightGroup(_voiceProfile);
        var prof = GetActiveProfileSettings();

        var gptPath = prof?.GptWeightsPath
                      ?? (string.IsNullOrWhiteSpace(tts.GptWeightsPath) ? null : tts.GptWeightsPath)
                      ?? FindVoiceAsset(targetGroup, "*.ckpt");
        var sovitsPath = prof?.SovitsWeightsPath
                         ?? (string.IsNullOrWhiteSpace(tts.SovitsWeightsPath) ? null : tts.SovitsWeightsPath)
                         ?? FindVoiceAsset(targetGroup, "*.pth");

        if (!string.IsNullOrWhiteSpace(gptPath))
        {
            var url = $"{baseUrl}/set_gpt_weights?weights_path={Uri.EscapeDataString(gptPath)}";
            using var r = await _http.GetAsync(url, ct);
            if (!r.IsSuccessStatusCode)
                _logger.LogWarning("set_gpt_weights returned {Status}", (int)r.StatusCode);
        }

        if (!string.IsNullOrWhiteSpace(sovitsPath))
        {
            var url = $"{baseUrl}/set_sovits_weights?weights_path={Uri.EscapeDataString(sovitsPath)}";
            using var r = await _http.GetAsync(url, ct);
            if (!r.IsSuccessStatusCode)
                _logger.LogWarning("set_sovits_weights returned {Status}", (int)r.StatusCode);
        }

        _loadedWeightGroup = targetGroup;
        _pendingWeightReload = false;
        _weightsInitialized = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.Writer.TryComplete();
        _cts.Cancel();
        _cts.Dispose();
        KillManagedOrLocalSoVitsServers();
    }
}
