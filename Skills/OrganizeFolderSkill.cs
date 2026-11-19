using System.Text;
using System.Text.Json;
using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Services;

namespace AiSecretary.Desktop.Skills;

/// <summary>
/// 三階段資料夾整理技能（掃描結構、安全搬移與暫存待刪、請求權限刪除暫存資料夾、寫出結構化 CSV 稽核報表）。
/// </summary>
public sealed class OrganizeFolderSkill : IBasicSkill
{
    private const string QuarantineFolderPrefix = "_待刪除_";
    private const string OrganizeCsvPrefix = "organize_log_";
    private const string OrganizeCsvHeader = "Time,Stage,Action,Source,Destination,Status,Reason";

    private const string CsvStage1Move = "stage1_move";
    private const string CsvStage1Quarantine = "stage1_quarantine";
    private const string CsvStage2Delete = "stage2_delete";
    private const string CsvActionMove = "move";
    private const string CsvActionBatchMove = "batch_move";
    private const string CsvActionQuarantine = "quarantine";
    private const string CsvActionDeleteQuarantine = "delete_quarantine";
    private const string CsvActionKeepQuarantine = "keep_quarantine";
    private const string CsvStatusSuccess = "success";
    private const string CsvStatusSkippedProtected = "skipped_protected";
    private const string CsvStatusSkippedProtectedFile = "skipped_protected_file";
    private const string CsvStatusSkippedRootDelete = "skipped_root_delete";
    private const string CsvStatusSkippedUnauthorized = "skipped_unauthorized";
    private const string CsvStatusKeptManual = "kept_manual";
    private const string CsvDestDeleted = "(deleted)";
    private const string CsvReasonDeleteAuthorized = "user_authorized_quarantine_delete";
    private const string CsvReasonKeepManual = "user_declined_kept_for_review";

