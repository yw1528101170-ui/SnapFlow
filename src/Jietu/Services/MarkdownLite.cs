using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Jietu;

/// <summary>
/// AI 回答的轻量 Markdown 渲染：围栏代码块、表格、标题、分隔线、行内代码/加粗。
/// 不追求完整 CommonMark——视觉模型实际输出的结构基本就这几类，其余按原文显示，
/// 用户可通过窗口里的「原文」按钮随时查看未经渲染的文本。
/// </summary>
internal static class MarkdownLite
{
    // Consolas 没有中文字形，显式给出回退字体，避免表格里的中文落到随机字体上
    private static readonly FontFamily MonoFont = new("Consolas, Microsoft YaHei UI");

    public static void Render(Panel host, string markdown)
    {
        host.Children.Clear();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                FlushParagraph(host, paragraph);
                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph(host, paragraph);
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].Trim().StartsWith("```", StringComparison.Ordinal); i++)
                {
                    code.Add(lines[i]);
                }

                if (code.Count > 0)
                {
                    host.Children.Add(CodeBlock(string.Join('\n', code)));
                }

                continue;
            }

            if (IsTableLine(trimmed))
            {
                FlushParagraph(host, paragraph);
                var rows = new List<string>();
                for (; i < lines.Length && IsTableLine(lines[i].Trim()); i++)
                {
                    rows.Add(lines[i].Trim());
                }

                i--;
                if (rows.Count > 0)
                {
                    host.Children.Add(TableBlock(rows));
                }

                continue;
            }

            if (trimmed.Length is 3 or 4 or 5
                && (trimmed.All(c => c is '-') || trimmed.All(c => c is '*')))
            {
                FlushParagraph(host, paragraph);
                host.Children.Add(new Border
                {
                    Height = 1,
                    Background = Res("AiCardStrokeBrush"),
                    Margin = new Thickness(0, 4, 0, 4),
                });
                continue;
            }

            if (trimmed.Length > 2 && trimmed[0] == '#')
            {
                var level = 0;
                while (level < trimmed.Length && trimmed[level] == '#')
                {
                    level++;
                }

                if (level <= 6 && level < trimmed.Length && trimmed[level] == ' ')
                {
                    FlushParagraph(host, paragraph);
                    var heading = new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = level <= 2 ? 15 : 14,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = Res("TextFillColorPrimaryBrush"),
                    };
                    AppendInlines(heading.Inlines, trimmed[(level + 1)..].Trim());
                    host.Children.Add(heading);
                    continue;
                }
            }

            paragraph.Add(line);
        }

        FlushParagraph(host, paragraph);
    }

    private static bool IsTableLine(string trimmed)
        => trimmed.StartsWith('|') && trimmed.IndexOf('|', 1) > 0;

    private static void FlushParagraph(Panel host, List<string> paragraph)
    {
        if (paragraph.Count == 0)
        {
            return;
        }

        // ⚠ 开头的独立段落是截断/警示提示（如“回答因长度上限被截断”），染警示色与正文区分
        var isWarning = paragraph.Any(l => l.TrimStart().StartsWith("⚠", StringComparison.Ordinal));
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            LineHeight = 22,
            IsTextSelectionEnabled = true,
            Foreground = isWarning ? Res("SnapFlowWarningBrush") : Res("TextFillColorPrimaryBrush"),
        };

        for (var i = 0; i < paragraph.Count; i++)
        {
            if (i > 0)
            {
                text.Inlines.Add(new LineBreak());
            }

            AppendInlines(text.Inlines, paragraph[i]);
        }

        paragraph.Clear();
        host.Children.Add(text);
    }

    private static void AppendInlines(InlineCollection inlines, string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var code = text.IndexOf('`', i);
            var bold = text.IndexOf("**", i, StringComparison.Ordinal);
            if (code < 0 && bold < 0)
            {
                AddRun(inlines, text[i..]);
                break;
            }

            // 取先出现的记号；`code` 优先于 `**`（代码里的星号不该被吃掉）
            var useCode = code >= 0 && (bold < 0 || code < bold);
            var marker = useCode ? "`" : "**";
            var start = useCode ? code : bold;
            if (start > i)
            {
                AddRun(inlines, text[i..start]);
            }

            var close = text.IndexOf(marker, start + marker.Length, StringComparison.Ordinal);
            if (close < 0)
            {
                // 没有闭合记号：按普通文本原样输出，避免吞内容
                AddRun(inlines, text[start..]);
                break;
            }

            var inner = text[(start + marker.Length)..close];
            if (useCode)
            {
                inlines.Add(new Run
                {
                    Text = inner,
                    FontFamily = MonoFont,
                    FontSize = 12,
                    Foreground = Res("SnapFlowAccentBrush"),
                });
            }
            else
            {
                inlines.Add(new Run
                {
                    Text = inner,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                });
            }

            i = close + marker.Length;
        }
    }

    private static void AddRun(InlineCollection inlines, string text)
    {
        if (text.Length > 0)
        {
            inlines.Add(new Run { Text = text });
        }
    }

    // ── 代码块 / 表格：等宽 + 不折行 + 横向滚动 ───────────────────────
    // 宽表格宁可横滚也不能折行——折到第二行后列对齐全毁，这正是这类输出的阅读价值所在。
    // 纵向滚轮会穿透到外层结果区（这里纵向滚动是 Disabled），不影响整页上下翻。

    private static Border CodeBlock(string code)
        => new()
        {
            Background = Res("AiInsetFillBrush"),
            BorderBrush = Res("AiCardStrokeBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new Microsoft.UI.Xaml.CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Child = HorizontalScroll(MonoText(code)),
        };

    private static Border TableBlock(List<string> rows)
    {
        // Markdown 约定：分隔行（| --- | :---: |）的上一行是表头。
        var grid = new List<string[]>();
        var separatorAt = new HashSet<int>();
        foreach (var row in rows)
        {
            var cells = row.Trim('|').Split('|');
            for (var c = 0; c < cells.Length; c++)
            {
                cells[c] = cells[c].Trim();
            }

            if (string.Concat(cells).Length > 0
                && string.Concat(cells).All(c => c is '-' or ':' || char.IsWhiteSpace(c)))
            {
                separatorAt.Add(grid.Count);
                continue;
            }

            grid.Add(cells);
        }

        var headerIndex = separatorAt.Count > 0 && separatorAt.Min() >= 1 ? separatorAt.Min() - 1 : -1;

        // 列宽按“显示宽度”算：等宽字体里 ASCII 占 1 列，CJK 占 2 列，
        // 按字符数补空格会让中文表头和数字内容错位。
        var columns = grid.Count == 0 ? 0 : grid.Max(r => r.Length);
        var widths = new int[columns];
        foreach (var cells in grid)
        {
            for (var c = 0; c < cells.Length; c++)
            {
                widths[c] = Math.Max(widths[c], DisplayWidth(cells[c]));
            }
        }

        var text = new TextBlock
        {
            FontFamily = MonoFont,
            FontSize = 12,
            LineHeight = 19,
            TextWrapping = TextWrapping.NoWrap,
            IsTextSelectionEnabled = true,
            Foreground = Res("TextFillColorPrimaryBrush"),
        };

        for (var r = 0; r < grid.Count; r++)
        {
            if (r > 0)
            {
                text.Inlines.Add(new LineBreak());
            }

            var bold = r == headerIndex;
            var cells = grid[r];
            for (var c = 0; c < columns; c++)
            {
                if (c > 0)
                {
                    text.Inlines.Add(new Run { Text = "  " });
                }

                var cell = c < cells.Length ? cells[c] : "";
                var run = new Run { Text = PadDisplay(cell, widths[c]) };
                if (bold)
                {
                    run.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                }

                text.Inlines.Add(run);
            }
        }

        return new Border
        {
            Background = Res("AiInsetFillBrush"),
            BorderBrush = Res("AiCardStrokeBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new Microsoft.UI.Xaml.CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Child = HorizontalScroll(text),
        };
    }

    private static TextBlock MonoText(string code)
        => new()
        {
            Text = code,
            FontFamily = MonoFont,
            FontSize = 12,
            LineHeight = 19,
            TextWrapping = TextWrapping.NoWrap,
            IsTextSelectionEnabled = true,
            Foreground = Res("TextFillColorPrimaryBrush"),
        };

    private static ScrollViewer HorizontalScroll(TextBlock content)
        => new()
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
        };

    // ── 显示宽度：CJK/全角按 2 列，其余按 1 列 ────────────────────────

    private static int DisplayWidth(string text)
    {
        var width = 0;
        foreach (var ch in text)
        {
            width += IsWide(ch) ? 2 : 1;
        }

        return width;
    }

    private static string PadDisplay(string cell, int targetWidth)
    {
        var pad = targetWidth - DisplayWidth(cell);
        if (pad <= 0)
        {
            return cell;
        }

        return cell + new string(' ', pad);
    }

    private static bool IsWide(char c)
        => c >= 0x1100 && (c <= 0x115F                          // 谚文字母
           || (c >= 0x2E80 && c <= 0xA4CF)                      // CJK 部首～彝文
           || (c >= 0xAC00 && c <= 0xD7A3)                      // 谚文音节
           || (c >= 0xF900 && c <= 0xFAFF)                      // CJK 兼容
           || (c >= 0xFE30 && c <= 0xFE4F)                      // CJK 兼容形式
           || (c >= 0xFF00 && c <= 0xFF60)                      // 全角形式
           || (c >= 0xFFE0 && c <= 0xFFE6)
           || (c >= 0x20000 && c <= 0x3FFFD));                  // CJK 扩展

    private static Brush? Res(string key)
        => Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush ? brush : null;
}
