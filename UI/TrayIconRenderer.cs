using System.Drawing.Imaging;

namespace AIUsage.UI;

/// <summary>
/// Draws one vertical orange bar per provider on a transparent background.
/// Half full means usage is exactly on pace for the period; full means twice the pace or more.
/// </summary>
static class TrayIconRenderer
{
    public static Bitmap Render(IReadOnlyList<double?> fills, int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);

        int count = Math.Max(fills.Count, 1);
        int gap = Math.Max(1, (int)Math.Round(size / 8.0));
        int barWidth = Math.Max(1, (size - gap * (count - 1)) / count);
        int left = (size - (barWidth * count + gap * (count - 1))) / 2;
        int top = Math.Max(0, (int)Math.Round(size / 16.0));
        int height = size - top * 2;

        using var track = new SolidBrush(Color.FromArgb(80, Theme.Orange));
        using var unavailable = new SolidBrush(Color.FromArgb(90, 150, 150, 150));
        using var fill = new SolidBrush(Theme.Orange);

        for (int i = 0; i < count; i++)
        {
            var value = i < fills.Count ? fills[i] : null;
            int x = left + i * (barWidth + gap);
            g.FillRectangle(value is null ? unavailable : track, x, top, barWidth, height);
            if (value is double v && v > 0)
            {
                int filled = Math.Max(1, (int)Math.Round(v * height));
                g.FillRectangle(fill, x, top + height - filled, barWidth, filled);
            }
        }
        return bitmap;
    }

    public static Icon ToIcon(Bitmap bitmap)
    {
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            Native.DestroyIcon(handle);
        }
    }

    public static Icon AppIcon(int size = 32)
    {
        using var bitmap = Render([0.5, 0.8, 0.35], size);
        return ToIcon(bitmap);
    }
}
