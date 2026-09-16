using System.Windows;
using DiskUsageAnalyzer.App.Theming;

namespace DiskUsageAnalyzer.App;

public partial class App : System.Windows.Application
{
    public ThemeManager ThemeManager { get; } = new(new ThemeSettingsStore());

    protected override void OnStartup(StartupEventArgs e)
    {
        ThemeManager.Apply(ThemeManager.Preference, persist: false);
        base.OnStartup(e);
    }
}
