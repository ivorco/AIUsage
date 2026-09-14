using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace AIUsage.UI;

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
