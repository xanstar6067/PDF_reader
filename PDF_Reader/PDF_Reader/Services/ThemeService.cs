using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace PDF_Reader.Services;

public enum AppTheme { System, Light, Dark }

/// <summary>Changes UI resources only; PDF rendering and annotation colours are independent.</summary>
public sealed class ThemeService : IDisposable
{
    private readonly SettingsService _settings;
    public AppTheme Theme => _settings.Value.Theme;
    public bool IsDark { get; private set; }

    public ThemeService(SettingsService settings)
    {
        _settings = settings;
        SystemEvents.UserPreferenceChanged += PreferencesChanged;
        Apply();
    }

    public void SetTheme(AppTheme theme)
    {
        _settings.Value.Theme = theme;
        Apply();
        _settings.TrySave();
    }

    private void PreferencesChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (Theme == AppTheme.System)
            Application.Current?.Dispatcher.BeginInvoke(Apply);
    }

    private void Apply()
    {
        IsDark = Theme == AppTheme.Dark || Theme == AppTheme.System && SystemIsDark();
        var resources = Application.Current.Resources;
        string[] keys = ["Accent", "Ink", "Muted", "Line", "WindowBackground", "Surface", "Subtle", "Workspace", "Hover", "Pressed", "Selected", "Comment", "Info", "Danger"];
        string[] light = ["#D73532", "#242834", "#747B88", "#E4E6EB", "#F6F7F9", "#FFFFFF", "#F7F8FA", "#E7E9EE", "#F0F2F6", "#E9EDF3", "#FDEDEC", "#FFF9E7", "#F2F6FD", "#B6403A"];
        string[] dark = ["#FF8580", "#F3F3F3", "#B5B5B5", "#454545", "#202020", "#272727", "#2D2D2D", "#181818", "#383838", "#414141", "#49302F", "#3A3425", "#293747", "#FF9690"];
        var palette = IsDark ? dark : light;
        for (var i = 0; i < keys.Length; i++)
            resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette[i]));

        // Default WPF templates (trees, tab content, popups) also use these system keys.
        resources[SystemColors.WindowBrushKey] = resources["Surface"];
        resources[SystemColors.WindowTextBrushKey] = resources["Ink"];
        resources[SystemColors.ControlBrushKey] = resources["Surface"];
        resources[SystemColors.ControlTextBrushKey] = resources["Ink"];
        resources[SystemColors.ControlDarkBrushKey] = resources["Line"];
        resources[SystemColors.ControlLightBrushKey] = resources["Hover"];
        resources[SystemColors.ControlLightLightBrushKey] = resources["Surface"];
        resources[SystemColors.GrayTextBrushKey] = resources["Muted"];
        resources[SystemColors.HighlightBrushKey] = resources["Selected"];
        resources[SystemColors.HighlightTextBrushKey] = resources["Ink"];
        resources[SystemColors.InactiveSelectionHighlightBrushKey] = resources["Hover"];
        resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = resources["Ink"];
        foreach (Window window in Application.Current.Windows) UpdateTitleBar(window);
    }

    private static bool SystemIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return false;
        }
    }

    public static void UpdateTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var dark = Application.Current.TryFindResource("WindowBackground") is SolidColorBrush brush && brush.Color.R < 128 ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        var caption = dark == 1 ? 0x00202020 : 0x00F9F7F6;
        var text = dark == 1 ? 0x00F3F3F3 : 0x00342824;
        _ = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public void Dispose() => SystemEvents.UserPreferenceChanged -= PreferencesChanged;
}
