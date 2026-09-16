using System.IO;
using System.Text.Json;

namespace DiskUsageAnalyzer.App.Theming;

public sealed class ThemeSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;

    public ThemeSettingsStore(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(AppContext.BaseDirectory, "settings.json");
    }

    public ThemePreference Load()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return ThemePreference.System;
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath), SerializerOptions);
            return Enum.TryParse<ThemePreference>(settings?.Theme, true, out var preference)
                ? preference
                : ThemePreference.System;
        }
        catch (IOException)
        {
            return ThemePreference.System;
        }
        catch (UnauthorizedAccessException)
        {
            return ThemePreference.System;
        }
        catch (JsonException)
        {
            return ThemePreference.System;
        }
    }

    public void Save(ThemePreference preference)
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(_settingsPath,
                JsonSerializer.Serialize(new AppSettings { Theme = preference.ToString() }, SerializerOptions));
        }
        catch (IOException)
        {
            // A read-only portable location must not prevent the application from running.
        }
        catch (UnauthorizedAccessException)
        {
            // A read-only portable location must not prevent the application from running.
        }
    }

    private sealed class AppSettings
    {
        public string? Theme { get; init; }
    }
}
