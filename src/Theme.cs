using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace TaskbarLyrics;

/// <summary>
/// Светлая/тёмная тема. Цвета кладутся в ресурсы приложения (DynamicResource), поэтому
/// меню и окно выбора шрифта перекрашиваются на лету.
/// </summary>
public static class Theme
{
    public static bool IsLight { get; private set; } = true;
    public static Color Accent { get; private set; } = Color.FromRgb(0x00, 0x78, 0xD4);

    static bool ReadLight(string name, bool fallback)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue(name) is int v ? v == 1 : fallback;
        }
        catch { return fallback; }
    }

    /// <summary>Светлая ли панель задач (от неё зависит цвет строки текста).</summary>
    public static bool TaskbarIsLight(ThemeMode m) => m switch
    {
        ThemeMode.Light => true,
        ThemeMode.Dark => false,
        _ => ReadLight("SystemUsesLightTheme", false),
    };

    /// <summary>Светлая ли тема приложений (от неё зависят меню и окна).</summary>
    public static bool AppsAreLight(ThemeMode m) => m switch
    {
        ThemeMode.Light => true,
        ThemeMode.Dark => false,
        _ => ReadLight("AppsUseLightTheme", true),
    };

    public static void Apply(ThemeMode mode)
    {
        IsLight = AppsAreLight(mode);
        var r = Application.Current.Resources;
        var accent = AccentColor();
        Accent = accent;

        void Set(string key, Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            r[key] = b;
        }

        if (IsLight)
        {
            Set("MenuBackground", Color.FromArgb(0x99, 0xF9, 0xF9, 0xF9));
            Set("MenuBorder", Color.FromArgb(0x26, 0x00, 0x00, 0x00));
            Set("MenuForeground", Color.FromRgb(0x1A, 0x1A, 0x1A));
            Set("MenuForegroundDim", Color.FromArgb(0x99, 0x1A, 0x1A, 0x1A));
            Set("MenuHover", Color.FromArgb(0x12, 0x00, 0x00, 0x00));
            Set("MenuSeparator", Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
            Set("WindowBackground", Color.FromRgb(0xF3, 0xF3, 0xF3));
            Set("ControlBackground", Color.FromRgb(0xFF, 0xFF, 0xFF));
            Set("ControlBorder", Color.FromArgb(0x29, 0x00, 0x00, 0x00));
            Set("ScrollThumb", Color.FromArgb(0x55, 0x00, 0x00, 0x00));
        }
        else
        {
            Set("MenuBackground", Color.FromArgb(0x8C, 0x2B, 0x2B, 0x2B));
            Set("MenuBorder", Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            Set("MenuForeground", Color.FromRgb(0xF2, 0xF2, 0xF2));
            Set("MenuForegroundDim", Color.FromArgb(0x99, 0xF2, 0xF2, 0xF2));
            Set("MenuHover", Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
            Set("MenuSeparator", Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
            Set("WindowBackground", Color.FromRgb(0x20, 0x20, 0x20));
            Set("ControlBackground", Color.FromRgb(0x2D, 0x2D, 0x2D));
            Set("ControlBorder", Color.FromArgb(0x29, 0xFF, 0xFF, 0xFF));
            Set("ScrollThumb", Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
        }

        Set("Accent", accent);
        Set("AccentForeground", Colors.White);
        Set("SelectedBackground", Color.FromArgb(0x40, accent.R, accent.G, accent.B));
    }

    /// <summary>Акцентный цвет Windows.</summary>
    static Color AccentColor()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (k?.GetValue("AccentColor") is int v)
            {
                var u = unchecked((uint)v); // ABGR
                return Color.FromRgb((byte)u, (byte)(u >> 8), (byte)(u >> 16));
            }
        }
        catch { }
        return Color.FromRgb(0x00, 0x78, 0xD4);
    }

    /// <summary>Tint for the blur on Windows 10 (on Windows 11 the system acrylic is used).</summary>
    public static Color BlurTint => IsLight ? Color.FromArgb(0x99, 0xF3, 0xF3, 0xF3) : Color.FromArgb(0x99, 0x20, 0x20, 0x20);
}
