using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Resources;

namespace AiSecretary.Desktop.Skills;

/// <summary>
/// 沙盒目錄檔案讀寫與管理技能（存取信任範圍外路徑時透過 <see cref="SkillRuntime"/> 請求授權）。
/// </summary>
public sealed class SandboxFileSkill : IBasicSkill
{
    private readonly GuardService _guard;
    private readonly SandboxService _sandbox;
    private readonly SkillRuntime _skills;

    public SandboxFileSkill(GuardService guard, SandboxService sandbox, SkillRuntime skills)
    {
        _guard = guard;
        _sandbox = sandbox;
        _skills = skills;
    }

    public string Name => "sandbox_file";
    public string DisplayDescription => "Read, write, list, or delete sandbox files";
    public string MainAgentSpec => """
            sandbox_file
               - Purpose: Read, write, list, or delete files inside the authorized sandbox directory.
               - Args: {"action": "read|write|list|delete", "path": "relative path", "content": "for write"}
            
        """;
    public string PlannerSpec => """sandbox_file: Read/write/list/delete sandbox files. args: {"action":"list|read|write|append|delete","path":"...","content":"..."}""";

    public async Task ExecuteAsync(SkillExecutionContext ctx)
    {
        var action = ctx.GetArg("action") ?? "list";
        var path = ctx.GetArg("path");
        var content = ctx.GetArg("content");
        await ctx.OnStatusAsync(AppMessages.Localize($"skill_status:{Name}", "Accessing sandbox files..."));
        var res = await SandboxFileAsync(action, path, content, ctx.ConfirmAsync, ctx.Ct);
        ctx.Observations.Add(res);
        ctx.CompletedSteps.Add($"sandbox_file:{action}");
    }

    public async Task<string> SandboxFileAsync(
        string action,
        string? relativeOrFullPath,
        string? content,
        Func<string, string, Task<bool>> confirmAsync,
        CancellationToken ct = default)
    {
        var check = _guard.CheckSkillExecution("sandbox_file");
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
            throw new UnauthorizedAccessException("Security policy forbids accessing credential or database files via sandbox_file.");
        }

        if (!_sandbox.IsPathInsideSandbox(resolved))
        {
            var decision = await _skills.RequestPathPermissionAsync(
                AppMessages.SandboxOutsideTitle,
                AppMessages.SandboxOutsideMessage(act),
                resolved,
                confirmAsync);
            if (decision == PathPermissionDecision.Deny)
                return $"User denied access outside sandbox: {resolved}";
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
                await File.WriteAllTextAsync(resolved, content ?? "", ct);
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
                return $"Unsupported sandbox_file action: {act}";
        }
    }
}
