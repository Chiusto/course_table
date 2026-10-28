using System.Collections.Generic;
using System.Windows.Media;
using NcuCourseTable.Desktop.Models;

namespace NcuCourseTable.Desktop.Services;

/// <summary>
/// 日程配色：优先使用事件自选颜色键；留空时按“标题 + 参与人”的 FNV-1a 哈希
/// 稳定映射到 8 组 Apple 系统色，保证同名日程在各视图颜色一致。
/// </summary>
public static class EventPalette
{
    public sealed record Swatch(string Key, string Name, string Solid, string Tint);

    public static readonly IReadOnlyList<Swatch> Swatches = new[]
    {
        new Swatch("blue", "蓝", "#007AFF", "#E7F1FF"),
        new Swatch("indigo", "靛", "#5856D6", "#ECEBFC"),
        new Swatch("purple", "紫", "#AF52DE", "#F7EBFC"),
        new Swatch("pink", "粉", "#FF2D55", "#FFE9EF"),
        new Swatch("red", "红", "#FF3B30", "#FFEBEA"),
        new Swatch("orange", "橙", "#FF9500", "#FFF2E2"),
        new Swatch("green", "绿", "#34C759", "#E8F8ED"),
        new Swatch("teal", "青", "#30B0C7", "#E4F5F8"),
    };

    private static readonly Dictionary<string, (Brush Solid, Brush Soft, Brush Edge)> Cache = new();

    public static string ResolveKey(ScheduleEvent e)
    {
        string key = (e.ColorKey ?? "").Trim().ToLowerInvariant();
        if (key.Length > 0 && Swatches.Any(s => s.Key == key)) return key;
        return Swatches[IndexFor(e.Title + "\u0001" + e.Teacher)].Key;
    }

    public static Swatch? Find(string? key) =>
        Swatches.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));

    public static int IndexFor(string key)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (char ch in key ?? "")
            {
                h ^= ch;
                h *= 16777619;
            }
            return (int)(h & 0x7fffffff) % Swatches.Count;
        }
    }

    private static (Brush Solid, Brush Soft, Brush Edge) Get(string key)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(key, out var trio))
            {
                Swatch sw = Find(key) ?? Swatches[0];
                var solid = new SolidColorBrush((Color)ColorConverter.ConvertFromString(sw.Solid));
                var soft = new SolidColorBrush((Color)ColorConverter.ConvertFromString(sw.Tint));
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

    public static Brush SolidBrush(ScheduleEvent e) => Get(ResolveKey(e)).Solid;

    public static Brush SoftBrush(ScheduleEvent e) => Get(ResolveKey(e)).Soft;

    public static Brush EdgeBrush(ScheduleEvent e) => Get(ResolveKey(e)).Edge;

    public static Brush SolidBrush(string? colorKey) => Get(Find(colorKey)?.Key ?? Swatches[0].Key).Solid;

    public static Brush SoftBrush(string? colorKey) => Get(Find(colorKey)?.Key ?? Swatches[0].Key).Soft;
}
