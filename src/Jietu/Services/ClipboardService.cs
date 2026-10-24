using System.Security.Cryptography;
using System.Text;

namespace Jietu;

internal readonly record struct CapturedClip(string Kind, string? Text, byte[]? Png, string Hash, byte[]? Html = null, byte[]? Rtf = null);

internal sealed class ClipboardService
{
    // 入库文本上限 64KB：历史列表预览只取 2000 字符，1MB 全文没有任何消费方，
    // 却会被 trigram FTS 触发器灌出 2~3 倍的索引体积。纯应用层截断，主表/触发器
    // 结构不变，无需数据库迁移；存量超长行随保留期清理自然淘汰。
    private const int TextCap = 65_536;
    private const int FormatCap = 4_000_000;
    private const int PngCap = 40_000_000;
    private const int DibCap = 160_000_000;

    private readonly IntPtr _owner;
    private readonly object _clip = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);

    // 「请勿收录」标记（密码管理器等工具写入剪贴板时附带的隐私契约）。此前只按前台进程
    // 名单过滤：名单外的管理器、以及复制后立刻切换窗口的时序绕过，都会把一次性密码明文
    // 写进历史库并被全文索引。标记附着在剪贴板数据本身上，不随窗口切换失效。
    private static uint _excludeMonitorFormat;
    private static uint _includeHistoryFormat;
    private static uint _viewerIgnoreFormat;
    private uint _pngFormat;
    private uint _htmlFormat;
    private uint _rtfFormat;
    private uint _ownSequence;
    private uint _lastSequence;
    private long _ownUntil;
    private string? _ownHash;
    private volatile bool _paused;
    private volatile bool _running;
    private int _dirty;
    private int _posted;
    private int _failStreak;
    private Task? _loop;

    public ClipboardService(IntPtr owner)
    {
        _owner = owner;
    }

    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    public event Action<CapturedClip>? Captured;

    public void Listen()
    {
        _pngFormat = NativeMethods.RegisterClipboardFormat("PNG");
        _htmlFormat = NativeMethods.RegisterClipboardFormat("HTML Format");
        _rtfFormat = NativeMethods.RegisterClipboardFormat("Rich Text Format");
        if (!NativeMethods.AddClipboardFormatListener(_owner))
        {
            Log.Error("无法监听剪贴板。");
            return;
        }

        _running = true;
        _loop = Task.Run(LoopAsync);
    }

    public void Unlisten()
    {
        NativeMethods.RemoveClipboardFormatListener(_owner);
        _running = false;
        Signal();
        try
        {
            _loop?.Wait(2000);
        }
        catch (Exception ex)
        {
            Log.Info("停止剪贴板监听：" + ex.Message);
        }
    }

    public void NotifyChanged()
    {
        if (!_running)
        {
            return;
        }

        Interlocked.Exchange(ref _dirty, 1);
        Signal();
    }

    public void SetText(string text)
    {
        var bytes = Encoding.Unicode.GetBytes(text + "\0");
        Edit(() =>
        {
            NativeMethods.EmptyClipboard();
            var handle = NativeMethods.AllocBytes(bytes);
            if (NativeMethods.SetClipboardData(NativeMethods.CfUnicodeText, handle) == IntPtr.Zero)
            {
                NativeMethods.GlobalFree(handle);
                throw new InvalidOperationException("写入文字剪贴板失败。");
            }
        });
        FinishOwn(Hashing.OfText(text));
    }

    public void SetImage(byte[] png, byte[] bgra, int width, int height)
    {
        var dib = DibImage.Build(bgra, width, height);
        var pngFormat = _pngFormat == 0 ? NativeMethods.RegisterClipboardFormat("PNG") : _pngFormat;
        Edit(() =>
        {
            NativeMethods.EmptyClipboard();
            Put(NativeMethods.CfDib, dib);
            Put(pngFormat, png);
        });
        FinishOwn(Hashing.OfBytes(png));
    }

    public void SetFiles(IReadOnlyList<string> paths)
    {
        var text = string.Join('\n', paths);
        var drop = FileDrop.Build(paths);
        Edit(() =>
        {
            NativeMethods.EmptyClipboard();
            Put(NativeMethods.CfHdrop, drop);
        });
        FinishOwn(Hashing.OfText(text));
    }

    public void SetRich(string text, byte[]? html, byte[]? rtf)
    {
        if (_htmlFormat == 0)
        {
            _htmlFormat = NativeMethods.RegisterClipboardFormat("HTML Format");
            _rtfFormat = NativeMethods.RegisterClipboardFormat("Rich Text Format");
        }

        Edit(() =>
        {
            NativeMethods.EmptyClipboard();
            if (text.Length > 0)
            {
                Put(NativeMethods.CfUnicodeText, Encoding.Unicode.GetBytes(text + "\0"));
            }

            if (html is { Length: > 0 })
            {
                Put(_htmlFormat, html);
            }

            if (rtf is { Length: > 0 })
            {
                Put(_rtfFormat, rtf);
            }
        });
        FinishOwn(Hashing.OfRich(text, html, rtf));
    }

    private async Task LoopAsync()
    {
        while (_running)
        {
            await _signal.WaitAsync();
            Interlocked.Exchange(ref _posted, 0);
            if (!_running)
            {
                return;
            }

            if (Interlocked.Exchange(ref _dirty, 0) == 0)
            {
                continue;
            }

            await WaitQuietAsync();
            if (!_running)
            {
                return;
            }

            try
            {
                await DrainAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }
    }

    private async Task WaitQuietAsync()
    {
        for (var spin = 0; spin < 4 && _running; spin++)
        {
            await Task.Delay(25);
            if (Volatile.Read(ref _dirty) == 0)
            {
                return;
            }

            Interlocked.Exchange(ref _dirty, 0);
        }
    }

    private async Task DrainAsync()
    {
        if (SkipKnown())
        {
            return;
        }

        if (_paused)
        {
            NoteSequence();
            return;
        }

        if (SensitiveForeground.IsBlocked())
        {
            Log.Info("前台程序受保护，这条复制没有进入历史。");
            NoteSequence();
            return;
        }

        var started = Environment.TickCount64;
        CapturedClip? clip = null;
        byte[]? pendingDib = null;
        var opened = false;
        for (var attempt = 0; attempt < 12 && _running; attempt++)
        {
            opened = TryCopy(out clip, out pendingDib);
            if (opened)
            {
                break;
            }

            await Task.Delay(40);
        }

        if (!opened)
        {
            if (++_failStreak <= 3)
            {
                Interlocked.Exchange(ref _dirty, 1);
                Signal();
                await Task.Delay(150);
            }
            else
            {
                _failStreak = 0;
                Log.Info("读取剪贴板时打不开。");
            }

            return;
        }

        _failStreak = 0;
        if (pendingDib != null && _running)
        {
            try
            {
                var image = await ImageFromDibAsync(pendingDib);
                if (image != null)
                {
                    clip = image;
                }
            }
            catch (Exception ex)
            {
                Log.Info("图片转存失败：" + ex.Message);
            }

            if (IsOwnHash(clip))
            {
                clip = null;
            }
        }

        if (clip == null || !_running)
        {
            return;
        }

        if (SensitiveForeground.IsBlocked())
        {
            Log.Info("前台程序受保护，这条复制没有进入历史。");
            return;
        }

        var elapsed = Environment.TickCount64 - started;
        if (elapsed >= 100)
        {
            Log.Info($"剪贴板记录 {clip.Value.Kind} 用了 {elapsed} 毫秒");
        }

        Captured?.Invoke(clip.Value);
    }

    private bool SkipKnown()
    {
        lock (_clip)
        {
            var sequence = NativeMethods.GetClipboardSequenceNumber();
            if (sequence == 0 || (sequence != _lastSequence && sequence != _ownSequence))
            {
                return false;
            }

            _lastSequence = sequence;
            return true;
        }
    }

    private void NoteSequence()
    {
        lock (_clip)
        {
            var sequence = NativeMethods.GetClipboardSequenceNumber();
            if (sequence != 0)
            {
                _lastSequence = sequence;
            }
        }
    }

    private bool TryCopy(out CapturedClip? clip, out byte[]? pendingDib)
    {
        clip = null;
        pendingDib = null;
        byte[]? png = null;
        byte[]? dib = null;
        byte[]? html = null;
        byte[]? rtf = null;
        string? text = null;
        List<string> files = [];
        var takeImage = false;
        lock (_clip)
        {
            if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                return false;
            }

            try
            {
                var sequence = NativeMethods.GetClipboardSequenceNumber();
                var own = sequence != 0 && (sequence == _ownSequence || sequence == _lastSequence);
                if (sequence != 0)
                {
                    _lastSequence = sequence;
                }

                if (own)
                {
                    return true;
                }

                if (HasExclusionMarker())
                {
                    // 来源程序（密码管理器等）声明这条内容不要被剪贴板工具收录：
                    // 跳过且不入库。序号已在上面记录，同一条内容不会被反复重试。
                    return true;
                }

                files = ReadFiles();
                if (files.Count == 0)
                {
                    text = ReadText();
                    takeImage = !PreferFormatted(text);
                    if (takeImage)
                    {
                        png = CopyPng();
                        if (png == null)
                        {
                            dib = CopyDib();
                        }
                    }

                    if (!takeImage || (png == null && dib == null))
                    {
                        html = CopyLimited(_htmlFormat, FormatCap);
                        rtf = CopyLimited(_rtfFormat, FormatCap);
                        html = TrimZeros(html);
                        rtf = TrimZeros(rtf);
                    }
                }
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }

        if (files.Count > 0)
        {
            var joined = string.Join('\n', files);
            if (joined.Length > TextCap)
            {
                // 回退到最近的换行处：保证截断后剩下的都是完整路径，粘贴时不会出现半截路径。
                joined = joined[..TextCap];
                var cut = joined.LastIndexOf('\n');
                if (cut > 0)
                {
                    joined = joined[..cut];
                }
            }

            clip = new CapturedClip(ClipKind.File, joined, null, Hashing.OfText(joined));
            return Accept(ref clip);
        }

        if (png != null)
        {
            clip = new CapturedClip(ClipKind.Image, null, png, Hashing.OfBytes(png));
            return Accept(ref clip);
        }

        clip = TextClip(text, html, rtf);
        if (dib != null)
        {
            pendingDib = dib;
            return true;
        }

        return Accept(ref clip);
    }

    private bool Accept(ref CapturedClip? clip)
    {
        if (IsOwnHash(clip))
        {
            clip = null;
        }

        return true;
    }

    private static async Task<CapturedClip?> ImageFromDibAsync(byte[] dib)
    {
        if (!DibImage.TryRead(dib, out var pixels, out var width, out var height))
        {
            return null;
        }

        var encoded = await ImageBytes.ToPngAsync(pixels, width, height);
        return new CapturedClip(ClipKind.Image, null, encoded, Hashing.OfBytes(encoded));
    }

    private static CapturedClip? TextClip(string? text, byte[]? html, byte[]? rtf)
    {
        html = Cap(html, FormatCap);
        rtf = Cap(rtf, FormatCap);
        if (text != null && text.Length > TextCap)
        {
            text = text[..TextCap];
        }

        if ((html != null || rtf != null) && ClipFormat.ShouldKeep(text, html, rtf))
        {
            text ??= "";
            return new CapturedClip(ClipKind.Rich, text, null, Hashing.OfRich(text, html, rtf), html, rtf);
        }

        if (!string.IsNullOrEmpty(text))
        {
            return new CapturedClip(ClipKind.Text, text, null, Hashing.OfText(text));
        }

        return null;
    }

    private byte[]? CopyPng()
    {
        var png = CopyLimited(_pngFormat, PngCap);
        if (png == null || png.Length <= 8 || png[0] != 0x89 || png[1] != 0x50)
        {
            return null;
        }

        return png;
    }

    private static byte[]? CopyDib()
    {
        var v5 = CopyLimited(NativeMethods.CfDibV5, DibCap);
        if (v5 != null)
        {
            return v5;
        }

        return CopyLimited(NativeMethods.CfDib, DibCap);
    }

    private string? ReadText()
    {
        var raw = CopyLimited(NativeMethods.CfUnicodeText, TextCap * 2 + 16);
        return raw == null ? null : Encoding.Unicode.GetString(raw).TrimEnd('\0');
    }

    private static byte[]? CopyLimited(uint format, int maxBytes)
    {
        if (format == 0 || !NativeMethods.IsClipboardFormatAvailable(format))
        {
            return null;
        }

        var handle = NativeMethods.GetClipboardData(format);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var size = NativeMethods.GlobalSize(handle);
        if (size == 0 || size > (nuint)maxBytes)
        {
            return null;
        }

        return NativeMethods.CopyGlobalBytes(handle);
    }

    private static void EnsureExclusionFormats()
    {
        if (_excludeMonitorFormat == 0)
        {
            _excludeMonitorFormat = NativeMethods.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
        }

        if (_includeHistoryFormat == 0)
        {
            _includeHistoryFormat = NativeMethods.RegisterClipboardFormat("CanIncludeInClipboardHistory");
        }

        if (_viewerIgnoreFormat == 0)
        {
            _viewerIgnoreFormat = NativeMethods.RegisterClipboardFormat("CF_CLIPBOARD_VIEWER_IGNORE");
        }
    }

    /// <summary>必须在 OpenClipboard 成功后调用：命中任一「请勿收录」标记返回 true。</summary>
    private static bool HasExclusionMarker()
    {
        EnsureExclusionFormats();
        if ((_excludeMonitorFormat != 0 && NativeMethods.IsClipboardFormatAvailable(_excludeMonitorFormat))
            || (_viewerIgnoreFormat != 0 && NativeMethods.IsClipboardFormatAvailable(_viewerIgnoreFormat)))
        {
            return true;
        }

        if (_includeHistoryFormat == 0 || !NativeMethods.IsClipboardFormatAvailable(_includeHistoryFormat))
        {
            return false;
        }

        // CanIncludeInClipboardHistory 是带值的 DWORD：0 = 不要进剪贴板历史。
        // 格式存在但读不出值时按排除处理——宁可少记一条，不可错记一条密码。
        var value = CopyLimited(_includeHistoryFormat, 4);
        return value == null || value.Length < 4 || BitConverter.ToUInt32(value, 0) == 0;
    }

    /// <summary>自检用：独立打开剪贴板检查排除标记，不依赖 Listen 生命周期。</summary>
    internal static bool ClipboardMarkedExcluded()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    return HasExclusionMarker();
                }
                finally
                {
                    NativeMethods.CloseClipboard();
                }
            }

            Thread.Sleep(15);
        }

        return false;
    }

    private static List<string> ReadFiles()
    {
        if (!NativeMethods.IsClipboardFormatAvailable(NativeMethods.CfHdrop))
        {
            return [];
        }

        var drop = NativeMethods.GetClipboardData(NativeMethods.CfHdrop);
        if (drop == IntPtr.Zero)
        {
            return [];
        }

        var count = (int)NativeMethods.DragQueryFile(drop, 0xFFFFFFFF, null, 0);
        var paths = new List<string>();
        for (var i = 0; i < count && paths.Count < 500; i++)
        {
            var length = (int)NativeMethods.DragQueryFile(drop, (uint)i, null, 0);
            if (length <= 0)
            {
                continue;
            }

            var buffer = new char[length + 1];
            NativeMethods.DragQueryFile(drop, (uint)i, buffer, (uint)buffer.Length);
            paths.Add(new string(buffer, 0, length));
        }

        return paths;
    }

    private static bool PreferFormatted(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.Contains('\n') || trimmed.Contains('\r') || trimmed.Length > 2048)
        {
            return true;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "file")
        {
            return false;
        }

        return true;
    }

    private static byte[]? Cap(byte[]? data, int max)
    {
        if (data == null || data.Length == 0 || data.Length > max)
        {
            return null;
        }

        return data;
    }

    private static byte[]? TrimZeros(byte[]? data)
    {
        if (data == null)
        {
            return null;
        }

        var end = data.Length;
        while (end > 0 && data[end - 1] == 0)
        {
            end--;
        }

        return end == data.Length ? data : data[..end];
    }

    private void Edit(Action action)
    {
        // 打开剪贴板可能要重试（其他进程正占用），重试等待不能攥着 _clip 锁，
        // 否则 8×15ms 的退避会把 FinishOwn 等其他锁用户一起卡住。
        // 锁只包住「成功打开后的那次写入」，保证多个写入动作之间互斥、开与关成对出现。
        for (var attempt = 0; attempt < 8; attempt++)
        {
            lock (_clip)
            {
                if (NativeMethods.OpenClipboard(_owner))
                {
                    try
                    {
                        action();
                        return;
                    }
                    finally
                    {
                        NativeMethods.CloseClipboard();
                    }
                }
            }

            Thread.Sleep(15);
        }

        throw new InvalidOperationException("无法打开剪贴板。");
    }

    private static void Put(uint format, byte[] bytes)
    {
        var handle = NativeMethods.AllocBytes(bytes);
        if (NativeMethods.SetClipboardData(format, handle) == IntPtr.Zero)
        {
            NativeMethods.GlobalFree(handle);
        }
    }

    private void FinishOwn(string hash)
    {
        lock (_clip)
        {
            _ownHash = hash;
            _ownUntil = Environment.TickCount64 + 1500;
            var sequence = NativeMethods.GetClipboardSequenceNumber();
            if (sequence != 0)
            {
                _ownSequence = sequence;
                _lastSequence = sequence;
            }
        }
    }

    private bool IsOwnHash(CapturedClip? clip)
    {
        return _ownSequence == 0
            && clip != null
            && _ownHash != null
            && Environment.TickCount64 < _ownUntil
            && clip.Value.Hash == _ownHash;
    }

    private void Signal()
    {
        if (Interlocked.CompareExchange(ref _posted, 1, 0) == 0)
        {
            _signal.Release();
        }
    }
}

