using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AIUsage.UI;

/// <summary>Colors for one provider's bars, derived from its accent color.</summary>
/// <param name="DarkText">Whether text drawn over <see cref="Fill"/> must be dark to stay readable.</param>
readonly record struct BarPalette(Color Fill, Color Pale, Color Marker, bool DarkText);

static class Theme
{
    public static readonly Color Background = Color.FromArgb(0x14, 0x14, 0x13);
    public static readonly Color Surface = Color.FromArgb(0x26, 0x25, 0x23);
    public static readonly Color Divider = Color.FromArgb(0x33, 0x31, 0x2E);
    public static readonly Color Track = Color.FromArgb(0x2A, 0x28, 0x25);
    public static readonly Color Orange = Color.FromArgb(0xD9, 0x77, 0x57);
    public static readonly Color PaleOrange = Color.FromArgb(0xC9, 0x8B, 0x74);
    public static readonly Color PaceMarker = Color.FromArgb(0xF7, 0xDA, 0xCE);
    public static readonly Color Text = Color.FromArgb(0xF5, 0xF4, 0xEF);
    public static readonly Color Muted = Color.FromArgb(0xB0, 0xAA, 0xA0);
    public static readonly Color Faint = Color.FromArgb(0x80, 0x7B, 0x73);
    public static readonly Color Error = Color.FromArgb(0xE8, 0x8C, 0x7D);

    public const string UiFont = "Segoe UI";
    public const string UiFontSemibold = "Segoe UI Semibold";

    public static string GlyphFont { get; } = HasFont("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    public static bool IsLight(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255 > 0.6;

    /// <summary>Solid accent for "used", a muted version for "expected", and a marker/text color that contrasts with the accent.</summary>
    public static BarPalette PaletteFor(Color accent) => IsLight(accent)
        ? new BarPalette(accent, Blend(accent, Color.FromArgb(0x50, 0x50, 0x50), 0.45), Background, DarkText: true)
        : new BarPalette(accent, Blend(accent, Color.FromArgb(0xAA, 0xAA, 0xAA), 0.4), Blend(accent, Color.White, 0.75), DarkText: false);

    /// <summary>Tray bars need contrast with the taskbar: a white accent turns dark on a light taskbar.</summary>
    public static Color TrayColor(Color accent, bool lightTaskbar) =>
        lightTaskbar && IsLight(accent) ? Color.FromArgb(0x30, 0x30, 0x30) : accent;

    public static bool TaskbarIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static Color Blend(Color a, Color b, double t) => Color.FromArgb(
        (int)Math.Round(a.R + (b.R - a.R) * t),
        (int)Math.Round(a.G + (b.G - a.G) * t),
        (int)Math.Round(a.B + (b.B - a.B) * t));

    static bool HasFont(string name)
    {
        using var fonts = new InstalledFontCollection();
        return fonts.Families.Any(f => f.Name == name);
    }
}

static class Native
{
    const int DwmUseImmersiveDarkMode = 20;
    const int DwmWindowCornerPreference = 33;
    const int DwmBorderColor = 34;

    public static void UseDarkChrome(IntPtr hwnd, bool rounded = false, Color? border = null)
    {
        int dark = 1;
        DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
        if (rounded)
        {
            int round = 2;
            DwmSetWindowAttribute(hwnd, DwmWindowCornerPreference, ref round, sizeof(int));
        }
        if (border is { } c)
        {
            int colorRef = c.R | (c.G << 8) | (c.B << 16);
            DwmSetWindowAttribute(hwnd, DwmBorderColor, ref colorRef, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);
}
