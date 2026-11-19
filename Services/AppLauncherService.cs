using System.Diagnostics;
using AiSecretary.Desktop.Resources;
using Microsoft.Win32;

namespace AiSecretary.Desktop.Services;

public sealed record AppLaunchResult(bool Success, string Message, string? ResolvedTarget = null);

/// <summary>
/// Safely resolves and launches Windows desktop applications, Start Menu shortcuts, or web URLs.
/// </summary>
public sealed class AppLauncherService
{
    private static readonly Dictionary<string, string> BuiltInAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["記事本"] = "notepad.exe",
        ["notepad"] = "notepad.exe",
        ["小算盤"] = "calc.exe",
        ["計算機"] = "calc.exe",
        ["calc"] = "calc.exe",
        ["calculator"] = "calc.exe",
        ["小畫家"] = "mspaint.exe",
        ["畫圖"] = "mspaint.exe",
        ["paint"] = "mspaint.exe",
        ["mspaint"] = "mspaint.exe",
        ["檔案總管"] = "explorer.exe",
        ["資料夾"] = "explorer.exe",
        ["explorer"] = "explorer.exe",
        ["chrome"] = "chrome.exe",
        ["google chrome"] = "chrome.exe",
        ["瀏覽器"] = "msedge.exe",
        ["edge"] = "msedge.exe",
        ["microsoft edge"] = "msedge.exe",
        ["vscode"] = "code",
        ["vs code"] = "code",
        ["visual studio code"] = "code",
        ["code"] = "code",
        ["word"] = "winword.exe",
        ["excel"] = "excel.exe",
        ["powerpoint"] = "powerpnt.exe",
        ["ppt"] = "powerpnt.exe",
        ["outlook"] = "outlook.exe",
        ["工作管理員"] = "taskmgr.exe",
        ["taskmgr"] = "taskmgr.exe",
        ["設定"] = "ms-settings:",
        ["settings"] = "ms-settings:"
    };

    private static readonly HashSet<string> BlockedTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "format", "diskpart", "bcdedit", "shutdown", "del", "erase", "rmdir", "rd", "taskkill",
        "cmd", "cmd.exe", "powershell", "powershell.exe", "pwsh", "pwsh.exe", "wt", "wt.exe", "wscript", "cscript", "mshta"
    };

    private static readonly char[] ForbiddenArgChars = { '&', '|', ';', '`', '$', '(', ')', '<', '>', '\r', '\n', '"' };

    private readonly ILogger<AppLauncherService> _logger;

    public AppLauncherService(ILogger<AppLauncherService> logger)
    {
        _logger = logger;
    }

    public AppLaunchResult Launch(string appName, string? arguments = null)
    {
        var query = appName?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(query))
        {
            return new AppLaunchResult(false, AppMessages.Localize("LaunchEmpty", "Application name is required."));
        }

        if (BlockedTokens.Contains(query) ||
            BlockedTokens.Any(b => query.StartsWith(b + " ", StringComparison.OrdinalIgnoreCase)))
        {
            return new AppLaunchResult(false, AppMessages.LocalizeFormat("LaunchBlockedApp", "Security policy forbids launching system or shell command '{0}'.", query));
        }

        if (!string.IsNullOrWhiteSpace(arguments) && !IsSafeArgument(arguments))
        {
            return new AppLaunchResult(false, AppMessages.Localize("LaunchInvalidArgs", "Security policy rejected launch arguments (only safe URLs or existing local paths are allowed)."));
        }

        var target = ResolveTarget(query);
        if (string.IsNullOrWhiteSpace(target))
        {
            return new AppLaunchResult(false, AppMessages.LocalizeFormat("LaunchNotFound", "Application or shortcut '{0}' not found.", query));
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            };

            if (!string.IsNullOrWhiteSpace(arguments))
            {
                psi.Arguments = arguments.Trim();
            }

            Process.Start(psi);
            _logger.LogInformation("Launched app '{Query}' -> '{Target}'", query, target);
            return new AppLaunchResult(true, AppMessages.LocalizeFormat("LaunchSuccess", "Opened '{0}'.", query), target);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to launch '{Target}'", target);
            return new AppLaunchResult(false, AppMessages.LocalizeFormat("LaunchFailed", "Failed to open '{0}': {1}", query, ex.Message), target);
        }
    }

    private static bool IsSafeArgument(string arguments)
    {
        var trimmed = arguments.Trim();
        if (trimmed.IndexOfAny(ForbiddenArgChars) >= 0)
            return false;

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return uri.Scheme is "http" or "https";

        return File.Exists(trimmed) || Directory.Exists(trimmed);
    }

    private static string? ResolveTarget(string query)
    {
        // 1. Direct http/https URL or Windows URI scheme (e.g. ms-settings:)
        if (Uri.TryCreate(query, UriKind.Absolute, out var uri) &&
            (uri.Scheme is "http" or "https" or "ms-settings" or "mailto"))
        {
            return query;
        }

        // 2. Built-in aliases
        if (BuiltInAliases.TryGetValue(query, out var aliased))
        {
            // Prefer chrome.exe if "瀏覽器" is requested and Chrome is registered
            if (query == "瀏覽器" && FindInAppPaths("chrome.exe") is string chromePath)
                return chromePath;
            return aliased;
        }

        // 3. Direct existing file path
        if (File.Exists(query) || Directory.Exists(query))
        {
            return query;
        }

        // 4. Windows Registry App Paths
        var exeCandidate = query.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? query : query + ".exe";
        if (FindInAppPaths(exeCandidate) is string regPath)
        {
            return regPath;
        }

        // 5. Start Menu .lnk shortcuts
        if (FindStartMenuShortcut(query) is string lnkPath)
        {
            return lnkPath;
        }

        // 6. Fallback: if it looks like a simple executable name without path traversal
        if (!query.Contains("&&") && !query.Contains('|') && !query.Contains(';'))
        {
            return query;
        }

        return null;
    }

    private static string? FindInAppPaths(string exeName)
    {
        const string subKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = root.OpenSubKey($@"{subKey}\{exeName}");
                var val = key?.GetValue(null) as string;
                if (!string.IsNullOrWhiteSpace(val))
                {
                    var cleaned = val.Trim().Trim('"');
                    if (File.Exists(cleaned)) return cleaned;
                }
            }
            catch
            {
                // Ignore inaccessible registry keys
            }
        }
        return null;
    }

    private static string? FindStartMenuShortcut(string query)
    {
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        }
        .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
        .Distinct(StringComparer.OrdinalIgnoreCase);

        var candidates = new List<string>();
        foreach (var folder in folders)
        {
            try
            {
                candidates.AddRange(Directory.EnumerateFiles(folder, "*.lnk", SearchOption.AllDirectories));
            }
            catch
            {
                // Ignore permission errors
            }
        }

        // Exact filename match first, then substring match
        var exact = candidates.FirstOrDefault(p =>
            Path.GetFileNameWithoutExtension(p).Equals(query, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        return candidates.FirstOrDefault(p =>
            Path.GetFileNameWithoutExtension(p).Contains(query, StringComparison.OrdinalIgnoreCase));
    }
}
