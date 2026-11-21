using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Services;

namespace AiSecretary.Desktop.Skills;

/// <summary>
/// 沙盒目錄檔案讀寫與管理技能（存取信任範圍外路徑時透過 <see cref="SkillRuntime"/> 請求授權）。
/// </summary>
public sealed class SandboxFileSkill : IBasicSkill
{
    private readonly GuardService _guard;
    private readonly SandboxService _sandbox;
    private readonly SkillRuntime _skills;
    private readonly UiLocalizer _localizer;

    public SandboxFileSkill(GuardService guard, SandboxService sandbox, SkillRuntime skills, UiLocalizer localizer)
    {
        _guard = guard;
        _sandbox = sandbox;
        _skills = skills;
        _localizer = localizer;
    }

    public string Name => "sandbox_file";
    public string DisplayDescription => "Read, write, list, or delete sandbox files";
    public string StatusText => "Accessing sandbox files...";
    public string MainAgentSpec => """
            sandbox_file
               - Purpose: Read, write, list, or delete files inside the authorized sandbox directory.
               - Args: {"action": "read|write|list|delete", "path": "relative path", "content": "for write"}
            
        """;
    public string PlannerSpec => """sandbox_file: Read/write/list/delete sandbox files. args: {"action":"list|read|write|append|delete","path":"...","content":"..."}""";

    public async Task ExecuteAsync(SkillExecutionContext ctx)
    {
        var skillName = ctx.EffectiveName(Name);
        var action = ctx.GetArg("action") ?? "list";
        var path = ctx.GetArg("path");
        var content = ctx.GetArg("content");
        await ctx.OnStatusAsync(await ctx.TextAsync($"skill_status:{skillName}", StatusText));
        var res = await SandboxFileAsync(action, path, content, ctx.ConfirmAsync, ctx.Ct, skillName, ctx);
        ctx.Observations.Add(res);
        if (ctx.Outcome == StepOutcome.Ok)
            ctx.CompletedSteps.Add($"{skillName}:{action}");
    }

    public async Task<string> SandboxFileAsync(
        string action,
        string? relativeOrFullPath,
        string? content,
        Func<string, string, Task<bool>> confirmAsync,
        CancellationToken ct = default,
        string? skillName = null,
        SkillExecutionContext? ctx = null)
    {
        var check = _guard.CheckSkillExecution(string.IsNullOrWhiteSpace(skillName) ? Name : skillName);
        if (!check.Allowed) throw new UnauthorizedAccessException(check.Reason);

        var act = (action ?? "list").Trim().ToLowerInvariant();
        var rawPath = string.IsNullOrWhiteSpace(relativeOrFullPath) ? "." : relativeOrFullPath.Trim();
        var resolved = Path.IsPathRooted(rawPath)
            ? Path.GetFullPath(rawPath)
            : Path.GetFullPath(Path.Combine(_sandbox.RootPath, rawPath));

        var fileName = Path.GetFileName(resolved);
        if (fileName.Equals(".env", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("gmail_token.json", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("gmail_credentials.json", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
        {
            ctx?.MarkDenied();
            return "Security policy forbids accessing credential or database files via sandbox_file.";
        }

        if (!_sandbox.IsPathInsideSandbox(resolved))
        {
            var outsideTitle = await _localizer.TextAsync("SandboxOutsideTitle", "Outside-Sandbox Path Permission", Array.Empty<object?>(), ct);
            var outsideMsg = await _localizer.TextAsync("SandboxOutsideMessage", "Assistant requests access outside trusted folders for '{0}'. Choose an option:", new object?[] { act }, ct);
            var decision = await _skills.RequestPathPermissionAsync(
                outsideTitle,
                outsideMsg,
                resolved,
                confirmAsync);
            if (decision == PathPermissionDecision.Deny)
            {
                ctx?.MarkDenied();
                return $"User denied access outside sandbox: {resolved}";
            }
        }

        switch (act)
        {
            case "list":
                if (!Directory.Exists(resolved)) return $"Directory not found: {resolved}";
                var entries = Directory.EnumerateFileSystemEntries(resolved)
                    .Take(50)
                    .Select(p => Directory.Exists(p) ? $"[DIR] {Path.GetFileName(p)}" : Path.GetFileName(p));
                return $"Directory {resolved}:\n" + string.Join("\n", entries);

            case "read":
                if (!File.Exists(resolved)) return $"File not found: {resolved}";
                var text = await File.ReadAllTextAsync(resolved, ct);
                return text.Length > 4000 ? text[..4000] + "\n...(truncated)" : text;

            case "write":
                Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
                // Windows PowerShell 5.1 treats UTF-8 .ps1 files without BOM as system ANSI (CP950), causing Chinese mojibake.
                var writeEnc = resolved.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
                    ? new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
                    : System.Text.Encoding.UTF8;
                await File.WriteAllTextAsync(resolved, content ?? "", writeEnc, ct);
                return $"Written file: {resolved}";

            case "append":
                Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
                await File.AppendAllTextAsync(resolved, content ?? "", ct);
                return $"Appended file: {resolved}";

            case "delete":
                if (!File.Exists(resolved)) return $"File not found: {resolved}";
                File.Delete(resolved);
                return $"Deleted file: {resolved}";

            default:
                ctx?.MarkFailed();
                return $"Unsupported sandbox_file action: {act}";
        }
    }
}
