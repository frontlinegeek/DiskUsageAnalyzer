using System.IO;
using DiskUsageAnalyzer.App.Theming;
using DiskUsageAnalyzer.Tests;

namespace DiskUsageAnalyzer.App.Tests;

public sealed class ThemeSettingsStoreTests
{
    [Theory]
    [InlineData(ThemePreference.Light)]
    [InlineData(ThemePreference.Dark)]
    [InlineData(ThemePreference.System)]
    public void SaveAndLoad_RoundTripsThemePreference(ThemePreference preference)
    {
        using var directory = TemporaryDirectory.Create();
        var store = new ThemeSettingsStore(Path.Combine(directory.Path, "settings.json"));

        store.Save(preference);

        Assert.Equal(preference, store.Load());
    }

    [Fact]
    public void Load_InvalidSettingsFallsBackToSystem()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(path, "{ invalid json");

        Assert.Equal(ThemePreference.System, new ThemeSettingsStore(path).Load());
    }
}
