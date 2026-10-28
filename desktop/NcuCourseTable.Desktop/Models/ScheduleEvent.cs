using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NcuCourseTable.Desktop.Models;

/// <summary>
/// 周视图中的一条日程（已合并连续节次）。既可由用户手动创建，
/// 也可由数据源插件导入；Source 区分归属，插件同步只替换自己的数据。
/// </summary>
public sealed class ScheduleEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = "";

    public string Category { get; set; } = EventCategories.Default;

    /// <summary>教师 / 参与人（插件导入的课程通常为教师）。</summary>
    public string Teacher { get; set; } = "";

    /// <summary>地点，如 "前湖北校区研究生院316"（含校区，可直接用于地图搜索）。</summary>
    public string Location { get; set; } = "";

    public string Notes { get; set; } = "";

    /// <summary>颜色键（blue/indigo/...）；留空按标题稳定取色。</summary>
    public string ColorKey { get; set; } = "";

    /// <summary>local = 手动创建；其余为插件 Id。</summary>
    public string Source { get; set; } = "local";

    /// <summary>1=周一 ... 7=周日。</summary>
    public int Weekday { get; set; } = 1;

    /// <summary>起始节次（1 起）。</summary>
    public int StartSection { get; set; } = 1;

    /// <summary>结束节次（含）。</summary>
    public int EndSection { get; set; } = 1;

    /// <summary>周次表达式，如 "1-11周"、"1-9周(单)"。</summary>
    public string WeeksRaw { get; set; } = "1-16周";

    /// <summary>单次日程日期（yyyy-MM-dd）；为空表示按周重复。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Date { get; set; }

    /// <summary>原始单元格文本（插件追溯用，可空）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Raw { get; set; }

    /// <summary>最后修改时间（UTC，ISO-8601）；云端合并冲突时用。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UpdatedAt { get; set; }

    [JsonIgnore]
    private WeekRule? _rule;

    [JsonIgnore]
    private HashSet<int>? _cancelWeeks;

    [JsonIgnore]
    private HashSet<int>? _makeupWeeks;

    private static readonly Regex BracketRe = new(@"\[([^\]]+)\]", RegexOptions.Compiled);
    private static readonly Regex TagRe = new(@"<[^>]*>", RegexOptions.Compiled);

    [JsonIgnore]
    public WeekRule Rule => _rule ??= WeekRule.TryParse(WeeksRaw);

    [JsonIgnore]
    public bool IsOneOff => OnDate is not null;

    [JsonIgnore]
    public DateTime? OnDate => DateTime.TryParseExact(Date, "yyyy-MM-dd",
        CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d) ? d.Date : null;

    /// <summary>是否出现在指定教学周：单次日程按日期折算周次，周期日程按周次规则。</summary>
    public bool OccursInWeek(int week, DateTime? termStart)
    {
        if (OnDate is not { } date)
        {
            return ContainsWeek(week);
        }
        if (termStart is null)
        {
            return week == 1 && Weekday == WeekdayOf(date);
        }
        int days = (date - termStart.Value.Date).Days;
        if (days < 0) return false;
        return days / 7 + 1 == week;
    }

    /// <summary>周期日程本周是否发生：补课优先，其次停课，最后按基础周次规则。</summary>
    public bool ContainsWeek(int week)
    {
        EnsureExceptions();
        if (_makeupWeeks!.Contains(week)) return true;
        if (_cancelWeeks!.Contains(week)) return false;
        return Rule.Contains(week);
    }

    /// <summary>剔除插件单元格里混入的停课/补课标记后，用于展示的教师名。</summary>
    [JsonIgnore]
    public string DisplayTeacher
    {
        get
        {
            string t = TagRe.Replace(Teacher ?? "", " ");
            int cut = t.IndexOfAny(new[] { '[', '【' });
            if (cut >= 0) t = t[..cut];
            return t.Trim();
        }
    }

    /// <summary>
    /// 展示用地点。含停课/补课单元格时，Location 可能是“8-8周 (停课)”，
    /// 此时从 Raw 中取第一个真实地点（不含“周”的方括号片段）。
    /// </summary>
    [JsonIgnore]
    public string DisplayLocation
    {
        get
        {
            string location = TagRe.Replace(Location ?? "", " ").Trim();
            if (location.Length > 0 && !location.Contains('周') && !location.Contains("停课") && !location.Contains("补课"))
            {
                return location;
            }
            if (!string.IsNullOrEmpty(Raw))
            {
                foreach (Match m in BracketRe.Matches(Raw))
                {
                    string token = TagRe.Replace(m.Groups[1].Value, " ").Trim();
                    if (token.Length > 0 && !token.Contains('周') && !Regex.IsMatch(token, @"^\d+\s*[-~]\s*\d+$"))
                    {
                        return token;
                    }
                }
            }
            return location;
        }
    }

    private void EnsureExceptions()
    {
        if (_cancelWeeks is not null) return;
        _cancelWeeks = new HashSet<int>();
        _makeupWeeks = new HashSet<int>();
        if (string.IsNullOrEmpty(Raw)) return;

        foreach (Match m in BracketRe.Matches(Raw))
        {
            string token = m.Groups[1].Value;
            if (!token.Contains('周')) continue;
            if (token.Contains("停课")) AddWeeks(_cancelWeeks, token);
            else if (token.Contains("补课")) AddWeeks(_makeupWeeks, token);
        }
    }

    private static void AddWeeks(HashSet<int> set, string token)
    {
        WeekRule rule = WeekRule.TryParse(token);
        foreach ((int start, int end) in rule.Ranges)
        {
            for (int w = start; w <= end; w++) set.Add(w);
        }
    }

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

    [JsonIgnore]
    public string ScheduleText => OnDate is { } d
        ? $"{d:MM月dd日} · {WeekdayName(WeekdayOf(d))} · 仅一次"
        : Rule.Display;

    [JsonIgnore]
    public string SourceLabel => Source is "local" or "" ? "手动创建" : "插件导入";

    public static int WeekdayOf(DateTime date) =>
        date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek;

    public static string WeekdayName(int weekday) =>
        new[] { "周一", "周二", "周三", "周四", "周五", "周六", "周日" }[Math.Clamp(weekday, 1, 7) - 1];

    public ScheduleEvent Clone() => new()
    {
        Id = Id,
        Title = Title,
        Category = Category,
        Teacher = Teacher,
        Location = Location,
        Notes = Notes,
        ColorKey = ColorKey,
        Source = Source,
        Weekday = Weekday,
        StartSection = StartSection,
        EndSection = EndSection,
        WeeksRaw = WeeksRaw,
        Date = Date,
        Raw = Raw,
        UpdatedAt = UpdatedAt,
    };

    public void CopyFrom(ScheduleEvent from)
    {
        Title = from.Title;
        Category = from.Category;
        Teacher = from.Teacher;
        Location = from.Location;
        Notes = from.Notes;
        ColorKey = from.ColorKey;
        Weekday = from.Weekday;
        StartSection = from.StartSection;
        EndSection = from.EndSection;
        WeeksRaw = from.WeeksRaw;
        Date = from.Date;
        Raw = from.Raw;
    }
}

public static class EventCategories
{
    public const string Default = "日程";

    public static readonly string[] All =
    {
        "日程", "课程", "工作", "学习", "生活", "运动", "其他",
    };
}
