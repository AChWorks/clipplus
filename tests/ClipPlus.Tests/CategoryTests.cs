using ClipPlus.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ClipPlus.Tests;

public sealed class CategoryTests
{
    [Fact]
    public void QuickSearchUsesOnlyActualPinsAndRanksTitlesFirst()
    {
        using var store = new ClipStore(":memory:");
        var work = store.CreateCategory("Work");
        var title = store.Capture(ClipPayload.FromText("original payload"));
        store.SetPinned(title, true);
        store.SetTitle(title, "Match عنوان");
        store.SetCategory(title, work);
        var content = store.Capture(ClipPayload.FromText("MATCH عنوان in payload"));
        store.SetPinned(content, true);
        var recent = store.Capture(ClipPayload.FromText("match recent"));
        store.SetTitle(recent, "Match recent title");
        var other = store.Capture(ClipPayload.FromText("untitled private text"));
        store.SetPinned(other, true);
        Assert.Equal(new[] { other, content, title }, store.List(pinnedOnly: true).Select(c => c.Id));
        Assert.Equal(new[] { title, content }, store.List("match", pinnedOnly: true).Select(c => c.Id));
        Assert.Equal(new[] { title, content }, store.List("عنوان", view: ClipStore.RecentView, pinnedOnly: true).Select(c => c.Id));
        Assert.Equal(other, Assert.Single(store.List("private text", pinnedOnly: true)).Id);
        Assert.Empty(store.List("No Title", pinnedOnly: true));
        Assert.Empty(store.List("missing", pinnedOnly: true));
        Assert.Equal("original payload", store.Read(title)!.Text);
        store.SetPinned(content, false);
        Assert.Equal(title, Assert.Single(store.List("match", pinnedOnly: true)).Id);
        Assert.Equal(new[] { title, recent, content }, store.List("match").Select(c => c.Id));
    }

