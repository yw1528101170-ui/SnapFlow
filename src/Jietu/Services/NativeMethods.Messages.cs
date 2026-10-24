using System.Runtime.InteropServices;

namespace Jietu;

internal static partial class NativeMethods
{
    // 注册自定义窗口消息，返回系统分配的消息 id（失败返回 0）。
    // Explorer 重启后会用 "TaskbarCreated" 向所有顶层窗口广播，托盘图标必须据此重新添加。
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterWindowMessage(string lpString);
}
