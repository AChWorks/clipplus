using System.IO;
using System.Security.Cryptography;
using System.Text;
using ClipPlus;
using ClipPlus.Core;
using System.Security.Principal;
using Microsoft.Data.Sqlite;

internal static partial class Program
{
    // No real clipboard or user data. The lifecycle runner guards that both
    // profile locations are absent before creating its synthetic fixtures.
    private static int ProfileFixture(string[] args)
    {
        var path = args[Array.IndexOf(args, "--database") + 1];
        var settings = Path.Combine(Path.GetDirectoryName(path)!, "settings.json");
        if (args.Contains("--seed-v020-profile"))
        {
            var name = args[Array.IndexOf(args, "--profile") + 1];
            if (name == "administrator")
            {
                using var identity = WindowsIdentity.GetCurrent();
                var protectedDirectory = StateDirectory.Open(identity.User!.Value);
                Check(string.Equals(Path.GetDirectoryName(path), protectedDirectory, StringComparison.OrdinalIgnoreCase),
                    "fixture uses protected administrator directory");
            }
            using var store = new ClipStore(path);
            Check(store.List().Count == 0 && store.Categories().Count == 0, "empty synthetic profile");
            var category = store.CreateCategory(name + " category");
            var second = store.CreateCategory(name + " other");
            store.MoveCategory(second, 0);
            for (var i = 0; i < 12; i++)
            {
                var text = $"{name} payload {i} — سلام";
                var formats = new Dictionary<ClipFormat, byte[]>
                {
                    [ClipFormat.UnicodeText] = Encoding.Unicode.GetBytes(text + "\0")
                };
                if (i % 3 == 0) formats[ClipFormat.Html] = Encoding.UTF8.GetBytes($"<b>{text}</b>\0");
                if (i % 4 == 0) formats[ClipFormat.Png] = Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGOwbz3wHwAFDwKEY5hFdAAAAABJRU5ErkJggg==");
                if (i % 5 == 0) formats[ClipFormat.NoCloudUpload] = new byte[4];
                var id = store.Capture(new ClipPayload(formats));
                store.SetTitle(id, $"{name} title {i}");
                store.SetColor(id, ClipColor.Green);
                store.SetCategory(id, i % 2 == 0 ? category : second);
                if (i < 5) store.SetPinned(id, true);
            }
            // Match the exact serialized property order and value types of the
            // released Preferences record. v0.2.0 rewrites settings at startup;
            // the fixture must survive that rewrite byte for byte.
            var legacySettings = new { WindowHeight = name == "normal" ? 410d : 470d,
                WindowPosition = new WindowPosition(25, 40), Shortcut = "CtrlShiftV",
                CompactView = name == "normal", ActiveView = (long?)category };
            var quickGeometry = new PopupGeometry(name == "normal" ? 205 : 240, name == "normal" ? 385 : 355,
                new WindowPosition(55, 75));
            var quickSettings = new
            {
                legacySettings.WindowHeight, legacySettings.WindowPosition, legacySettings.Shortcut,
                QuickPanel = quickGeometry, legacySettings.CompactView, legacySettings.ActiveView
            };
            object settingsValue = args.Contains("--with-floating-geometry")
                ? new Preferences
                {
                    WindowHeight = legacySettings.WindowHeight,
                    WindowPosition = legacySettings.WindowPosition,
                    Shortcut = Shortcut.CtrlShiftV,
                    QuickPanel = quickGeometry,
                    AutoCollapseQuickPanel = name == "normal",
                    FloatingPosition = new WindowPosition(name == "normal" ? 190 : 260, 110),
                    CompactView = legacySettings.CompactView,
                    ActiveView = legacySettings.ActiveView,
                    WindowWidth = name == "normal" ? 310d : 420d
                }
                : args.Contains("--with-width-geometry")
                ? new
                {
                    quickSettings.WindowHeight, quickSettings.WindowPosition, quickSettings.Shortcut,
                    quickSettings.QuickPanel, quickSettings.CompactView, quickSettings.ActiveView,
                    WindowWidth = name == "normal" ? 310d : 420d
                }
                : args.Contains("--with-quick-geometry") ? quickSettings : legacySettings;
            File.WriteAllText(settings, System.Text.Json.JsonSerializer.Serialize(settingsValue,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        SqlitePlatform.Initialize();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        var snapshot = new StringBuilder();
        foreach (var query in new[] { "SELECT * FROM clips ORDER BY id",
            "SELECT * FROM formats ORDER BY clip_id,format", "SELECT * FROM categories ORDER BY id" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = query;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.GetValue(i);
                    var text = value is byte[] bytes ? Convert.ToBase64String(bytes) :
                        Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
                    snapshot.Append(text.Length).Append(':').Append(text);
                }
                snapshot.AppendLine();
            }
        }
        snapshot.Append(File.ReadAllText(settings));
        using var sha = SHA256.Create();
        Console.WriteLine(BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(snapshot.ToString()))).Replace("-", ""));
        return 0;
    }
}
