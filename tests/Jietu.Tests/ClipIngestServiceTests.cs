namespace Jietu.Tests;

/// <summary>
/// ClipIngestService 是截图与剪贴板两条入库路径的共用实现，
/// 这里锁住它的核心契约：查重、写盘、孤儿副本清理与保留路径解析。
/// </summary>
public sealed class ClipIngestServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly HistoryStore _store;
    private readonly ClipIngestService _ingest;
    private readonly byte[] _png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    public ClipIngestServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "jietu-ingest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new HistoryStore(
            Path.Combine(_dir, "history.db"),
            Path.Combine(_dir, "images"),
            _ => false);
        _ingest = new ClipIngestService(_store);
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
        }
    }

    [Fact]
    public void StoreImage_FirstAdd_WritesFileAndReturnsRecord()
    {
        var (added, kept) = _ingest.StoreImage(_png, ClipSource.Screenshot);
        Assert.NotNull(added);
        Assert.Equal(ClipKind.Image, added!.Kind);
        Assert.NotNull(kept);
        Assert.True(File.Exists(kept));
        Assert.Equal(kept, added.ImagePath);
        Assert.Single(Directory.GetFiles(_store.ImageDirectory));
    }

    [Fact]
    public void StoreImage_LatestDuplicate_ReturnsExistingWithoutNewFile()
    {
        var (first, firstPath) = _ingest.StoreImage(_png, ClipSource.Screenshot);
        var (dup, dupPath) = _ingest.StoreImage(_png, ClipSource.Screenshot);
        Assert.Null(dup);
        Assert.Equal(firstPath, dupPath);
        Assert.Single(Directory.GetFiles(_store.ImageDirectory));
    }

    [Fact]
    public async Task AddCapturedAsync_TextRoundTrips()
    {
        var added = await _ingest.AddCapturedAsync(new CapturedClip(ClipKind.Text, "hello", null, Hashing.OfText("hello")));
        Assert.NotNull(added);
        Assert.Equal("hello", _store.GetText(added!.Id));
    }

    [Fact]
    public async Task AddCapturedAsync_ImageDuplicate_LeavesOneFile()
    {
        var hash = Hashing.OfBytes(_png);
        var first = await _ingest.AddCapturedAsync(new CapturedClip(ClipKind.Image, null, _png, hash));
        var second = await _ingest.AddCapturedAsync(new CapturedClip(ClipKind.Image, null, _png, hash));
        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Single(Directory.GetFiles(_store.ImageDirectory));
    }
}
