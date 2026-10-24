using System.Runtime.InteropServices;

namespace Jietu;

internal static partial class NativeMethods
{
    // CRYPTPROTECT_UI_FORBIDDEN：禁止 DPAPI 弹任何界面，加密/解密失败直接返回 FALSE。
    public const uint CryptprotectUiForbidden = 0x1;

    /// <summary>DPAPI 的 DATA_BLOB：DWORD + BYTE*，x64 下按自然对齐正好 16 字节。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DataBlob
    {
        public int CbData;
        public IntPtr PbData;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>
    /// 用「当前用户」作用域加密（不传可选熵，只有同一 Windows 账户能解）。
    /// 失败返回 null，调用方自行决定退回明文，保证功能不中断。
    /// </summary>
    public static byte[]? DpapiProtect(byte[] plain)
    {
        if (plain.Length == 0)
        {
            return Array.Empty<byte>();
        }

        var input = Marshal.AllocHGlobal(plain.Length);
        try
        {
            Marshal.Copy(plain, 0, input, plain.Length);
            var inBlob = new DataBlob { CbData = plain.Length, PbData = input };
            if (!CryptProtectData(ref inBlob, "SnapFlow", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out var outBlob))
            {
                Log.Info("DPAPI 加密失败，错误码 " + Marshal.GetLastWin32Error());
                return null;
            }

            try
            {
                return CopyBlob(outBlob);
            }
            finally
            {
                // 输出缓冲区由 DPAPI 用 LocalAlloc 分配，必须 LocalFree。
                LocalFree(outBlob.PbData);
            }
        }
        catch (Exception ex)
        {
            Log.Info("DPAPI 加密异常：" + ex.Message);
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(input);
        }
    }

    /// <summary>解密 DpapiProtect 产出的密文；失败（换账户、换机器、密文损坏）返回 null。</summary>
    public static byte[]? DpapiUnprotect(byte[] cipher)
    {
        if (cipher.Length == 0)
        {
            return Array.Empty<byte>();
        }

        var input = Marshal.AllocHGlobal(cipher.Length);
        try
        {
            Marshal.Copy(cipher, 0, input, cipher.Length);
            var inBlob = new DataBlob { CbData = cipher.Length, PbData = input };
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out var outBlob))
            {
                Log.Info("DPAPI 解密失败，错误码 " + Marshal.GetLastWin32Error());
                return null;
            }

            try
            {
                return CopyBlob(outBlob);
            }
            finally
            {
                LocalFree(outBlob.PbData);
            }
        }
        catch (Exception ex)
        {
            Log.Info("DPAPI 解密异常：" + ex.Message);
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(input);
        }
    }

    private static byte[]? CopyBlob(DataBlob blob)
    {
        if (blob.PbData == IntPtr.Zero || blob.CbData <= 0)
        {
            return null;
        }

        var bytes = new byte[blob.CbData];
        Marshal.Copy(blob.PbData, bytes, 0, bytes.Length);
        return bytes;
    }
}
