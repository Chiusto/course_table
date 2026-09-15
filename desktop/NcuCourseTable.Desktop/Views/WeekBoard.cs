using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NcuCourseTable.Desktop.Models;
using NcuCourseTable.Desktop.Services;

namespace NcuCourseTable.Desktop.Views;

/// <summary>
/// 周视图画布（原生 WPF，自绘网格与课程块）。
///
/// 布局：
///   左上角为节次时间轴（第 N 节 + 起止时间），右侧 7 列为周一~周日；
///   课程块按 (start_section-1)*RowHeight 纵向定位、跨 end-start+1 行。
/// 难点处理：
///   1) 同日重叠课程 -> 贪心分栏（lane），每门课占 1/N 列宽并排显示；
///   2) 节次纵向合并 -> 与 ncu_sdk parser 相同语义，一条记录一个块；
///   3) 点击/双击 -> 选中详情、双击编辑、空白双击按落点新建课程。
///
/// 参考 TrafficMonitor 的“点窗内任意处拖窗”思路：本组件头部留白区域可拖动窗口，
/// 拖拽逻辑在 MainWindow 中通过 WM/控件的 MouseLeftButtonDown 实现。
/// </summary>
public sealed class WeekBoard : Canvas
{
    public const double HeaderHeight = 48;
    public const double TimeAxisWidth = 62;
    public const double RowHeight = 54;
    public const double BlockGap = 3;

