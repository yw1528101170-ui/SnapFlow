using System.Text;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Jietu;

internal static class OcrService
{
    /// <summary>
    /// 识别前把长边补到这个尺寸。系统 OCR 对小字号（截图里的 12px 界面文字）
    /// 分辨率不足，放大后行切分与字形判别都会明显变好；原本就够大的截图则不再放大。
    /// </summary>
    private const double OcrTargetSide = 1600;

    /// <summary>放大倍数上限，避免小图被拉到失真。</summary>
    private const double OcrMaxUpscale = 3.0;

    private static readonly Lazy<OcrEngine?> Engine = new(CreateEngine);

    private static OcrEngine? CreateEngine()
    {
        // 优先简体中文：系统语言是英文时 TryCreateFromUserProfileLanguages 会拿到
        // 英文引擎，中文识别会明显变差，所以这里按可用语言显式挑选。
        string[] preferred = ["zh-Hans", "zh-Hant"];
        var available = OcrEngine.AvailableRecognizerLanguages;
        foreach (var tag in preferred)
        {
            foreach (var language in available)
            {
                if (language.LanguageTag.StartsWith(tag, StringComparison.OrdinalIgnoreCase))
                {
                    var engine = OcrEngine.TryCreateFromLanguage(language);
                    if (engine != null)
                    {
                        return engine;
                    }
                }
            }
        }

        return OcrEngine.TryCreateFromUserProfileLanguages()
            ?? (available.Count > 0 ? OcrEngine.TryCreateFromLanguage(available[0]) : null);
    }

    public record OcrWordBox(string Text, int X, int Y, int Width, int Height);

    public record OcrLineInfo(string Text, IReadOnlyList<OcrWordBox> Words, int X, int Y, int Width, int Height);

    public static async Task<IReadOnlyList<OcrLineInfo>?> ReadLayoutAsync(byte[] png)
    {
        var engine = Engine.Value;
        if (engine == null)
        {
            return null;
        }

        var (prepared, scale) = await ImageBytes.DecodeForOcrAsync(png, OcrEngine.MaxImageDimension, OcrTargetSide, OcrMaxUpscale);
        using (prepared)
        {
            var result = await engine.RecognizeAsync(prepared);
            if (result.Lines == null || result.Lines.Count == 0)
            {
                return null;
            }

            // 系统 OCR 对中文的分行不可靠：常把整张图塞进一个 Line，用它自带的
            // 分行结果预览时会变成一整段没有换行的长句。这里按词框的纵向重叠
            // 重新分行，还原真实的视觉行。
            var words = new List<OcrWordBox>();
            foreach (var line in result.Lines)
            {
                foreach (var word in line.Words)
                {
                    var text = word.Text ?? "";
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    var x = (int)Math.Round(word.BoundingRect.X * scale);
                    var y = (int)Math.Round(word.BoundingRect.Y * scale);
                    var w = Math.Max(1, (int)Math.Round(word.BoundingRect.Width * scale));
                    var h = Math.Max(1, (int)Math.Round(word.BoundingRect.Height * scale));
                    words.Add(new OcrWordBox(text, x, y, w, h));
                }
            }

            if (words.Count == 0)
            {
                return null;
            }

            return GroupLines(words);
        }
    }

    public static async Task<string?> ReadAsync(byte[] png)
    {
        var lines = await ReadLayoutAsync(png);
        if (lines == null || lines.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            var text = line.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(text);
        }

        var assembled = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(assembled) ? null : assembled;
    }

    /// <summary>把词框按纵向重叠聚成视觉行，并按阅读顺序排序。</summary>
    private static List<OcrLineInfo> GroupLines(List<OcrWordBox> words)
    {
        var ordered = words
            .OrderBy(w => w.Y + (w.Height / 2))
            .ThenBy(w => w.X)
            .ToList();

        var buckets = new List<List<OcrWordBox>>();
        var tops = new List<double>();

        foreach (var word in ordered)
        {
            var center = word.Y + (word.Height / 2.0);
            var placed = false;
            for (var i = buckets.Count - 1; i >= 0 && i >= buckets.Count - 6; i--)
            {
                var tolerance = Math.Max(4.0, Math.Max(word.Height, buckets[i][0].Height) * 0.55);
                if (Math.Abs(tops[i] - center) <= tolerance)
                {
                    buckets[i].Add(word);
                    tops[i] = (tops[i] + center) / 2.0;
                    placed = true;
                    break;
                }
            }

            if (!placed)
            {
                buckets.Add([word]);
                tops.Add(center);
            }
        }

        var lines = new List<OcrLineInfo>(buckets.Count);
        foreach (var bucket in buckets.OrderBy(b => b[0].Y))
        {
            var sorted = bucket.OrderBy(w => w.X).ToList();
            int minX = sorted.Min(w => w.X);
            int minY = sorted.Min(w => w.Y);
            int maxX = sorted.Max(w => w.X + w.Width);
            int maxY = sorted.Max(w => w.Y + w.Height);
            lines.Add(new OcrLineInfo(
                ComposeLine(sorted),
                sorted,
                minX,
                minY,
                Math.Max(0, maxX - minX),
                Math.Max(0, maxY - minY)));
        }

        return lines;
    }

