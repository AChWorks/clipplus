using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Interop;
using ClipPlus;
using ClipPlus.Core;
using Microsoft.Data.Sqlite;

// Only invoked by the isolated Windows lifecycle test. Seed the database
// actually created by the released app, without opening it via the new store.
internal static class LegacyHistoryFixture
{
    private const int OldRecentCount = 50;
    private const int PinCount = 2;
    private const long FirstId = 10001;
    private const string LegacySettings = """
        {"WindowHeight":623,"WindowPosition":{"X":60,"Y":80},"Shortcut":"CtrlAltV"}
        """;

    private static string Database(string[] args)
    {
        var index = Array.IndexOf(args, "--database");
        if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Missing --database");
        return args[index + 1];
    }

    private static SqliteConnection Open(string database, SqliteOpenMode mode)
    {
        SqlitePlatform.Initialize();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database, Mode = mode, Pooling = false
        }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return connection;
    }

    private static long Count(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    private static bool Migrated(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(clips)";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (reader.GetString(1) == "category_id") return true;
        return false;
    }

    internal static int EmptyClipboard()
    {
        using var source = new HwndSource(new HwndSourceParameters("ClipPlus isolated legacy fixture") { WindowStyle = 0 });
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (Native.OpenClipboard(source.Handle))
            {
                try
                {
                    if (!Native.EmptyClipboard()) throw Native.Error("Empty isolated fixture clipboard");
                    return 0;
                }
                finally { Native.CloseClipboard(); }
            }
            Thread.Sleep(50);
        }
        throw Native.Error("Open isolated fixture clipboard");
    }

    internal static int Seed(string[] args)
    {
        var database = Database(args);
        using (var connection = Open(database, SqliteOpenMode.ReadWrite))
        {
            if (Migrated(connection) || Count(connection, "SELECT COUNT(*) FROM clips") != 0 ||
                Count(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name='categories'") != 0)
                throw new InvalidOperationException("Expected the empty database created by the released v0.1.0 executable.");
            using var transaction = connection.BeginTransaction();
            for (var i = 0; i < OldRecentCount + PinCount; i++)
            {
                var text = $"Released history fixture {i:D2} — سلام";
                var formats = new Dictionary<ClipFormat, byte[]>
                {
                    [ClipFormat.UnicodeText] = Encoding.Unicode.GetBytes(text + "\0")
                };
                if (i % 3 == 0) formats[ClipFormat.Html] = Encoding.UTF8.GetBytes($"<b>{text}</b>\0");
                if (i % 4 == 0) formats[ClipFormat.Png] = Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGOwbz3wHwAFDwKEY5hFdAAAAABJRU5ErkJggg==");
                if (i % 5 == 0) formats[ClipFormat.NoCloudUpload] = new byte[4];
                var payload = new ClipPayload(formats);
                using var clip = connection.CreateCommand();
                clip.Transaction = transaction;
                clip.CommandText = """
                    INSERT INTO clips(id,fingerprint,title,color,pinned,touched,text,has_image,has_html)
                    VALUES($id,$hash,$title,$color,$pin,$touched,$text,$image,$html)
                    """;
                clip.Parameters.AddWithValue("$id", FirstId + i);
                clip.Parameters.AddWithValue("$hash", payload.Fingerprint());
                clip.Parameters.AddWithValue("$title", i % 2 == 0 ? $"Legacy title {i}" : "");
                clip.Parameters.AddWithValue("$color", i % 9);
                clip.Parameters.AddWithValue("$pin", i < PinCount);
                clip.Parameters.AddWithValue("$touched", i + 1);
                clip.Parameters.AddWithValue("$text", payload.Text);
                clip.Parameters.AddWithValue("$image", payload.HasImage);
                clip.Parameters.AddWithValue("$html", payload.HasHtml);
                clip.ExecuteNonQuery();
                foreach (var format in payload.Formats)
                {
                    using var value = connection.CreateCommand();
                    value.Transaction = transaction;
                    value.CommandText = "INSERT INTO formats(clip_id,format,data) VALUES($id,$format,$data)";
                    value.Parameters.AddWithValue("$id", FirstId + i);
                    value.Parameters.AddWithValue("$format", (int)format.Key);
                    value.Parameters.AddWithValue("$data", format.Value);
                    value.ExecuteNonQuery();
                }
            }
            transaction.Commit();
        }
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(database)!, "settings.json"), LegacySettings);
        Console.WriteLine("PASS: seeded actual v0.1.0 history with 50 recent clips, two pins, mixed original formats and legacy preferences.");
        return 0;
    }

    internal static int Snapshot(string[] args)
    {
        var database = Database(args);
        using var connection = Open(database, SqliteOpenMode.ReadOnly);
        var migrated = args.Contains("--expect-migrated");
        if (Migrated(connection) != migrated ||
            Count(connection, "SELECT COUNT(*) FROM clips WHERE pinned=0") != OldRecentCount ||
            Count(connection, "SELECT COUNT(*) FROM clips WHERE pinned=1") != PinCount)
            throw new InvalidOperationException("Legacy history count or migration state changed.");
        if (migrated && (Count(connection, "SELECT COUNT(*) FROM categories") != 0 ||
                         Count(connection, "SELECT COUNT(*) FROM clips WHERE category_id IS NOT NULL") != 0))
            throw new InvalidOperationException("Unexpected category state in migrated fixture.");
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA foreign_key_check";
            using var reader = check.ExecuteReader();
            if (reader.Read()) throw new InvalidOperationException("Invalid migrated foreign key.");
        }
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT c.id,c.fingerprint,c.title,c.color,c.pinned,c.touched,c.text,c.has_image,c.has_html,f.format,f.data
            FROM clips c JOIN formats f ON f.clip_id=c.id ORDER BY c.id,f.format
            """;
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, true);
        using (var reader = cmd.ExecuteReader())
        {
            var ids = new HashSet<long>();
            var formats = new HashSet<long>();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                ids.Add(id);
                writer.Write(id);
                writer.Write(reader.GetString(1));
                writer.Write(reader.GetString(2));
                for (var i = 3; i <= 5; i++) writer.Write(reader.GetInt64(i));
                writer.Write(reader.GetString(6));
                for (var i = 7; i <= 9; i++) writer.Write(reader.GetInt64(i));
                formats.Add(reader.GetInt64(9));
                var bytes = (byte[])reader.GetValue(10);
                writer.Write(bytes.Length);
                writer.Write(bytes);
            }
            if (ids.Count != OldRecentCount + PinCount || formats.Count < 4 ||
                !ids.SetEquals(Enumerable.Range(0, OldRecentCount + PinCount).Select(i => FirstId + i)))
                throw new InvalidOperationException("Missing legacy IDs or formats.");
        }
        var preferences = Preferences.Load(Path.Combine(Path.GetDirectoryName(database)!, "settings.json"));
        if (preferences.WindowHeight != 623 || preferences.WindowPosition != new WindowPosition(60, 80) ||
            preferences.Shortcut != Shortcut.CtrlAltV)
            throw new InvalidOperationException("Existing preferences were not preserved semantically.");
        writer.Write(preferences.WindowHeight);
        writer.Write(preferences.WindowPosition!.X);
        writer.Write(preferences.WindowPosition.Y);
        writer.Write((int)preferences.Shortcut);
        writer.Flush();
        using var sha = SHA256.Create();
        Console.WriteLine(BitConverter.ToString(sha.ComputeHash(buffer.ToArray())).Replace("-", ""));
        return 0;
    }

    internal static int Exercise(string[] args)
    {
        using var store = new ClipStore(Database(args));
        var category = store.CreateCategory("Migrated history");
        var original = store.Read(FirstId)?.Fingerprint();
        if (original is null) throw new InvalidOperationException("Missing migrated payload.");
        store.SetCategory(FirstId, category);
        var item = store.List(view: category).Single();
        if (item.Id != FirstId || !item.Pinned || store.Read(FirstId)?.Fingerprint() != original)
            throw new InvalidOperationException("Migrated category membership changed legacy data.");
        store.DeleteCategory(category);
        if (store.List().Single(c => c.Id == FirstId).CategoryId is not null)
            throw new InvalidOperationException("Migrated category was not detached.");
        Console.WriteLine("PASS: migrated history supports category assignment, lookup and non-destructive deletion.");
        return 0;
    }
}
