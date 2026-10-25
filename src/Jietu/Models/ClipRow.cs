using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Jietu;

public sealed class ClipRow : INotifyPropertyChanged
{
    private const int ThumbnailCap = 64;

    private static readonly Dictionary<string, BitmapImage> Thumbnails = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> ThumbnailOrder = new();
    private static readonly SemaphoreSlim ThumbnailGate = new(3);
    private static readonly object ThumbnailSync = new();

    private string _preview = "";
    private string? _recognizedText;
    private ImageSource? _thumbnail;
    private bool _loading;
    private bool _pinned;
    private bool _missing;
    private string _shortcut = "";
    private string _missingText = "这张图片已经找不到了";

    public required string Id { get; init; }
    public required string Kind { get; init; }
    public string? ImagePath { get; init; }
    public required string TimeText { get; init; }
    public long CreatedUtc { get; init; }
    public string Shortcut => _shortcut;
    public Visibility HasShortcutVisibility => !string.IsNullOrEmpty(_shortcut) ? Visibility.Visible : Visibility.Collapsed;
    public string PinTip => Pinned ? "取消固定" : "固定后，清空和过期都不会删除";
    public Visibility PinOnVisibility => Pinned ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PinOffVisibility => Pinned ? Visibility.Collapsed : Visibility.Visible;
    public bool IsImage => Kind == ClipKind.Image;

    /// <summary>列表行左侧的类型徽标字形。</summary>
    public string KindGlyph => Kind switch
    {
        ClipKind.Image => "\uEB9F",
        ClipKind.File => "\uE8A5",
        ClipKind.Rich => "\uE8D2",
        _ => "\uE8FD",
    };
    public string Preview => _missing && IsImage ? _missingText : _preview;
    public Visibility TextVisibility => !IsImage || _missing ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ImageVisibility => IsImage && !_missing ? Visibility.Visible : Visibility.Collapsed;
    public string? RecognizedText => _recognizedText;
    public string RecognizedTextSnippet => !string.IsNullOrEmpty(_recognizedText) ? OcrSnippet(_recognizedText) : "";
    public bool HasRecognizedText => !string.IsNullOrWhiteSpace(_recognizedText);
    public Visibility HasRecognizedTextVisibility => HasRecognizedText ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoRecognizedTextVisibility => HasRecognizedText ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>仅图片行显示「图像记录」占位，文本行不参与；
    /// OCR 只认出噪声（清完不剩内容）时也按没文字处理。</summary>
    public Visibility ImageNoTextVisibility =>
        IsImage && !_missing && string.IsNullOrEmpty(RecognizedTextSnippet) ? Visibility.Visible : Visibility.Collapsed;

