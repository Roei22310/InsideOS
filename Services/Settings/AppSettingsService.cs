using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace InsideOS.Services.Settings;

/// <summary>
/// Tiny persistent app settings (JSON on disk). Load never throws — a broken
/// or missing file simply yields defaults.
/// </summary>
public sealed class AppSettingsService
{
    private static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InsideOS");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public bool OnboardingCompleted { get; set; }

    public List<string> CompletedLessons { get; set; } = new();

    public static AppSettingsService Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettingsService>(File.ReadAllText(SettingsPath)) ?? new AppSettingsService();
        }
        catch
        {
            // Corrupt settings — fall back to defaults.
        }
        return new AppSettingsService();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this));
        }
        catch
        {
            // Non-fatal: onboarding would simply show again next launch.
        }
    }
}
