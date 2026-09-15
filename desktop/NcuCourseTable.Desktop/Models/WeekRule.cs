using System.Text.RegularExpressions;

namespace NcuCourseTable.Desktop.Models;

/// <summary>
/// 周次规则 —— C# 移植 ncu_sdk.weeks.WeekSpec（sdk/ncu_sdk/weeks.py），
/// 解析语法保持一致：1-11周 / 6-6周 / 1-9周(单) / 2-10周(双) / 1,3,5周 / 1-8周,10-12周。
/// </summary>
public sealed class WeekRule
{
    private static readonly Regex RangeRe = new(
        @"(\d{1,2})\s*(?:[-~—－]\s*(\d{1,2}))?\s*周?",
        RegexOptions.Compiled);

    private static readonly Regex SingleRe = new(@"[（(]\s*单\s*[)）]", RegexOptions.Compiled);
    private static readonly Regex DoubleRe = new(@"[（(]\s*双\s*[)）]", RegexOptions.Compiled);

    public IReadOnlyList<(int Start, int End)> Ranges { get; }

    /// <summary>true = 只在单周上课（周次为奇数）。</summary>
    public bool OddOnly { get; }

    /// <summary>true = 只在双周上课（周次为偶数）。</summary>
    public bool EvenOnly { get; }

    public string Raw { get; }

    public WeekRule(IReadOnlyList<(int Start, int End)> ranges, bool oddOnly, bool evenOnly, string raw)
    {
        Ranges = ranges;
        OddOnly = oddOnly;
        EvenOnly = evenOnly;
        Raw = raw;
    }

    public static WeekRule Parse(string raw)
    {
        string text = (raw ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            throw new FormatException("周次表达式为空");
        }

        bool odd = SingleRe.IsMatch(text);
        bool even = !odd && DoubleRe.IsMatch(text);

        var ranges = new List<(int, int)>();
        foreach (Match m in RangeRe.Matches(text))
        {
            int start = int.Parse(m.Groups[1].Value);
            int end = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : start;
            if (end < start)
            {
                (start, end) = (end, start);
            }
            ranges.Add((start, end));
        }

        if (ranges.Count == 0)
        {
            throw new FormatException($"无法解析周次表达式：{raw!}");
        }

        return new WeekRule(ranges, odd, even, text);
    }

    /// <summary>宽松解析：失败时返回整学期规则（1-总周数），便于导入脏数据时降级。</summary>
    public static WeekRule TryParse(string? raw, int totalWeeks = 20)
    {
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                return Parse(raw);
            }
            catch (FormatException)
            {
                // fall through
            }
        }
        return new WeekRule(new[] { (1, totalWeeks) }, false, false, $"{1}-{totalWeeks}周");
    }

    /// <summary>第 week 周是否上课（与 weeks.py WeekSpec.contains 逻辑一致）。</summary>
    public bool Contains(int week)
    {
        bool inRange = false;
        foreach (var (s, e) in Ranges)
        {
            if (s <= week && week <= e)
            {
                inRange = true;
                break;
            }
        }
        if (!inRange)
        {
            return false;
        }
        if (OddOnly && week % 2 == 0)
        {
            return false;
        }
        if (EvenOnly && week % 2 == 1)
        {
            return false;
        }
        return true;
    }

    public int StartWeek => Ranges.Min(r => r.Start);
    public int EndWeek => Ranges.Max(r => r.End);

    /// <summary>展示文本：优先保留原始写法（如 "5-15周(单)"）。</summary>
    public string Display => Raw.Length > 0 ? Raw : string.Join("、", Ranges.Select(r =>
        r.Start == r.End ? $"{r.Start}周" : $"{r.Start}-{r.End}周"));
}
