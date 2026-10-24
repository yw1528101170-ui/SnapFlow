namespace Jietu;

internal enum ToastKind
{
    Success,
    Warning,
    Info,
}

/// <summary>
/// 系统级 Toast 胶囊渲染器：深色玻璃材质 + Apple 式徽章图标，
/// 入场做「缩放 + 上浮 + 淡入」，进行中显示旋转指示器。
/// </summary>
internal static class ToastRenderer
{
    private const float CapsuleHeightDip = 40f;
    private const float PadLeftDip = 13f;
    private const float IconDip = 22f;
    private const float GapDip = 11f;
    private const float PadRightDip = 17f;
    internal const float ShadowPadDip = 14f;
    internal const float RiseDip = 14f;

    private const uint CapsuleTop = 0xE63E3E46;
    private const uint CapsuleBottom = 0xF218181D;
    private const uint BorderColor = 0x24FFFFFF;
    private const uint LabelColor = 0xF7FFFFFF;
    private const uint SecondaryLabel = 0xBFFFFFFF;

    internal readonly record struct State(
        string Message,
        ToastKind Kind,
        bool Busy,
        float Width,
        float Presence,
        float Spin);

    internal static float MeasureWidth(IntPtr g, float scale, string message)
    {
        var font = OverlayGdi.TextFont(13f * scale, bold: true);
        float textW = OverlayGdi.MeasureText(g, message, font);
        if (textW <= 0f)
        {
            textW = message.Length * 13f * scale;
        }

        return PadLeftDip * scale + IconDip * scale + GapDip * scale + textW + PadRightDip * scale;
    }

    internal static (int Width, int Height) BufferSize(float scale, float capsuleWidth)
    {
        float pad = ShadowPadDip * scale;
        float capH = CapsuleHeightDip * scale;
        int width = (int)Math.Ceiling(capsuleWidth + pad * 2f);
        int height = (int)Math.Ceiling(capH + pad * 2f + RiseDip * scale);
        return (Math.Max(width, 8), Math.Max(height, 8));
    }

    internal static void Draw(IntPtr g, float scale, in State state, int bufferWidth, int bufferHeight)
    {
        float pad = ShadowPadDip * scale;
        float capH = CapsuleHeightDip * scale;
        float presence = OverlayGdi.Clamp01(state.Presence);
        float alpha = OverlayGdi.Clamp01(presence * 1.45f);
        float offsetY = (1f - presence) * RiseDip * scale;
        float scaleFactor = 0.9f + 0.1f * presence;

        float w = MathF.Max(state.Width, 40f * scale);
        float x = bufferWidth - pad - w;
        float y = pad + offsetY;
        float cx = x + w * 0.5f;
        float cy = y + capH * 0.5f;

        var saved = IntPtr.Zero;
        bool scaled = MathF.Abs(scaleFactor - 1f) > 0.002f;
        if (scaled)
        {
            saved = OverlayGdi.SaveGraphics(g);
            OverlayGdi.ScaleAbout(g, cx, cy, scaleFactor);
        }

        var (iconTop, iconBottom) = Palette(state.Kind);

        // ---------- 1. 柔和环境投影（多层衰减，略带向下的位移） ----------
        if (alpha > 0.01f)
        {
            OverlayGdi.DrawSoftShadow(g, alpha, 16f * scale, expand =>
            {
                float oy = 2.5f * scale;
                return OverlayGdi.BuildRoundRect(x - expand, y - expand + oy, w + expand * 2f, capH + expand * 2f,
                    capH * 0.5f + expand);
            });
        }

        var capsule = OverlayGdi.BuildRoundRect(x, y, w, capH, capH * 0.5f);

        // ---------- 2. 深色玻璃胶囊 ----------
        OverlayGdi.FillPathVertical(g, capsule, OverlayGdi.WithAlpha(CapsuleTop, alpha),
            OverlayGdi.WithAlpha(CapsuleBottom, alpha), cx, y, y + capH);

        OverlayGdi.SetClip(g, capsule);

        // 顶部高光：模拟环境光在玻璃表面的柔和反射
        var sheen = OverlayGdi.BuildRoundRect(x, y, w, capH * 0.62f, capH * 0.5f);
        OverlayGdi.FillPathVertical(g, sheen, OverlayGdi.WithAlpha(0x1CFFFFFF, alpha), OverlayGdi.WithAlpha(0x00FFFFFF, alpha),
            cx, y, y + capH * 0.62f);
        OverlayGdi.GdipDeletePath(sheen);

        // ---------- 3. 徽章 / 指示器 ----------
        float iconSize = IconDip * scale;
        float iconX = x + PadLeftDip * scale;
        float iconY = y + (capH - iconSize) * 0.5f;

        if (state.Busy)
        {
            DrawSpinner(g, iconX + iconSize * 0.5f, iconY + iconSize * 0.5f, iconSize * 0.42f, state.Spin, alpha, scale);
        }
        else
        {
            DrawIconBadge(g, iconX, iconY, iconSize, iconTop, iconBottom, state.Kind, alpha, scale);
        }

        // ---------- 4. 文案 ----------
        float textX = iconX + iconSize + GapDip * scale;
        float textW = MathF.Max(w - (textX - x) - PadRightDip * scale, 8f);
        var font = OverlayGdi.TextFont(13f * scale, bold: true);
        OverlayGdi.DrawText(g, state.Message, textX, y, textW, capH, font, OverlayGdi.AlignNear,
            OverlayGdi.WithAlpha(state.Busy ? SecondaryLabel : LabelColor, alpha));

        OverlayGdi.ResetClip(g);

        // ---------- 5. 1px 内描边：区分玻璃与背景 ----------
        OverlayGdi.StrokePath(g, capsule, OverlayGdi.WithAlpha(BorderColor, alpha), MathF.Max(1f, scale));
        OverlayGdi.GdipDeletePath(capsule);

        if (scaled)
        {
            OverlayGdi.RestoreGraphics(g, saved);
        }
    }

