namespace Jietu.Tests;

/// <summary>
/// 用临时目录里的真实 SQLite 库做集成测试：覆盖入库、去重、删除、固定豁免与保留期清理，
/// 这些是历史库最容易在重构时被破坏的行为契约。
/// </summary>
public sealed class HistoryStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly HistoryStore _store;

    public HistoryStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "jietu-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new HistoryStore(
            Path.Combine(_dir, "history.db"),
            Path.Combine(_dir, "images"),
            _ => false);
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            // 测试目录清理失败不影响结论。
        }
    }

    [Fact]
    public void TryAdd_ReturnsRecord_AndRoundTripsText()
    {
        var hash = Hashing.OfText("hello");
        var added = _store.TryAdd(ClipKind.Text, "hello", null, hash, ClipSource.Clipboard);
        Assert.NotNull(added);
        Assert.Equal(ClipKind.Text, added!.Kind);
        Assert.Equal(hash, added.ContentHash);
        Assert.Equal(ClipSource.Clipboard, added.Source);
        Assert.True(_store.ContainsHash(hash));
        Assert.Equal("hello", _store.GetText(added.Id));
    }

    [Fact]
    public void TryAdd_LatestDuplicate_ReturnsNull()
    {
        var hash = Hashing.OfText("dup");
        Assert.NotNull(_store.TryAdd(ClipKind.Text, "dup", null, hash, ClipSource.Clipboard));
        Assert.Null(_store.TryAdd(ClipKind.Text, "dup", null, hash, ClipSource.Clipboard));
    }

    [Fact]
    public void TryAdd_OlderDuplicate_BumpsCreatedAndKeepsId()
    {
        var hash = Hashing.OfText("bump");
        var first = _store.TryAdd(ClipKind.Text, "bump", null, hash, ClipSource.Clipboard, created: DateTimeOffset.UtcNow.AddDays(-5));
        Assert.NotNull(first);
        // 插入更新的记录，让上面的重复项不再是“最新”
        Assert.NotNull(_store.TryAdd(ClipKind.Text, "other", null, Hashing.OfText("other"), ClipSource.Clipboard));
        var again = _store.TryAdd(ClipKind.Text, "bump", null, hash, ClipSource.Clipboard);
        Assert.NotNull(again);
        Assert.Equal(first!.Id, again!.Id);
        Assert.True(again.CreatedUtc > first.CreatedUtc);
    }

    [Fact]
    public void Query_FindsBySearch()
    {
        Assert.NotNull(_store.TryAdd(ClipKind.Text, "独特搜索词", null, Hashing.OfText("独特搜索词"), ClipSource.Clipboard));
        Assert.Single(_store.Query("独特搜索词"));
        Assert.Single(_store.Query(null!, 10));
    }

    [Fact]
    public void Delete_RemovesRowAndHash()
    {
        var added = _store.TryAdd(ClipKind.Text, "gone", null, Hashing.OfText("gone"), ClipSource.Clipboard);
        Assert.NotNull(added);
        _store.Delete(added!.Id);
        Assert.Null(_store.GetText(added.Id));
        Assert.False(_store.ContainsHash(added.ContentHash));
    }

    [Fact]
    public void Purge_RemovesStaleUnpinned_KeepsPinnedAndFresh()
    {
        var stale = _store.TryAdd(ClipKind.Text, "stale", null, Hashing.OfText("stale"), ClipSource.Clipboard, created: DateTimeOffset.UtcNow.AddDays(-40));
        var oldPinned = _store.TryAdd(ClipKind.Text, "kept", null, Hashing.OfText("kept"), ClipSource.Clipboard, created: DateTimeOffset.UtcNow.AddDays(-40));
        _store.SetPinned(oldPinned!.Id, true);
        var fresh = _store.TryAdd(ClipKind.Text, "fresh", null, Hashing.OfText("fresh"), ClipSource.Clipboard);

        var removed = _store.Purge(30);

        Assert.Equal(1, removed);
        Assert.Null(_store.GetText(stale!.Id));
        Assert.Equal("kept", _store.GetText(oldPinned.Id));
        Assert.NotNull(_store.GetText(fresh!.Id));
    }

    [Fact]
    public void RichText_RoundTrips()
    {
        var html = System.Text.Encoding.UTF8.GetBytes("<b>hi</b>");
        var rtf = System.Text.Encoding.ASCII.GetBytes("{\\rtf1 \\b hi}");
        var hash = Hashing.OfRich("hi", html, rtf);
        var added = _store.TryAdd(ClipKind.Rich, "hi", null, hash, ClipSource.Clipboard, html: html, rtf: rtf);
        Assert.NotNull(added);
        Assert.Equal(ClipKind.Rich, added!.Kind);
        var rich = _store.GetRich(added.Id);
        Assert.Equal(html, rich.Html);
        Assert.Equal(rtf, rich.Rtf);
    }
}
