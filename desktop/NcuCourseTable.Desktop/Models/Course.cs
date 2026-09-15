using System.Text.Json.Serialization;

namespace NcuCourseTable.Desktop.Models;

/// <summary>
/// 一门课的一次排课（已合并连续节次）。
/// 字段对齐 ncu_sdk.models.Course.to_dict()：termcode/name/teacher/room/weekday/
/// start_section/end_section/weeks_raw，桌面端额外增加 Id 用于本地增删改。
/// </summary>
public sealed class Course
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Termcode { get; set; } = "";

    public string Name { get; set; } = "";

    public string Teacher { get; set; } = "";

    /// <summary>教室/地点，如 "前湖北校区研究生院316"（含校区，可直接用于地图搜索）。</summary>
    public string Room { get; set; } = "";

    /// <summary>1=周一 ... 7=周日。</summary>
    public int Weekday { get; set; } = 1;

    /// <summary>起始节次（jcid，1..13）。</summary>
    public int StartSection { get; set; } = 1;

    /// <summary>结束节次（含）。</summary>
    public int EndSection { get; set; } = 1;

    /// <summary>周次表达式，如 "1-11周"、"6-6周"、"1-9周(单)"。</summary>
    public string WeeksRaw { get; set; } = "1-16周";

    /// <summary>原始单元格文本（来自 gmsstu，仅用于追溯，可空）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Raw { get; set; }

    [JsonIgnore]
    private WeekRule? _rule;

    [JsonIgnore]
    public WeekRule Rule => _rule ??= WeekRule.TryParse(WeeksRaw);

    public bool ContainsWeek(int week) => Rule.Contains(week);

    [JsonIgnore]
    public int SectionCount => EndSection - StartSection + 1;

    [JsonIgnore]
    public string SectionRangeText =>
        EndSection > StartSection ? $"{StartSection}-{EndSection}节" : $"{StartSection}节";

    /// <summary>时间文本，如 "14:00-16:30"；未配置节次时间时返回空串。</summary>
    public string TimeText(IReadOnlyDictionary<int, SectionTime> sections)
    {
        if (sections.TryGetValue(StartSection, out var s) &&
            sections.TryGetValue(EndSection, out var e) &&
            s.Start.Length > 0 && e.End.Length > 0)
        {
            return $"{s.Start}-{e.End}";
        }
        return "";
    }

    public Course Clone() => new()
    {
        Id = Id,
        Termcode = Termcode,
        Name = Name,
        Teacher = Teacher,
        Room = Room,
        Weekday = Weekday,
        StartSection = StartSection,
        EndSection = EndSection,
        WeeksRaw = WeeksRaw,
        Raw = Raw,
    };
}
