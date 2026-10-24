namespace Jietu;

/// <summary>
/// 结果通知出口：Pipeline 处理器只依赖此接口，不再直接依赖 WinUI 的 ToastWindow，
/// 服务层与 UI 层的依赖方向恢复为单向（UI 实现接口、服务消费接口）。
/// </summary>
internal interface IToastSink
{
    void Show(string message);

    void ShowSticky(string message);
}
