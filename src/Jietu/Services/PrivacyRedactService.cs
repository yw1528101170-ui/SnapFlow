using System.Text.RegularExpressions;

namespace Jietu;

internal record RedactedBox(
    int X,
    int Y,
    int Width,
    int Height,
    string Category,
    string MatchedText
);

internal static class PrivacyRedactService
{
    private static readonly (string Category, Regex Pattern)[] SensitiveRules =
    [
        ("手机号", new Regex(@"(?<!\d)1[3-9]\d{9}(?!\d)", RegexOptions.Compiled)),
        ("身份证", new Regex(@"(?<!\d)[1-9]\d{5}(18|19|20)\d{2}(0[1-9]|1[0-2])(0[1-9]|[12]\d|3[01])\d{3}[\dXx](?!\d)", RegexOptions.Compiled)),
        ("邮箱", new Regex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.Compiled)),
        ("IP地址", new Regex(@"(?<!\d)(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)(?!\d)", RegexOptions.Compiled)),
        ("银行卡", new Regex(@"(?<!\d)\d{4}[\s-]?\d{4}[\s-]?\d{4}[\s-]?(?:\d{4}|\d{7})(?!\d)", RegexOptions.Compiled)),
        ("密钥Token", new Regex(@"(?i)(?:sk-[a-zA-Z0-9]{20,}|ghp_[a-zA-Z0-9]{20,}|bearer\s+[a-zA-Z0-9_\-\.]{20,})", RegexOptions.Compiled))
    ];

    public static async Task<IReadOnlyList<RedactedBox>> DetectAsync(byte[] png)
    {
        var layout = await OcrService.ReadLayoutAsync(png);
        if (layout == null || layout.Count == 0)
        {
            return Array.Empty<RedactedBox>();
        }

        var results = new List<RedactedBox>();
        foreach (var line in layout)
        {
            // 行文本经过 ComposeLine/Normalize 重排（补空格、删空格），不能用 IndexOf 回推词的
            // 位置（回推失败时坐标漂移会漏盖）。这里按词框实际顺序取"词 → 行内字符区间"索引表，
            // 词 i 覆盖行文本的 [Start, Start+Length)，与敏感词匹配区间直接求交。
            var (lineText, spans) = OcrService.ComposeLineWithSpans(line.Words);
            foreach (var (category, regex) in SensitiveRules)
            {
                var matches = regex.Matches(lineText);
                foreach (Match m in matches)
                {
                    var mStart = m.Index;
                    var mEnd = m.Index + m.Length;
                    int minX = int.MaxValue;
                    int minY = int.MaxValue;
                    int maxX = 0;
                    int maxY = 0;
                    bool foundWord = false;

                    for (var i = 0; i < spans.Length && i < line.Words.Count; i++)
                    {
                        var span = spans[i];
                        if (span == null)
                        {
                            continue;
                        }

                        var wStart = span.Value.Start;
                        var wEnd = wStart + span.Value.Length;
                        if (Math.Max(wStart, mStart) >= Math.Min(wEnd, mEnd))
                        {
                            continue;
                        }

                        var w = line.Words[i];
                        foundWord = true;
                        minX = Math.Min(minX, w.X);
                        minY = Math.Min(minY, w.Y);
                        maxX = Math.Max(maxX, w.X + w.Width);
                        maxY = Math.Max(maxY, w.Y + w.Height);
                    }

                    if (!foundWord || minX == int.MaxValue)
                    {
                        // 词框映射失败（词对不上敏感词位置）时退回整行框：宁可多盖不可漏盖。
                        minX = line.X;
                        minY = line.Y;
                        maxX = line.X + line.Width;
                        maxY = line.Y + line.Height;
                    }

                    const int pad = 3;
                    var padX = Math.Max(0, minX - pad);
                    var padY = Math.Max(0, minY - pad);
                    var padW = (maxX - minX) + (pad * 2);
                    var padH = (maxY - minY) + (pad * 2);

                    results.Add(new RedactedBox(padX, padY, padW, padH, category, m.Value));
                }
            }
        }

        return results;
    }
}
