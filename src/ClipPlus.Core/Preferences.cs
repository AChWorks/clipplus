using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipPlus.Core;

[JsonConverter(typeof(JsonStringEnumConverter<Shortcut>))]
public enum Shortcut { WinV, CtrlShiftV, CtrlAltV }

public sealed record Preferences
{
    public double WindowHeight { get; init; } = 520;
    public Shortcut Shortcut { get; init; } = Shortcut.WinV;

    public static Preferences Load(string path)
    {
        if (!File.Exists(path)) return new();
        var value = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path)) ?? new();
        return value with
        {
            WindowHeight = double.IsFinite(value.WindowHeight) && value.WindowHeight > 0 ? value.WindowHeight : 520,
            Shortcut = Enum.IsDefined(value.Shortcut) ? value.Shortcut : Shortcut.WinV
        };
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }
}
