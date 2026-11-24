using System.Text;
using ClipPlus.Core;
using Xunit;

namespace ClipPlus.Tests;

public sealed class StoreTests
{
    [Fact]
    public void RetainsOneHundredRecentAndAllPins()
    {
        using var store = new ClipStore(":memory:");
        var pin = store.Capture(ClipPayload.FromText("pinned"));
        store.SetPinned(pin, true);
        for (var i = 0; i < 125; i++) store.Capture(ClipPayload.FromText($"item {i}"));
        var rows = store.List();
        Assert.Equal(101, rows.Count);
        Assert.Equal(pin, rows[0].Id);
        Assert.Equal("item 124", rows[1].Text);
        Assert.Equal("item 25", rows[rows.Count - 1].Text);
        store.ClearRecent();
        Assert.Equal(pin, Assert.Single(store.List()).Id);
    }

    [Fact]
    public void DuplicatePreservesMetadataAndPinnedOrder()
    {
        using var store = new ClipStore(":memory:");
        var first = store.Capture(ClipPayload.FromText("first"));
        store.SetPinned(first, true);
        store.SetTitle(first, "Important");
        store.SetColor(first, ClipColor.Green);
        var second = store.Capture(ClipPayload.FromText("second"));
        store.SetPinned(second, true);
        Assert.Equal(first, store.Capture(ClipPayload.FromText("first")));
        var rows = store.List();
        Assert.Equal(second, rows[0].Id);
        Assert.Equal(first, rows[1].Id);
        Assert.Equal("Important", rows[1].Title);
        Assert.Equal(ClipColor.Green, rows[1].Color);
        Assert.Equal(first, Assert.Single(store.List("IMPORTANT")).Id);
        Assert.Equal(first, Assert.Single(store.List("FIRST")).Id);
        Assert.Equal("first", store.Read(first)!.Text);
    }

    [Fact]
    public void UnpinMaintainsRecentCapacityAndColor()
    {
        using var store = new ClipStore(":memory:");
        var pin = store.Capture(ClipPayload.FromText("pin"));
        store.SetPinned(pin, true);
        store.SetColor(pin, ClipColor.Purple);
        for (var i = 0; i < 100; i++) store.Capture(ClipPayload.FromText($"{i}"));
        store.SetPinned(pin, false);
        Assert.Equal(100, store.List().Count);
        Assert.Equal(pin, store.List()[0].Id);
        Assert.Equal(ClipColor.Purple, store.List()[0].Color);
        store.SetColor(pin, ClipColor.Default);
        Assert.Equal(ClipColor.Default, store.List()[0].Color);
    }

    [Fact]
    public void PayloadFormatRoundTripAndNormalization()
    {
        using var store = new ClipStore(":memory:");
        var text = Encoding.Unicode.GetBytes("سلام🌍\0padding");
        var html = Encoding.UTF8.GetBytes("HTML\0ignored");
        var png = new byte[] { 137, 80, 78, 71, 13, 10 };
        var payload = new ClipPayload(new Dictionary<ClipFormat, byte[]>
        {
            [ClipFormat.UnicodeText] = text, [ClipFormat.Html] = html, [ClipFormat.Png] = png
        });
        var id = store.Capture(payload);
        var read = store.Read(id)!;
        Assert.Equal("سلام🌍", read.Text);
        Assert.Equal(Encoding.UTF8.GetBytes("HTML\0"), read.Formats[ClipFormat.Html]);
        Assert.Equal(png, read.Formats[ClipFormat.Png]);
        Assert.Equal(payload.Fingerprint(), read.Fingerprint());
        Assert.NotEqual(id, store.Capture(ClipPayload.FromText("سلام🌍")));
        store.Delete(id);
        Assert.Null(store.Read(id));
    }

