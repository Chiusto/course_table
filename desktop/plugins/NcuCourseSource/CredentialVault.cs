using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace NcuCourseSource;

/// <summary>
/// 账号凭据保险箱（数据目录下的 credentials.bin，与旧版桌面端兼容）。
/// 明文永不落盘：{username,password} 经 Windows DPAPI（当前用户）加密；
/// 文件拷走或换 Windows 用户都无法还原密码。损坏时按“未配置”处理。
/// </summary>
public sealed class CredentialVault
{
    public const string FileName = "credentials.bin";

    // 与旧版桌面端保持一致的熵，已保存的凭据迁移到插件后仍可解密
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NcuCourseTable.Desktop.Credentials.v1");

    public string Dir { get; }

    private string FilePath => Path.Combine(Dir, FileName);

    public CredentialVault(string dir)
    {
        Dir = dir;
    }

    public bool HasCredentials => File.Exists(FilePath);

    public (string Username, string Password)? Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            byte[] plain = Unprotect(File.ReadAllBytes(FilePath));
            using JsonDocument doc = JsonDocument.Parse(Encoding.UTF8.GetString(plain));
            string user = doc.RootElement.GetProperty("username").GetString() ?? "";
            string pass = doc.RootElement.GetProperty("password").GetString() ?? "";
            return user.Length > 0 && pass.Length > 0 ? (user, pass) : null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"credentials.bin 解密失败：{ex.Message}");
            return null;
        }
    }

    public string UsernameOrEmpty => Read()?.Username ?? "";

    public void Save(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("账号与密码不能为空");
        }
        Directory.CreateDirectory(Dir);
        string json = JsonSerializer.Serialize(new { username = username.Trim(), password });
        byte[] blob = Protect(Encoding.UTF8.GetBytes(json));
        string tmp = FilePath + ".tmp";
        File.WriteAllBytes(tmp, blob);
        if (File.Exists(FilePath)) File.Delete(FilePath);
        File.Move(tmp, FilePath);
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

    private static void FreeOutputBlob(DataBlob blob)
    {
        if (blob.pbData != IntPtr.Zero)
        {
            LocalFree(blob.pbData);
        }
    }

    private static byte[] Protect(byte[] plain)
    {
        DataBlob inBlob = ToBlob(plain);
        DataBlob entropy = ToBlob(Entropy);
        DataBlob outBlob = default;
        try
        {
            if (!CryptProtectData(ref inBlob, null, ref entropy, IntPtr.Zero, IntPtr.Zero,
                    0, out outBlob))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            byte[] result = new byte[outBlob.cbData];
            if (result.Length > 0) Marshal.Copy(outBlob.pbData, result, 0, result.Length);
            return result;
        }
        finally
        {
            FreeBlob(inBlob);
            FreeBlob(entropy);
            FreeOutputBlob(outBlob);
        }
    }

    private static byte[] Unprotect(byte[] blob)
    {
        DataBlob inBlob = ToBlob(blob);
        DataBlob entropy = ToBlob(Entropy);
        DataBlob outBlob = default;
        try
        {
            if (!CryptUnprotectData(ref inBlob, out IntPtr descr, ref entropy, IntPtr.Zero,
                    IntPtr.Zero, 0, out outBlob))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            if (descr != IntPtr.Zero) LocalFree(descr);
            byte[] result = new byte[outBlob.cbData];
            if (result.Length > 0) Marshal.Copy(outBlob.pbData, result, 0, result.Length);
            return result;
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
        Marshal.Copy(data, 0, p, data.Length);
        return new DataBlob { cbData = data.Length, pbData = p };
    }

    private static void FreeBlob(DataBlob blob)
    {
        if (blob.pbData != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(blob.pbData);
        }
    }
}
