using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace NcuCourseTable.Desktop.Services;

/// <summary>
/// 当前 Windows 用户的 DPAPI 加解密，用于坚果云应用密码：
/// 密文随文件拷走或换 Windows 用户都无法还原明文。
/// </summary>
internal static class DpapiSecret
{
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("NcuCourseTable.Desktop.Nutstore.v1");

    public static string Protect(string plain)
    {
        byte[] blob = ProtectBytes(Encoding.UTF8.GetBytes(plain));
        return Convert.ToBase64String(blob);
    }

    public static string TryUnprotect(string? protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64)) return "";
        try
        {
            byte[] blob = Convert.FromBase64String(protectedBase64);
            return Encoding.UTF8.GetString(UnprotectBytes(blob));
        }
        catch
        {
            return "";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob pDataIn, string? szDataDescr,
        ref DataBlob pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct,
        uint dwFlags, out DataBlob pDataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob pDataIn, out IntPtr ppszDataDescr,
        ref DataBlob pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct,
        uint dwFlags, out DataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static byte[] ProtectBytes(byte[] plain)
    {
        DataBlob inBlob = ToBlob(plain);
        DataBlob entropy = ToBlob(Entropy);
        DataBlob outBlob = default;
        try
        {
            if (!CryptProtectData(ref inBlob, null, ref entropy, IntPtr.Zero, IntPtr.Zero,
                    0, out outBlob))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return CopyBlob(outBlob);
        }
        finally
        {
            FreeBlob(inBlob);
            FreeBlob(entropy);
            FreeOutputBlob(outBlob);
        }
    }

    private static byte[] UnprotectBytes(byte[] blob)
    {
        DataBlob inBlob = ToBlob(blob);
        DataBlob entropy = ToBlob(Entropy);
        DataBlob outBlob = default;
        try
        {
            if (!CryptUnprotectData(ref inBlob, out IntPtr descr, ref entropy, IntPtr.Zero,
                    IntPtr.Zero, 0, out outBlob))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            if (descr != IntPtr.Zero) LocalFree(descr);
            return CopyBlob(outBlob);
        }
        finally
        {
            FreeBlob(inBlob);
            FreeBlob(entropy);
            FreeOutputBlob(outBlob);
        }
    }

    private static DataBlob ToBlob(byte[] data)
    {
        IntPtr p = Marshal.AllocHGlobal(data.Length);
        if (data.Length > 0) Marshal.Copy(data, 0, p, data.Length);
        return new DataBlob { cbData = data.Length, pbData = p };
    }

    private static byte[] CopyBlob(DataBlob blob)
    {
        byte[] result = new byte[blob.cbData];
        if (result.Length > 0) Marshal.Copy(blob.pbData, result, 0, result.Length);
        return result;
    }

    private static void FreeBlob(DataBlob blob)
    {
        if (blob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(blob.pbData);
    }

    private static void FreeOutputBlob(DataBlob blob)
    {
        if (blob.pbData != IntPtr.Zero) LocalFree(blob.pbData);
    }
}