    [Fact]
    public void PreserveCloudExclusionAndDoNotCreateMarkerOnlyHistory()
    {
        using var store = new ClipStore(":memory:");
        var payload = new ClipPayload(new Dictionary<ClipFormat, byte[]>
        {
            [ClipFormat.UnicodeText] = Encoding.Unicode.GetBytes("private cloud policy\\0"),
            [ClipFormat.NoCloudUpload] = new byte[] { 0, 0, 0, 0, 42, 42 }
        });
        var id = store.Capture(payload);
        Assert.Equal(new byte[4], store.Read(id)!.Formats[ClipFormat.NoCloudUpload]);
        Assert.Throws<ArgumentException>(() => new ClipPayload(new Dictionary<ClipFormat, byte[]>
        {
            [ClipFormat.NoCloudUpload] = new byte[4]
        }));
    }

    [Fact]
    public void RecentDuplicateMovesToFrontWithoutChangingTitleOrColor()
    {
        using var store = new ClipStore(":memory:");
        var first = store.Capture(ClipPayload.FromText("اول"));
        store.SetTitle(first, "عنوان");
        store.SetColor(first, ClipColor.Blue);
        store.Capture(ClipPayload.FromText("second"));
        Assert.Equal(first, store.Capture(ClipPayload.FromText("اول")));
        Assert.Equal(first, store.List()[0].Id);
        Assert.Equal("عنوان", store.List()[0].Title);
        Assert.Equal(ClipColor.Blue, store.List()[0].Color);
        store.SetTitle(first, " ");
        Assert.Equal("No Title", store.List()[0].DisplayTitle);
        Assert.Equal(first, Assert.Single(store.List("اول")).Id);
    }

    [Fact]
    public void SearchPrioritizesRealTitlesAcrossPinnedAndRecentClips()
    {
        using var store = new ClipStore(":memory:");
        var titleRecent = store.Capture(ClipPayload.FromText("command one"));
        store.SetTitle(titleRecent, "My Server");
        var titlePin = store.Capture(ClipPayload.FromText("command two"));
        store.SetTitle(titlePin, "SERVER SSH");
        store.SetPinned(titlePin, true);
        var contentPin = store.Capture(ClipPayload.FromText("server content pinned"));
        store.SetPinned(contentPin, true);
        var contentRecent = store.Capture(ClipPayload.FromText("server content recent"));
        Assert.Equal(new[] { contentPin, titlePin, contentRecent, titleRecent }, store.List().Select(item => item.Id));
        Assert.Equal(new[] { titlePin, titleRecent, contentPin, contentRecent }, store.List("sErVeR").Select(item => item.Id));
        Assert.Equal(new[] { titlePin, titleRecent, contentPin, contentRecent }, store.List("s").Select(item => item.Id));
        Assert.Empty(store.List("No Title"));
        store.SetTitle(titleRecent, "");
        Assert.Equal(new[] { titlePin, contentPin, contentRecent }, store.List("SERVER").Select(item => item.Id));
        Assert.Equal("No Title", store.List().Single(item => item.Id == titleRecent).DisplayTitle);
        store.SetTitle(titleRecent, "سرور");
        Assert.Equal(titleRecent, Assert.Single(store.List("سر")).Id);
    }

    [Fact]
    public void DatabaseAndPreferencesSurviveRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var database = Path.Combine(directory, "history.db");
            long id;
            using (var store = new ClipStore(database))
            {
                id = store.Capture(ClipPayload.FromText("durable"));
                store.SetTitle(id, "Stored");
                store.SetColor(id, ClipColor.Pink);
                store.SetPinned(id, true);
            }
            using (var store = new ClipStore(database))
            {
                var item = Assert.Single(store.List());
                Assert.Equal(id, item.Id);
                Assert.Equal("Stored", item.Title);
                Assert.True(item.Pinned);
                Assert.Equal(ClipColor.Pink, item.Color);
            }
            var file = Path.Combine(directory, "settings.json");
            var preferences = new Preferences { WindowHeight = 734, WindowPosition = new WindowPosition(-1500, 240), Shortcut = Shortcut.CtrlAltV, CompactView = true, ActiveView = 7 };
            preferences.Save(file);
            Assert.Equal(preferences, Preferences.Load(file));
        }
        finally { Directory.Delete(directory, true); }
    }
}
