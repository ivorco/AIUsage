using System.Drawing.Imaging;
using AIUsage.Core;

namespace AIUsage.UI;

/// <summary>
/// Draws one vertical bar per provider, in the provider's color, on a transparent background.
/// Each bar shows the provider's most urgent meter (see <see cref="ProviderSnapshot.TrayMeter"/>).
/// </summary>
static class TrayIconRenderer
{
    public static List<(double? Fill, Color Color)> Bars(IEnumerable<ProviderSnapshot> snapshots, DateTimeOffset now, bool lightTaskbar) =>
        snapshots.Select(s => (s.TrayFill(now), Theme.TrayColor(s.Accent, lightTaskbar))).ToList();

    public static Bitmap Render(IReadOnlyList<(double? Fill, Color Color)> bars, int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);

        int count = Math.Max(bars.Count, 1);
        int gap = Math.Max(1, (int)Math.Round(size / 8.0));
        int barWidth = Math.Max(1, (size - gap * (count - 1)) / count);
        int left = (size - (barWidth * count + gap * (count - 1))) / 2;
        int top = Math.Max(0, (int)Math.Round(size / 16.0));
        int height = size - top * 2;

        using var unavailable = new SolidBrush(Color.FromArgb(90, 150, 150, 150));
        for (int i = 0; i < count; i++)
        {
            var (value, color) = i < bars.Count ? bars[i] : ((double?)null, Color.Gray);
            int x = left + i * (barWidth + gap);
            if (value is not double v)
            {
                g.FillRectangle(unavailable, x, top, barWidth, height);
                continue;
            }

            using var track = new SolidBrush(Color.FromArgb(80, color));
            g.FillRectangle(track, x, top, barWidth, height);
            if (v > 0)
            {
                using var fill = new SolidBrush(color);
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
        double[] samples = [0.5, 0.8, 0.35];
        var bars = ProviderRegistry.Create().Select((p, i) => ((double?)samples[i % samples.Length], p.Accent)).ToList();
        using var bitmap = Render(bars, size);
        return ToIcon(bitmap);
    }
}
