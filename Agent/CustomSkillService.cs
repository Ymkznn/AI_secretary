using System.Diagnostics;
using System.Text.RegularExpressions;
using AiSecretary.Desktop.Data;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Resources;
using AiSecretary.Desktop.Services;
using AiSecretary.Desktop.Skills;
using Microsoft.Extensions.DependencyInjection;

namespace AiSecretary.Desktop.Agent;

/// <summary>
/// 管理由 Agent 動態建立／刪除與執行的自訂技能（Custom Skills）。
/// 專注於技能 CRUD 與於沙盒目錄下啟動進程；所有風險攔截、參數檢查與授權皆委由 GuardService 處理。
/// </summary>
public sealed class CustomSkillService
{
    private readonly SqliteStore _store;
    private readonly SandboxService _sandbox;
    private readonly GuardService _guard;
    private readonly AppSettingsService _settings;
    private readonly IServiceProvider _services;
    private readonly ILogger<CustomSkillService> _logger;

    /// <summary>自訂技能清單異動時觸發（供 UI 與 SSE 同步更新）。</summary>
    public event Action? SkillsChanged;

    public CustomSkillService(
        SqliteStore store,
        SandboxService sandbox,
        GuardService guard,
        AppSettingsService settings,
        IServiceProvider services,
        ILogger<CustomSkillService> logger)
    {
        _store = store;
        _sandbox = sandbox;
        _guard = guard;
        _settings = settings;
        _services = services;
        _logger = logger;
    }

    /// <summary>列出所有已儲存的自訂技能。</summary>
    public Task<List<CustomSkillRecord>> ListAsync(CancellationToken ct = default)
        => _store.GetCustomSkillsAsync(ct);

    /// <summary>依技能名稱（不分大小寫）查詢單一自訂技能。</summary>
    public async Task<CustomSkillRecord?> FindAsync(string name, CancellationToken ct = default)
    {
        var all = await _store.GetCustomSkillsAsync(ct);
        return all.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 驗證並建立自訂技能：檢查命名規範與內建技能衝突，高風險指令由 GuardService 直接拒絕，低風險則請使用者確認後寫入資料庫。
    /// </summary>
    public async Task<(bool Ok, string Message)> CreateAsync(
        string name,
        string description,
        string command,
        Func<string, string, Task<bool>> confirmAsync,
        CancellationToken ct = default)
    {
        var cleanName = name.Trim().Replace(" ", "_").ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(cleanName) || string.IsNullOrWhiteSpace(description) || string.IsNullOrWhiteSpace(command))
            return (false, "Skill name, description, and command cannot be empty.");

        // 限制為小寫蛇形命名，避免特殊字元或過長名稱
        if (!Regex.IsMatch(cleanName, @"^[a-z][a-z0-9_]{1,47}$"))
            return (false, "Custom skill name must be 2-48 chars, lowercase letters/digits/underscores starting with a letter.");

        // 禁止覆蓋或冒名內建技能
        if (_services.GetRequiredService<SkillRegistry>().Contains(cleanName))
            return (false, $"Skill name '{cleanName}' collides with a built-in skill.");

        var guardCheck = _guard.CheckCommandExecution($"create_skill:{cleanName}", command);
        if (!guardCheck.Allowed && !guardCheck.RequiresUserConfirmation)
            return (false, $"Guard blocked: {guardCheck.Reason}");

        var prompt = AppMessages.CreateSkillPrompt(cleanName, description, command, _sandbox.RootPath);
        var approved = await confirmAsync(AppMessages.CreateSkillTitle, prompt);
        if (!approved)
            return (false, $"User declined creating skill '{cleanName}'.");

        var rec = new CustomSkillRecord(cleanName, description.Trim(), command.Trim(), DateTimeOffset.UtcNow);
        await _store.UpsertCustomSkillAsync(rec, ct);
        SkillsChanged?.Invoke();
        return (true, $"Created and enabled skill '{cleanName}'.");
    }

    /// <summary>經使用者確認後刪除指定的自訂技能。</summary>
    public async Task<(bool Ok, string Message)> DeleteAsync(
        string name,
        Func<string, string, Task<bool>> confirmAsync,
        CancellationToken ct = default)
    {
        var existing = await FindAsync(name, ct);
        if (existing is null)
            return (false, $"Custom skill '{name}' not found.");

        var prompt = AppMessages.DeleteSkillPrompt(existing.Name, existing.Description);
        var approved = await confirmAsync(AppMessages.DeleteSkillTitle, prompt);
        if (!approved)
            return (false, $"User declined deleting skill '{existing.Name}'.");

        await _store.DeleteCustomSkillAsync(existing.Name, ct);
        SkillsChanged?.Invoke();
        return (true, $"Deleted skill '{existing.Name}'.");
    }

    /// <summary>
    /// 執行自訂技能：授權與參數驗證委由 GuardService 處理，通過後於沙盒目錄下啟動程序並受逾時保護。
    /// </summary>
    public async Task<string> ExecuteAsync(
        CustomSkillRecord skill,
        string? extraArgs,
        string? taskId = null,
        CancellationToken ct = default)
    {
        var (approved, denialReason) = await _guard.AuthorizeCommandAsync(
            skill.Name, skill.Command, extraArgs, _sandbox.RootPath, taskId, ct);
        if (!approved)
            return denialReason ?? $"User declined running skill '{skill.Name}'.";

        var cfg = _settings.Current.CustomSkill;
        var timeoutSec = Math.Clamp(cfg.TimeoutSeconds, 5, 600);
        var shellExe = string.IsNullOrWhiteSpace(cfg.ShellExecutable)
            ? "powershell.exe"
            : cfg.ShellExecutable.Trim();

        var psi = new ProcessStartInfo
        {
            FileName = shellExe,
            WorkingDirectory = _sandbox.RootPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!string.IsNullOrWhiteSpace(extraArgs))
            psi.Environment["SKILL_ARGS"] = extraArgs.Trim();
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(skill.Command);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start process.");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
        await proc.WaitForExitAsync(timeoutCts.Token);

        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();

        _logger.LogInformation("Executed custom skill {Name} (exit={Code})", skill.Name, proc.ExitCode);
        if (proc.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
            return $"Execution failed (exit={proc.ExitCode}): {stderr}";

        return string.IsNullOrWhiteSpace(stdout) ? "Completed (no output)." : stdout;
    }
}
