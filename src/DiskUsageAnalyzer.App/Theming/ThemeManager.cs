using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace DiskUsageAnalyzer.App.Theming;

public sealed class ThemeManager
{
    private const int DwmUseImmersiveDarkMode = 20;
    private readonly ThemeSettingsStore _settings;
    private ResourceDictionary? _palette;

    public ThemeManager(ThemeSettingsStore settings)
    {
        _settings = settings;
        Preference = settings.Load();
    }

    public ThemePreference Preference { get; private set; }
    public bool IsDarkTheme { get; private set; }

    public void Apply(ThemePreference preference, bool persist = true)
    {
        Preference = preference;
        if (persist) _settings.Save(preference);

        IsDarkTheme = preference == ThemePreference.Dark
            || preference == ThemePreference.System && SystemUsesDarkTheme();

        var resources = System.Windows.Application.Current.Resources.MergedDictionaries;
        if (_palette is not null) resources.Remove(_palette);

        _palette = new ResourceDictionary
        {
            Source = new Uri(
                $"/DiskUsageAnalyzer;component/Themes/{(IsDarkTheme ? "Dark" : "Light")}.xaml",
                UriKind.Relative)
        };
        resources.Insert(0, _palette);

        foreach (Window window in System.Windows.Application.Current.Windows)
            ApplyTitleBar(window);
    }

    public void RefreshSystemTheme()
    {
        if (Preference == ThemePreference.System) Apply(Preference, persist: false);
    }

    public void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var enabled = IsDarkTheme ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref enabled, sizeof(int));
    }

    private static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);
}
