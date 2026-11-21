using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace AiSecretary.Desktop.Services;

/// <summary>
/// Windows 11 Fluent theming (WPF-UI) strictly scoped to Fluent windows.
/// Never calls ApplicationThemeManager.Apply so non-Fluent windows (pet overlay, chat, monitor) are untouched.
/// Uses explicit Win11 Fluent surface colors (#202020 dark / #F3F3F3 light) + DWM dark mode &amp; rounded corners,
/// avoiding DWM Mica tint mismatches when app theme differs or WPF-UI's global manager is unused.
/// </summary>
public static class FluentThemeHelper
{
    private static ThemesDictionary? _appThemes;
    private static ApplicationTheme _currentTheme = ThemeHelper.IsDarkEffective("System")
        ? ApplicationTheme.Dark
        : ApplicationTheme.Light;

    private static readonly SolidColorBrush DarkCanvasBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)));
    private static readonly SolidColorBrush LightCanvasBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3)));
    private static readonly SolidColorBrush DarkTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)));
    private static readonly SolidColorBrush LightTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)));

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    /// <summary>Call BEFORE a Fluent window's InitializeComponent so accent keys exist while its dictionaries load.</summary>
    public static void EnsureSystemAccent()
    {
        EnsureAppThemesHost();
        PublishAccent(_currentTheme);
    }

    public static void Apply(Window window, bool isDark)
    {
        var theme = isDark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        _currentTheme = theme;

        EnsureAppThemesHost();
        if (_appThemes is not null)
            _appThemes.Theme = theme;

        foreach (var dict in Enumerate(window.Resources))
        {
            if (dict is ThemesDictionary themes)
                themes.Theme = theme;
        }

        PublishAccent(theme);
        MirrorAccentIntoWindow(window);

        window.Background = isDark ? DarkCanvasBrush : LightCanvasBrush;
        window.Foreground = isDark ? DarkTextBrush : LightTextBrush;

        Win11BackdropHelper.Apply(window, isDark);
    }

    private static void EnsureAppThemesHost()
    {
        var app = Application.Current;
        if (app is null || _appThemes is not null) return;

        try
        {
            _appThemes = new ThemesDictionary { Theme = _currentTheme };
            app.Resources.MergedDictionaries.Add(_appThemes);
            if (!app.Resources.Contains("DefaultIconFontSize"))
                app.Resources["DefaultIconFontSize"] = 16d;
        }
        catch
        {
            _appThemes = null;
        }
    }

    private static void PublishAccent(ApplicationTheme theme)
    {
        try
        {
            if (_appThemes is not null)
                _appThemes.Theme = theme;
            ApplicationAccentColorManager.ApplySystemAccent();
        }
        catch
        {
            // Fall back to WPF-UI default accent
        }
    }

    private static void MirrorAccentIntoWindow(Window window)
    {
        var app = Application.Current;
        if (app is null) return;

        try
        {
            foreach (var keyObj in app.Resources.Keys.Cast<object>().ToList())
            {
                if (keyObj is not string key || key.IndexOf("Accent", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var value = app.Resources[key];
                window.Resources[key] = value;

                if (value is Color color && !key.EndsWith("Brush", StringComparison.Ordinal))
                {
                    var brush = new SolidColorBrush(color);
                    brush.Freeze();
                    window.Resources[key + "Brush"] = brush;
                }
            }
        }
        catch
        {
            // Cosmetic only
        }
    }

    private static IEnumerable<ResourceDictionary> Enumerate(ResourceDictionary root)
    {
        yield return root;
        foreach (var child in root.MergedDictionaries)
        {
            foreach (var nested in Enumerate(child))
                yield return nested;
        }
    }
}
