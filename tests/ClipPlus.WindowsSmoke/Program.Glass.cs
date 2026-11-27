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

    private static void VerifyRoundedSearch(PopupWindow popup, string state)
    {
        popup.UpdateLayout();
        var search = (TextBox)popup.FindName("SearchBox");
        var surface = (Border)search.Template.FindName("SearchSurface", search);
        var host = (ScrollViewer)search.Template.FindName("PART_ContentHost", search);
        var borderResource = search.IsKeyboardFocused ? "FocusBrush" :
            search.IsMouseOver ? "MutedBrush" : "BorderBrush";
        Check(surface.CornerRadius == new CornerRadius(4) &&
            surface.BorderThickness == new Thickness(1) &&
            surface.RenderSize == search.RenderSize &&
            Math.Abs(search.ActualHeight - 34) <= 0.5 &&
            search.Padding == new Thickness(28, 5, 6, 5) &&
            !host.Focusable && host.Content is not null,
            state + ": rounded 4-DIP search surface retains native text host and layout");
        Check(surface.Background is SolidColorBrush background &&
            background.Color == ((SolidColorBrush)popup.FindResource("FieldBrush")).Color &&
            surface.BorderBrush is SolidColorBrush border &&
            border.Color == ((SolidColorBrush)popup.FindResource(borderResource)).Color,
            state + ": search uses the current theme and focus/hover state");
        // Render just the actual shared TextBox, not a synthetic shape.
        var dpi = VisualTreeHelper.GetDpi(search);
        var image = new RenderTargetBitmap((int)Math.Ceiling(search.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(search.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        image.Render(search);
        var w = image.PixelWidth;
        var h = image.PixelHeight;
        Check(PixelAlpha(image, 0, 0) < 128 && PixelAlpha(image, w - 1, 0) < 128 &&
            PixelAlpha(image, 0, h - 1) < 128 && PixelAlpha(image, w - 1, h - 1) < 128 &&
            PixelAlpha(image, w / 2, h / 2) == 255,
            state + ": actual search render has rounded corners and an opaque field");
    }

    private static void VerifyGlassIcon(FloatingIconWindow icon, int outsideX, int outsideY)
    {
        var button = (Button)icon.FindName("FloatingButton");
        void Move(int x, int y) => Check(Native.SetCursorPos(x, y), "move pointer for glass icon test");
        var center = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
        try
        {
            // Override only this window's dynamic system resource. This
            // exercises WPF's live resource invalidation path without changing
            // the runner's actual accessibility settings.
            icon.Resources[SystemParameters.HighContrastKey] = false;
            Check(icon.ReadLocalValue(FloatingIconWindow.SystemHighContrastProperty) != DependencyProperty.UnsetValue,
                "visible icon tracks the native system contrast resource");
            // Test the selected production silhouette rather than shipping or
            // dynamically switching between the discarded design alternatives.
            foreach (var theme in new[] { (Dark: false, Name: "light"), (Dark: true, Name: "dark") })
            {
                Move(outsideX, outsideY);
                InstalledFlow.Pump(TimeSpan.FromMilliseconds(80));
                icon.ApplyTheme(theme.Dark);
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

            // The last iteration has already faded to idle. Change the live
            // resource, not the ApplyTheme method, to exercise the production
            // accessibility notification and cancellation path.
            void VerifyContrast(string state)
            {
                PumpUntil(() => button.Opacity == 1 && button.Background is SolidColorBrush);
                Check(button.Opacity == 1 &&
                    !DependencyPropertyHelper.GetValueSource(button, UIElement.OpacityProperty).IsAnimated &&
                    button.Background is SolidColorBrush fill && fill.Color == SystemColors.HighlightColor &&
                    button.Foreground is SolidColorBrush glyph && glyph.Color == SystemColors.HighlightTextColor,
                    state + ": live contrast cancels fade and restores opaque system colors");
                InstalledFlow.Pump(TimeSpan.FromMilliseconds(1200));
                Check(button.Opacity == 1, state + ": contrast icon remains fully visible beyond the idle delay");
            }
            icon.Resources[SystemParameters.HighContrastKey] = true;
            VerifyContrast("already-dimmed icon");
            icon.Resources[SystemParameters.HighContrastKey] = false;
            PumpUntil(() => button.Background is LinearGradientBrush);
            Check(button.Background is LinearGradientBrush && button.Opacity == 1,
                "leaving contrast restores the ordinary presentation and starts a fresh idle delay");

            if (SystemParameters.ClientAreaAnimation)
            {
                PumpUntil(() => button.Opacity < 1 && button.Opacity > FloatingIconWindow.IdleOpacity);
                Check(button.Opacity < 1 && button.Opacity > FloatingIconWindow.IdleOpacity,
                    "ordinary fade is in progress before live contrast change");
                icon.Resources[SystemParameters.HighContrastKey] = true;
                VerifyContrast("active fade");
                icon.Resources[SystemParameters.HighContrastKey] = false;
            }
            else
            {
                Console.WriteLine("INFO: client-area animations disabled by Windows; active fade transition not applicable.");
            }
            PumpUntil(() => Math.Abs(button.Opacity - FloatingIconWindow.IdleOpacity) < 0.001);
            Check(button.Background is LinearGradientBrush &&
                Math.Abs(button.Opacity - FloatingIconWindow.IdleOpacity) < 0.001,
                "leaving contrast restores the ordinary delayed idle opacity");
        }
        finally
        {
            icon.Resources.Remove(SystemParameters.HighContrastKey);
            icon.ApplyTheme(false);
            Move(outsideX, outsideY);
        }
    }
}
