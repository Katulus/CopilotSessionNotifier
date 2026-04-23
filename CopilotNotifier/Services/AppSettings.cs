using System.IO;
using System.Text.Json;

namespace CopilotNotifier.Services;

public class AppSettings
{
    public bool AutoStart { get; set; }
    public bool PlaySound { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 3;
    public bool NotifyOnWaitingForInput { get; set; } = true;
    public bool NotifyOnSessionComplete { get; set; } = true;
    public bool NotifyOnTaskComplete { get; set; } = true;

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CopilotNotifier",
        "settings.json"
    );

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { }

        var settings = new AppSettings();
        settings.AutoStart = AutoStartService.IsAutoStartEnabled();
        return settings;
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);

            AutoStartService.SetAutoStart(AutoStart);
        }
        catch { }
    }
}
