using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipPlus.Core;

[JsonConverter(typeof(JsonStringEnumConverter<Shortcut>))]
public enum Shortcut { WinV, CtrlShiftV, CtrlAltV }

// Native screen coordinates, including negative positions on left/top monitors.
public sealed record WindowPosition(int X, int Y);

// Each popup mode remembers its own preferred geometry.
public sealed record PopupGeometry(double Width, double Height, WindowPosition? Position);

public sealed record Preferences
{
    public const double DefaultNormalWidth = 360;
    public const double DefaultQuickWidth = 260;

    public double WindowHeight { get; init; } = 520;
    public WindowPosition? WindowPosition { get; init; }
    public Shortcut Shortcut { get; init; } = Shortcut.WinV;
    public PopupGeometry QuickPanel { get; init; } = new(DefaultQuickWidth, 420, null);
    public bool CompactView { get; init; }
    public long? ActiveView { get; init; }
    public double WindowWidth { get; init; } = DefaultNormalWidth;

    public PopupGeometry Geometry(bool quick) =>
        quick ? QuickPanel : new(WindowWidth, WindowHeight, WindowPosition);

    public Preferences WithGeometry(PopupGeometry geometry, bool quick) =>
        quick ? this with { QuickPanel = geometry } : this with
        {
            WindowWidth = geometry.Width, WindowHeight = geometry.Height, WindowPosition = geometry.Position
        };

    public static Preferences Load(string path)
    {
        if (!File.Exists(path)) return new();
        var value = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path)) ?? new();
        return value with
        {
            WindowHeight = ValidSize(value.WindowHeight) ? value.WindowHeight : 520,
            WindowWidth = ValidSize(value.WindowWidth) ? value.WindowWidth : DefaultNormalWidth,
            Shortcut = Enum.IsDefined(typeof(Shortcut), value.Shortcut) ? value.Shortcut : Shortcut.WinV,
            QuickPanel = value.QuickPanel is { } quick && ValidSize(quick.Width) && ValidSize(quick.Height)
                ? quick : new(DefaultQuickWidth, 420, null)
        };
    }

    private static bool ValidSize(double value) => value > 0 && !double.IsInfinity(value) && !double.IsNaN(value);

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        if (File.Exists(path)) File.Replace(temp, path, null);
        else File.Move(temp, path);
    }
}
