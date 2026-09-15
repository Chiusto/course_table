namespace NcuCourseTable.Desktop.Models;

/// <summary>节次时间表一项，对齐 ncu_sdk.models.Section（index=jcid 1..13）。</summary>
public sealed class SectionTime
{
    public int Index { get; set; }
    public string Label { get; set; } = "";   // 中文序号：一、二 ...
    public string Start { get; set; } = "";   // "09:50"
    public string End { get; set; } = "";     // "10:30"

    public string Display => $"第{Label}节 {Start}~{End}";

    public static IReadOnlyList<SectionTime> Default() => new List<SectionTime>
    {
        new() { Index = 1, Label = "一", Start = "08:00", End = "08:40" },
        new() { Index = 2, Label = "二", Start = "08:50", End = "09:30" },
        new() { Index = 3, Label = "三", Start = "09:50", End = "10:30" },
        new() { Index = 4, Label = "四", Start = "10:40", End = "11:20" },
        new() { Index = 5, Label = "五", Start = "11:30", End = "12:10" },
        new() { Index = 6, Label = "六", Start = "14:00", End = "14:40" },
        new() { Index = 7, Label = "七", Start = "14:50", End = "15:30" },
        new() { Index = 8, Label = "八", Start = "15:50", End = "16:30" },
        new() { Index = 9, Label = "九", Start = "16:40", End = "17:20" },
        new() { Index = 10, Label = "十", Start = "17:30", End = "18:10" },
        new() { Index = 11, Label = "十一", Start = "19:00", End = "19:40" },
        new() { Index = 12, Label = "十二", Start = "19:50", End = "20:30" },
        new() { Index = 13, Label = "十三", Start = "20:40", End = "21:20" },
    };

    public static IReadOnlyDictionary<int, SectionTime> DefaultMap() =>
        Default().ToDictionary(s => s.Index);
}