    [Fact]
    public void QuickGeometryPersistsIndependentlyAndOldPreferencesLoad()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        try
        {
            File.WriteAllText(path, "{\"WindowHeight\":650,\"WindowPosition\":{\"X\":-400,\"Y\":50}}");
            var original = Preferences.Load(path);
            Assert.Equal(650, original.WindowHeight);
            Assert.Equal(new WindowPosition(-400, 50), original.WindowPosition);
            Assert.Equal(new QuickGeometry(260, 420, null), original.QuickPanel);
            var updated = original with { QuickPanel = new QuickGeometry(280, 350, new WindowPosition(120, 30)) };
            updated.Save(path);
            Assert.Equal(updated, Preferences.Load(path));
            Assert.Equal(original.WindowPosition, updated.WindowPosition);
            Assert.Equal(original.WindowHeight, updated.WindowHeight);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ViewsAndGlobalSearchPreservePinAndTitleOrdering()
    {
        using var store = new ClipStore(":memory:");
        var work = store.CreateCategory("Work");
        var personal = store.CreateCategory("Personal");
        var pin = store.Capture(ClipPayload.FromText("match pinned"));
        store.SetCategory(pin, work);
        store.SetPinned(pin, true);
        var recent = store.Capture(ClipPayload.FromText("match recent"));
        store.SetCategory(recent, work);
        var other = store.Capture(ClipPayload.FromText("other"));
        store.SetTitle(other, "Match title");
        store.SetCategory(other, personal);
        var unassigned = store.Capture(ClipPayload.FromText("uncategorized"));
        Assert.Equal(new[] { pin, recent }, store.List(view: work).Select(c => c.Id));
        Assert.Equal(new[] { pin, unassigned, other, recent }, store.List(view: ClipStore.PinnedView).Select(c => c.Id));
        Assert.Equal(new[] { unassigned, other, recent }, store.List(view: ClipStore.RecentView).Select(c => c.Id));
        Assert.Equal(new[] { other, pin, recent }, store.List("MATCH", work).Select(c => c.Id));
        Assert.Equal((true, true), store.AvailableViews());
        store.SetCategory(recent, personal);
        Assert.Equal(pin, Assert.Single(store.List(view: work)).Id);
        store.SetCategory(recent, null);
        Assert.Null(store.List().Single(c => c.Id == recent).CategoryId);
    }

    [Fact]
    public void DuplicateRenameAndDeletionPreserveAllClipMetadata()
    {
        using var store = new ClipStore(":memory:");
        var category = store.CreateCategory("  کار  ");
        var payload = ClipPayload.FromText("synthetic content");
        var id = store.Capture(payload);
        store.SetTitle(id, "Title");
        store.SetColor(id, ClipColor.Green);
        store.SetPinned(id, true);
        store.SetCategory(id, category);
        Assert.Equal(id, store.Capture(payload));
        store.RenameCategory(category, "  Renamed  ");
        Assert.Equal("Renamed", Assert.Single(store.Categories()).Name);
        Assert.Equal(category, Assert.Single(store.List()).CategoryId);
        store.DeleteCategory(category);
        Assert.Empty(store.Categories());
        var item = Assert.Single(store.List());
        Assert.Null(item.CategoryId);
        Assert.Equal("Title", item.Title);
        Assert.Equal(ClipColor.Green, item.Color);
        Assert.True(item.Pinned);
        Assert.Equal(id, item.Id);
        Assert.Equal(payload.Fingerprint(), store.Read(id)!.Fingerprint());
        Assert.NotEqual(category, store.CreateCategory("Renamed"));
    }

    [Fact]
    public void CategoryNamesCapacityAndOrderRemainConsistent()
    {
        using var store = new ClipStore(":memory:");
        Assert.Throws<ArgumentException>(() => store.CreateCategory(" "));
        Assert.Throws<ArgumentException>(() => store.CreateCategory("PINNED"));
        Assert.Throws<ArgumentException>(() => store.CreateCategory("Recent"));
        Assert.Throws<ArgumentException>(() => store.CreateCategory("No category"));
        var first = store.CreateCategory("First");
        Assert.Throws<ArgumentException>(() => store.CreateCategory(" first "));
        var last = store.CreateCategory("Last");
        Assert.Throws<ArgumentException>(() => store.RenameCategory(last, "FIRST"));
        store.RenameCategory(first, "FIRST");
        store.MoveCategory(last, 0);
        Assert.Equal(new[] { last, first }, store.Categories().Select(c => c.Id));
        store.MoveCategory(last, 1);
        Assert.Equal(new[] { first, last }, store.Categories().Select(c => c.Id));
        store.MoveCategory(last, 2);
        Assert.Equal(new[] { first, last }, store.Categories().Select(c => c.Id));
        for (var i = 2; i < ClipStore.CategoryCapacity; i++) store.CreateCategory($"Category {i}");
        Assert.Equal(10, store.Categories().Count);
        Assert.Throws<InvalidOperationException>(() => store.CreateCategory("Overflow"));
        store.DeleteCategory(first);
        store.CreateCategory("Replacement");
        Assert.Equal(10, store.Categories().Count);
        Assert.Equal("Replacement", store.Categories().Last().Name);
        var clip = store.Capture(ClipPayload.FromText("foreign key"));
        Assert.Throws<SqliteException>(() => store.SetCategory(clip, first));
    }

    [Fact]
    public void CategorizationDoesNotChangeGlobalRetentionOrClear()
    {
        using var store = new ClipStore(":memory:");
        var category = store.CreateCategory("Work");
        var pinned = store.Capture(ClipPayload.FromText("permanent"));
        store.SetCategory(pinned, category);
        store.SetPinned(pinned, true);
        var old = store.Capture(ClipPayload.FromText("old categorized"));
        store.SetCategory(old, category);
        for (var i = 0; i < 100; i++) store.Capture(ClipPayload.FromText($"new {i}"));
        Assert.Null(store.Read(old));
        Assert.Equal(100, store.List(view: ClipStore.RecentView).Count);
        Assert.Equal(pinned, Assert.Single(store.List(view: category)).Id);
        var recent = store.List(view: ClipStore.RecentView)[0].Id;
        store.SetCategory(recent, category);
        store.ClearRecent();
        Assert.Null(store.Read(recent));
        Assert.Equal(pinned, Assert.Single(store.List()).Id);
        Assert.Equal(category, Assert.Single(store.List()).CategoryId);
        Assert.Equal((true, false), store.AvailableViews());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyDatabaseMigratesAndReopensWithoutLosingPayloadsOrSettings(bool failFirst)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "history.db");
            var payload = ClipPayload.FromText("legacy payload");
            SqlitePlatform.Initialize();
            // Exact v0.1.0 schema, populated before the new store opens it.
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    PRAGMA foreign_keys = ON;
                    CREATE TABLE clips (
                        id INTEGER PRIMARY KEY, fingerprint TEXT NOT NULL UNIQUE,
                        title TEXT NOT NULL DEFAULT '', color INTEGER NOT NULL DEFAULT 0,
                        pinned INTEGER NOT NULL DEFAULT 0, touched INTEGER NOT NULL,
                        text TEXT NOT NULL, has_image INTEGER NOT NULL, has_html INTEGER NOT NULL
                    );
                    CREATE TABLE formats (
                        clip_id INTEGER NOT NULL REFERENCES clips(id) ON DELETE CASCADE,
                        format INTEGER NOT NULL, data BLOB NOT NULL, PRIMARY KEY(clip_id, format)
                    );
                    CREATE INDEX clips_order ON clips(pinned DESC, touched DESC);
                    INSERT INTO clips VALUES(42,$hash,'Legacy title',$color,1,10,$text,0,0);
                    INSERT INTO formats VALUES(42,$format,$data);
                    """;
                cmd.Parameters.AddWithValue("$hash", payload.Fingerprint());
                cmd.Parameters.AddWithValue("$color", (int)ClipColor.Purple);
                cmd.Parameters.AddWithValue("$text", payload.Text);
                cmd.Parameters.AddWithValue("$format", (int)ClipFormat.UnicodeText);
                cmd.Parameters.AddWithValue("$data", payload.Formats[ClipFormat.UnicodeText]);
                cmd.ExecuteNonQuery();
            }
            if (failFirst)
            {
                // Force the schema initialization to fail after creating the
                // categories table, with all legacy clips/formats untouched.
                // A conflicting index name makes SQLite reject CREATE INDEX.
                using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
                {
                    connection.Open();
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "DROP INDEX clips_order; CREATE TABLE clips_order(id INTEGER);";
                    cmd.ExecuteNonQuery();
                }
                var error = Assert.Throws<SqliteException>(() => new ClipStore(path));
                Assert.Contains("clips_order", error.Message);
                using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
                {
                    connection.Open();
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='categories'";
                    Assert.Equal(0L, cmd.ExecuteScalar());
                    cmd.CommandText = "PRAGMA table_info(clips)";
                    var columns = new List<string>();
                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read()) columns.Add(reader.GetString(1));
                    Assert.Equal(new[] { "id", "fingerprint", "title", "color", "pinned", "touched", "text", "has_image", "has_html" }, columns);
                    cmd.CommandText = "SELECT data FROM formats WHERE clip_id=42 AND format=1";
                    Assert.Equal(payload.Formats[ClipFormat.UnicodeText], (byte[])cmd.ExecuteScalar()!);
                    cmd.CommandText = "SELECT fingerprint FROM clips WHERE id=42";
                    Assert.Equal(payload.Fingerprint(), cmd.ExecuteScalar());
                    cmd.CommandText = "DROP TABLE clips_order; CREATE INDEX clips_order ON clips(pinned DESC,touched DESC)";
                    cmd.ExecuteNonQuery();
                }
            }
            long category;
            long second;
            using (var store = new ClipStore(path))
            {
                var item = Assert.Single(store.List());
                Assert.Equal(42L, item.Id);
                Assert.Equal("Legacy title", item.Title);
                Assert.Equal(ClipColor.Purple, item.Color);
                Assert.True(item.Pinned);
                Assert.Null(item.CategoryId);
                Assert.Equal(payload.Fingerprint(), store.Read(42)!.Fingerprint());
                category = store.CreateCategory("Legacy");
                second = store.CreateCategory("Second");
                store.MoveCategory(second, 0);
                store.SetCategory(42, category);
            }
            using (var store = new ClipStore(path))
            {
                Assert.Equal(category, Assert.Single(store.List()).CategoryId);
                Assert.Equal(payload.Fingerprint(), store.Read(42)!.Fingerprint());
                Assert.Equal(new[] { second, category }, store.Categories().Select(c => c.Id));
                store.DeleteCategory(category);
                Assert.Null(Assert.Single(store.List()).CategoryId);
            }
            var settings = Path.Combine(directory, "settings.json");
            File.WriteAllText(settings, "{\"WindowHeight\":650,\"Shortcut\":\"CtrlAltV\"}");
            var previous = Preferences.Load(settings);
            Assert.Equal(650, previous.WindowHeight);
            Assert.Equal(Shortcut.CtrlAltV, previous.Shortcut);
            Assert.False(previous.CompactView);
            Assert.Null(previous.ActiveView);
            var current = previous with { CompactView = true, ActiveView = second };
            current.Save(settings);
            Assert.Equal(current, Preferences.Load(settings));
        }
        finally { Directory.Delete(directory, true); }
    }
}
