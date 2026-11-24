using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace ClipPlus;

internal static class ThirdPartyNotices
{
    internal static string Read()
    {
        const string prefix = "ClipPlus.Notices.";
        var assembly = typeof(ThirdPartyNotices).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(name => name == prefix + "index.txt" ? 0 : 1)
            .ThenBy(name => name, StringComparer.Ordinal);
        var text = new StringBuilder();
        foreach (var name in names)
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException("Missing notice resource: " + name);
            using var reader = new StreamReader(stream);
            text.AppendLine(reader.ReadToEnd()).AppendLine();
        }
        using var license = assembly.GetManifestResourceStream("ClipPlus.License.MPL-2.0.txt")
            ?? throw new InvalidOperationException("Missing ClipPlus license resource.");
        using var licenseReader = new StreamReader(license);
        text.AppendLine("ClipPlus first-party license (MPL-2.0)").AppendLine(licenseReader.ReadToEnd());
        return text.ToString();
    }

    internal static void Show()
    {
        var viewer = new TextBox
        {
            Text = Read(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(12), AcceptsReturn = true
        };
        var window = new Window
        {
            Title = "ClipPlus - License and notices",
            Content = viewer, Width = 700, Height = 600,
            MinWidth = 280, MinHeight = 200,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        window.Show();
    }
}
