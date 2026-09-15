using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace NcuCourseTable.Desktop.Services;

/// <summary>
/// 课程卡片配色：按 (课程名+教师) 的 FNV-1a 哈希稳定映射到 12 色板，
/// 保证同一门课在不同视图（周视图/日列表）颜色一致，且不随存储顺序变化。
/// </summary>
public static class CoursePalette
{
    // 12 个可辨识色（Material 500），浅色主题下深色描边 + 同色淡底
    private static readonly string[] Hex = {
        "#EF5350", "#AB47BC", "#5C6BC0", "#29B6F6", "#26A69A", "#9CCC65",
        "#FFA726", "#8D6E63", "#EC407A", "#7E57C2", "#66BB6A", "#FF7043",
    };

    private static readonly Dictionary<string, (Brush Solid, Brush Soft)> Cache = new();

    private static (Brush Solid, Brush Soft) Get(string key)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(key, out var pair))
            {
                int idx = IndexFor(key);
                Color c = (Color)ColorConverter.ConvertFromString(Hex[idx]);
                Color soft = Color.FromArgb(0x26, c.R, c.G, c.B); // ~15% 透明，浅底
                pair = (new SolidColorBrush(c), new SolidColorBrush(soft));
                pair.Solid.Freeze();
                pair.Soft.Freeze();
                Cache[key] = pair;
            }
            return pair;
        }
    }

    public static int IndexFor(string key)
    {
        // FNV-1a 32bit
        unchecked
        {
            uint h = 2166136261;
            foreach (char ch in key ?? "")
            {
                h ^= ch;
                h *= 16777619;
            }
            return (int)(h & 0x7fffffff) % Hex.Length;
        }
    }

    public static Brush SolidBrush(string name, string teacher) => Get(name + "\u0001" + teacher).Solid;
    public static Brush SoftBrush(string name, string teacher) => Get(name + "\u0001" + teacher).Soft;
}