internal static class DibImage
{
    public static byte[] Build(byte[] bgra, int width, int height)
    {
        var stride = width * 4;
        var dib = new byte[40 + stride * height];
        BitConverter.TryWriteBytes(dib.AsSpan(0), 40);
        BitConverter.TryWriteBytes(dib.AsSpan(4), width);
        BitConverter.TryWriteBytes(dib.AsSpan(8), height);
        BitConverter.TryWriteBytes(dib.AsSpan(12), (ushort)1);
        BitConverter.TryWriteBytes(dib.AsSpan(14), (ushort)32);
        for (var y = 0; y < height; y++)
        {
            Buffer.BlockCopy(bgra, (height - 1 - y) * stride, dib, 40 + y * stride, stride);
        }

        return dib;
    }

    public static bool TryRead(byte[] dib, out byte[] pixels, out int width, out int height)
    {
        pixels = [];
        width = 0;
        height = 0;
        if (dib.Length < 40)
        {
            return false;
        }

        var headerSize = BitConverter.ToInt32(dib, 0);
        width = BitConverter.ToInt32(dib, 4);
        var rawHeight = BitConverter.ToInt32(dib, 8);
        var bitCount = BitConverter.ToUInt16(dib, 14);
        var compression = BitConverter.ToUInt32(dib, 16);
        if (width <= 0 || width > 16000 || rawHeight == 0 || headerSize < 40 || headerSize > dib.Length)
        {
            return false;
        }

        var bottomUp = rawHeight > 0;
        height = Math.Abs(rawHeight);
        if (height > 16000 || bitCount is not (24 or 32) || compression is not (0 or 3))
        {
            return false;
        }

        var offset = headerSize;
        // 只有 40 字节 BITMAPINFOHEADER + BI_BITFIELDS 的三个通道掩码紧跟在头之后（+12）；
        // 52/56/108/124（V2~V5 头）的掩码字段在头内部，无需额外跳过。
        // 前面已保证 headerSize >= 40，故这里的 <= 40 等价于 == 40。
        if (compression == 3 && headerSize <= 40)
        {
            offset += 12;
        }

        if (headerSize >= 124)
        {
            var profileAt = BitConverter.ToInt32(dib, 112);
            var profileBytes = BitConverter.ToInt32(dib, 116);
            var profileEnd = profileAt + profileBytes;
            if (profileAt >= headerSize && profileBytes > 0 && profileEnd > offset && profileEnd <= dib.Length)
            {
                offset = profileEnd;
            }
        }

        var stride = ((width * bitCount + 31) / 32) * 4;
        if (offset < 0 || offset + stride * height > dib.Length)
        {
            return false;
        }

        pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var sourceRow = bottomUp ? height - 1 - y : y;
            var source = offset + sourceRow * stride;
            var dest = y * width * 4;
            if (bitCount == 32)
            {
                Buffer.BlockCopy(dib, source, pixels, dest, width * 4);
            }
            else
            {
                for (var x = 0; x < width; x++)
                {
                    pixels[dest + x * 4] = dib[source + x * 3];
                    pixels[dest + x * 4 + 1] = dib[source + x * 3 + 1];
                    pixels[dest + x * 4 + 2] = dib[source + x * 3 + 2];
                    pixels[dest + x * 4 + 3] = 255;
                }
            }
        }

