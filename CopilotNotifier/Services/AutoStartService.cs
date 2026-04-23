using Microsoft.Win32;

namespace CopilotNotifier.Services;

public static class AutoStartService
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "CopilotNotifier";

    public static bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(AppName) != null;
    }

    public static void SetAutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (key == null) return;

        if (enabled)
        {
            var exePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
            key.SetValue(AppName, $"\"{exePath}\" --minimized");
        }
        else
        {
            key.DeleteValue(AppName, false);
        }
    }
}
