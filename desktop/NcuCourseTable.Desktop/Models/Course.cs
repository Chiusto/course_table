using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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
    private HashSet<int>? _cancelWeeks;

    [JsonIgnore]
    private HashSet<int>? _makeupWeeks;

    private static readonly Regex BracketRe = new(@"\[([^\]]+)\]", RegexOptions.Compiled);
    private static readonly Regex TagRe = new(@"<[^>]*>", RegexOptions.Compiled);

    [JsonIgnore]
    public WeekRule Rule => _rule ??= WeekRule.TryParse(WeeksRaw);

    /// <summary>本周是否上课：补课优先，其次停课，最后按基础周次规则。</summary>
    public bool ContainsWeek(int week)
    {
        EnsureExceptions();
        if (_makeupWeeks!.Contains(week)) return true;
        if (_cancelWeeks!.Contains(week)) return false;
        return Rule.Contains(week);
    }

    /// <summary>剔除 gmsstu 单元格里混入的停课/补课标记后，用于展示的教师名。</summary>
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
    /// 展示用地点。含停课/补课单元格时，Room 字段可能是“8-8周 (停课)”，
    /// 此时从 Raw 中取第一个真实地点（不含“周”的方括号片段）。
    /// </summary>
    [JsonIgnore]
    public string DisplayRoom
    {
        get
        {
            string room = TagRe.Replace(Room ?? "", " ").Trim();
            if (room.Length > 0 && !room.Contains('周') && !room.Contains("停课") && !room.Contains("补课"))
            {
                return room;
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
            return room.Length > 0 ? room : "—";
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
