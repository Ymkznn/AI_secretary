using System.Text.Json;
using AiSecretary.Desktop.Models;

namespace AiSecretary.Desktop.Services;

/// <summary>
/// Central settings provider that binds <see cref="AppSettings"/> from IConfiguration
/// and persists runtime user overrides to %LOCALAPPDATA%\AiSecretary.Desktop\user_settings.json.
/// </summary>
public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly IConfiguration _config;
    private readonly object _lock = new();

    public AppSettings Current { get; private set; }

    public AppSettingsService(IConfiguration config)
    {
        _config = config;
        Current = LoadFromConfiguration();
    }

    public void Reload()
    {
        lock (_lock)
        {
            Current = LoadFromConfiguration();
        }
    }

    public async Task SaveOverridesAsync(Action<AppSettings> mutate, CancellationToken ct = default)
    {
        AppSettings snapshot;
        lock (_lock)
        {
            mutate(Current);
            snapshot = Current;
        }

        Directory.CreateDirectory(AppPaths.UserDataRoot);
        var json = JsonSerializer.Serialize(snapshot, JsonOpts);
        await File.WriteAllTextAsync(AppPaths.UserSettingsPath, json, ct);
    }

    private AppSettings LoadFromConfiguration()
    {
        var settings = new AppSettings();
        _config.GetSection("Llm").Bind(settings.Llm);
        if (_config.GetValue<int?>("Llm:MaxTokensPerSession") is null &&
            _config.GetValue<int?>("Foundry:MaxTokensPerSession") is int legacyTokens)
        {
            settings.Llm.MaxTokensPerSession = legacyTokens;
        }

        _config.GetSection("CustomSkill").Bind(settings.CustomSkill);
        _config.GetSection("Tts").Bind(settings.Tts);
        _config.GetSection("Scheduler").Bind(settings.Scheduler);

        if (_config.GetValue<int?>("Scheduler:ProactiveIdleMinutes") is null &&
            _config.GetValue<int?>("ProactiveChat:IdleMinutes") is int legacyIdle)
        {
            settings.Scheduler.ProactiveIdleMinutes = legacyIdle;
        }
        if (_config.GetValue<bool?>("Scheduler:ProactiveEnabledWithStyle") is null &&
            _config.GetValue<bool?>("ProactiveChat:EnabledWithStyle") is bool legacyProactive)
        {
            settings.Scheduler.ProactiveEnabledWithStyle = legacyProactive;
        }

        return settings;
    }
}
