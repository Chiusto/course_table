using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace NcuCourseTable.Desktop.Services;

/// <summary>
/// 课程卡片配色：按 (课程名+教师) 的 FNV-1a 哈希稳定映射到 8 组 Apple 系统色，
/// 保证同一门课在不同视图颜色一致，同时保持浅色界面的低饱和纸感。
/// </summary>
public static class CoursePalette
{
    private static readonly (string Solid, string Tint)[] Colors =
    {
        ("#007AFF", "#E7F1FF"), // 蓝
        ("#5856D6", "#ECEBFC"), // 靛
        ("#AF52DE", "#F7EBFC"), // 紫
        ("#FF2D55", "#FFE9EF"), // 粉
        ("#FF3B30", "#FFEBEA"), // 红
        ("#FF9500", "#FFF2E2"), // 橙
        ("#34C759", "#E8F8ED"), // 绿
        ("#30B0C7", "#E4F5F8"), // 青
    };

    private static readonly Dictionary<string, (Brush Solid, Brush Soft, Brush Edge)> Cache = new();

    private static (Brush Solid, Brush Soft, Brush Edge) Get(string key)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(key, out var trio))
            {
                int idx = IndexFor(key);
                (string solidHex, string tintHex) = Colors[idx];
                var solid = new SolidColorBrush((Color)ColorConverter.ConvertFromString(solidHex));
                var soft = new SolidColorBrush((Color)ColorConverter.ConvertFromString(tintHex));
                Color c = solid.Color;
                var edge = new SolidColorBrush(Color.FromArgb(0x33, c.R, c.G, c.B));
                solid.Freeze();
                soft.Freeze();
                edge.Freeze();
                trio = (solid, soft, edge);
                Cache[key] = trio;
            }
            return trio;
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
            return (int)(h & 0x7fffffff) % Colors.Length;
        }
    }

    public static Brush SolidBrush(string name, string teacher) => Get(name + "\u0001" + teacher).Solid;
    public static Brush SoftBrush(string name, string teacher) => Get(name + "\u0001" + teacher).Soft;
    public static Brush EdgeBrush(string name, string teacher) => Get(name + "\u0001" + teacher).Edge;
}
