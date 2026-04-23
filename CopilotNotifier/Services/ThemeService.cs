using Microsoft.Win32;

namespace CopilotNotifier.Services;

public enum AppTheme
{
    Dark,
    Light
}

public static class ThemeService
{
    public static AppTheme GetSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize", false);
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int intVal)
                return intVal == 1 ? AppTheme.Light : AppTheme.Dark;
        }
        catch { }

        return AppTheme.Dark;
    }

    public static void ApplyTheme(System.Windows.Application app)
    {
        var theme = GetSystemTheme();
        var resources = app.Resources;

        if (theme == AppTheme.Light)
        {
            // Light theme
            resources["PopupBackground"] = ParseBrush("#E6F5F5F5");
            resources["PopupBorderBrush"] = ParseBrush("#E6DDDDDD");
            resources["PopupShadowColor"] = ParseColor("#40000000");
            resources["TitleForeground"] = ParseBrush("#1E1E1E");
            resources["MessageForeground"] = ParseBrush("#555555");
            resources["TimeForeground"] = ParseBrush("#888888");
            resources["CloseButtonForeground"] = ParseBrush("#999999");
            resources["CloseButtonHoverForeground"] = ParseBrush("#333333");

            resources["SettingsBackground"] = ParseBrush("#F3F3F3");
            resources["SettingsHeaderForeground"] = ParseBrush("#1E1E1E");
            resources["SettingsSectionForeground"] = ParseBrush("#666666");
            resources["SettingsTextForeground"] = ParseBrush("#1E1E1E");
            resources["SettingsButtonBackground"] = ParseBrush("#E0E0E0");
            resources["SettingsButtonForeground"] = ParseBrush("#1E1E1E");
            resources["SettingsButtonBorder"] = ParseBrush("#CCCCCC");
            resources["AccentButtonBackground"] = ParseBrush("#0078D4");
            resources["AccentButtonForeground"] = ParseBrush("#FFFFFF");
        }
        else
        {
            // Dark theme
            resources["PopupBackground"] = ParseBrush("#E6202020");
            resources["PopupBorderBrush"] = ParseBrush("#E6333333");
            resources["PopupShadowColor"] = ParseColor("#80000000");
            resources["TitleForeground"] = ParseBrush("#FFFFFF");
            resources["MessageForeground"] = ParseBrush("#CCCCCC");
            resources["TimeForeground"] = ParseBrush("#888888");
            resources["CloseButtonForeground"] = ParseBrush("#888888");
            resources["CloseButtonHoverForeground"] = ParseBrush("#FFFFFF");

            resources["SettingsBackground"] = ParseBrush("#1E1E1E");
            resources["SettingsHeaderForeground"] = ParseBrush("#FFFFFF");
            resources["SettingsSectionForeground"] = ParseBrush("#AAAAAA");
            resources["SettingsTextForeground"] = ParseBrush("#FFFFFF");
            resources["SettingsButtonBackground"] = ParseBrush("#333333");
            resources["SettingsButtonForeground"] = ParseBrush("#FFFFFF");
            resources["SettingsButtonBorder"] = ParseBrush("#555555");
            resources["AccentButtonBackground"] = ParseBrush("#0078D4");
            resources["AccentButtonForeground"] = ParseBrush("#FFFFFF");
        }
    }

    private static System.Windows.Media.SolidColorBrush ParseBrush(string hex)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        return new System.Windows.Media.SolidColorBrush(color);
    }

    private static System.Windows.Media.Color ParseColor(string hex)
    {
        return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
    }
}
