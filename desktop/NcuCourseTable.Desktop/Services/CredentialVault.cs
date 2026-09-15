using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace NcuCourseTable.Desktop.Services;

/// <summary>
/// 账号凭据保险箱（credentials.bin，存放在数据目录，与 widget.json / courses.json 同层）。
/// 安全设计：明文永不落盘 —— {username, password} 序列化后交给 Windows DPAPI
/// （CryptProtectData，当前用户作用域）加密成二进制；只有本机当前 Windows 用户能解开，
/// 文件被拷走或换用户登录都无法还原密码。损坏/非本用户解密失败时容错返回 null
/// （调用方按“未配置”处理），读写风格与 WidgetConfig / JsonStore 一致（原子写：先 .tmp 再替换）。
/// </summary>
public sealed class CredentialVault
{
    public const string FileName = "credentials.bin";

    /// <summary>DPAPI 可选熵：与本应用绑定，防止用其它工具直接替换密文。</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NcuCourseTable.Desktop.Credentials.v1");

    public string Dir { get; }

    private string FilePath => Path.Combine(Dir, FileName);

    public CredentialVault(string dir)
    {
        Dir = dir;
    }

    /// <summary>数据目录下是否已存在凭据文件（含损坏文件，真正可用性以 Read() 为准）。</summary>
    public bool HasCredentials => File.Exists(FilePath);

    /// <summary>读取已保存的账号密码；未保存 / 损坏 / 非本用户解密失败时返回 null。</summary>
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

    /// <summary>已配置的账号名（供托盘菜单脱敏展示）；未配置返回空串。</summary>
    public string UsernameOrEmpty => Read()?.Username ?? "";

    /// <summary>加密保存（原子写：先写 .tmp 再替换，与 widget.json 相同）。</summary>
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

    // ---------------------------------------------------------------- DPAPI

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

    /// <summary>DPAPI 输出 blob 由 Crypt32 内部以 LocalAlloc 分配，须用 LocalFree 释放。</summary>
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
        DataBlob entropy = ToBlob(Entropy); // 熵以 DATA_BLOB 形式传入（指向数据块）
        DataBlob outBlob = default;
        try
        {
            if (!CryptProtectData(ref inBlob, null, ref entropy, IntPtr.Zero, IntPtr.Zero,
                    0 /* 默认当前用户作用域，无 UI */, out outBlob))
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
            if (descr != IntPtr.Zero) LocalFree(descr); // 描述串（未使用，防御性释放）
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
