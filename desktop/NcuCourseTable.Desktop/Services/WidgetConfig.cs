using System;
using System.IO;
using System.Text.Json;

namespace NcuCourseTable.Desktop.Services;

/// <summary>
/// 桌面小组件本地设置（widget.json，存放在数据目录，与 courses.json 同层）：
/// 屏幕位置 / 透明度 / 是否锁定(点击穿透) / 宿主策略。
/// 纯文件、容错读写 —— 损坏或缺失时回退默认值。
/// </summary>
public sealed class WidgetConfig
{
    public const string FileName = "widget.json";

    public double X { get; set; } = double.NaN;   // NaN = 未摆放过，居中
    public double Y { get; set; } = double.NaN;
    public double Opacity { get; set; } = 0.92;
    public double Scale { get; set; } = 1.0;
    public bool Locked { get; set; }               // 锁定 = 点击穿透
    public string Host { get; set; } = "bottom";   // bottom=图标上层 | wallpaper=壁纸层
    public bool AutoStart { get; set; }            // 开机自启（写入 HKCU Run）

    public static WidgetConfig Load(string dir)
    {
        var cfg = new WidgetConfig();
        try
        {
            string file = Path.Combine(dir, FileName);
            if (File.Exists(file))
            {
                WidgetConfig? parsed =
                    JsonSerializer.Deserialize<WidgetConfig>(File.ReadAllText(file), JsonOpts);
                if (parsed is not null)
                {
                    cfg = parsed;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"widget.json 解析失败：{ex.Message}");
        }
        return cfg;
    }

    public void Save(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, FileName);
            string json = JsonSerializer.Serialize(this, JsonOpts);
            File.WriteAllText(file + ".tmp", json, new System.Text.UTF8Encoding(false));
            if (File.Exists(file)) File.Delete(file);
            File.Move(file + ".tmp", file);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"widget.json 保存失败：{ex.Message}");
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
}
