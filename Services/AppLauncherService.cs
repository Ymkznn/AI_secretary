using System.Diagnostics;
using Microsoft.Win32;

namespace AiSecretary.Desktop.Services;

public sealed record AppLaunchResult(bool Success, string Message, string? ResolvedTarget = null, bool Denied = false);

/// <summary>
/// Safely resolves and launches Windows desktop applications, Start Menu shortcuts, or web URLs.
/// </summary>
public sealed class AppLauncherService
{
    private static readonly Dictionary<string, string> BuiltInAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["notepad"] = "notepad.exe",
        ["calc"] = "calc.exe",
        ["calculator"] = "calc.exe",
        ["paint"] = "mspaint.exe",
        ["mspaint"] = "mspaint.exe",
        ["explorer"] = "explorer.exe",
        ["file explorer"] = "explorer.exe",
        ["chrome"] = "chrome.exe",
        ["google chrome"] = "chrome.exe",
        ["browser"] = "msedge.exe",
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
        ["taskmgr"] = "taskmgr.exe",
        ["task manager"] = "taskmgr.exe",
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
            return new AppLaunchResult(false, "Application name is required.");
        }

        if (BlockedTokens.Contains(query) ||
            BlockedTokens.Any(b => query.StartsWith(b + " ", StringComparison.OrdinalIgnoreCase)))
        {
            return new AppLaunchResult(false, $"Security policy forbids launching system or shell command '{query}'.", Denied: true);
        }

        if (!string.IsNullOrWhiteSpace(arguments) && !IsSafeArgument(arguments))
        {
            return new AppLaunchResult(false, "Security policy rejected launch arguments (only safe URLs or existing local paths are allowed).", Denied: true);
        }

        // If "browser" is requested with an http(s) URL, hand the URL directly to Windows Shell
        // so it always opens in the user's actual default browser (Edge, Chrome, Firefox, etc.).
        if (query.Equals("browser", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(arguments) &&
            Uri.TryCreate(arguments.Trim(), UriKind.Absolute, out var argUri) &&
            argUri.Scheme is "http" or "https")
        {
            query = arguments.Trim();
            arguments = null;
        }

        var target = ResolveTarget(query);
        if (string.IsNullOrWhiteSpace(target))
        {
            return new AppLaunchResult(false, $"Application or shortcut '{query}' not found.");
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
            return new AppLaunchResult(true, $"Opened '{query}'.", target);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to launch '{Target}'", target);
            return new AppLaunchResult(false, $"Failed to open '{query}': {ex.Message}", target);
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
            if (query.Equals("browser", StringComparison.OrdinalIgnoreCase) &&
                FindDefaultBrowserExecutable() is string defaultBrowser)
                return defaultBrowser;
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

    private static string? FindDefaultBrowserExecutable()
    {
        try
        {
            using var choice = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            var progId = choice?.GetValue("ProgId") as string;
            if (string.IsNullOrWhiteSpace(progId)) return null;

            using var cmdKey = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
            var cmd = cmdKey?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(cmd)) return null;

            cmd = cmd.Trim();
            string exePath;
            if (cmd.StartsWith('"'))
            {
                var endQuote = cmd.IndexOf('"', 1);
                exePath = endQuote > 1 ? cmd[1..endQuote] : cmd.Trim('"');
            }
            else
            {
                var space = cmd.IndexOf(' ');
                exePath = space > 0 ? cmd[..space] : cmd;
            }
            return File.Exists(exePath) ? exePath : null;
        }
        catch
        {
            return null;
        }
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
