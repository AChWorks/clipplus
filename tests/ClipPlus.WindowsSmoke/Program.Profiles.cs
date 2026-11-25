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
                var id = store.Capture(ClipPayload.FromText($"{name} payload {i}"));
                store.SetTitle(id, $"{name} title {i}");
                store.SetColor(id, ClipColor.Green);
                store.SetCategory(id, i % 2 == 0 ? category : second);
                if (i < 5) store.SetPinned(id, true);
            }
            new Preferences { WindowHeight = name == "normal" ? 410 : 470,
                WindowPosition = new WindowPosition(25, 40), CompactView = name == "normal",
                ActiveView = category, Shortcut = Shortcut.CtrlShiftV }.Save(settings);
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
