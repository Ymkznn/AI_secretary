using Microsoft.Win32;

namespace AiSecretary.Desktop.Services;

public static class ThemeHelper
{
    public static bool IsDarkEffective(string mode) => mode switch
    {
        "Dark" => true,
        "Light" => false,
        _ => IsWindowsDarkTheme()
    };

    private static bool IsWindowsDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int val)
            {
                return val == 0;
            }
        }
        catch
        {
            // Default to light if registry read fails
        }
        return false;
    }
}
