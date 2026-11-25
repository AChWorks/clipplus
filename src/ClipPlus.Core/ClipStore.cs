using Microsoft.Data.Sqlite;

namespace ClipPlus.Core;

// One owner (the UI dispatcher), one connection; transactional writes make
// metadata preservation and history trimming inseparable from each change.
public sealed class ClipStore : IDisposable
{
    public const int RecentCapacity = 100;
    public const int CategoryCapacity = 10;
    public const long PinnedView = -1;
    public const long RecentView = 0;
    private readonly SqliteConnection _db;

    public ClipStore(string path)
    {
        SqlitePlatform.Initialize();
        if (path != ":memory:")
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
            cmd.ExecuteNonQuery();
            using var tx = _db.BeginTransaction();
            cmd.Transaction = tx;
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS categories (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL,
                    position INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS clips (
                    id INTEGER PRIMARY KEY,
                    fingerprint TEXT NOT NULL UNIQUE,
                    title TEXT NOT NULL DEFAULT '',
                    color INTEGER NOT NULL DEFAULT 0,
                    pinned INTEGER NOT NULL DEFAULT 0,
                    touched INTEGER NOT NULL,
                    text TEXT NOT NULL,
                    has_image INTEGER NOT NULL,
                    has_html INTEGER NOT NULL,
                    category_id INTEGER REFERENCES categories(id) ON DELETE SET NULL
                );
                CREATE TABLE IF NOT EXISTS formats (
                    clip_id INTEGER NOT NULL REFERENCES clips(id) ON DELETE CASCADE,
                    format INTEGER NOT NULL,
                    data BLOB NOT NULL,
                    PRIMARY KEY(clip_id, format)
                );
                CREATE INDEX IF NOT EXISTS clips_order ON clips(pinned DESC, touched DESC);
                """;
            cmd.ExecuteNonQuery();

            // v0.1.0 has no category column. SQLite supports an additive,
            // nullable foreign key even on older Windows-provided engines.
            cmd.CommandText = "PRAGMA table_info(clips)";
            var hasCategory = false;
            using (var columns = cmd.ExecuteReader())
                while (columns.Read())
                    if (columns.GetString(1) == "category_id") hasCategory = true;
            if (!hasCategory)
            {
                cmd.CommandText = "ALTER TABLE clips ADD COLUMN category_id INTEGER REFERENCES categories(id) ON DELETE SET NULL";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch { _db.Dispose(); throw; }
    }

    private SqliteCommand Command(string sql, SqliteTransaction? tx = null,
        params (string Key, object Value)[] values)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (key, value) in values) cmd.Parameters.AddWithValue(key, value);
        return cmd;
    }

    public long Capture(ClipPayload payload)
    {
        using var tx = _db.BeginTransaction();
        var fingerprint = payload.Fingerprint();
        using var existing = Command("SELECT id FROM clips WHERE fingerprint = $hash", tx, ("$hash", fingerprint));
        var found = existing.ExecuteScalar();
        long id;
        if (found is long known)
        {
            id = known;
            using var touch = Command("UPDATE clips SET touched = (SELECT COALESCE(MAX(touched),0)+1 FROM clips) WHERE id=$id AND pinned=0",
                tx, ("$id", id));
            touch.ExecuteNonQuery();
        }
        else
        {
            using var insert = Command("""
                INSERT INTO clips(fingerprint,touched,text,has_image,has_html)
                VALUES ($hash,(SELECT COALESCE(MAX(touched),0)+1 FROM clips),$text,$image,$html)
                """, tx, ("$hash", fingerprint), ("$text", payload.Text), ("$image", payload.HasImage), ("$html", payload.HasHtml));
            // Older Windows system SQLite builds predate INSERT RETURNING.
            insert.ExecuteNonQuery();
            using var insertedId = Command("SELECT last_insert_rowid()", tx);
            id = (long)insertedId.ExecuteScalar()!;
            foreach (var pair in payload.Formats)
            {
                var format = pair.Key;
                var bytes = pair.Value;
                using var add = Command("INSERT INTO formats(clip_id,format,data) VALUES($id,$format,$data)",
                    tx, ("$id", id), ("$format", (int)format), ("$data", bytes));
                add.ExecuteNonQuery();
            }
        }
        Trim(tx);
        tx.Commit();
        return id;
    }

    private void Trim(SqliteTransaction tx)
    {
        using var cmd = Command("""
            DELETE FROM clips WHERE id IN (
                SELECT id FROM clips WHERE pinned=0 ORDER BY touched DESC,id DESC LIMIT -1 OFFSET $capacity
            )
            """, tx, ("$capacity", RecentCapacity));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<ClipItem> List(string query = "", long? view = null, bool pinnedOnly = false)
    {
        var filter = string.IsNullOrWhiteSpace(query) ? view : null;
        var where = pinnedOnly ? " WHERE pinned=1" : filter switch
        {
            PinnedView => "",
            RecentView => " WHERE pinned=0",
            null => "",
            _ => " WHERE category_id=$view"
        };
        using var cmd = Command("SELECT id,title,color,pinned,text,has_image,has_html,category_id FROM clips" +
            where + " ORDER BY pinned DESC,touched DESC,id DESC");
        if (where == " WHERE category_id=$view") cmd.Parameters.AddWithValue("$view", filter!.Value);
        using var reader = cmd.ExecuteReader();
        var items = new List<ClipItem>();
        while (reader.Read())
        {
            var item = new ClipItem(reader.GetInt64(0), reader.GetString(1),
                (ClipColor)reader.GetInt32(2), reader.GetBoolean(3),
                reader.GetString(4), reader.GetBoolean(5), reader.GetBoolean(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7));
            if (string.IsNullOrWhiteSpace(query) ||
                item.TitleMatches(query) ||
                item.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (item.HasImage && "Image".IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                (item.HasHtml && "HTML content".IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))
                items.Add(item);
        }
        // Stable ordering retains pinned-first and recency within each tier.
        // An untitled clip has no title match, regardless of its placeholder.
        return string.IsNullOrWhiteSpace(query) ? items :
            items.OrderByDescending(item => item.TitleMatches(query)).ToList();
    }

    public (bool Pinned, bool Recent) AvailableViews()
    {
        using var cmd = Command("SELECT EXISTS(SELECT 1 FROM clips WHERE pinned=1), EXISTS(SELECT 1 FROM clips WHERE pinned=0)");
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return (reader.GetBoolean(0), reader.GetBoolean(1));
    }

    public IReadOnlyList<ClipCategory> Categories()
    {
        using var cmd = Command("SELECT id,name FROM categories ORDER BY position,id");
        using var reader = cmd.ExecuteReader();
        var result = new List<ClipCategory>();
        while (reader.Read()) result.Add(new ClipCategory(reader.GetInt64(0), reader.GetString(1)));
        return result;
    }

    public static string? CategoryNameError(string name, IReadOnlyList<ClipCategory> categories, long? except = null)
    {
        name = name.Trim();
        if (name.Length == 0) return "Enter a category name.";
        if (name.Equals("Pinned", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Recent", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("No category", StringComparison.OrdinalIgnoreCase))
            return "Choose a name other than Pinned, Recent or No category.";
        if (categories.Any(c => c.Id != except && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return "A category with this name already exists.";
        return null;
    }

    public long CreateCategory(string name)
    {
        var categories = Categories();
        if (categories.Count >= CategoryCapacity) throw new InvalidOperationException("Category capacity reached.");
        if (CategoryNameError(name, categories) is string error) throw new ArgumentException(error, nameof(name));
        using var tx = _db.BeginTransaction();
        using var cmd = Command("""
            INSERT INTO categories(name,position) VALUES($name,(SELECT COALESCE(MAX(position),-1)+1 FROM categories))
            """, tx, ("$name", name.Trim()));
        cmd.ExecuteNonQuery();
        using var inserted = Command("SELECT last_insert_rowid()", tx);
        var id = (long)inserted.ExecuteScalar()!;
        tx.Commit();
        return id;
    }

    public void RenameCategory(long id, string name)
    {
        if (CategoryNameError(name, Categories(), id) is string error) throw new ArgumentException(error, nameof(name));
        using var cmd = Command("UPDATE categories SET name=$name WHERE id=$id", null, ("$name", name.Trim()), ("$id", id));
        cmd.ExecuteNonQuery();
    }

    public void MoveCategory(long id, int index)
    {
        var categories = Categories().ToList();
        var from = categories.FindIndex(c => c.Id == id);
        if (from < 0 || index < 0 || index >= categories.Count || from == index) return;
        var category = categories[from];
        categories.RemoveAt(from);
        categories.Insert(index, category);
        using var tx = _db.BeginTransaction();
        for (var i = 0; i < categories.Count; i++)
        {
            using var cmd = Command("UPDATE categories SET position=$position WHERE id=$id", tx,
                ("$position", i), ("$id", categories[i].Id));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void DeleteCategory(long id)
    {
        using var cmd = Command("DELETE FROM categories WHERE id=$id", null, ("$id", id));
        cmd.ExecuteNonQuery();
    }

    public void SetCategory(long id, long? categoryId)
    {
        using var cmd = Command("UPDATE clips SET category_id=$category WHERE id=$id", null,
            ("$category", (object?)categoryId ?? DBNull.Value), ("$id", id));
        cmd.ExecuteNonQuery();
    }

    public ClipPayload? Read(long id)
    {
        using var cmd = Command("SELECT format,data FROM formats WHERE clip_id=$id", null, ("$id", id));
        using var reader = cmd.ExecuteReader();
        var formats = new Dictionary<ClipFormat, byte[]>();
        while (reader.Read())
            formats[(ClipFormat)reader.GetInt32(0)] = (byte[])reader.GetValue(1);
        return formats.Count == 0 ? null : new ClipPayload(formats);
    }

    public void SetTitle(long id, string title)
    {
        using var cmd = Command("UPDATE clips SET title=$title WHERE id=$id", null, ("$id", id), ("$title", title.Trim()));
        cmd.ExecuteNonQuery();
    }

    public void SetColor(long id, ClipColor color)
    {
        if (!Enum.IsDefined(typeof(ClipColor), color)) throw new ArgumentOutOfRangeException(nameof(color));
        using var cmd = Command("UPDATE clips SET color=$color WHERE id=$id", null, ("$id", id), ("$color", (int)color));
        cmd.ExecuteNonQuery();
    }

    public void SetPinned(long id, bool pinned)
    {
        using var tx = _db.BeginTransaction();
        using var cmd = Command("""
            UPDATE clips SET pinned=$pinned,touched=(SELECT COALESCE(MAX(touched),0)+1 FROM clips)
            WHERE id=$id AND pinned<>$pinned
            """, tx, ("$id", id), ("$pinned", pinned));
        cmd.ExecuteNonQuery();
        Trim(tx);
        tx.Commit();
    }

    public void Delete(long id)
    {
        using var cmd = Command("DELETE FROM clips WHERE id=$id", null, ("$id", id));
        cmd.ExecuteNonQuery();
    }

    public void ClearRecent()
    {
        using var cmd = Command("DELETE FROM clips WHERE pinned=0");
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();
}
