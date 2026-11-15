namespace Jietu;

/// <summary>菜单的版式结果：每行在画布里的位置、每个分段胶囊的位置，绘制只认这份数据。</summary>
internal sealed class TrayMenuLayout
{
    public List<TrayRow> Rows = [];
    public List<float> Top = [];
    public List<float> RowHeight = [];
    public List<List<(float X, float W, int Index, TrayCommand Command)>> Chips = [];
    public int Width;
    public int Height;
    public float Scale = 1f;
}

/// <summary>
/// 托盘菜单的绘制层。只有尺寸、文字、形状，没有任何窗口/消息概念，
/// 所以同一套像素既能画进真实的玻璃浮层，也能在离屏预览里导成 PNG 核对。
/// </summary>
internal static class TrayMenuRenderer
{
    #region 版式常量（逻辑像素，绘制时按 DPI 缩放）

    public const float CardWidth = 300f;
    private const float CardPad = 8f;
    private const float TextLeft = 16f;
    private const float TextRight = 16f;
    private const float HoverInset = 6f;
    private const float HoverRadius = 8f;
    public const float CardRadius = 14f;
    private const float DividerInset = 14f;

    private const float HeaderHeight = 28f;
    private const float ActionHeight = 32f;
    private const float PrimaryHeight = 38f;
    private const float ToggleHeight = 32f;
    private const float ChipsHeight = 34f;
    private const float DividerHeight = 8f;

    private const float ChipHeight = 22f;
    private const float ChipPadX = 9f;
    private const float ChipGap = 5f;

    private const float SwitchWidth = 30f;
    private const float SwitchHeight = 16f;

    // 冷雾玻璃上的墨色文字：主色、次色、强调色、分隔线、描边
    private const uint InkPrimary = 0xFF1B1F2A;
    private const uint InkSecondary = 0xFF6E7686;
    private const uint InkDisabled = 0x661B1F2A;
    private const uint Accent = 0xFF2D6EFA;
    private const uint HoverFill = 0x142D6EFA;
    private const uint SelectFill = 0x242D6EFA;
    private const uint PrimaryFill = 0x0E2D6EFA;
    private const uint DividerInk = 0x14101828;
    private const uint CardStroke = 0x1A0F172A;
    private const uint ChipFill = 0x14000000;
    private const uint ChipFillHover = 0x24000000;
    private const uint SwitchOffTrack = 0x33000000;
    private const uint SwitchKnob = 0xFFFFFFFF;

    /// <summary>玻璃失败时的兜底：一块半透明白板，保证菜单永远能读。</summary>
    private const uint PlateFill = 0xF7FFFFFF;

    #endregion

    public static float ScaleOf(float scale, float value) => value * scale;

    /// <summary>量出整块菜单的尺寸与每行位置。行高与文字无关，所以这一步不需要 Graphics。</summary>
    public static TrayMenuLayout Measure(List<TrayRow> rows, float scale)
    {
        var layout = new TrayMenuLayout { Rows = rows, Scale = scale };

        var y = CardPad * scale;
        foreach (var row in rows)
        {
            var h = RowHeightFor(row, scale);
            layout.Top.Add(y);
            layout.RowHeight.Add(h);
            layout.Chips.Add([]);
            y += h;
        }

        layout.Width = (int)MathF.Ceiling(CardWidth * scale);
        layout.Height = (int)MathF.Ceiling(y + CardPad * scale);
        return layout;
    }

    private static float RowHeightFor(TrayRow row, float scale)
    {
        return row.Kind switch
        {
            TrayRowKind.Header => HeaderHeight * scale,
            TrayRowKind.Divider => DividerHeight * scale,
            TrayRowKind.Chips => ChipsHeight * scale,
            TrayRowKind.Toggle => ToggleHeight * scale,
            _ => (row.Primary ? PrimaryHeight : ActionHeight) * scale,
        };
    }

