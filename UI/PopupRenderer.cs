using System.Drawing.Drawing2D;
using System.Drawing.Text;
using AIUsage.Core;

namespace AIUsage.UI;

sealed record PopupState(bool Refreshing, DateTimeOffset? LastUpdated, Point? Hover);

/// <summary>Hit-test areas in device pixels.</summary>
sealed class PopupHitAreas
{
    public RectangleF Gear;
    public RectangleF Refresh;
}

/// <summary>
/// Paints the usage popup. All layout is in 96-DPI units and scaled, so the same code renders
/// the live window and headless snapshots.
/// </summary>
static class PopupRenderer
{
    const float Width = 540, Pad = 18, HeaderH = 40, SectionGap = 14, TitleH = 26, NoteH = 20;
    const float RowH = 32, RowDetailH = 42, FooterH = 36, LabelW = 132, DateW = 104, ColGap = 12, BarH = 22;

    static readonly Color LegendUsed = Color.FromArgb(0xC8, 0xC4, 0xBC);
    static readonly Color LegendExpected = Color.FromArgb(0x6E, 0x6A, 0x64);

    sealed class Fonts : IDisposable
    {
        public readonly Font Title = new(Theme.UiFontSemibold, 15f, GraphicsUnit.Pixel);
        public readonly Font Name = new(Theme.UiFontSemibold, 14f, GraphicsUnit.Pixel);
        public readonly Font Body = new(Theme.UiFont, 12.5f, GraphicsUnit.Pixel);
        public readonly Font Small = new(Theme.UiFont, 11.5f, GraphicsUnit.Pixel);
        public readonly Font Bar = new(Theme.UiFontSemibold, 12f, GraphicsUnit.Pixel);
        public readonly Font Glyph = new(Theme.GlyphFont, 15f, GraphicsUnit.Pixel);

        public void Dispose()
        {
            foreach (var font in new[] { Title, Name, Body, Small, Bar, Glyph })
                font.Dispose();
        }
    }

    public static Size Measure(IReadOnlyList<ProviderSnapshot> snapshots, float scale)
    {
        float height = Pad + HeaderH + snapshots.Sum(s => SectionGap + SectionHeight(s)) + SectionGap + FooterH;
        return new Size((int)Math.Ceiling(Width * scale), (int)Math.Ceiling(height * scale));
    }

    static float SectionHeight(ProviderSnapshot s) =>
        TitleH + (Note(s) is null ? 0 : NoteH) + s.Meters.Sum(m => m.Detail is null ? RowH : RowDetailH);

    static string? Note(ProviderSnapshot s) =>
        s.Error ?? (s.Meters.Count > 0 ? null : s.IsLoading ? "Loading…" : "No usage data returned.");

