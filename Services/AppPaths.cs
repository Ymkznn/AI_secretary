namespace AiSecretary.Desktop.Services;

/// <summary>
/// Centralizes writable per-user data paths under %LOCALAPPDATA%\AiSecretary.Desktop
/// and performs one-time non-destructive migration from legacy app/working directories.
/// </summary>
public static class AppPaths
{
    public static string UserDataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AiSecretary.Desktop");

    public static string DatabasePath => Path.Combine(UserDataRoot, "aisecretary.db");
    public static string GuardAuditDatabasePath => Path.Combine(UserDataRoot, "guard_audit.db");
    public static string SandboxRoot => Path.Combine(UserDataRoot, "sandbox");
    public static string UserStylesDir => Path.Combine(UserDataRoot, "styles");
    public static string GmailTokenPath => Path.Combine(UserDataRoot, "gmail_token.json");
    public static string UserSettingsPath => Path.Combine(UserDataRoot, "user_settings.json");
    public static string BrowserProfileDir => Path.Combine(UserDataRoot, "browser_profile");
    public static string OfficeBackupsDir => Path.Combine(UserDataRoot, "office_backups");

    public static void EnsureAndMigrateLegacyData()
    {
        Directory.CreateDirectory(UserDataRoot);
        Directory.CreateDirectory(SandboxRoot);
        Directory.CreateDirectory(UserStylesDir);
        Directory.CreateDirectory(BrowserProfileDir);
        Directory.CreateDirectory(OfficeBackupsDir);

        var legacyRoots = new[]
        {
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory()
        }
        .Where(p => !string.IsNullOrWhiteSpace(p))
        .Select(p => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        .Where(p => !p.Equals(UserDataRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

        if (!File.Exists(DatabasePath))
        {
            foreach (var root in legacyRoots)
            {
                var srcDb = Path.Combine(root, "aisecretary.db");
                if (!File.Exists(srcDb)) continue;
                try
                {
                    File.Copy(srcDb, DatabasePath, overwrite: false);
                    foreach (var suffix in new[] { "-wal", "-shm" })
                    {
                        var sidecar = srcDb + suffix;
                        if (File.Exists(sidecar))
                            File.Copy(sidecar, DatabasePath + suffix, overwrite: false);
                    }
                    break;
                }
                catch { }
            }
        }

        if (!File.Exists(GmailTokenPath))
        {
            foreach (var root in legacyRoots)
            {
                var srcToken = Path.Combine(root, "gmail_token.json");
                if (!File.Exists(srcToken)) continue;
                try
                {
                    File.Copy(srcToken, GmailTokenPath, overwrite: false);
                    break;
                }
                catch { }
            }
        }

        if (!Directory.EnumerateFileSystemEntries(SandboxRoot).Any())
        {
            foreach (var root in legacyRoots)
            {
                var srcSandbox = Path.Combine(root, "sandbox");
                if (!Directory.Exists(srcSandbox) || !Directory.EnumerateFileSystemEntries(srcSandbox).Any())
                    continue;
                try
                {
                    CopyDirectory(srcSandbox, SandboxRoot);
                    break;
                }
                catch { }
            }
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            if (!File.Exists(destFile))
                File.Copy(file, destFile, overwrite: false);
        }
        foreach (var sub in Directory.EnumerateDirectories(sourceDir))
        {
            CopyDirectory(sub, Path.Combine(destDir, Path.GetFileName(sub)));
        }
    }
}