    /// <summary>分段胶囊从右往左排，右侧留出与文字同样的内衬。必须在拿到画布 Graphics 之后调用。</summary>
    public static void MeasureChips(TrayMenuLayout layout, IntPtr g)
    {
        // 量宽必须用和绘制同样的字号，否则胶囊会比文字宽一截，把左侧标签挤掉
        var labelFont = OverlayGdi.TextFont(10.5f * layout.Scale, false);
        for (var i = 0; i < layout.Rows.Count; i++)
        {
            var row = layout.Rows[i];
            if (row.Kind != TrayRowKind.Chips)
            {
                continue;
            }

            var right = layout.Width - TextRight * layout.Scale;
            var boxes = new List<(float X, float W, int Index, TrayCommand Command)>();
            for (var c = row.Chips.Count - 1; c >= 0; c--)
            {
                var chip = row.Chips[c];
                var textW = OverlayGdi.MeasureText(g, chip.Label, labelFont);
                var w = MathF.Ceiling(textW + ChipPadX * layout.Scale * 2f);
                right -= w;
                boxes.Insert(0, (right, w, c, chip.Command));
                right -= ChipGap * layout.Scale;
            }

            layout.Chips[i] = boxes;
        }
    }

    /// <summary>把整块菜单画进 <paramref name="g"/>。glassImage 为空时画兜底白板。</summary>
    public static void Paint(IntPtr g, TrayMenuLayout layout, IntPtr glassImage, int hoverRow, int hoverChip,
        int selectRow)
    {
        if (g == IntPtr.Zero)
        {
            return;
        }

        var scale = layout.Scale;
        var cardPath = OverlayGdi.BuildRoundRect(0f, 0f, layout.Width, layout.Height, CardRadius * scale);
        try
        {
            if (glassImage != IntPtr.Zero)
            {
                OverlayGdi.GdipDrawImage(g, glassImage, 0f, 0f);
            }
            else
            {
                OverlayGdi.FillPath(g, cardPath, PlateFill);
            }

            // 不再额外描边：玻璃引擎自己会在轮廓上画一圈亮边，外面再补一道深墨内衬
            // 只会把那条亮边压暗——浅色桌面上的顶边尤其明显，看起来就像「顶部被压掉了」。

            for (var i = 0; i < layout.Rows.Count; i++)
            {
                DrawRow(g, layout, i, hoverRow, hoverChip, selectRow);
            }
        }
        finally
        {
            OverlayGdi.GdipDeletePath(cardPath);
        }
    }

