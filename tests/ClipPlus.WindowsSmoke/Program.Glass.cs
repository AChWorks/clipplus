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
        var defaultRadius = (CornerRadius)icon.Resources["FloatingRadius"];
        var previews = new List<(string Name, BitmapSource Idle, BitmapSource Hover)>();
        void Move(int x, int y) => Check(Native.SetCursorPos(x, y), "move pointer for glass icon test");
        try
        {
            foreach (var shape in new[] { (Name: "radius10", Radius: 10.0), (Name: "circle", Radius: FloatingIconWindow.IconSize / 2) })
            {
                icon.Resources["FloatingRadius"] = new CornerRadius(shape.Radius);
                icon.ApplyTheme(false, false);
                Move(outsideX, outsideY);
                InstalledFlow.Pump(TimeSpan.FromMilliseconds(160));
                Check(Math.Abs(button.Opacity - 1) < 0.001, shape.Name + ": no immediate dimming after pointer leaves");
                PumpUntil(() => Math.Abs(button.Opacity - FloatingIconWindow.IdleOpacity) < 0.001);
                Check(Math.Abs(button.Opacity - FloatingIconWindow.IdleOpacity) < 0.001, shape.Name + ": delayed idle fade completes");
                var idle = RenderPopup(icon);
                var w = idle.PixelWidth;
                var h = idle.PixelHeight;
                var idleAlpha = PixelAlpha(idle, w / 2, h / 2);
                Check(idleAlpha >= 110 && idleAlpha <= 120 && PixelAlpha(idle, 0, 0) == 0,
                    shape.Name + ": actual per-pixel transparency across glyph and glass surface");
                SaveImageEvidence(idle, "quick-glass-" + shape.Name + "-idle");

                var center = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
                Move((int)center.X, (int)center.Y);
                PumpUntil(() => button.IsMouseOver && button.Opacity == 1);
                Check(button.IsMouseOver && button.Opacity == 1, shape.Name + ": hover restores full opacity");
                InstalledFlow.Pump(TimeSpan.FromMilliseconds(1100));
                Check(button.Opacity == 1, shape.Name + ": hover remains fully clear beyond idle delay");
                var hover = RenderPopup(icon);
                Check(PixelAlpha(hover, w / 2, h / 2) == 255 && PixelAlpha(hover, 0, 0) == 0,
                    shape.Name + ": full hover opacity with transparent corner");
                // At quarter-size offsets, a 10-DIP corner is filled while
                // a 21-DIP circular corner is not. Both use the same glyph.
                var offset = Math.Max(1, (int)Math.Round(w * 0.095));
                var shapeAlpha = PixelAlpha(hover, offset, offset);
                Check(shape.Radius == 10 ? shapeAlpha >= 200 : shapeAlpha <= 20,
                    shape.Name + ": requested silhouette is actually rendered");
                SaveImageEvidence(hover, "quick-glass-" + shape.Name + "-hover");
                previews.Add((shape.Name, idle, hover));
                Move(outsideX, outsideY);
                InstalledFlow.Pump(TimeSpan.FromMilliseconds(150));
                Check(button.Opacity == 1, shape.Name + ": pointer departure retains opacity until delay");
                PumpUntil(() => Math.Abs(button.Opacity - FloatingIconWindow.IdleOpacity) < 0.001);
                Check(Math.Abs(button.Opacity - FloatingIconWindow.IdleOpacity) < 0.001, shape.Name + ": leaving hover restarts fade");
            }

            // Composite the actual WPF alpha renders over a synthetic
            // background. No desktop recording or product screen capture.
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 380, 168));
                for (var row = 0; row < previews.Count; row++)
                {
                    var preview = previews[row];
                    var y = row * 84.0;
                    for (var column = 0; column < 4; column++)
                    {
                        var x = column * 95.0;
                        var dark = column >= 2;
                        dc.DrawRectangle(dark ? new SolidColorBrush(Color.FromRgb(34, 39, 49)) : new SolidColorBrush(Color.FromRgb(239, 244, 249)), null,
                            new Rect(x, y, 95, 84));
                        var stripe = dark ? new SolidColorBrush(Color.FromRgb(84, 97, 113)) : new SolidColorBrush(Color.FromRgb(201, 212, 227));
                        for (var line = 0; line < 5; line++)
                            dc.DrawRoundedRectangle(stripe, null, new Rect(x + 5, y + 10 + line * 15, 82 - line % 2 * 15, 4), 2, 2);
                        dc.DrawImage(column % 2 == 0 ? preview.Idle : preview.Hover, new Rect(x + 19.5, y + 14, 56, 56));
                    }
                }
            }
            var comparison = new RenderTargetBitmap(380, 168, 96, 96, PixelFormats.Pbgra32);
            comparison.Render(visual);
            SaveImageEvidence(comparison, "quick-glass-comparison");

            icon.ApplyTheme(false, true);
            InstalledFlow.Pump(TimeSpan.FromMilliseconds(1200));
            Check(button.Opacity == 1, "high contrast icon remains fully visible while idle");
        }
        finally
        {
            icon.Resources["FloatingRadius"] = defaultRadius;
            icon.ApplyTheme(false, false);
            Move(outsideX, outsideY);
        }
    }
}