    public static PopupHitAreas Paint(Graphics g, IReadOnlyList<ProviderSnapshot> snapshots, DateTimeOffset now, float scale, PopupState state)
    {
        g.Clear(Theme.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var saved = g.Save();
        g.ScaleTransform(scale, scale);

        using var fonts = new Fonts();
        PointF? hover = state.Hover is { } p ? new PointF(p.X / scale, p.Y / scale) : null;
        var hits = new PopupHitAreas();

        // Header
        float y = Pad;
        DrawText(g, "AI Usage", fonts.Title, Theme.Text, new RectangleF(Pad, y, 240, HeaderH), StringAlignment.Near);
        var gear = new RectangleF(Width - Pad - 32, y + (HeaderH - 32) / 2, 32, 32);
        var refresh = new RectangleF(gear.X - 36, gear.Y, 32, 32);
        DrawIconButton(g, "", gear, fonts.Glyph, hover, dim: false);
        DrawIconButton(g, "", refresh, fonts.Glyph, hover, dim: state.Refreshing);
        hits.Gear = ToDevice(gear, scale);
        hits.Refresh = ToDevice(refresh, scale);
        y += HeaderH;

        foreach (var snapshot in snapshots)
        {
            DrawDivider(g, y + SectionGap / 2);
            y += SectionGap;

            var palette = Theme.PaletteFor(snapshot.Accent);
            var trayMeter = snapshot.TrayMeter(now);

            using (var dot = new SolidBrush(snapshot.Accent))
                g.FillEllipse(dot, Pad, y + TitleH / 2 - 4, 8, 8);
            float nameX = Pad + 14;
            var nameWidth = g.MeasureString(snapshot.DisplayName, fonts.Name).Width;
            DrawText(g, snapshot.DisplayName, fonts.Name, Theme.Text, new RectangleF(nameX, y, nameWidth + 2, TitleH), StringAlignment.Near);
            if (snapshot.PlanText is { } plan)
            {
                float planX = nameX + nameWidth + 4;
                DrawText(g, plan, fonts.Small, Theme.Muted, new RectangleF(planX, y + 1, Width - Pad - planX, TitleH), StringAlignment.Near);
            }
            y += TitleH;

            if (Note(snapshot) is { } note)
            {
                DrawText(g, note, fonts.Small, snapshot.Error is null ? Theme.Muted : Theme.Error, new RectangleF(Pad, y, Width - Pad * 2, NoteH), StringAlignment.Near);
                y += NoteH;
            }

            foreach (var meter in snapshot.Meters)
            {
                float rowHeight = meter.Detail is null ? RowH : RowDetailH;
                DrawMeter(g, meter, now, new RectangleF(Pad, y, Width - Pad * 2, rowHeight), fonts, palette, drivesTray: ReferenceEquals(meter, trayMeter));
                y += rowHeight;
            }
        }

        // Footer: legend and last update
        DrawDivider(g, y + SectionGap / 2);
        y += SectionGap;
        var line = new RectangleF(0, y, 0, FooterH - Pad / 2);
        float x = DrawLegendItem(g, Pad, line, LegendUsed, marker: false, "Used", fonts.Small);
        DrawLegendItem(g, x + 16, line, LegendExpected, marker: true, "Where you should be by now", fonts.Small);
        var updated = state.Refreshing ? "Refreshing…" : state.LastUpdated is { } t ? $"Updated {t.ToLocalTime():HH:mm}" : "";
        DrawText(g, updated, fonts.Small, Theme.Faint, new RectangleF(Width - Pad - 140, line.Y, 140, line.Height), StringAlignment.Far);

        g.Restore(saved);
        return hits;
    }

    /// <param name="drivesTray">The meter shown in the tray gets a brighter label.</param>
    static void DrawMeter(Graphics g, UsageMeter meter, DateTimeOffset now, RectangleF row, Fonts fonts, BarPalette palette, bool drivesTray)
    {
        var labelColor = drivesTray ? Theme.Text : Theme.Muted;
        if (meter.Detail is null)
        {
            DrawText(g, meter.Label, fonts.Body, labelColor, new RectangleF(row.X, row.Y, LabelW, row.Height), StringAlignment.Near);
        }
        else
        {
            float mid = row.Y + row.Height / 2;
            DrawText(g, meter.Label, fonts.Body, labelColor, new RectangleF(row.X, mid - 18, LabelW, 18), StringAlignment.Near);
            DrawText(g, meter.Detail, fonts.Small, Theme.Faint, new RectangleF(row.X, mid, LabelW, 17), StringAlignment.Near);
        }

        var bar = new RectangleF(row.X + LabelW + ColGap, row.Y + (row.Height - BarH) / 2, row.Width - LabelW - DateW - ColGap * 2, BarH);
        DrawBar(g, bar, meter, now, fonts.Bar, palette);
        DrawText(g, Format.ResetText(meter, now), fonts.Small, Theme.Muted, new RectangleF(row.Right - DateW, row.Y, DateW, row.Height), StringAlignment.Far);
    }

    /// <summary>Solid = used; muted = elapsed share of the period, with a marker line so it stays visible under the solid part.</summary>
    static void DrawBar(Graphics g, RectangleF r, UsageMeter meter, DateTimeOffset now, Font font, BarPalette palette)
    {
        using var path = RoundedRect(r, 5);
        using (var track = new SolidBrush(Theme.Track))
            g.FillPath(track, path);

        var expected = meter.Expected(now);
        float usedRight = r.X + r.Width * (float)Math.Clamp(meter.Used ?? 0, 0, 1);
        var clip = g.Save();
        g.SetClip(path);
        if (expected is double e && e > 0)
        {
            using var pale = new SolidBrush(palette.Pale);
            g.FillRectangle(pale, r.X, r.Y, r.Width * (float)e, r.Height);
        }
        if (usedRight > r.X)
        {
            using var fill = new SolidBrush(palette.Fill);
            g.FillRectangle(fill, r.X, r.Y, usedRight - r.X, r.Height);
        }
        if (expected is double m && m is > 0 and < 1)
        {
            using var pen = new Pen(palette.Marker, 2f);
            float mx = r.X + r.Width * (float)m;
            g.DrawLine(pen, mx, r.Y, mx, r.Bottom);
        }
        g.Restore(clip);

        var text = meter.Used is not double used ? "—"
            : expected is double ex ? $"{Format.Percent(used)} used / {Format.Percent(ex)} expected"
            : $"{Format.Percent(used)} used";
        var textRect = RectangleF.Inflate(r, -9, 0);

        // Light text over the track and the muted part; over the solid part, whatever contrasts with the accent.
        clip = g.Save();
        g.SetClip(new RectangleF(usedRight, r.Y, r.Right - usedRight, r.Height));
        DrawShadowedText(g, text, font, textRect);
        g.Restore(clip);

        clip = g.Save();
        g.SetClip(new RectangleF(r.X, r.Y, usedRight - r.X, r.Height));
        if (palette.DarkText)
            DrawText(g, text, font, Theme.Background, textRect, StringAlignment.Near);
        else
            DrawShadowedText(g, text, font, textRect);
        g.Restore(clip);
    }

    static void DrawShadowedText(Graphics g, string text, Font font, RectangleF rect)
    {
        DrawText(g, text, font, Color.FromArgb(150, 0, 0, 0), new RectangleF(rect.X + 1, rect.Y + 1, rect.Width, rect.Height), StringAlignment.Near);
        DrawText(g, text, font, Color.White, rect, StringAlignment.Near);
    }

    static float DrawLegendItem(Graphics g, float x, RectangleF line, Color color, bool marker, string text, Font font)
    {
        var swatch = new RectangleF(x, line.Y + line.Height / 2 - 5, 18, 10);
        using (var path = RoundedRect(swatch, 3))
        using (var brush = new SolidBrush(color))
            g.FillPath(brush, path);
        if (marker)
        {
            using var pen = new Pen(Theme.Text, 2f);
            g.DrawLine(pen, swatch.Right - 1, swatch.Y - 2, swatch.Right - 1, swatch.Bottom + 2);
        }
        var width = g.MeasureString(text, font).Width;
        DrawText(g, text, font, Theme.Muted, new RectangleF(swatch.Right + 5, line.Y, width + 2, line.Height), StringAlignment.Near);
        return swatch.Right + 5 + width;
    }

    static void DrawIconButton(Graphics g, string glyph, RectangleF rect, Font font, PointF? hover, bool dim)
    {
        if (hover is { } h && rect.Contains(h))
        {
            using var path = RoundedRect(rect, 6);
            using var brush = new SolidBrush(Theme.Surface);
            g.FillPath(brush, path);
        }
        DrawText(g, glyph, font, dim ? Theme.Faint : Theme.Muted, rect, StringAlignment.Center);
    }

    static void DrawDivider(Graphics g, float y)
    {
        using var pen = new Pen(Theme.Divider, 1f);
        g.DrawLine(pen, Pad, y, Width - Pad, y);
    }

    static void DrawText(Graphics g, string text, Font font, Color color, RectangleF rect, StringAlignment alignment)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat(StringFormatFlags.NoWrap)
        {
            Alignment = alignment,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
        };
        g.DrawString(text, font, brush, rect, format);
    }

    static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    static RectangleF ToDevice(RectangleF r, float scale) => new(r.X * scale, r.Y * scale, r.Width * scale, r.Height * scale);
}
