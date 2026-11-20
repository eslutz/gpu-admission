using Microsoft.Win32;
using System.Text.Json.Nodes;

enum AppearanceMode { System, Light, Dark }

sealed record Palette(Color Page, Color Chrome, Color Text, Color Muted, Color Divider, Color Border,
    Color Hover, Color Accent, Color AccentText, Color Focus, Color Disabled, Color DisabledText,
    Color Pause, Color PauseText, bool Dark = false, bool HighContrast = false)
{
    static Color Hex(int rgb) => Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);

    public static readonly Palette Light = new(Hex(0xf7f8fb), Hex(0xedf1f7), Hex(0x151d2b), Hex(0x455468),
        Hex(0xc5cfdd), Hex(0x728097), Hex(0xe4ebf6), Hex(0x2254ad), Color.White, Hex(0x204b9b),
        Hex(0xe1e7f0), Hex(0x4a596e), Hex(0xffe3b5), Hex(0x73420c));

    public static readonly Palette Night = new(Hex(0x161a22), Hex(0x13171e), Hex(0xf3f5f9), Hex(0xacb8ca),
        Hex(0x495566), Hex(0x68788d), Hex(0x283344), Hex(0x86abff), Hex(0x102043), Hex(0xb8d0ff),
        Hex(0x252e39), Hex(0xa7b5c8), Hex(0xffb747), Hex(0x33220d), Dark: true);

    public static Palette WindowsContrast => new(SystemColors.Window, SystemColors.Window, SystemColors.WindowText,
        SystemColors.WindowText, SystemColors.WindowText, SystemColors.WindowText, SystemColors.Window,
        SystemColors.Highlight, SystemColors.HighlightText, SystemColors.WindowText, SystemColors.Window,
        SystemColors.WindowText, SystemColors.Window, SystemColors.WindowText, HighContrast: true);
}

sealed class Appearance
{
    readonly string path;
    public AppearanceMode Mode { get; private set; }
    public Palette Colors { get; private set; } = Palette.Light;
    public event Action? Changed;

    public Appearance(string root)
    {
        path = Path.Combine(root, "appearance.json");
        Mode = AdmissionFiles.Value(AdmissionFiles.Read(path), "theme") switch
        {
            "Light" => AppearanceMode.Light,
            "Dark" => AppearanceMode.Dark,
            _ => AppearanceMode.System
        };
        Refresh();
    }

    public void Set(AppearanceMode mode)
    {
        if (Mode == mode) return;
        AdmissionFiles.Write(path, new JsonObject { ["theme"] = mode.ToString() });
        Mode = mode;
        Refresh(preferenceChanged: true);
    }

    public void Refresh(bool preferenceChanged = false)
    {
        var next = Resolve(Mode, Mode == AppearanceMode.System && WindowsPrefersDark(), SystemInformation.HighContrast);
        bool redraw = next != Colors || preferenceChanged || next.HighContrast;
        Colors = next;
        var nativeMode = Colors.Dark ? SystemColorMode.Dark : SystemColorMode.Classic;
        if (Application.ColorMode != nativeMode)
            Application.SetColorMode(nativeMode);
        if (redraw) Changed?.Invoke();
    }

    internal static Palette Resolve(AppearanceMode mode, bool windowsDark, bool highContrast) =>
        highContrast ? Palette.WindowsContrast :
        mode == AppearanceMode.Dark || (mode == AppearanceMode.System && windowsDark) ? Palette.Night : Palette.Light;

    static bool WindowsPrefersDark()
    {
        try
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1) is 0;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return Application.SystemColorMode == SystemColorMode.Dark;
        }
    }
}
