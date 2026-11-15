namespace Jietu;

/// <summary>托盘菜单的一行是什么形态：分组标题、普通命令、开关、行内分段、分隔线。</summary>
internal enum TrayRowKind
{
    Header,
    Action,
    Toggle,
    Chips,
    Divider,
}

/// <summary>行内分段里的一个小胶囊（延时秒数 / 悬浮窗停靠屏幕）。</summary>
internal readonly record struct TrayChip(string Label, TrayCommand Command, bool Selected);

/// <summary>
/// 托盘菜单的一行。文案、右侧提示、状态全在这里定，绘制层只负责摆放。
/// 用不可变对象表达，菜单每次弹出重建一次，不做增量更新。
/// </summary>
internal sealed class TrayRow
{
    public TrayRowKind Kind { get; init; } = TrayRowKind.Action;

    public TrayCommand Command { get; init; } = TrayCommand.None;

    public string Label { get; init; } = string.Empty;

    /// <summary>右侧灰字：快捷键，或数量一类的补充信息。</summary>
    public string Hint { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

    /// <summary>主操作（截图）：字号加粗、字号略大，让人一眼看到。</summary>
    public bool Primary { get; init; }

    /// <summary>从属动作（锁定密码库这种跟在主动作后面的）：字色更淡，读起来是一组的第二项。</summary>
    public bool Secondary { get; init; }

    /// <summary>开关行的当前状态：右侧画一枚小开关。</summary>
    public bool ToggleOn { get; init; }

    public IReadOnlyList<TrayChip> Chips { get; init; } = [];
}

/// <summary>
/// 把运行状态翻成托盘菜单的结构与文案。
/// 文案原则：用「用户要做的事」而不是内部术语；数量与快捷键放右侧。
/// 只留托盘里真会点的动作——开机自启、悬浮窗位置、打开文件夹、清空历史在设置页或
/// 历史窗里已有入口，重复放进菜单只会把它撑成一张两页长的清单。
/// </summary>
internal static class TrayMenuModel
{
    public static List<TrayRow> Build(in TrayMenuContext ctx)
    {
        var rows = new List<TrayRow>
        {
            new()
            {
                Command = TrayCommand.CaptureNow,
                Label = "区域截图",
                Hint = ctx.RegionHotkey,
                Primary = true,
            },
            new()
            {
                Kind = TrayRowKind.Chips,
                Label = "延时截图",
                Chips =
                [
                    new("3 秒", TrayCommand.CaptureDelay3, false),
                    new("5 秒", TrayCommand.CaptureDelay5, false),
                    new("10 秒", TrayCommand.CaptureDelay10, false),
                ],
            },
            new() { Kind = TrayRowKind.Divider },
            new()
            {
                Command = TrayCommand.ShowHistory,
                Label = "剪贴板历史",
                Hint = ctx.HistoryHotkey,
            },
            new()
            {
                Kind = TrayRowKind.Toggle,
                Command = TrayCommand.TogglePause,
                Label = "暂停剪贴板记录",
                ToggleOn = ctx.Paused,
            },
            new() { Kind = TrayRowKind.Divider },
            new()
            {
                Command = TrayCommand.ShowVault,
                Label = "密码库",
                Hint = ctx.VaultHotkey,
            },
        };

        if (ctx.VaultUnlocked)
        {
            rows.Add(new TrayRow { Command = TrayCommand.LockVault, Label = "锁定密码库", Secondary = true });
        }

        rows.Add(new TrayRow { Kind = TrayRowKind.Divider });
        rows.Add(new TrayRow
        {
            Command = TrayCommand.ClosePins,
            Label = "关闭所有贴图",
            Hint = ctx.PinCount > 0 ? $"{ctx.PinCount} 张" : "无",
            Enabled = ctx.PinCount > 0,
        });

        if (ctx.HasGhostPins)
        {
            rows.Add(new TrayRow { Command = TrayCommand.RestoreGhostPins, Label = "恢复贴图交互" });
        }

        rows.Add(new TrayRow { Kind = TrayRowKind.Divider });
        rows.Add(new TrayRow
        {
            Kind = TrayRowKind.Toggle,
            Command = TrayCommand.ToggleOsd,
            Label = "性能悬浮窗",
            ToggleOn = ctx.ShowOsd,
        });

        // 和系统兜底菜单对齐：悬浮窗驻留哪块屏幕这里也能切，玻璃菜单不再缺功能
        bool followCursor = string.Equals(ctx.OsdDisplay, "FollowCursor", StringComparison.OrdinalIgnoreCase);
        rows.Add(new TrayRow
        {
            Kind = TrayRowKind.Chips,
            Label = "驻留屏幕",
            Chips =
            [
                new("主显示器", TrayCommand.OsdDisplayPrimary, !followCursor),
                new("跟随鼠标", TrayCommand.OsdDisplayFollowCursor, followCursor),
            ],
        });

        rows.Add(new TrayRow { Kind = TrayRowKind.Divider });
        rows.Add(new TrayRow { Command = TrayCommand.ShowSettings, Label = "设置…" });
        rows.Add(new TrayRow { Kind = TrayRowKind.Divider });
        rows.Add(new TrayRow { Command = TrayCommand.Exit, Label = "退出 SnapFlow" });

        return rows;
    }
}