    private static void DrawIconBadge(IntPtr g, float x, float y, float size, uint top, uint bottom, ToastKind kind,
        float alpha, float scale)
    {
        float cx = x + size * 0.5f;
        float cy = y + size * 0.5f;
        float r = size * 0.5f;

        var circle = OverlayGdi.BuildEllipse(cx, cy, r);
        OverlayGdi.FillPathVertical(g, circle, OverlayGdi.WithAlpha(top, alpha), OverlayGdi.WithAlpha(bottom, alpha),
            cx, y, y + size);
        OverlayGdi.StrokePath(g, circle, OverlayGdi.WithAlpha(0x33FFFFFF, alpha), MathF.Max(1f, scale * 0.9f));
        OverlayGdi.GdipDeletePath(circle);

        // 顶部弧形高光，让圆形徽章有一点玻璃质感
        OverlayGdi.DrawArc(g, cx, cy, r - 0.9f * scale, 200f, 60f, OverlayGdi.WithAlpha(0x59FFFFFF, alpha),
            MathF.Max(1f, 1.1f * scale));

        uint glyph = OverlayGdi.WithAlpha(0xFFFFFFFFu, alpha);
        float stroke = MathF.Max(1.4f, 1.9f * scale);
        float unit = size / 22f;

        switch (kind)
        {
            case ToastKind.Success:
                OverlayGdi.DrawPolyline(g, glyph, stroke,
                [
                    (cx - 5.2f * unit, cy + 0.2f * unit),
                    (cx - 1.4f * unit, cy + 3.8f * unit),
                    (cx + 5.4f * unit, cy - 3.4f * unit),
                ]);
                break;

            case ToastKind.Warning:
                OverlayGdi.DrawLine(g, cx, cy - 5.2f * unit, cx, cy + 1.2f * unit, glyph, stroke);
                var dot = OverlayGdi.BuildEllipse(cx, cy + 4.5f * unit, 1.15f * unit);
                OverlayGdi.FillPath(g, dot, glyph);
                OverlayGdi.GdipDeletePath(dot);
                break;

            default:
                // 信息：竖三点
                for (int i = -1; i <= 1; i++)
                {
                    var dotPath = OverlayGdi.BuildEllipse(cx, cy + i * 4f * unit, 1.25f * unit);
                    OverlayGdi.FillPath(g, dotPath, glyph);
                    OverlayGdi.GdipDeletePath(dotPath);
                }

                break;
        }
    }

    private static void DrawSpinner(IntPtr g, float cx, float cy, float radius, float spin, float alpha, float scale)
    {
        float width = MathF.Max(1.6f, 2f * scale);
        // 轨道
        var track = OverlayGdi.BuildEllipse(cx, cy, radius);
        OverlayGdi.StrokePath(g, track, OverlayGdi.WithAlpha(0x2EFFFFFF, alpha), width);
        OverlayGdi.GdipDeletePath(track);

        // 拖尾：两段不同透明度的弧，形成 Apple 风格的转速感
        OverlayGdi.DrawArc(g, cx, cy, radius, spin, 210f, OverlayGdi.WithAlpha(0xFFFFFFFF, alpha * 0.95f), width);
        OverlayGdi.DrawArc(g, cx, cy, radius, spin + 210f, 55f, OverlayGdi.WithAlpha(0xFFFFFFFFu, alpha * 0.28f), width);
    }

    private static (uint Top, uint Bottom) Palette(ToastKind kind) => kind switch
    {
        ToastKind.Success => (0xFF43D96F, 0xFF1CA24C),
        ToastKind.Warning => (0xFFFFB44A, 0xFFFF5B3D),
        _ => (0xFF59A9FF, 0xFF0A63D6),
    };
}
