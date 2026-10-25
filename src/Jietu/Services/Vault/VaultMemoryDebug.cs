using System.Runtime.CompilerServices;

namespace Jietu;

#if DEBUG
/// <summary>
/// 密钥材料清零的**调试探针**（仅在 DEBUG 编译）。
///
/// 用法：在需要验证的地方 <c>Track("password", buffer)</c>，测试结束时 <c>VerifyAllZeroed()</c>。
/// 它只是登记引用，不复制内容、不清零任何东西——清零是业务代码的责任，本类只负责"看得见"。
///
/// 为什么必须有：清零这种要求最容易被写成"看起来做了"。没有可断言的口径时，
/// "内存零残留"会退化成一句无法验证的口号。
///
/// 能力边界（不许夸大）：本类能断言的只有"**登记过的、我们自己持有的 byte[] 缓冲**"是否全为 0。
/// 以下三件事本类**做不到**，也不该在文档里冒充做到：
/// ① <c>string</c> 主密码无法清零（不可变对象，来自 PasswordBox.Password）；
/// ② <c>string</c>→<c>byte[]</c> 之间可能存在的 JIT/Runtime 中间副本无法枚举；
/// ③ 无法证明托管堆里"一个字节都没留下"。
/// </summary>
internal static class VaultMemoryDebug
{
    private static readonly List<(string Name, byte[] Buffer)> Tracked = [];
    private static readonly Lock Gate = new();

    /// <summary>总开关。默认关闭，避免测试之外的任何分配开销。</summary>
    public static bool Enabled { get; set; }

    /// <summary>登记一块需要"事后全 0"的缓冲。未开启时不做事。</summary>
    public static void Track(string name, byte[]? buffer)
    {
        if (!Enabled || buffer == null)
        {
            return;
        }

        lock (Gate)
        {
            Tracked.Add((name, buffer));
        }
    }

    /// <summary>断言全部登记缓冲已清零。返回 false 时 <paramref name="failure"/> 指出第一块没清干净的。</summary>
    public static bool VerifyAllZeroed(out string? failure)
    {
        lock (Gate)
        {
            foreach (var (name, buffer) in Tracked)
            {
                foreach (var value in buffer)
                {
                    if (value != 0)
                    {
                        failure = $"缓冲未清零：{name}（{buffer.Length} 字节）";
                        return false;
                    }
                }
            }

            Tracked.Clear();
        }

        failure = null;
        return true;
    }

    /// <summary>清空登记表（不验证）。测试之间隔离用。</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            Tracked.Clear();
        }

        Enabled = false;
    }
}
#else
/// <summary>
/// 密钥材料清零探针的 Release 空实现：保留同一组签名，让调用点无需 <c>#if</c> 包裹。
/// Release 下不做任何登记与断言（也就无法证明清零——这正是"零残留"只能作为
/// DEBUG 断言口径、而不是运行期保证的原因）。
/// </summary>
internal static class VaultMemoryDebug
{
    public static bool Enabled { get; set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Track(string name, byte[]? buffer)
    {
    }

    public static bool VerifyAllZeroed(out string? failure)
    {
        failure = null;
        return true;
    }

    public static void Reset()
    {
        Enabled = false;
    }
}
#endif