        return true;
    }
}

internal static class Hashing
{
    public static string OfText(string text)
    {
        Span<byte> hash = stackalloc byte[32];
        var maxBytes = Encoding.UTF8.GetMaxByteCount(text.Length);
        if (maxBytes <= 512)
        {
            Span<byte> utf8 = stackalloc byte[512];
            var written = Encoding.UTF8.GetBytes(text, utf8);
            SHA256.HashData(utf8[..written], hash);
            return Convert.ToHexString(hash);
        }

        var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(maxBytes);
        try
        {
            var written = Encoding.UTF8.GetBytes(text, rented);
            SHA256.HashData(rented.AsSpan(0, written), hash);
            return Convert.ToHexString(hash);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static string OfBytes(byte[] data) => OfBytes((ReadOnlySpan<byte>)data);

    public static string OfBytes(ReadOnlySpan<byte> data)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(data, hash);
        return Convert.ToHexString(hash);
    }

    public static string OfRich(string text, byte[]? html, byte[]? rtf)
    {
        var textBytes = Encoding.UTF8.GetBytes(text);
        var buffer = new byte[textBytes.Length + (html?.Length ?? 0) + (rtf?.Length ?? 0) + 8];
        BitConverter.TryWriteBytes(buffer.AsSpan(0), textBytes.Length);
        textBytes.CopyTo(buffer.AsSpan(4));
        var offset = 4 + textBytes.Length;
        BitConverter.TryWriteBytes(buffer.AsSpan(offset), html?.Length ?? 0);
        offset += 4;
        if (html != null)
        {
            html.CopyTo(buffer.AsSpan(offset));
            offset += html.Length;
        }

        rtf?.CopyTo(buffer.AsSpan(offset));
        return OfBytes(buffer);
    }
}

internal static class FileDrop
{
    public static byte[] Build(IReadOnlyList<string> paths)
    {
        var builder = new StringBuilder();
        foreach (var path in paths)
        {
            builder.Append(path);
            builder.Append('\0');
        }

        builder.Append('\0');
        var chars = Encoding.Unicode.GetBytes(builder.ToString());
        var bytes = new byte[20 + chars.Length];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), 20);
        BitConverter.TryWriteBytes(bytes.AsSpan(16), 1);
        Buffer.BlockCopy(chars, 0, bytes, 20, chars.Length);
        return bytes;
    }

    public static List<string> Parse(byte[] data)
    {
        if (data.Length < 20)
        {
            return [];
        }

        var offset = BitConverter.ToInt32(data, 0);
        var wide = BitConverter.ToInt32(data, 16) != 0;
        if (offset < 0 || offset >= data.Length)
        {
            return [];
        }

        var text = wide
            ? Encoding.Unicode.GetString(data, offset, data.Length - offset - ((data.Length - offset) % 2))
            : Encoding.Default.GetString(data, offset, data.Length - offset);
        return text.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }
}
