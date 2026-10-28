using System;
using System.IO;
using System.Text.Json;
using NcuCourseTable.Desktop.Models;

namespace NcuCourseTable.Desktop.Services;

/// <summary>
/// 同步基线（nutstore-state.json，仅本机保留）：
/// Base 是上次成功同步后的完整数据，用于三方合并；RemoteETag 用于快速判断云端是否变化。
/// </summary>
public sealed class NutstoreSyncState
{
    public const string FileName = "nutstore-state.json";

    /// <summary>服务器 + 账号 + 远程路径；变更后旧基线失效。</summary>
    public string RemoteKey { get; set; } = "";

    public string RemoteETag { get; set; } = "";

    public StoreData? Base { get; set; }

    public static NutstoreSyncState Load(string dir)
    {
        try
        {
            string file = Path.Combine(dir, FileName);
            if (!File.Exists(file)) return new NutstoreSyncState();
            return JsonSerializer.Deserialize<NutstoreSyncState>(File.ReadAllText(file), DataStore.JsonOpts)
                ?? new NutstoreSyncState();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"nutstore-state.json 解析失败：{ex.Message}");
            return new NutstoreSyncState();
        }
    }

    public void Save(string dir)
    {
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, FileName);
        string json = JsonSerializer.Serialize(this, DataStore.JsonOpts);
        File.WriteAllText(file + ".tmp", json, new System.Text.UTF8Encoding(false));
        if (File.Exists(file)) File.Delete(file);
        File.Move(file + ".tmp", file);
    }
}