    public bool Pinned
    {
        get => _pinned;
        set
        {
            if (_pinned == value)
            {
                return;
            }

            _pinned = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PinTip));
            OnPropertyChanged(nameof(PinOnVisibility));
            OnPropertyChanged(nameof(PinOffVisibility));
        }
    }

    public void SetShortcut(string shortcut)
    {
        if (_shortcut == shortcut)
        {
            return;
        }

        _shortcut = shortcut;
        OnPropertyChanged(nameof(Shortcut));
        OnPropertyChanged(nameof(HasShortcutVisibility));
    }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            _thumbnail = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public void UpdateRecognizedText(string text)
    {
        var trimmed = text.Trim();
        if (_recognizedText == trimmed)
        {
            return;
        }

        _recognizedText = trimmed;
        OnPropertyChanged(nameof(RecognizedText));
        OnPropertyChanged(nameof(RecognizedTextSnippet));
        OnPropertyChanged(nameof(HasRecognizedText));
        OnPropertyChanged(nameof(HasRecognizedTextVisibility));
        OnPropertyChanged(nameof(NoRecognizedTextVisibility));
        OnPropertyChanged(nameof(ImageNoTextVisibility));
    }

    public static ClipRow From(ClipRecord record)
    {
        var row = new ClipRow
        {
            Id = record.Id,
            Kind = record.Kind,
            ImagePath = record.ImagePath,
            _preview = record.Kind switch
            {
                ClipKind.File => PreviewText(ClipFiles.Preview(record.Text)),
                ClipKind.Rich => string.IsNullOrWhiteSpace(record.Text) ? "带格式的文字" : PreviewText(record.Text),
                ClipKind.Text => PreviewText(record.Text),
                _ => "",
            },
            _recognizedText = record.Kind == ClipKind.Image && !string.IsNullOrWhiteSpace(record.Text) ? record.Text.Trim() : null,
            TimeText = FormatTime(record.CreatedUtc, record.Source) + KindSuffix(record.Kind),
            CreatedUtc = record.CreatedUtc,
        };
        row._pinned = record.Pinned;
        return row;
    }

    public static string PreviewPath(string imagePath) => Path.Combine(AppPaths.Thumbs, Path.GetFileName(imagePath));

    public static void ForgetThumbnail(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        lock (ThumbnailSync)
        {
            Thumbnails.Remove(path);
        }

        try
        {
            var preview = PreviewPath(path);
            if (File.Exists(preview))
            {
                File.Delete(preview);
            }
        }
        catch (Exception ex)
        {
            Log.Info("删除缩略图失败：" + ex.Message);
        }
    }

    public static void ClearThumbnails()
    {
        lock (ThumbnailSync)
        {
            Thumbnails.Clear();
            ThumbnailOrder.Clear();
        }
    }

    public async Task EnsureThumbnailAsync()
    {
        if (!IsImage || Thumbnail != null || _loading || _missing || string.IsNullOrEmpty(ImagePath))
        {
            return;
        }

        lock (ThumbnailSync)
        {
            if (Thumbnails.TryGetValue(ImagePath, out var cached))
            {
                Thumbnail = cached;
                return;
            }
        }

        _loading = true;
        var taken = false;
        try
        {
            await ThumbnailGate.WaitAsync();
            taken = true;
            lock (ThumbnailSync)
            {
                if (Thumbnails.TryGetValue(ImagePath, out var cached))
                {
                    Thumbnail = cached;
                    return;
                }
            }

            if (!File.Exists(ImagePath))
            {
                MarkMissing("这张图片已经找不到了");
                return;
            }

            var preview = PreviewPath(ImagePath);
            if (!File.Exists(preview))
            {
                try
                {
                    await ImageBytes.SavePreviewAsync(ImagePath, preview);
                }
                catch (Exception ex)
                {
                    Log.Info("缩略图生成失败：" + ex.Message);
                }
            }

            // 先读图像头算比例：DecodePixelWidth/DecodePixelHeight 同时设置会把位图拉变形，
            // 所以只对受限的那一维设置解码上限（长图按高度 720 封顶，避免解码整幅 2 万像素高的原图）。
            var image = new BitmapImage();
            var loadPath = File.Exists(preview) ? preview : ImagePath;
            await using var file = new FileStream(loadPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var stream = file.AsRandomAccessStream();
            try
            {
                var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
                var sourceWidth = Math.Max(1u, decoder.PixelWidth);
                var sourceHeight = Math.Max(1u, decoder.PixelHeight);
                const double MaxWidth = 360, MaxHeight = 720;
                var widthFactor = MaxWidth / sourceWidth;
                var heightFactor = MaxHeight / sourceHeight;
                var factor = Math.Min(widthFactor, heightFactor);
                if (factor < 1d)
                {
                    if (widthFactor <= heightFactor)
                    {
                        image.DecodePixelWidth = Math.Max(1, (int)Math.Floor(sourceWidth * factor));
                    }
                    else
                    {
                        image.DecodePixelHeight = Math.Max(1, (int)Math.Floor(sourceHeight * factor));
                    }
                }
            }
            catch (Exception ex)
            {
                // 头解析失败时退回整幅解码，交给 SetSourceAsync 自己处理。
                Log.Info("缩略图尺寸探测失败：" + ex.Message);
            }

            stream.Seek(0);
            await image.SetSourceAsync(stream);
            RememberThumbnail(ImagePath, image);
            Thumbnail = image;
        }
        catch (Exception ex)
        {
            Log.Info("缩略图读取失败：" + ex.Message);
            MarkMissing("这张图片无法显示");
        }
        finally
        {
            if (taken)
            {
                ThumbnailGate.Release();
            }

            _loading = false;
        }
    }

    private static void RememberThumbnail(string path, BitmapImage image)
    {
        lock (ThumbnailSync)
        {
            if (!Thumbnails.ContainsKey(path))
            {
                ThumbnailOrder.Enqueue(path);
            }

            Thumbnails[path] = image;
            while (ThumbnailOrder.Count > ThumbnailCap)
            {
                var old = ThumbnailOrder.Dequeue();
                if (!old.Equals(path, StringComparison.OrdinalIgnoreCase))
                {
                    Thumbnails.Remove(old);
                }
            }
        }
    }

    private void MarkMissing(string message)
    {
        if (_missing)
        {
            return;
        }

        _missing = true;
        _missingText = message;
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(TextVisibility));
        OnPropertyChanged(nameof(ImageVisibility));
        OnPropertyChanged(nameof(ImageNoTextVisibility));
    }

    private static string PreviewText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        return normalized.Length > 400 ? normalized[..400] : normalized;
    }

    /// <summary>
    /// 列表行的 OCR 摘要：压平换行，让两行截断按自然断句铺满行宽；
    /// 并滤掉系统 OCR 把设置页开关圆钮认成的孤立「0/O」——它们总是单独成词、
    /// 不与内容相连，列表里看起来就像每条记录都以「0 」开头。全文预览与
    /// 复制识别文字仍用原始文本，不受这里影响。
    /// </summary>
    internal static string OcrSnippet(string text)
    {
        var flat = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ').Replace('\n', ' ');
        var builder = new System.Text.StringBuilder(flat.Length);
        foreach (var part in flat.Split(' ', '\u3000', StringSplitOptions.RemoveEmptyEntries))
        {
            // 「0」与中文之间 OCR 不补空格，会黏成「0常规与自启」这种词，按词首再剥一层。
            var token = part.Length > 1 && (part[0] is '0' or 'O') && IsCjk(part[1]) ? part[1..] : part;
            if (token.Length == 1 && token[0] is '0' or 'O' or 'o')
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(token);
        }

        return builder.Length > 400 ? builder.ToString(0, 400) : builder.ToString();
    }

    private static bool IsCjk(char ch) => ch is >= '\u2E80' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF';

    private static string FormatTime(long unixMs, string source)
    {
        var time = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime();
        var now = DateTimeOffset.Now;
        var delta = now - time;
        string label;
        if (delta < TimeSpan.FromMinutes(1))
        {
            label = "刚刚";
        }
        else if (delta < TimeSpan.FromHours(1))
        {
            label = $"{(int)delta.TotalMinutes} 分钟前";
        }
        else if (time.Date == now.Date)
        {
            label = $"今天 {time:HH:mm}";
        }
        else if (time.Date == now.Date.AddDays(-1))
        {
            label = $"昨天 {time:HH:mm}";
        }
        else if (time.Year == now.Year)
        {
            label = time.ToString("M月d日 HH:mm");
        }
        else
        {
            label = time.ToString("yyyy年M月d日 HH:mm");
        }

        return source == ClipSource.Screenshot ? label + " · 截图" : label;
    }

    private static string KindSuffix(string kind)
    {
        return kind switch
        {
            ClipKind.File => " · 文件",
            ClipKind.Rich => " · 带格式",
            _ => "",
        };
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
