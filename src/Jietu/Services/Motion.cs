using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace Jietu;

/// <summary>
/// 轻量动效辅助：全部基于 Storyboard + CompositeTransform，收敛时长与曲线，
/// 统一成 Apple 式的「快出慢停 + 轻微回弹」节奏。
/// 所有动画都带显式起止值，结束后保持在终值，避免与布局基值互相踩踏。
/// </summary>
internal static class Motion
{
    public const string ScaleXPath = "(UIElement.RenderTransform).(CompositeTransform.ScaleX)";
    public const string ScaleYPath = "(UIElement.RenderTransform).(CompositeTransform.ScaleY)";
    public const string TranslateXPath = "(UIElement.RenderTransform).(CompositeTransform.TranslateX)";
    public const string TranslateYPath = "(UIElement.RenderTransform).(CompositeTransform.TranslateY)";

    // 高频路径（RowIn 随列表滚动反复触发）每次 new 缓动对象是纯浪费：
    // 缓动求值无状态，跨动画共享同一实例是安全的；带自定义振幅的 Spring 仍按需创建。
    private static readonly CubicEase EaseOutInstance = new() { EasingMode = EasingMode.EaseOut };
    private static readonly CubicEase EaseInInstance = new() { EasingMode = EasingMode.EaseIn };
    private static readonly BackEase SpringInstance = new() { Amplitude = 0.22, EasingMode = EasingMode.EaseOut };

    /// <summary>默认曲线：启动快、收尾缓，接近 Apple 的 ease-out。</summary>
    public static EasingFunctionBase Ease => EaseOutInstance;

    /// <summary>收起/离场曲线。</summary>
    public static EasingFunctionBase EaseIn => EaseInInstance;

    /// <summary>带轻微回弹的曲线，用于位移与缩放的收尾。</summary>
    public static EasingFunctionBase Spring(double amplitude = 0.22) =>
        amplitude == 0.22 ? SpringInstance : new BackEase { Amplitude = amplitude, EasingMode = EasingMode.EaseOut };

    /// <summary>确保元素拥有可动画的复合变换。</summary>
    public static CompositeTransform Prep(FrameworkElement element)
    {
        if (element.RenderTransform is CompositeTransform existing)
        {
            return existing;
        }

        var transform = new CompositeTransform();
        element.RenderTransform = transform;
        return transform;
    }

    /// <summary>透明度过渡。</summary>
    public static void Fade(UIElement target, double from, double to, int milliseconds,
        EasingFunctionBase? easing = null, int delay = 0, Action? completed = null)
    {
        Run(target, "Opacity", from, to, milliseconds, easing ?? Ease, delay, completed);
    }

    /// <summary>把透明度立刻钉到某个值，同时覆盖可能仍在保持的旧动画（列表回收时重置状态用）。</summary>
    public static void Snap(UIElement target, double opacity)
    {
        target.Opacity = opacity;
        Run(target, "Opacity", opacity, opacity, 1, Ease, 0);
    }

    /// <summary>纵向位移过渡（像素）。</summary>
    public static void MoveY(UIElement target, CompositeTransform transform, double from, double to,
        int milliseconds, EasingFunctionBase? easing = null, int delay = 0, Action? completed = null)
    {
        Run(target, TranslateYPath, from, to, milliseconds, easing ?? Ease, delay, completed);
    }

    /// <summary>横向位移过渡（像素）。</summary>
    public static void MoveX(UIElement target, CompositeTransform transform, double from, double to,
        int milliseconds, EasingFunctionBase? easing = null, int delay = 0, Action? completed = null)
    {
        Run(target, TranslateXPath, from, to, milliseconds, easing ?? Ease, delay, completed);
    }

    /// <summary>等比缩放过渡。</summary>
    public static void Scale(UIElement target, CompositeTransform transform, double from, double to,
        int milliseconds, EasingFunctionBase? easing = null, int delay = 0, Action? completed = null)
    {
        Run(target, ScaleXPath, from, to, milliseconds, easing ?? Ease, delay, completed);
        Run(target, ScaleYPath, from, to, milliseconds, easing ?? Ease, delay);
    }

    /// <summary>窗口内容入场：整体淡入 + 极轻微放大，模拟 macOS 面板弹出。</summary>
    public static void WindowIn(FrameworkElement root)
    {
        var transform = Prep(root);
        root.RenderTransformOrigin = new Point(0.5, 0.5);
        Scale(root, transform, 0.986, 1, 340, Ease);
        Fade(root, 0, 1, 190, Ease);
    }

    /// <summary>列表行入场：淡入 + 上浮，按序号轻微错峰。上浮用 EaseOut 收尾，overshoot 会在 16px 间距里被看出来。</summary>
    public static void RowIn(FrameworkElement container, int index)
    {
        var transform = Prep(container);
        var delay = Math.Min(Math.Max(index, 0), 10) * 22;
        Fade(container, 0, 1, 220, Ease, delay);
        MoveY(container, transform, 6, 0, 300, Ease, delay);
    }

    private static void Run(UIElement target, string path, double from, double to, int milliseconds,
        EasingFunctionBase easing, int delay, Action? completed = null)
    {
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = from });
        if (delay > 0)
        {
            animation.KeyFrames.Add(new LinearDoubleKeyFrame
            {
                KeyTime = TimeSpan.FromMilliseconds(delay),
                Value = from,
            });
        }

        animation.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = TimeSpan.FromMilliseconds(delay + Math.Max(milliseconds, 1)),
            Value = to,
            EasingFunction = easing,
        });

        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, path);

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        if (completed != null)
        {
            storyboard.Completed += (_, _) => completed();
        }

        storyboard.Begin();
    }
}