    /// <summary>
    /// 拼接同一行的词。中文之间不插空格（系统 OCR 会把每个词当成独立分词，
    /// 直接拼会变成「全 部 文 本」这种断句），只有两侧都是西文时才补一个空格。
    /// </summary>
    private static string ComposeLine(IReadOnlyList<OcrWordBox> words)
        => ComposeLineWithSpans(words).Text;

    /// <summary>
    /// 拼接同一行，同时给出每个词框在最终文本中的字符区间 [Start, Start+Length)。
    /// 行文本经过补空格 + Normalize 重排，不能事后用 IndexOf 回推词的位置；
    /// 这里在拼接时同步记录每个字符来自哪个词，供敏感信息遮盖做词级定位。
    /// 空文本的词没有区间，对应项为 null。
    /// </summary>
    public static (string Text, (int Start, int Length)?[] Spans) ComposeLineWithSpans(IReadOnlyList<OcrWordBox> words)
    {
        var builder = new StringBuilder();
        // 每个已拼字符对应的词序号；拼接时补的空格记 -1。
        var origins = new List<int>();
        var spans = new (int Start, int Length)?[words.Count];
        for (var i = 0; i < words.Count; i++)
        {
            var text = words[i].Text ?? "";
            if (text.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0 && NeedsSpace(builder[^1], text[0]))
            {
                builder.Append(' ');
                origins.Add(-1);
            }

            var start = builder.Length;
            foreach (var ch in text)
            {
                builder.Append(ch);
                origins.Add(i);
            }

            spans[i] = (start, builder.Length - start);
        }

        // Normalize 会删掉部分空格：记录每个最终字符的来源词，再据此重算词区间。
        var (finalText, finalOrigins) = NormalizeWithOrigins(builder.ToString(), origins);
        for (var i = 0; i < spans.Length; i++)
        {
            if (spans[i] == null)
            {
                continue;
            }

            int first = -1;
            int last = -1;
            for (var j = 0; j < finalOrigins.Count; j++)
            {
                if (finalOrigins[j] != i)
                {
                    continue;
                }

                if (first < 0)
                {
                    first = j;
                }

                last = j;
            }

            spans[i] = first < 0 ? null : (first, last - first + 1);
        }

        return (finalText, spans);
    }

    /// <summary>与 Normalize 完全相同的规则，但额外保留每个最终字符在输入串里的下标。</summary>
    private static (string Text, List<int> Origins) NormalizeWithOrigins(string text, List<int> origins)
    {
        if (string.IsNullOrEmpty(text))
        {
            return ("", []);
        }

        var chars = new List<char>(text.Length);
        var charOrigins = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == ' ' || ch == '\u00A0' || ch == '\t')
            {
                if (chars.Count == 0)
                {
                    continue;
                }

                var prev = chars[^1];
                if (prev == ' ' || IsCjk(prev) || IsFullWidth(prev))
                {
                    continue;
                }

                chars.Add(' ');
                charOrigins.Add(-1);
                continue;
            }

            if (chars.Count > 0 && chars[^1] == ' ' && (IsCjk(ch) || IsFullWidth(ch)))
            {
                chars.RemoveAt(chars.Count - 1);
                charOrigins.RemoveAt(charOrigins.Count - 1);
            }

            chars.Add(ch);
            charOrigins.Add(origins[i]);
        }

        // 与 Normalize 末尾的 Trim() 对齐：去掉首尾空白。
        while (chars.Count > 0 && char.IsWhiteSpace(chars[0]))
        {
            chars.RemoveAt(0);
            charOrigins.RemoveAt(0);
        }

        while (chars.Count > 0 && char.IsWhiteSpace(chars[^1]))
        {
            chars.RemoveAt(chars.Count - 1);
            charOrigins.RemoveAt(charOrigins.Count - 1);
        }

        return (new string(chars.ToArray()), charOrigins);
    }

    private static bool NeedsSpace(char left, char right)
    {
        if (left == ' ' || right == ' ')
        {
            return false;
        }

        if (IsCjk(left) || IsCjk(right) || IsFullWidth(left) || IsFullWidth(right))
        {
            return false;
        }

        return IsLatin(left) && IsLatin(right);
    }

    private static bool IsLatin(char ch) =>
        ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    private static bool IsCjk(char ch) =>
        ch is >= '\u2E80' and <= '\u9FFF'
            or >= '\uF900' and <= '\uFAFF'
            or >= '\uFF00' and <= '\uFFEF';

    private static bool IsFullWidth(char ch) =>
        ch is >= '\u3000' and <= '\u303F' or >= '\uFF00' and <= '\uFFEF';
}