    private static readonly HashSet<string> DriveRootProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "Users",
        "Recovery", "System Volume Information", "$Recycle.Bin", "$WinREAgent",
        "PerfLogs", "OneDriveTemp", "Config.Msi", "Documents and Settings",
        "hiberfil.sys", "pagefile.sys", "swapfile.sys", "DumpStack.log.tmp", "bootmgr", "BOOTNXT"
    };

    private static readonly HashSet<string> UserProfileProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppData", "Desktop", "Documents", "Downloads", "Pictures", "Music", "Videos",
        "OneDrive", "Favorites", "Contacts", "Links", "Saved Games", "Searches", "3D Objects",
        "NTUSER.DAT", "ntuser.dat.LOG1", "ntuser.dat.LOG2", "ntuser.ini"
    };

    private readonly GuardService _guard;
    private readonly SandboxService _sandbox;
    private readonly SkillRuntime _skills;
    private readonly UiLocalizer _localizer;

    public OrganizeFolderSkill(GuardService guard, SandboxService sandbox, SkillRuntime skills, UiLocalizer localizer)
    {
        _guard = guard;
        _sandbox = sandbox;
        _skills = skills;
        _localizer = localizer;
    }

    public string Name => "organize_folder";
    public string DisplayDescription => "Scan and organize local folders safely";
    public string StatusText => "Processing folder...";
    public string MainAgentSpec => """
            organize_folder
               - Purpose: Two-step safe folder organizer (scan first, then execute moves/quarantine/CSV log).
               - Args: {"action": "scan|execute", "target_folder": "full path", "moves": [{"source": "...", "destination": "...", "reason": "..."}, {"source_dir": ".", "source_pattern": "*.jpg", "destination_dir": "sub", "reason": "..."}], "delete_candidates": [{"source": "...", "reason": "..."}]}
            
        """;
    public string PlannerSpec => """organize_folder: Scan or execute safe folder organization. args: {"action":"scan|execute","target_folder":"...","moves":[],"delete_candidates":[]}""";

    public async Task ExecuteAsync(SkillExecutionContext ctx)
    {
        var skillName = ctx.EffectiveName(Name);
        var action = ctx.GetArg("action") ?? "scan";
        var targetFolder = ctx.GetArg("target_folder") ?? ctx.GetArg("path");
        await ctx.OnStatusAsync(await ctx.TextAsync($"skill_status:{skillName}", StatusText));
        var res = await OrganizeFolderAsync(action, targetFolder, ctx.Args, ctx.ConfirmAsync, ctx.Ct, skillName);
        ctx.Observations.Add(res);
        ctx.CompletedSteps.Add($"{skillName}:{action}");
    }

    public async Task<string> OrganizeFolderAsync(
        string action,
        string? targetFolder,
        JsonElement args,
        Func<string, string, Task<bool>> confirmAsync,
        CancellationToken ct = default,
        string? skillName = null)
    {
        var check = _guard.CheckSkillExecution(string.IsNullOrWhiteSpace(skillName) ? Name : skillName);
        if (!check.Allowed) throw new UnauthorizedAccessException(check.Reason);

        if (string.IsNullOrWhiteSpace(targetFolder))
            return "organize_folder: target_folder is required.";

        var rootFull = Path.GetFullPath(targetFolder.Trim());
        if (!Directory.Exists(rootFull))
            return $"Target folder not found: {rootFull}";

        var isDriveRoot = IsDriveRoot(rootFull);
        var isUserProfileRoot = IsUserProfileRoot(rootFull);
        if (IsSystemDirectory(rootFull))
            return $"Security policy forbids organizing system directory: {rootFull}";

        var act = (action ?? "scan").Trim().ToLowerInvariant();
        if (act == "scan")
        {
            var sb = new StringBuilder();
            var scopeNote = isDriveRoot
                ? " (drive root: top-level non-system items only)"
                : isUserProfileRoot
                    ? " (user profile root: top-level non-system items only)"
                    : "";
            sb.AppendLine($"target_folder: {rootFull}{scopeNote}");
            var topOnly = isDriveRoot || isUserProfileRoot;
            var searchOpt = topOnly ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories;
            var allDirs = Directory.EnumerateDirectories(rootFull, "*", searchOpt)
                .Where(d => !IsProtectedFromOrganize(d, isDriveRoot, isUserProfileRoot))
                .Take(100)
                .ToList();
            var allFiles = Directory.EnumerateFiles(rootFull, "*", searchOpt)
                .Where(f => !IsProtectedFromOrganize(f, isDriveRoot, isUserProfileRoot))
                .Take(300)
                .ToList();

            sb.AppendLine($"subdirectories (first {allDirs.Count}):");
            foreach (var d in allDirs)
                sb.AppendLine($"  [DIR] {Path.GetRelativePath(rootFull, d)}");

            sb.AppendLine($"files (first {allFiles.Count}):");
            foreach (var f in allFiles)
            {
                var fi = new FileInfo(f);
                sb.AppendLine($"  {Path.GetRelativePath(rootFull, f)} ({fi.Length} bytes, {fi.LastWriteTime:yyyy-MM-dd HH:mm})");
            }
            return sb.ToString();
        }

        if (act != "execute")
            return $"Unsupported organize_folder action: {act} (expected scan or execute)";

        if (!_sandbox.IsPathInsideSandbox(rootFull))
        {
            var orgPermTitle = await _localizer.TextAsync("OrganizePermTitle", "Folder Organize Permission", Array.Empty<object?>(), ct);
            var orgPermMsg = await _localizer.TextAsync("OrganizePermMessage", "Allow organizing files inside external folder '{0}'?", new object?[] { rootFull }, ct);
            var decision = await _skills.RequestPathPermissionAsync(orgPermTitle, orgPermMsg, rootFull, confirmAsync);
            if (decision == PathPermissionDecision.Deny)
                return $"User denied organizing folder outside sandbox: {rootFull}";
        }

        if (isDriveRoot || isUserProfileRoot)
        {
            var label = isDriveRoot
                ? await _localizer.TextAsync("RootLabelDrive", "Drive Root", Array.Empty<object?>(), ct)
                : await _localizer.TextAsync("RootLabelUserProfile", "User Profile Root", Array.Empty<object?>(), ct);
            var rootTitle = await _localizer.TextAsync("OrganizeRootTitle", "Confirm Organizing {0}", new object?[] { label }, ct);
            var rootMsg = await _localizer.TextAsync(
                "OrganizeRootMessage",
                "About to organize moves in {0} {1}.\nSystem/default folders (Windows, Program Files, Users, AppData, Desktop, Documents, Downloads, etc.) and hidden/system files are skipped automatically, and no deletion will run.\n\nContinue?",
                new object?[] { label, rootFull },
                ct);
            var approvedRoot = await confirmAsync(rootTitle, rootMsg);
            if (!approvedRoot)
                return $"User denied organizing root folder: {rootFull}";
        }

        var logRows = new List<(string Time, string Stage, string Action, string Source, string Destination, string Status, string Reason)>();
        var approvedExternalRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var declinedExternalRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        async Task<bool> EnsurePathTrustedAsync(string path, string opLabel)
        {
            if (IsUnderFolder(path, rootFull) || _sandbox.IsPathInsideSandbox(path))
                return true;

            var parent = Path.GetDirectoryName(path) ?? path;
            if (approvedExternalRoots.Contains(parent))
                return true;
            if (declinedExternalRoots.Contains(parent))
                return false;

            var crossTitle = await _localizer.TextAsync("CrossFolderTitle", "Cross-Folder Path Permission", Array.Empty<object?>(), ct);
            var crossMsg = await _localizer.TextAsync("CrossFolderMessage", "Organize operation's '{0}' path escapes target folder and trusted list (target: {1}). Allow access?", new object?[] { opLabel, rootFull }, ct);
            var decision = await _skills.RequestPathPermissionAsync(
                crossTitle,
                crossMsg,
                path,
                confirmAsync);
            if (decision == PathPermissionDecision.TrustPermanently)
                return true;
            if (decision == PathPermissionDecision.AllowOnce)
            {
                approvedExternalRoots.Add(parent);
                return true;
            }
            declinedExternalRoots.Add(parent);
            return false;
        }

        int movedCount = 0;
        int skippedCount = 0;
        var movedDetails = new List<string>();

        async Task MoveOneAsync(string src, string dst, string reason)
        {
            var nowStr = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
            if (IsSensitiveFileName(src) || IsSensitiveFileName(dst) ||
                IsProtectedFromOrganize(src, isDriveRoot, isUserProfileRoot) ||
                IsRunningAppPath(src))
            {
                skippedCount++;
                logRows.Add((nowStr, CsvStage1Move, CsvActionMove, src, dst, CsvStatusSkippedProtected, reason));
                return;
            }

            var srcLabel = await _localizer.TextAsync("OpLabelSource", "Source", Array.Empty<object?>(), ct);
            var dstLabel = await _localizer.TextAsync("OpLabelDest", "Destination", Array.Empty<object?>(), ct);
            if (!await EnsurePathTrustedAsync(src, srcLabel) || !await EnsurePathTrustedAsync(dst, dstLabel))
            {
                skippedCount++;
                logRows.Add((nowStr, CsvStage1Move, CsvActionMove, src, dst, CsvStatusSkippedUnauthorized, reason));
                return;
            }

            var (ok, actualDst, err) = MoveEntrySafely(src, dst);
            if (ok)
            {
                movedCount++;
                movedDetails.Add($"{Path.GetRelativePath(rootFull, src)} -> {Path.GetRelativePath(rootFull, actualDst)}");
                logRows.Add((nowStr, CsvStage1Move, CsvActionMove, src, actualDst, CsvStatusSuccess, reason));
            }
            else
            {
                skippedCount++;
                logRows.Add((nowStr, CsvStage1Move, CsvActionMove, src, dst, $"failed:{err}", reason));
            }
        }

        if (args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty("moves", out var movesEl) &&
            movesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in movesEl.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                var reason = item.TryGetProperty("reason", out var rEl) ? rEl.GetString() ?? "" : "";
                var srcPattern = item.TryGetProperty("source_pattern", out var spEl) ? spEl.GetString() : null;
                var srcDirRaw = item.TryGetProperty("source_dir", out var sdEl) ? sdEl.GetString() : null;
                var dstDirRaw = item.TryGetProperty("destination_dir", out var ddEl) ? ddEl.GetString() : null;

                var srcRaw = item.TryGetProperty("source", out var sEl) ? sEl.GetString() : null;
                var dstRaw = item.TryGetProperty("destination", out var dEl) ? dEl.GetString() : null;

                if (!string.IsNullOrWhiteSpace(srcPattern) ||
                    (!string.IsNullOrWhiteSpace(srcRaw) && (srcRaw.Contains('*') || srcRaw.Contains('?'))))
                {
                    string pattern;
                    string srcDir;
                    if (!string.IsNullOrWhiteSpace(srcPattern))
                    {
                        pattern = srcPattern.Trim();
                        srcDir = ResolveInsideOrFull(rootFull, string.IsNullOrWhiteSpace(srcDirRaw) ? "." : srcDirRaw);
                    }
                    else
                    {
                        var fullPatternPath = ResolveInsideOrFull(rootFull, srcRaw!);
                        srcDir = Path.GetDirectoryName(fullPatternPath) ?? rootFull;
                        pattern = Path.GetFileName(fullPatternPath);
                    }

                    var dstDirTarget = !string.IsNullOrWhiteSpace(dstDirRaw) ? dstDirRaw : dstRaw;
                    if (string.IsNullOrWhiteSpace(dstDirTarget) || !Directory.Exists(srcDir))
                        continue;

                    var dstDir = ResolveInsideOrFull(rootFull, dstDirTarget);
                    var srcDirLabel = await _localizer.TextAsync("OpLabelSourceDir", "Source Folder", Array.Empty<object?>(), ct);
                    var dstDirLabel = await _localizer.TextAsync("OpLabelDestDir", "Destination Folder", Array.Empty<object?>(), ct);
                    if (!await EnsurePathTrustedAsync(srcDir, srcDirLabel) || !await EnsurePathTrustedAsync(dstDir, dstDirLabel))
                    {
                        skippedCount++;
                        logRows.Add((DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"), CsvStage1Move, CsvActionBatchMove, $"{srcDir}\\{pattern}", dstDir, CsvStatusSkippedUnauthorized, reason));
                        continue;
                    }

                    foreach (var matchedFile in Directory.EnumerateFiles(srcDir, pattern, SearchOption.TopDirectoryOnly).ToList())
                    {
                        ct.ThrowIfCancellationRequested();
                        var targetFile = Path.Combine(dstDir, Path.GetFileName(matchedFile));
                        await MoveOneAsync(matchedFile, targetFile, reason);
                    }
                    continue;
                }

                if (string.IsNullOrWhiteSpace(srcRaw) || string.IsNullOrWhiteSpace(dstRaw))
                    continue;

                var src = ResolveInsideOrFull(rootFull, srcRaw);
                var dst = ResolveInsideOrFull(rootFull, dstRaw);
                await MoveOneAsync(src, dst, reason);
            }
        }

        string? quarantineDir = null;
        var quarantinedFiles = new List<string>();
        if (args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty("delete_candidates", out var delEl) &&
            delEl.ValueKind == JsonValueKind.Array &&
            delEl.GetArrayLength() > 0)
        {
            quarantineDir = Path.Combine(rootFull, $"{QuarantineFolderPrefix}{DateTimeOffset.Now:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(quarantineDir);

            foreach (var item in delEl.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                string? srcRaw = null;
                string reason = "";
                if (item.ValueKind == JsonValueKind.String)
                {
                    srcRaw = item.GetString();
                }
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    srcRaw = item.TryGetProperty("source", out var sEl) ? sEl.GetString() : null;
                    reason = item.TryGetProperty("reason", out var rEl) ? rEl.GetString() ?? "" : "";
                }
                if (string.IsNullOrWhiteSpace(srcRaw)) continue;

                var src = ResolveInsideOrFull(rootFull, srcRaw);
                var nowStr = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");

                if (isDriveRoot || isUserProfileRoot)
                {
                    skippedCount++;
                    logRows.Add((nowStr, CsvStage1Quarantine, CsvActionQuarantine, src, quarantineDir, CsvStatusSkippedRootDelete, reason));
                    continue;
                }

                if (IsSensitiveFileName(src) || IsProtectedFromOrganize(src, isDriveRoot, isUserProfileRoot) || IsRunningAppPath(src))
                {
                    skippedCount++;
                    logRows.Add((nowStr, CsvStage1Quarantine, CsvActionQuarantine, src, quarantineDir, CsvStatusSkippedProtectedFile, reason));
                    continue;
                }

                var delLabel = await _localizer.TextAsync("OpLabelDeleteSource", "Delete Candidate", Array.Empty<object?>(), ct);
                if (!await EnsurePathTrustedAsync(src, delLabel))
                {
                    skippedCount++;
                    logRows.Add((nowStr, CsvStage1Quarantine, CsvActionQuarantine, src, quarantineDir, CsvStatusSkippedUnauthorized, reason));
                    continue;
                }

                var relName = IsUnderFolder(src, rootFull)
                    ? Path.GetRelativePath(rootFull, src)
                    : Path.GetFileName(src);
                var dst = Path.Combine(quarantineDir, relName);

                var (ok, actualDst, err) = MoveEntrySafely(src, dst);
                if (ok)
                {
                    quarantinedFiles.Add(actualDst);
                    logRows.Add((nowStr, CsvStage1Quarantine, CsvActionQuarantine, src, actualDst, CsvStatusSuccess, reason));
                }
                else
                {
                    skippedCount++;
                    logRows.Add((nowStr, CsvStage1Quarantine, CsvActionQuarantine, src, dst, $"failed:{err}", reason));
                }
            }

            if (quarantinedFiles.Count == 0 && Directory.Exists(quarantineDir) && !Directory.EnumerateFileSystemEntries(quarantineDir).Any())
            {
                Directory.Delete(quarantineDir, false);
                quarantineDir = null;
            }
        }

        // Stage 2: Request permission to delete ONLY the quarantine folder.
        string stage2Status = "no items to delete";
        if (quarantineDir is not null && Directory.Exists(quarantineDir) && IsUnderFolder(quarantineDir, rootFull))
        {
            var previewLines = quarantinedFiles.Take(20).Select(p => $"- {Path.GetRelativePath(quarantineDir, p)}");
            var moreNote = quarantinedFiles.Count > 20
                ? await _localizer.TextAsync("Stage2MoreNote", "\n...and {0} more item(s)", new object?[] { quarantinedFiles.Count - 20 }, ct)
                : "";
            var stage2Title = await _localizer.TextAsync("Stage2DeleteTitle", "Stage 2: Confirm Deletion", Array.Empty<object?>(), ct);
            var stage2Body = await _localizer.TextAsync(
                "Stage2DeleteMessage",
                "Stage 1 moves finished.\nQuarantine folder (ONLY this folder will be deleted):\n{0}\n\nTotal {1} item(s):\n{2}{3}\n\nAllow deleting ONLY this quarantine folder?\n(Declining keeps it for manual review)",
                new object?[] { quarantineDir, quarantinedFiles.Count, string.Join("\n", previewLines), moreNote },
                ct);
            var approvedDelete = await confirmAsync(stage2Title, stage2Body);

            var nowStr = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
            if (approvedDelete)
            {
                Directory.Delete(quarantineDir, recursive: true);
                stage2Status = $"deleted quarantine folder ({quarantinedFiles.Count} items)";
                logRows.Add((nowStr, CsvStage2Delete, CsvActionDeleteQuarantine, quarantineDir, CsvDestDeleted, CsvStatusSuccess, CsvReasonDeleteAuthorized));
            }
            else
            {
                stage2Status = $"user denied deletion; kept quarantine folder: {quarantineDir}";
                logRows.Add((nowStr, CsvStage2Delete, CsvActionKeepQuarantine, quarantineDir, quarantineDir, CsvStatusKeptManual, CsvReasonKeepManual));
            }
        }

        // Stage 3: Write structured CSV audit log inside targetRoot.
        var csvPath = Path.Combine(rootFull, $"{OrganizeCsvPrefix}{DateTimeOffset.Now:yyyyMMdd_HHmmss}.csv");
        var csvSb = new StringBuilder();
        csvSb.AppendLine(OrganizeCsvHeader);
        foreach (var row in logRows)
        {
            csvSb.AppendLine(string.Join(",",
                CsvEscape(row.Time),
                CsvEscape(row.Stage),
                CsvEscape(row.Action),
                CsvEscape(row.Source),
                CsvEscape(row.Destination),
                CsvEscape(row.Status),
                CsvEscape(row.Reason)));
        }
        await File.WriteAllTextAsync(csvPath, csvSb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ct);

        var movedPreview = movedDetails.Count == 0
            ? ""
            : "\nmoved_items (first 30):\n" + string.Join("\n", movedDetails.Take(30).Select(x => $"  - {x}")) +
              (movedDetails.Count > 30 ? $"\n  ...and {movedDetails.Count - 30} more in CSV" : "");

        return $"organize_folder done: {rootFull}\n" +
               $"- stage1: moved={movedCount}, quarantined={quarantinedFiles.Count}, skipped_or_failed={skippedCount}\n" +
               $"- stage2: {stage2Status}\n" +
               $"- stage3 csv: {csvPath}{movedPreview}";
    }

    private static string ResolveInsideOrFull(string rootFull, string rawPath)
    {
        var trimmed = rawPath.Trim();
        return Path.IsPathRooted(trimmed)
            ? Path.GetFullPath(trimmed)
            : Path.GetFullPath(Path.Combine(rootFull, trimmed));
    }

    private static bool IsUnderFolder(string candidateFull, string rootFull)
    {
        var normRoot = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidateFull.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase) ||
               candidateFull.Equals(rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSensitiveFileName(string fullPath)
    {
        var fileName = Path.GetFileName(fullPath);
        return fileName.Equals(".env", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("gmail_token.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("gmail_credentials.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".db", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDriveRoot(string fullPath)
    {
        var trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length <= 2 && trimmed.EndsWith(':');
    }

    private static bool IsUserProfileRoot(string fullPath)
    {
        var trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrEmpty(userProfile) &&
               trimmed.Equals(userProfile.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private bool IsSystemDirectory(string fullPath)
    {
        var trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var usersRoot = !string.IsNullOrEmpty(userProfile) ? Path.GetDirectoryName(userProfile.TrimEnd(Path.DirectorySeparatorChar)) : null;
        var appDataRoot = !string.IsNullOrEmpty(userProfile) ? Path.Combine(userProfile, "AppData") : null;
        var isInsideSandbox = IsUnderFolder(trimmed, _sandbox.RootPath);

        return IsUnderFolder(trimmed, winDir) ||
               (!string.IsNullOrEmpty(progFiles) && IsUnderFolder(trimmed, progFiles)) ||
               (!string.IsNullOrEmpty(progFilesX86) && IsUnderFolder(trimmed, progFilesX86)) ||
               (!string.IsNullOrEmpty(progData) && IsUnderFolder(trimmed, progData)) ||
               (!string.IsNullOrEmpty(usersRoot) && trimmed.Equals(usersRoot, StringComparison.OrdinalIgnoreCase)) ||
               (!isInsideSandbox && !string.IsNullOrEmpty(appDataRoot) && IsUnderFolder(trimmed, appDataRoot));
    }

    private bool IsProtectedFromOrganize(string fullPath, bool isDriveRoot, bool isUserProfileRoot)
    {
        if (IsSystemDirectory(fullPath))
            return true;

        var name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (isDriveRoot && (string.IsNullOrEmpty(name) || DriveRootProtectedNames.Contains(name)))
            return true;

        if (isUserProfileRoot && (string.IsNullOrEmpty(name) || UserProfileProtectedNames.Contains(name) || name.StartsWith('.') || name.StartsWith("ntuser", StringComparison.OrdinalIgnoreCase)))
            return true;

        try
        {
            var attrs = File.GetAttributes(fullPath);
            if ((attrs & FileAttributes.System) != 0)
                return true;
            if ((isDriveRoot || isUserProfileRoot) && (attrs & FileAttributes.Hidden) != 0)
                return true;
        }
        catch { }

        return false;
    }

    private static bool IsRunningAppPath(string fullPath)
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !string.IsNullOrEmpty(baseDir) &&
               (IsUnderFolder(trimmed, baseDir) || IsUnderFolder(baseDir, trimmed));
    }

    private static (bool Ok, string ActualDest, string Error) MoveEntrySafely(string src, string dst)
    {
        try
        {
            if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase))
                return (true, dst, "");

            if (File.Exists(src))
            {
                var dstDir = Path.GetDirectoryName(dst)!;
                Directory.CreateDirectory(dstDir);
                var finalDst = EnsureUniqueFilePath(dst);
                File.Move(src, finalDst);
                return (true, finalDst, "");
            }

            if (Directory.Exists(src))
            {
                var dstParent = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dstParent))
                    Directory.CreateDirectory(dstParent);
                var finalDst = EnsureUniqueDirectoryPath(dst);
                Directory.Move(src, finalDst);
                return (true, finalDst, "");
            }

            return (false, dst, "source does not exist");
        }
        catch (Exception ex)
        {
            return (false, dst, ex.Message);
        }
    }

    private static string EnsureUniqueFilePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 1; i < 10000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem}_{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{stem}_{Guid.NewGuid():N}{ext}");
    }

    private static string EnsureUniqueDirectoryPath(string path)
    {
        if (!Directory.Exists(path)) return path;
        for (int i = 1; i < 10000; i++)
        {
            var candidate = $"{path}_{i}";
            if (!Directory.Exists(candidate)) return candidate;
        }
        return $"{path}_{Guid.NewGuid():N}";
    }

    private static string CsvEscape(string? value)
    {
        var v = value ?? "";
        return v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
            ? $"\"{v.Replace("\"", "\"\"")}\""
            : v;
    }
}
