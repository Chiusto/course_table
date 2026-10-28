using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NcuCourseTable.Desktop.Services;

/// <summary>
/// 坚果云 WebDAV 同步配置（数据目录下的 nutstore.json）。
/// 应用密码经 DPAPI 加密后写入 ProtectedPassword，明文只在内存中。
/// </summary>
public sealed class NutstoreSettings
{
    public const string FileName = "nutstore.json";
    public const string DefaultServer = "https://dav.jianguoyun.com/dav/";
    public const string DefaultRemotePath = "Timetable/schedule.json";

    public string Server { get; set; } = DefaultServer;

    public string Username { get; set; } = "";

    /// <summary>相对 WebDAV 根目录的 .json 文件路径。</summary>
    public string RemotePath { get; set; } = DefaultRemotePath;

    /// <summary>启动时与本地修改后自动同步。</summary>
    public bool AutoSync { get; set; } = true;

    public string ProtectedPassword { get; set; } = "";

    public string LastSyncUtc { get; set; } = "";

    public string LastSyncStatus { get; set; } = "";

    public bool LastSyncOk { get; set; } = true;

    [JsonIgnore]
    public string Password { get; set; } = "";

    [JsonIgnore]
    public bool IsConfigured =>
        Server.Trim().Length > 0 &&
        Username.Trim().Length > 0 &&
        Password.Length > 0;

    public static NutstoreSettings Load(string dir)
    {
        var settings = new NutstoreSettings();
        try
        {
            string file = Path.Combine(dir, FileName);
            if (File.Exists(file))
            {
                NutstoreSettings? parsed =
                    JsonSerializer.Deserialize<NutstoreSettings>(File.ReadAllText(file), JsonOpts);
                if (parsed is not null) settings = parsed;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"nutstore.json 解析失败：{ex.Message}");
        }

        settings.Normalize();
        settings.Password = DpapiSecret.TryUnprotect(settings.ProtectedPassword);
        if (settings.ProtectedPassword.Length > 0 && settings.Password.Length == 0)
        {
            settings.LastSyncStatus = "已保存的应用密码无法解密，请重新输入。";
            settings.LastSyncOk = false;
        }
        return settings;
    }

    public void Save(string dir)
    {
        Normalize();
        ProtectedPassword = Password.Length > 0 ? DpapiSecret.Protect(Password) : "";
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, FileName);
        string json = JsonSerializer.Serialize(this, JsonOpts);
        File.WriteAllText(file + ".tmp", json, new System.Text.UTF8Encoding(false));
        if (File.Exists(file)) File.Delete(file);
        File.Move(file + ".tmp", file);
    }

    public void Normalize()
    {
        string server = (Server ?? "").Trim();
        if (server.Length == 0) server = DefaultServer;
        if (!server.Contains("://", StringComparison.Ordinal)) server = "https://" + server;
        if (Uri.TryCreate(server, UriKind.Absolute, out Uri? uri))
        {
            server = uri.GetLeftPart(UriPartial.Path);
        }
        if (!server.EndsWith('/')) server += "/";
        Server = server;

        Username = (Username ?? "").Trim();

        string path = (RemotePath ?? "").Replace('\\', '/').Trim();
        path = path.Trim('/');
        while (path.Contains("//", StringComparison.Ordinal))
        {
            path = path.Replace("//", "/", StringComparison.Ordinal);
        }
        if (path.Length == 0) path = DefaultRemotePath;
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) path += ".json";
        RemotePath = path;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
