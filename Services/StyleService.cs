using System.Text;
using System.Text.RegularExpressions;

namespace AiSecretary.Desktop.Services;

public sealed record StyleProfileEntry(string DisplayName, string FilePath);

/// <summary>
/// Loads optional local Markdown persona/style prompts (without TTS) from the local styles/ folder.
/// </summary>
public sealed class StyleService
{
    private string? _activeStylePath;
    private string? _cachedPrompt;
    private string? _cachedTtsLang;
    private bool _ttsLangCached;

    public string? ActiveStylePath => _activeStylePath;

    public string StylesDirectory => Path.Combine(AppContext.BaseDirectory, "styles");
    public string UserStylesDirectory => AppPaths.UserStylesDir;

    public void SetActiveStylePath(string? path)
    {
        var normalized = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        if (!string.Equals(_activeStylePath, normalized, StringComparison.OrdinalIgnoreCase))
        {
            _activeStylePath = normalized;
            _cachedPrompt = null;
            _cachedTtsLang = null;
            _ttsLangCached = false;
        }
    }

    public IReadOnlyList<StyleProfileEntry> GetAvailableStyles()
    {
        var dirs = new[]
        {
            UserStylesDirectory,
            StylesDirectory,
            Path.Combine(Directory.GetCurrentDirectory(), "styles")
        }.Distinct(StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<StyleProfileEntry>();

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.md", SearchOption.TopDirectoryOnly))
            {
                var full = Path.GetFullPath(file);
                var name = Path.GetFileNameWithoutExtension(full);
                if (seen.Add(name))
                {
                    list.Add(new StyleProfileEntry(name, full));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(_activeStylePath) && File.Exists(_activeStylePath))
        {
            var full = Path.GetFullPath(_activeStylePath);
            var name = Path.GetFileNameWithoutExtension(full);
            if (seen.Add(name))
            {
                list.Add(new StyleProfileEntry(name, full));
            }
        }

        return list.OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public string ImportStyleFile(string sourcePath)
    {
        Directory.CreateDirectory(UserStylesDirectory);
        var fileName = Path.GetFileName(sourcePath);
        var dest = Path.Combine(UserStylesDirectory, fileName);
        File.Copy(sourcePath, dest, overwrite: true);
        SetActiveStylePath(dest);
        return dest;
    }

    public string? GetActiveStylePrompt()
    {
        if (string.IsNullOrWhiteSpace(_activeStylePath) || !File.Exists(_activeStylePath))
            return null;

        if (_cachedPrompt is not null)
            return _cachedPrompt;

        try
        {
            var md = File.ReadAllText(_activeStylePath, Encoding.UTF8);
            var m = Regex.Match(md, @"```(?:text)?\s*\r?\n([\s\S]*?)```", RegexOptions.IgnoreCase);
            _cachedPrompt = m.Success ? m.Groups[1].Value.Trim() : md.Trim();
            return _cachedPrompt;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>從目前啟用的 `.md` 讀取 `tts_lang` 設定（若未指定則回傳 null）。</summary>
    public string? GetActiveTtsLanguage()
    {
        if (string.IsNullOrWhiteSpace(_activeStylePath) || !File.Exists(_activeStylePath))
            return null;
        if (_ttsLangCached)
            return _cachedTtsLang;

        _cachedTtsLang = ParseTtsLanguageFromMd(_activeStylePath);
        _ttsLangCached = true;
        return _cachedTtsLang;
    }

    internal static string? ParseTtsLanguageFromMd(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var md = File.ReadAllText(path, Encoding.UTF8);
            var m = Regex.Match(md, @"(?im)^\s*(?:tts_lang|tts-lang|tts語言)\s*[:：]\s*([^\r\n]+)");
            if (!m.Success) return null;
            var val = m.Groups[1].Value.Trim().Trim('`', '"', '\'');
            return string.IsNullOrWhiteSpace(val) ? null : val;
        }
        catch
        {
            return null;
        }
    }
}
