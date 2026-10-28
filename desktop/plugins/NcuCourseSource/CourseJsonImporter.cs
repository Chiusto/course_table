using System.IO;
using System.Text.Json;
using Timetable.Plugins;

namespace NcuCourseSource;

/// <summary>读取 ncu_sdk 导出的课程 JSON（数组或 {courses:[...]}）并映射为插件日程。</summary>
public static class CourseJsonImporter
{
    public sealed record ImportResult(string Termcode, List<PluginEvent> Events);

    public static ImportResult ReadFile(string path, string? fallbackTerm = null)
    {
        return Parse(File.ReadAllText(path), fallbackTerm);
    }

    public static ImportResult Parse(string json, string? fallbackTerm = null)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        JsonElement arr = root.ValueKind == JsonValueKind.Array
            ? root
            : (root.TryGetProperty("courses", out var courses) ? courses
                : throw new FormatException("不是课程数组：缺少 courses 字段"));

        string term = fallbackTerm?.Trim() ?? "";
        if (term.Length == 0 && arr.GetArrayLength() > 0 &&
            arr[0].TryGetProperty("termcode", out var firstT) && firstT.ValueKind != JsonValueKind.Null)
        {
            term = firstT.ToString().Trim();
        }

        var imported = new List<PluginEvent>();
        foreach (JsonElement el in arr.EnumerateArray())
        {
            PluginEvent? mapped = Map(el);
            if (mapped is not null) imported.Add(mapped);
        }
        return new ImportResult(term, imported);
    }

    private static PluginEvent? Map(JsonElement el)
    {
        string Str(string snake, string pascal)
        {
            foreach (string key in new[] { snake, pascal })
            {
                if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(key, out var v) &&
                    v.ValueKind != JsonValueKind.Null)
                {
                    return v.ToString().Trim();
                }
            }
            return "";
        }

        int Num(string snake, string pascal, int dflt)
        {
            foreach (string key in new[] { snake, pascal })
            {
                if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(key, out var v) &&
                    v.ValueKind != JsonValueKind.Null && v.TryGetInt32(out int i))
                {
                    return i;
                }
            }
            return dflt;
        }

        string title = Str("name", "Name");
        if (title.Length == 0) title = Str("title", "Title");
        if (title.Length == 0) return null;

        string room = Str("room", "Room");
        if (room.Length == 0) room = Str("location", "Location");

        string weeks = Str("weeks_raw", "WeeksRaw");
        if (weeks.Length == 0) weeks = Str("weeks", "Weeks");

        int start = Math.Max(1, Num("start_section", "StartSection", 1));
        int end = Math.Max(1, Num("end_section", "EndSection", start));
        if (end < start) (start, end) = (end, start);

        return new PluginEvent
        {
            Title = title,
            Category = "课程",
            Teacher = Str("teacher", "Teacher"),
            Location = room,
            Notes = Str("notes", "Notes"),
            Weekday = Math.Clamp(Num("weekday", "Weekday", 1), 1, 7),
            StartSection = start,
            EndSection = end,
            WeeksRaw = weeks.Length > 0 ? weeks : "1-16周",
            Date = Str("date", "Date") is { Length: > 0 } date ? date : null,
            Raw = Str("raw", "Raw") is { Length: > 0 } raw ? raw : null,
        };
    }
}
