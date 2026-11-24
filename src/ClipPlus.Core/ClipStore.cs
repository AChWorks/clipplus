using Microsoft.Data.Sqlite;

namespace ClipPlus.Core;

// One owner (the UI dispatcher), one connection; transactional writes make
// metadata preservation and history trimming inseparable from each change.
public sealed class ClipStore : IDisposable
{
    public const int RecentCapacity = 50;
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
            cmd.CommandText = """
                PRAGMA foreign_keys = ON;
                PRAGMA busy_timeout = 5000;
                CREATE TABLE IF NOT EXISTS clips (
                    id INTEGER PRIMARY KEY,
                    fingerprint TEXT NOT NULL UNIQUE,
                    title TEXT NOT NULL DEFAULT '',
                    color INTEGER NOT NULL DEFAULT 0,
                    pinned INTEGER NOT NULL DEFAULT 0,
                    touched INTEGER NOT NULL,
                    text TEXT NOT NULL,
                    has_image INTEGER NOT NULL,
                    has_html INTEGER NOT NULL
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
            foreach (var (format, bytes) in payload.Formats)
            {
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

    public IReadOnlyList<ClipItem> List(string query = "")
    {
        using var cmd = Command("SELECT id,title,color,pinned,text,has_image,has_html FROM clips ORDER BY pinned DESC,touched DESC,id DESC");
        using var reader = cmd.ExecuteReader();
        var items = new List<ClipItem>();
        while (reader.Read())
        {
            var item = new ClipItem(reader.GetInt64(0), reader.GetString(1),
                (ClipColor)reader.GetInt32(2), reader.GetBoolean(3),
                reader.GetString(4), reader.GetBoolean(5), reader.GetBoolean(6));
            if (string.IsNullOrWhiteSpace(query) ||
                item.TitleMatches(query) ||
                item.Text.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (item.HasImage && "Image".Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                (item.HasHtml && "HTML content".Contains(query, StringComparison.OrdinalIgnoreCase)))
                items.Add(item);
        }
        // Stable ordering retains pinned-first and recency within each tier.
        // An untitled clip has no title match, regardless of its placeholder.
        return string.IsNullOrWhiteSpace(query) ? items :
            items.OrderByDescending(item => item.TitleMatches(query)).ToList();
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
        if (!Enum.IsDefined(color)) throw new ArgumentOutOfRangeException(nameof(color));
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
