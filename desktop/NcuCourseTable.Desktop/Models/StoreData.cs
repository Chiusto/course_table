namespace NcuCourseTable.Desktop.Models;

/// <summary>
/// 本地课表库（courses.json）的根结构。
/// </summary>
public sealed class StoreData
{
    public int Version { get; set; } = 1;

    /// <summary>学期代码，如 202620271（2026-2027-1）。</summary>
    public string Termcode { get; set; } = "";

    /// <summary>学期显示名，如 "2026-2027学年第1学期"。</summary>
    public string TermName { get; set; } = "";

    /// <summary>第 1 周周一日期（yyyy-MM-dd），用于“今天是第几周”。</summary>
    public string TermStart { get; set; } = "";

    /// <summary>教学周总数，默认 20。</summary>
    public int TotalWeeks { get; set; } = 20;

    /// <summary>节次时间表（默认 13 节）；留空使用内置时间表。</summary>
    public List<SectionTime> Sections { get; set; } = new();

    /// <summary>自定义跳转链接（可选），如门户/教务系统。</summary>
    public Dictionary<string, string> Links { get; set; } = new();

    public List<Course> Courses { get; set; } = new();
}
