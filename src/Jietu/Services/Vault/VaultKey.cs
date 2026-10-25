using System.Security.Cryptography;

namespace Jietu;

/// <summary>
/// 32 字节库密钥的唯一持有者。
///
/// 为什么是 class 而不是 struct：struct 会在赋值/装箱/传入 in 参数时被隐式复制，
/// 复制出来的副本没人负责清零，等于把密钥散落到托管堆的未知位置。引用类型只有一个身份，
/// <see cref="Dispose"/> 能保证"这个钥匙串上的每一份都归零"。
///
/// 能力边界（不许夸大）：本类只能保证**自己持有的那块缓冲**在 Dispose 后全为 0。
/// .NET 无法擦除 string（主密码原文来自 PasswordBox.Password），也无法证明托管堆里
/// 没有中间副本。所以我们声明的是"我们持有的缓冲在离开作用域后全为 0"。
/// </summary>
public sealed class VaultKey : IDisposable
{
    /// <summary>密钥字节数（AES-256 / Argon2 输出长度）。</summary>
    public const int Length = 32;

    private readonly byte[] _bytes;
    private bool _disposed;

    private VaultKey(byte[] bytes)
    {
        _bytes = bytes;
    }

    /// <summary>从外部字节复制一份密钥。调用方仍需自行清零它的来源缓冲。</summary>
    public static VaultKey FromBytes(ReadOnlySpan<byte> source)
    {
        if (source.Length != Length)
        {
            throw new ArgumentException($"密钥长度必须为 {Length} 字节。", nameof(source));
        }

        return new VaultKey(source.ToArray());
    }

    /// <summary>生成一把随机密钥（仅测试与占位用；生产密钥只来自 KDF）。</summary>
    public static VaultKey Random() => FromBytes(RandomNumberGenerator.GetBytes(Length));

    public bool IsDisposed => _disposed;

    /// <summary>
    /// 密钥字节的只读视图。Dispose 之后访问必须抛 <see cref="ObjectDisposedException"/>：
    /// 静默返回全 0 的缓冲会让"密钥已失效"这种错误在加密库里才暴露，堆栈更难查。
    /// </summary>
    public ReadOnlySpan<byte> Bytes
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _bytes;
        }
    }

    /// <summary>复制一把独立密钥（改主密码时新旧密钥需并存一小段时间）。</summary>
    public VaultKey Clone()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new VaultKey((byte[])_bytes.Clone());
    }

    /// <summary>
    /// 诊断用原始缓冲（跳过 Dispose 检查）。仅供单测与 <see cref="VaultMemoryDebug"/>
    /// 断言"清零之后全为 0"——生产代码不得用它取密钥。
    /// </summary>
    internal ReadOnlySpan<byte> SnapshotForDiagnostics() => _bytes;

    /// <summary>清零并标记失效。幂等：重复 Dispose 不抛。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_bytes);
        _disposed = true;
    }
}
