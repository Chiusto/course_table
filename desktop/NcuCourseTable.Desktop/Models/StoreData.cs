using System.Text.Json.Serialization;

namespace NcuCourseTable.Desktop.Models;

/// <summary>
/// 本地时间表库（schedule.json）的根结构。旧版 courses.json 会在载入时迁移为 events。
/// </summary>
public sealed class StoreData
{
    public int Version { get; set; } = 2;

    /// <summary>学期代码，如 202620271（2026-2027-1）；手动使用时可为空。</summary>
    public string Termcode { get; set; } = "";

    /// <summary>学期显示名，如 "2026-2027学年第1学期"。</summary>
    public string TermName { get; set; } = "";

    /// <summary>第 1 周周一日期（yyyy-MM-dd），用于“今天是第几周”。</summary>
    public string TermStart { get; set; } = "";

    /// <summary>教学周总数，默认 20。</summary>
    public int TotalWeeks { get; set; } = 20;

    /// <summary>节次时间表（默认 13 节）；留空使用内置时间表。</summary>
    public List<SectionTime> Sections { get; set; } = new();

    /// <summary>自定义跳转链接（可选）。</summary>
    public Dictionary<string, string> Links { get; set; } = new();

    public List<ScheduleEvent> Events { get; set; } = new();

    public List<TodoItem> Todos { get; set; } = new();

    public List<NoteItem> Notes { get; set; } = new();

    /// <summary>旧版数据中的课程数组（只读迁移，保存时会清空不再写回）。</summary>
    [JsonPropertyName("courses")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<LegacyCourse>? LegacyCourses { get; set; }
}

/// <summary>旧版 courses.json 中的课程条目，用于无损迁移。</summary>
public sealed class LegacyCourse
{
    public string Id { get; set; } = "";
    public string Termcode { get; set; } = "";
    public string Name { get; set; } = "";
    public string Teacher { get; set; } = "";
    public string Room { get; set; } = "";
    public int Weekday { get; set; } = 1;
    public int StartSection { get; set; } = 1;
    public int EndSection { get; set; } = 1;
    public string WeeksRaw { get; set; } = "1-16周";
    public string? Raw { get; set; }
}