    private static void DrawRow(IntPtr g, TrayMenuLayout layout, int index, int hoverRow, int hoverChip,
        int selectRow)
    {
        var scale = layout.Scale;
        var row = layout.Rows[index];
        var top = layout.Top[index];
        var h = layout.RowHeight[index];

        switch (row.Kind)
        {
            case TrayRowKind.Header:
            {
                var font = OverlayGdi.TextFont(10.5f * scale, false);
                OverlayGdi.DrawText(g, row.Label, TextLeft * scale, top, layout.Width - (TextLeft + TextRight) * scale,
                    h, font, OverlayGdi.AlignNear, InkSecondary);
                return;
            }

            case TrayRowKind.Divider:
            {
                var y = MathF.Round(top + h * 0.5f) + 0.5f;
                OverlayGdi.DrawLine(g, DividerInset * scale, y, layout.Width - DividerInset * scale, y, DividerInk, 1f);
                return;
            }

            default:
                break;
        }

        if (hoverRow == index || selectRow == index)
        {
            var hover = OverlayGdi.BuildRoundRect(HoverInset * scale, top, layout.Width - HoverInset * scale * 2f, h,
                HoverRadius * scale);
            OverlayGdi.FillPath(g, hover, selectRow == index ? SelectFill : HoverFill);
            OverlayGdi.GdipDeletePath(hover);
        }
        else if (row.Primary)
        {
            // 主操作常驻一层很淡的底色：不靠悬停也看得出「这一行是默认动作」
            var primary = OverlayGdi.BuildRoundRect(HoverInset * scale, top, layout.Width - HoverInset * scale * 2f, h,
                HoverRadius * scale);
            OverlayGdi.FillPath(g, primary, PrimaryFill);
            OverlayGdi.GdipDeletePath(primary);
        }

        var ink = !row.Enabled ? InkDisabled : (row.Secondary ? InkSecondary : InkPrimary);
        var labelFont = OverlayGdi.TextFont((row.Primary ? 13f : 12.5f) * scale, row.Primary);
        var hintFont = OverlayGdi.TextFont(10.5f * scale, false);
        var hintW = row.Hint.Length > 0 ? OverlayGdi.MeasureText(g, row.Hint, hintFont) : 0f;
        var rightReserve = TextRight * scale;

        if (row.Kind == TrayRowKind.Toggle)
        {
            // 开关自己就是状态，右侧不再重复写「已开启」
            DrawSwitch(g, layout, top + (h - SwitchHeight * scale) * 0.5f, row.ToggleOn, row.Enabled);
            rightReserve += (SwitchWidth + 10f) * scale;
        }
        else if (row.Kind == TrayRowKind.Chips)
        {
            foreach (var (x, w, chipIndex, _) in layout.Chips[index])
            {
                var chipHovered = hoverRow == index && hoverChip == chipIndex;
                DrawChip(g, layout, x, top + (h - ChipHeight * scale) * 0.5f, w, row, chipIndex, chipHovered);
            }

            rightReserve += layout.Chips[index].Sum(b => b.W + ChipGap * scale);
        }
        else if (row.Hint.Length > 0)
        {
            OverlayGdi.DrawText(g, row.Hint, layout.Width - TextRight * scale - hintW, top, hintW + 2f, h,
                hintFont, OverlayGdi.AlignNear, row.Enabled ? InkSecondary : InkDisabled);
            rightReserve += hintW + 12f * scale;
        }

        var labelW = layout.Width - TextLeft * scale - rightReserve;
        OverlayGdi.DrawText(g, row.Label, TextLeft * scale, top, MathF.Max(labelW, 0f), h, labelFont,
            OverlayGdi.AlignNear, ink);
    }

    private static void DrawChip(IntPtr g, TrayMenuLayout layout, float x, float y, float w, TrayRow row,
        int chipIndex, bool hovered)
    {
        if (chipIndex < 0 || chipIndex >= row.Chips.Count)
        {
            return;
        }

        var scale = layout.Scale;
        var chip = row.Chips[chipIndex];
        var chipH = ChipHeight * scale;
        var path = OverlayGdi.BuildRoundRect(x, y, w, chipH, chipH * 0.5f);
        OverlayGdi.FillPath(g, path, chip.Selected ? Accent : (hovered ? ChipFillHover : ChipFill));
        OverlayGdi.GdipDeletePath(path);

        var font = OverlayGdi.TextFont(10.5f * scale, chip.Selected);
        var ink = !row.Enabled ? InkDisabled : (chip.Selected ? 0xFFFFFFFF : InkPrimary);
        OverlayGdi.DrawText(g, chip.Label, x, y, w, chipH, font, OverlayGdi.AlignCenter, ink);
    }

    private static void DrawSwitch(IntPtr g, TrayMenuLayout layout, float y, bool on, bool enabled)
    {
        var scale = layout.Scale;
        var w = SwitchWidth * scale;
        var h = SwitchHeight * scale;
        var x = layout.Width - TextRight * scale - w;

        var track = OverlayGdi.BuildRoundRect(x, y, w, h, h * 0.5f);
        OverlayGdi.FillPath(g, track, on ? (enabled ? Accent : 0x552D6EFA) : SwitchOffTrack);
        OverlayGdi.GdipDeletePath(track);

        var knobR = h * 0.5f - 2f * scale;
        var knobX = on ? x + w - 2f * scale - knobR : x + 2f * scale + knobR;
        var knob = OverlayGdi.BuildEllipse(knobX, y + h * 0.5f, knobR);
        OverlayGdi.FillPath(g, knob, enabled ? SwitchKnob : 0xB3FFFFFF);
        OverlayGdi.GdipDeletePath(knob);
    }
}
