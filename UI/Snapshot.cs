using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using AIUsage.Core;

namespace AIUsage.UI;

/// <summary>
/// <c>AIUsage.exe --snapshot &lt;dir&gt;</c>: fetches all providers once, then writes snapshot.json plus PNG
/// renders of the tray icon, popup and settings dialog. Used to test without clicking through the tray.
/// </summary>
static class Snapshot
{
    public static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var service = new UsageService(ProviderRegistry.Create());
        service.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        var now = DateTimeOffset.Now;

        var report = service.Latest.Select(s => new
        {
            s.ProviderId,
            s.PlanText,
            s.Error,
            TrayFill = s.TrayFill(now),
            PaceRatio = s.PaceRatio(now),
            Meters = s.Meters.Select(m => new
            {
                m.Label,
                m.Used,
                Expected = m.Expected(now),
                PaceFill = m.PaceFill(now),
                m.AffectsTray,
                m.Detail,
                m.PeriodStart,
                m.PeriodEnd,
                Reset = Format.ResetText(m, now),
            }),
        });
        File.WriteAllText(Path.Combine(directory, "snapshot.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        var fills = service.Latest.Select(s => s.TrayFill(now)).ToList();
        foreach (var size in new[] { 16, 20, 24, 32 })
        {
            using var icon = TrayIconRenderer.Render(fills, size);
            icon.Save(Path.Combine(directory, $"tray-{size}.png"), ImageFormat.Png);
        }
        RenderTrayPreview(fills, Path.Combine(directory, "tray-preview.png"));

        foreach (var scale in new[] { 1f, 1.5f })
        {
            var size = PopupRenderer.Measure(service.Latest, scale);
            using var bitmap = new Bitmap(size.Width, size.Height);
            using (var g = Graphics.FromImage(bitmap))
                PopupRenderer.Paint(g, service.Latest, now, scale, new PopupState(false, service.LastUpdated, null));
            bitmap.Save(Path.Combine(directory, $"popup-{scale * 100:0}.png"), ImageFormat.Png);
        }

        using (var form = new SettingsForm(service.Providers))
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-20000, -20000);
            form.ShowInTaskbar = false;
            form.Show();
            Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(Path.Combine(directory, "settings.png"), ImageFormat.Png);
            form.Close();
        }
        return 0;
    }

    /// <summary>The 24px icon magnified 8× on a dark and a light taskbar color.</summary>
    static void RenderTrayPreview(IReadOnlyList<double?> fills, string path)
    {
        const int magnified = 24 * 8, margin = 20;
        using var icon = TrayIconRenderer.Render(fills, 24);
        using var preview = new Bitmap((magnified + margin * 2) * 2, magnified + margin * 2);
        using var g = Graphics.FromImage(preview);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        int half = preview.Width / 2;
        using (var dark = new SolidBrush(Color.FromArgb(0x1C, 0x1C, 0x1C)))
            g.FillRectangle(dark, 0, 0, half, preview.Height);
        using (var light = new SolidBrush(Color.FromArgb(0xEE, 0xEE, 0xEE)))
            g.FillRectangle(light, half, 0, half, preview.Height);
        g.DrawImage(icon, new Rectangle(margin, margin, magnified, magnified));
        g.DrawImage(icon, new Rectangle(half + margin, margin, magnified, magnified));
        preview.Save(path, ImageFormat.Png);
    }
}