    // 配色（浅色主题）
    private static readonly Brush LineBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xEE)));
    private static readonly Brush HeaderLineBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xD3, 0xD7, 0xDE)));
    private static readonly Brush AxisBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF6, 0xF7, 0xF9)));
    private static readonly Brush TodayColumnBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xF6, 0xE5)));
    private static readonly Brush SelectedColumnBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xFF)));
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2B, 0x2F, 0x38)));
    private static readonly Brush SubTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x6B, 0x70, 0x7A)));
    private static readonly Brush TodayBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)));

    // ------------------------------------------------------------------ 依赖属性
    public static readonly DependencyProperty CoursesProperty = DependencyProperty.Register(
        nameof(Courses), typeof(IEnumerable), typeof(WeekBoard), new PropertyMetadata(null, VisualAffect));

    public static readonly DependencyProperty SelectedWeekProperty = DependencyProperty.Register(
        nameof(SelectedWeek), typeof(int), typeof(WeekBoard), new PropertyMetadata(1, VisualAffect));

    public static readonly DependencyProperty SelectedDayProperty = DependencyProperty.Register(
        nameof(SelectedDay), typeof(int), typeof(WeekBoard), new PropertyMetadata(1, VisualAffect));

    public static readonly DependencyProperty TodayDayProperty = DependencyProperty.Register(
        nameof(TodayDay), typeof(int), typeof(WeekBoard), new PropertyMetadata(1, VisualAffect));

    public static readonly DependencyProperty HighlightTodayProperty = DependencyProperty.Register(
        nameof(HighlightToday), typeof(bool), typeof(WeekBoard), new PropertyMetadata(false, VisualAffect));

    public static readonly DependencyProperty SectionsProperty = DependencyProperty.Register(
        nameof(Sections), typeof(IEnumerable), typeof(WeekBoard), new PropertyMetadata(null, VisualAffect));

    public static readonly DependencyProperty TermStartProperty = DependencyProperty.Register(
        nameof(TermStart), typeof(DateTime?), typeof(WeekBoard), new PropertyMetadata(null, VisualAffect));

    public static readonly DependencyProperty SelectedCourseProperty = DependencyProperty.Register(
        nameof(SelectedCourse), typeof(Course), typeof(WeekBoard), new PropertyMetadata(null, VisualAffect));

    public IEnumerable? Courses
    {
        get => (IEnumerable?)GetValue(CoursesProperty);
        set => SetValue(CoursesProperty, value);
    }

    public int SelectedWeek
    {
        get => (int)GetValue(SelectedWeekProperty);
        set => SetValue(SelectedWeekProperty, value);
    }

    public int SelectedDay
    {
        get => (int)GetValue(SelectedDayProperty);
        set => SetValue(SelectedDayProperty, value);
    }

    public int TodayDay
    {
        get => (int)GetValue(TodayDayProperty);
        set => SetValue(TodayDayProperty, value);
    }

    public bool HighlightToday
    {
        get => (bool)GetValue(HighlightTodayProperty);
        set => SetValue(HighlightTodayProperty, value);
    }

    public IEnumerable? Sections
    {
        get => (IEnumerable?)GetValue(SectionsProperty);
        set => SetValue(SectionsProperty, value);
    }

    public DateTime? TermStart
    {
        get => (DateTime?)GetValue(TermStartProperty);
        set => SetValue(TermStartProperty, value);
    }

    public Course? SelectedCourse
    {
        get => (Course?)GetValue(SelectedCourseProperty);
        set => SetValue(SelectedCourseProperty, value);
    }

    // ------------------------------------------------------------------ 事件
    public event Action<Course>? CourseClicked;
    public event Action<Course>? CourseDoubleClicked;
    public event Action<int, int>? SlotDoubleClicked; // (weekday, section)

    // ------------------------------------------------------------------
    private readonly List<SectionTime> _sectionList = new();

    public WeekBoard()
    {
        Background = Brushes.White;
        MouseLeftButtonDown += OnBoardMouseDown;
        SizeChanged += (_, _) => Rebuild();
        Loaded += (_, _) => Rebuild();
    }

    private static void VisualAffect(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((WeekBoard)d).Rebuild();

    private static Brush Freeze(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }

    // ------------------------------------------------------------- 数据视图
    private int RowCount
    {
        get
        {
            int n = 13;
            if (_sectionList.Count > 0)
            {
                n = Math.Max(n, _sectionList.Max(s => s.Index));
            }
            return Math.Min(n, 16);
        }
    }

    public void Rebuild()
    {
        Children.Clear();
        double w = ActualWidth;
        double totalHeight = HeaderHeight + RowCount * RowHeight;
        Height = totalHeight;
        if (w <= TimeAxisWidth + 20)
        {
            return;
        }

        RefreshSections();
        double gridW = w - TimeAxisWidth;
        double colW = gridW / 7;

        DrawBackground(gridW, colW);
        DrawHeader(gridW, colW);
        DrawAxis();
        DrawGridLines(gridW, colW);
        DrawCourseBlocks(gridW, colW);
    }

    private void RefreshSections()
    {
        _sectionList.Clear();
        if (Sections is IEnumerable<SectionTime> secs)
        {
            _sectionList.AddRange(secs.OrderBy(s => s.Index));
        }
        else if (Sections is not null)
        {
            foreach (var s in Sections)
            {
                if (s is SectionTime st) _sectionList.Add(st);
            }
        }
        if (_sectionList.Count == 0)
        {
            _sectionList.AddRange(SectionTime.Default());
        }
    }

    private SectionTime? SectionOf(int index) => _sectionList.FirstOrDefault(s => s.Index == index);

    // ------------------------------------------------------------- 静态绘制
    private void DrawBackground(double gridW, double colW)
    {
        // 整体白底 + 时间轴底色
        AddRect(0, 0, TimeAxisWidth, Height, AxisBrush);
        // 选中日 / 今日列着色
        if (HighlightToday && TodayDay is >= 1 and <= 7)
        {
            AddRect(TimeAxisWidth + (TodayDay - 1) * colW, 0, colW, Height, TodayColumnBrush);
        }
        if (SelectedDay is >= 1 and <= 7 && !(HighlightToday && SelectedDay == TodayDay))
        {
            AddRect(TimeAxisWidth + (SelectedDay - 1) * colW, 0, colW, Height, SelectedColumnBrush);
        }
        _ = gridW;
    }

    private void DrawHeader(double gridW, double colW)
    {
        // 顶部表头：周X + 日期（日期随所选周滑动）
        for (int day = 1; day <= 7; day++)
        {
            double x = TimeAxisWidth + (day - 1) * colW;
            var panel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Width = colW - 8,
            };

            string date = "";
            if (TermStart is { } ts)
            {
                date = ts.AddDays((SelectedWeek - 1) * 7 + day - 1).ToString("MM-dd");
            }
            bool isToday = HighlightToday && day == TodayDay;
            bool isSelected = day == SelectedDay;

            var title = new TextBlock
            {
                Text = $"{WeekdayName(day)}{(isToday ? " · 今天" : "")}",
                FontSize = 12.5,
                FontWeight = FontWeights.DemiBold,
                Foreground = isToday ? TodayBrush : (isSelected ? BrushFrom(0x2F, 0x6F, 0xED) : TextBrush),
                TextAlignment = TextAlignment.Center,
            };
            panel.Children.Add(title);
            if (date.Length > 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = date,
                    FontSize = 10.5,
                    Foreground = SubTextBrush,
                    TextAlignment = TextAlignment.Center,
                });
            }
            Children.Add(panel);
            SetLeft(panel, x + 4);
            SetTop(panel, 4);
            SetZIndex(panel, 6);

            // 选中日顶条
            if (isSelected)
            {
                AddRect(x + 6, 2, colW - 12, 3, BrushFrom(0x2F, 0x6F, 0xED));
            }
        }

        // 表头底线
        AddRect(0, HeaderHeight - 1, TimeAxisWidth + gridW, 1.2, HeaderLineBrush);
    }

    private void DrawAxis()
    {
        for (int i = 0; i < RowCount; i++)
        {
            SectionTime? sec = SectionOf(i + 1);
            if (sec is null) continue;
            double y = HeaderHeight + i * RowHeight;

            // 节次名 + 开始时间在行带内垂直居中：课程块占满行带（上下留 BlockGap），
            // 居中后“第N节”与块内课程名一行、“开始时间”与块内时间一行上下对齐，
            // 消除原先时间贴行顶、与课程块错位约 15px 的观感。
            bool hasTime = !string.IsNullOrEmpty(sec.Start);
            double labelLine = 16.2, timeLine = 14, gap = 1.5; // YaHei UI 行高 ≈ 1.47×字号
            double pairH = labelLine + (hasTime ? gap + timeLine : 0);
            double top = y + (RowHeight - pairH) / 2.0;

            AddText(SecText(sec.Label), 11, FontWeights.Medium, TextBrush,
                TimeAxisWidth - 8, top, rightAligned: true);
            if (hasTime)
            {
                AddText(sec.Start, 9.5, FontWeights.Normal, SubTextBrush,
                    TimeAxisWidth - 8, top + labelLine + gap, rightAligned: true);
            }
        }
    }

    private void DrawGridLines(double gridW, double colW)
    {
        // 竖线
        for (int day = 0; day <= 7; day++)
        {
            double x = TimeAxisWidth + day * colW;
            AddRect(x, HeaderHeight, 1, RowCount * RowHeight, LineBrush);
        }
        // 横线
        for (int i = 0; i <= RowCount; i++)
        {
            double y = HeaderHeight + i * RowHeight;
            AddRect(TimeAxisWidth, y, gridW, 1, LineBrush);
        }
    }

    // ------------------------------------------------------------- 课程块
    private void DrawCourseBlocks(double gridW, double colW)
    {
        if (Courses is null)
        {
            return;
        }
        var all = Courses.OfType<Course>().Where(c => c.ContainsWeek(SelectedWeek)).ToList();

        foreach (int day in Enumerable.Range(1, 7))
        {
            var dayCourses = all.Where(c => c.Weekday == day)
                                .OrderBy(c => c.StartSection)
                                .ThenByDescending(c => c.EndSection)
                                .ToList();
            if (dayCourses.Count == 0) continue;

            // 贪心分栏：按开始时间扫掠，能复用空档则复用，否则开新栏
            int[] laneEnd = Array.Empty<int>();
            int laneCount = 0;
            var lanes = new int[dayCourses.Count];
            for (int k = 0; k < dayCourses.Count; k++)
            {
                Course c = dayCourses[k];
                int fit = -1;
                for (int li = 0; li < laneCount; li++)
                {
                    if (laneEnd[li] < c.StartSection)
                    {
                        fit = li;
                        break;
                    }
                }
                if (fit < 0)
                {
                    fit = laneCount++;
                    Array.Resize(ref laneEnd, laneCount);
                }
                laneEnd[fit] = c.EndSection;
                lanes[k] = fit;
            }

            double colX = TimeAxisWidth + (day - 1) * colW;
            for (int k = 0; k < dayCourses.Count; k++)
            {
                Course c = dayCourses[k];
                double span = Math.Max(1, (c.EndSection - c.StartSection + 1) * RowHeight - 2 * BlockGap);
                double wBlock = (colW - 2 * BlockGap - (laneCount - 1) * 2) / laneCount;
                double x = colX + BlockGap + lanes[k] * (wBlock + 2);
                double y = HeaderHeight + (c.StartSection - 1) * RowHeight + BlockGap;

                var block = CreateBlock(c, span, wBlock);
                Children.Add(block);
                SetLeft(block, x);
                SetTop(block, y);
                SetZIndex(block, 20);
            }
        }
        _ = gridW;
    }

    private Border CreateBlock(Course c, double height, double width)
    {
        var solid = CoursePalette.SolidBrush(c.Name, c.Teacher);
        var soft = CoursePalette.SoftBrush(c.Name, c.Teacher);
        bool selected = SelectedCourse?.Id == c.Id;

        var border = new Border
        {
            Width = Math.Max(46, width - 4),
            // 必须显式设高：span = (end-start+1)*RowHeight - 2*gap。缺省时 Border 按内容
            // 自适应成单行高度，6-8 节的课只画 1 行，与左侧时间轴完全对不上。
            Height = Math.Max(RowHeight - 2 * BlockGap, height),
            Background = soft,
            BorderBrush = solid,
            BorderThickness = new Thickness(selected ? 2.2 : 1.2),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(5, 4, 4, 3),
            Cursor = Cursors.Hand,
            Tag = c,
            ToolTip = BuildTooltip(c),
        };

        var panel = new StackPanel();
        var nameText = new TextBlock
        {
            Text = c.Name,
            FontSize = 12.5,
            FontWeight = FontWeights.DemiBold,
            Foreground = TextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        panel.Children.Add(nameText);

        var lines = new List<string> { c.SectionRangeText };
        if (height >= RowHeight * 1.5)
        {
            string t = _sectionList.TimeText(c.StartSection, c.EndSection);
            lines.Add(string.IsNullOrEmpty(t) ? (c.Room.Length > 0 ? c.Room : c.Teacher) : $"{t} · {c.Room}");
        }
        if (height >= RowHeight * 2.4)
        {
            lines.Add($"{c.Teacher} ｜ {c.Rule.Display}");
        }
        foreach (string line in lines.Take(2))
        {
            panel.Children.Add(new TextBlock
            {
                Text = line,
                FontSize = 10.3,
                Foreground = SubTextBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }

        border.Child = panel;

        border.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (e.ClickCount >= 2)
            {
                CourseDoubleClicked?.Invoke(c);
            }
            else
            {
                CourseClicked?.Invoke(c);
            }
        };
        return border;
    }

    private object BuildTooltip(Course c)
    {
        string t = _sectionList.TimeText(c.StartSection, c.EndSection);
        string when = $"第 {WeekdayName(c.Weekday)} · 第{c.StartSection}-{c.EndSection}节";
        if (t.Length > 0) when += $"  {t}";
        return $"{c.Name}\n教师：{(c.Teacher.Length > 0 ? c.Teacher : "—")}\n地点：{(c.Room.Length > 0 ? c.Room : "—")}\n时间：{when}\n周次：{c.Rule.Display}";
    }

    // ------------------------------------------------------------- 事件：空白双击新建
    private void OnBoardMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2 || e.OriginalSource != this)
        {
            return;
        }
        Point p = e.GetPosition(this);
        if (p.X < TimeAxisWidth || p.Y < HeaderHeight)
        {
            return;
        }
        double gridW = Math.Max(1, ActualWidth - TimeAxisWidth);
        double colW = gridW / 7;
        int day = Math.Clamp((int)((p.X - TimeAxisWidth) / colW) + 1, 1, 7);
        int section = Math.Clamp((int)((p.Y - HeaderHeight) / RowHeight) + 1, 1, RowCount);
        e.Handled = true;
        SlotDoubleClicked?.Invoke(day, section);
    }

    // ------------------------------------------------------------- 工具
    private static string WeekdayName(int day) =>
        new[] { "周一", "周二", "周三", "周四", "周五", "周六", "周日" }[Math.Clamp(day, 1, 7) - 1];

    private static string SecText(string label) => label.Length > 0 ? $"第{label}节" : "";

    private static SolidColorBrush BrushFrom(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

    private void AddRect(double x, double y, double w, double h, Brush fill)
    {
        var r = new Rectangle { Width = Math.Max(0.5, w), Height = Math.Max(0.5, h), Fill = fill };
        Children.Add(r);
        SetLeft(r, x);
        SetTop(r, y);
        SetZIndex(r, 1);
    }

    private void AddText(string text, double fontSize, FontWeight weight, Brush fg,
        double rightEdge, double top, bool rightAligned)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            FontWeight = weight,
            Foreground = fg,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Children.Add(tb);
        double ppd = 1.0;
        try
        {
            ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        }
        catch (InvalidOperationException)
        {
            // 未入视觉树时退回 96dpi 假设
        }
        var sz = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(tb.FontFamily, FontStyles.Normal, weight, FontStretches.Normal),
            fontSize, fg, ppd);
        if (rightAligned)
        {
            SetLeft(tb, rightEdge - sz.Width);
        }
        else
        {
            SetLeft(tb, rightEdge);
        }
        SetTop(tb, top);
        SetZIndex(tb, 5);
    }
}

internal static class SectionTimeExt
{
    public static string TimeText(this IEnumerable<SectionTime>? self, int startIndex, int endIndex)
    {
        if (self is null) return "";
        var list = self.ToList();
        var s = list.FirstOrDefault(x => x.Index == startIndex);
        var e = list.FirstOrDefault(x => x.Index == endIndex);
        if (s is not null && e is not null && s.Start.Length > 0 && e.End.Length > 0)
        {
            return $"{s.Start}-{e.End}";
        }
        return "";
    }
}
