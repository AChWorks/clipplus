using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipPlus;

internal static partial class Program
{
    private static byte PixelAlpha(BitmapSource image, int x, int y)
    {
        var pixel = new byte[4];
        image.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel[3];
    }

    private static void VerifyRoundedPanel(PopupWindow popup, string state)
    {
        var surface = (Border)popup.FindName("PanelSurface");
        Check(popup.AllowsTransparency && popup.Background is SolidColorBrush clear && clear.Color.A == 0 &&
            surface.Background is SolidColorBrush fill && fill.Color.A == 255,
            state + ": a single opaque rounded panel on a transparent window");
        var image = RenderPopup(popup);
        var w = image.PixelWidth;
        var h = image.PixelHeight;
        Check(PixelAlpha(image, 0, 0) == 0 && PixelAlpha(image, w - 1, 0) == 0 &&
            PixelAlpha(image, 0, h - 1) == 0 && PixelAlpha(image, w - 1, h - 1) == 0 &&
            PixelAlpha(image, w / 2, h / 2) == 255,
            state + ": all four outer corners are transparent, panel body stays opaque");
    }

    private static void VerifyGlassIcon(FloatingIconWindow icon, int outsideX, int outsideY)
    {
        var button = (Button)icon.FindName("FloatingButton");
        void Move(int x, int y) => Check(Native.SetCursorPos(x, y), "move pointer for glass icon test");
        var center = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
        try
        {
            // Test the selected production silhouette rather than shipping or
            // dynamically switching between the discarded design alternatives.
            foreach (var theme in new[] { (Dark: false, Name: "light"), (Dark: true, Name: "dark") })
            {
                Move(outsideX, outsideY);
                InstalledFlow.Pump(TimeSpan.FromMilliseconds(80));
                icon.ApplyTheme(theme.Dark, false);
                icon.UpdateLayout();
                var surface = (System.Windows.Shapes.Ellipse)button.Template.FindName("Surface", button);
                Check(surface.Stroke is null && button.BorderThickness == new Thickness(0),
                    theme.Name + ": circular icon has no decorative border or second tile");
                InstalledFlow.Pump(TimeSpan.FromMilliseconds(160));
                Check(button.Opacity == 1, theme.Name + ": no immediate dimming");
                PumpUntil(() => Math.Abs(button.Opacity - FloatingIconWindow.IdleOpacity) < 0.001);
                Check(Math.Abs(button.Opacity - FloatingIconWindow.IdleOpacity) < 0.001,
                    theme.Name + ": delayed idle fade completes");
                var idle = RenderPopup(icon);
                var w = idle.PixelWidth;
                var h = idle.PixelHeight;
                var idleAlpha = PixelAlpha(idle, w / 2, h / 2);
                Check(idleAlpha >= 110 && idleAlpha <= 120 && PixelAlpha(idle, 0, 0) == 0,
                    theme.Name + ": actual per-pixel transparency across glyph and glass surface");
                SaveImageEvidence(idle, "quick-glass-" + theme.Name + "-idle");

                Move((int)center.X, (int)center.Y);
                PumpUntil(() => button.IsMouseOver && button.Opacity == 1);
                Check(button.IsMouseOver && button.Opacity == 1, theme.Name + ": hover restores full opacity");
                InstalledFlow.Pump(TimeSpan.FromMilliseconds(1100));
                Check(button.Opacity == 1, theme.Name + ": hover remains fully clear beyond idle delay");
                var hover = RenderPopup(icon);
                Check(PixelAlpha(hover, w / 2, h / 2) == 255 &&
                    PixelAlpha(hover, 0, 0) == 0 && PixelAlpha(hover, w - 1, 0) == 0 &&
                    PixelAlpha(hover, 0, h - 1) == 0 && PixelAlpha(hover, w - 1, h - 1) == 0,
                    theme.Name + ": full hover opacity with four transparent corners");
                var offset = Math.Max(1, (int)Math.Round(w * 0.095));
                Check(PixelAlpha(hover, offset, offset) <= 20 &&
                    PixelAlpha(hover, w / 2, h / 8) >= 250,
                    theme.Name + ": circle silhouette is actually rendered");
                SaveImageEvidence(hover, "quick-glass-" + theme.Name + "-hover");
                Move(outsideX, outsideY);
                InstalledFlow.Pump(TimeSpan.FromMilliseconds(150));
                Check(button.Opacity == 1, theme.Name + ": pointer departure retains opacity until delay");
                PumpUntil(() => Math.Abs(button.Opacity - FloatingIconWindow.IdleOpacity) < 0.001);
                Check(Math.Abs(button.Opacity - FloatingIconWindow.IdleOpacity) < 0.001,
                    theme.Name + ": leaving hover restarts fade");

                // Composite actual WPF alpha renders over a synthetic
                // backdrop to demonstrate transparency, not backdrop blur.
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    var background = new SolidColorBrush(theme.Dark ? Color.FromRgb(34, 39, 49) : Color.FromRgb(239, 244, 249));
                    var stripe = new SolidColorBrush(theme.Dark ? Color.FromRgb(84, 97, 113) : Color.FromRgb(201, 212, 227));
                    dc.DrawRectangle(background, null, new Rect(0, 0, 190, 84));
                    for (var column = 0; column < 2; column++)
                    {
                        var x = column * 95.0;
                        for (var line = 0; line < 5; line++)
                            dc.DrawRoundedRectangle(stripe, null, new Rect(x + 5, 10 + line * 15, 82 - line % 2 * 15, 4), 2, 2);
                        dc.DrawImage(column == 0 ? idle : hover, new Rect(x + 19.5, 14, 56, 56));
                    }
                }
                var comparison = new RenderTargetBitmap(190, 84, 96, 96, PixelFormats.Pbgra32);
                comparison.Render(visual);
                SaveImageEvidence(comparison, "quick-glass-preview-" + theme.Name);
            }

            icon.ApplyTheme(false, true);
            InstalledFlow.Pump(TimeSpan.FromMilliseconds(1200));
            Check(button.Opacity == 1, "high contrast icon remains fully visible while idle");
            Check(button.Background is SolidColorBrush fill && fill.Color == SystemColors.HighlightColor &&
                button.Foreground is SolidColorBrush glyph && glyph.Color == SystemColors.HighlightTextColor,
                "high contrast uses the system highlight and matching text brush without a border");
        }
        finally
        {
            icon.ApplyTheme(false, false);
            Move(outsideX, outsideY);
        }
    }
}
