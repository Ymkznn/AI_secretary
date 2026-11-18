using AiSecretary.Desktop.Services;

namespace AiSecretary.Desktop.Agent;

/// <summary>
/// Defines and enforces the default workspace sandbox for agent-created skills and file actions.
/// Default path: &lt;AppContext.BaseDirectory&gt;\sandbox (configurable via Sandbox:RootPath).
/// Leaving the sandbox is only permitted for:
/// 1. launch_app (opening user-installed desktop apps / URLs),
/// 2. paths explicitly whitelisted in Sandbox:AllowedPaths, or
/// 3. actions explicitly approved by the user.
/// </summary>
public sealed class SandboxService
{
    private readonly object _lock = new();
    private readonly List<string> _allowedPaths;
    public string RootPath { get; }

    public SandboxService(IConfiguration config)
    {
        var configured = config.GetValue<string>("Sandbox:RootPath");
        var rawRoot = string.IsNullOrWhiteSpace(configured) ||
                      configured.Trim().Equals("sandbox", StringComparison.OrdinalIgnoreCase)
            ? AppPaths.SandboxRoot
            : (Path.IsPathRooted(configured) ? configured : Path.Combine(AppContext.BaseDirectory, configured));

        RootPath = Path.GetFullPath(rawRoot);
        Directory.CreateDirectory(RootPath);

        _allowedPaths = (config.GetSection("Sandbox:AllowedPaths").Get<string[]>() ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> GetTrustedPaths()
    {
        lock (_lock)
            return _allowedPaths.ToList();
    }

    public void SetTrustedPaths(IEnumerable<string> paths)
    {
        var normalized = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        lock (_lock)
        {
            _allowedPaths.Clear();
            _allowedPaths.AddRange(normalized);
        }
    }

    public bool AddTrustedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var norm = NormalizeFolderForTrust(path);
        lock (_lock)
        {
            if (_allowedPaths.Any(a => IsUnderDirectory(norm, a)))
                return false;
            _allowedPaths.Add(norm);
            return true;
        }
    }

    public static string NormalizePath(string path)
        => Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static string NormalizeFolderForTrust(string path)
    {
        var full = Path.GetFullPath(path.Trim());
        if (Directory.Exists(full))
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (File.Exists(full) || Path.HasExtension(full))
            return (Path.GetDirectoryName(full) ?? full).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public bool IsPathInsideSandbox(string path)
    {
        var full = Path.GetFullPath(path);
        if (IsUnderDirectory(full, RootPath))
            return true;

        lock (_lock)
            return _allowedPaths.Any(a => IsUnderDirectory(full, a));
    }

    private static bool IsUnderDirectory(string fullPath, string directory)
    {
        var normalizedDir = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase) ||
               fullPath.Equals(normalizedDir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    public bool CanAccessPath(string path, bool userApproved = false)
        => userApproved || IsPathInsideSandbox(path);
}
