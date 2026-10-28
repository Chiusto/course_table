using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NcuCourseTable.Desktop.Interop;
using NcuCourseTable.Desktop.Models;
using NcuCourseTable.Desktop.Services;

namespace NcuCourseTable.Desktop.Views;

/// <summary>
/// 周视图画布（原生 WPF，自绘网格与日程块）。
///
/// Apple 风格视觉：系统字体、发丝级分隔线、柔和色块、胶囊今日标记与红色现在线。
/// 布局：
///   左上角为节次时间轴，右侧 7 列为周一~周日；
///   日程块按 (start_section-1)*RowHeight 纵向定位、跨 end-start+1 行。
/// 难点处理：
///   1) 同日重叠日程 -> 贪心分栏（lane），每条日程占 1/N 列宽并排显示；
///   2) 节次纵向合并 -> 与 ncu_sdk parser 相同语义，一条记录一个块；
///   3) 点击/双击 -> 选中详情、双击编辑、空白双击按落点新建日程；
///   4) 今日列绘制实时时间线，30 秒刷新一次。
/// </summary>
public sealed class WeekBoard : Canvas
{
    public const double HeaderHeight = 54;
    public const double TimeAxisWidth = 64;
    public const double RowHeight = 50;
    public const double BlockGap = 3.0;

    // Apple 风格配色（与 Theme.xaml 保持一致）
    private static readonly Brush GutterBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFC)));
    private static readonly Brush WeekendBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFB, 0xFB, 0xFD)));
    private static readonly Brush RuleFaintBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF1, 0xF1, 0xF5)));
    private static readonly Brush RuleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEE)));
    private static readonly Brush RuleStrongBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xDA, 0xDA, 0xE0)));
    private static readonly Brush InkBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1D, 0x1D, 0x1F)));
    private static readonly Brush InkSoftBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x73)));
    private static readonly Brush InkFaintBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9F)));
    private static readonly Brush AccentBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xFF)));
    private static readonly Brush AccentSoftBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xF3, 0xFF)));
    private static readonly Brush DangerBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)));
    private static readonly Brush TodayTintBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x0E, 0x00, 0x7A, 0xFF)));
    private static readonly Brush SelectedTintBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x06, 0x00, 0x00, 0x00)));

    private static readonly FontFamily AppFont = new("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");

    // ------------------------------------------------------------------ 依赖属性
    public static readonly DependencyProperty EventsProperty = DependencyProperty.Register(
        nameof(Events), typeof(IEnumerable), typeof(WeekBoard), new PropertyMetadata(null, VisualAffect));

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

    public static readonly DependencyProperty SelectedEventProperty = DependencyProperty.Register(
        nameof(SelectedEvent), typeof(ScheduleEvent), typeof(WeekBoard), new PropertyMetadata(null, VisualAffect));

    public IEnumerable? Events
    {
        get => (IEnumerable?)GetValue(EventsProperty);
        set => SetValue(EventsProperty, value);
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

    public ScheduleEvent? SelectedEvent
    {
        get => (ScheduleEvent?)GetValue(SelectedEventProperty);
        set => SetValue(SelectedEventProperty, value);
    }

    // ------------------------------------------------------------------ 事件
    public event Action<ScheduleEvent>? EventClicked;
    public event Action<ScheduleEvent>? EventDoubleClicked;
    public event Action<int, int>? SlotDoubleClicked; // (weekday, section)

    // ------------------------------------------------------------------
    private readonly List<SectionTime> _sectionList = new();
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromSeconds(30) };

    public WeekBoard()
    {
        Background = Brushes.Transparent;
        MouseLeftButtonDown += OnBoardMouseDown;
        SizeChanged += (_, _) => Rebuild();
        Loaded += (_, _) => { _ticker.Start(); Rebuild(); };
        Unloaded += (_, _) => _ticker.Stop();
        _ticker.Tick += (_, _) => { if (IsLoaded) Rebuild(); };
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
        DrawGridLines(gridW, colW);
        DrawHeader(gridW, colW);
        DrawAxis();
        DrawEventBlocks(gridW, colW);
        DrawNowLine(colW);
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
        AddRect(0, 0, TimeAxisWidth, Height, GutterBrush);
        for (int day = 6; day <= 7; day++)
        {
            AddRect(TimeAxisWidth + (day - 1) * colW, 0, colW, Height, WeekendBrush);
        }
        if (HighlightToday && TodayDay is >= 1 and <= 7)
        {
            AddRect(TimeAxisWidth + (TodayDay - 1) * colW, 0, colW, Height, TodayTintBrush);
        }
        if (SelectedDay is >= 1 and <= 7 && !(HighlightToday && SelectedDay == TodayDay))
        {
            AddRect(TimeAxisWidth + (SelectedDay - 1) * colW, 0, colW, Height, SelectedTintBrush);
        }
        _ = gridW;
    }

    private void DrawHeader(double gridW, double colW)
    {
        for (int day = 1; day <= 7; day++)
        {
            double x = TimeAxisWidth + (day - 1) * colW;
            bool isToday = HighlightToday && day == TodayDay;

            if (isToday)
            {
                double pillW = Math.Max(46, colW - 18);
                var pill = new Border
                {
                    Width = pillW,
                    Height = 42,
                    CornerRadius = new CornerRadius(10),
                    Background = AccentSoftBrush,
                    IsHitTestVisible = false,
                };
                Children.Add(pill);
                SetLeft(pill, x + (colW - pillW) / 2);
                SetTop(pill, 6);
                SetZIndex(pill, 4);
            }

            var panel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Width = Math.Max(40, colW - 12),
                Margin = new Thickness(0, 9, 0, 0),
            };

            string date = "";
            if (TermStart is { } ts)
            {
                date = ts.AddDays((SelectedWeek - 1) * 7 + day - 1).ToString("MM-dd");
            }

            var dayText = new TextBlock
            {
                Text = WeekdayName(day),
                FontFamily = AppFont,
                FontSize = 12.5,
                FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Medium,
                Foreground = isToday ? AccentBrush : InkBrush,
                TextAlignment = TextAlignment.Center,
            };
            Typography.SetNumeralAlignment(dayText, FontNumeralAlignment.Tabular);
            panel.Children.Add(dayText);

            if (date.Length > 0)
            {
                var dateText = new TextBlock
                {
                    Text = date,
                    FontFamily = AppFont,
                    FontSize = 10,
                    Foreground = isToday ? AccentBrush : InkFaintBrush,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 2, 0, 0),
                };
                Typography.SetNumeralAlignment(dateText, FontNumeralAlignment.Tabular);
                panel.Children.Add(dateText);
            }

            Children.Add(panel);
            SetLeft(panel, x + 6);
            SetTop(panel, 8);
            SetZIndex(panel, 6);
        }

        AddText("时间", 9.5, FontWeights.Medium, InkFaintBrush,
            TimeAxisWidth - 10, 11, rightAligned: true, family: AppFont);
        AddRect(0, HeaderHeight - 1, TimeAxisWidth + gridW, 1, RuleBrush);
    }

    private void DrawAxis()
    {
        for (int i = 0; i < RowCount; i++)
        {
            SectionTime? sec = SectionOf(i + 1);
            if (sec is null) continue;
            double y = HeaderHeight + i * RowHeight;

            bool hasTime = !string.IsNullOrEmpty(sec.Start);
            AddText(hasTime ? sec.Start : SecText(sec.Label), hasTime ? 11.5 : 10,
                hasTime ? FontWeights.Medium : FontWeights.Normal,
                hasTime ? InkSoftBrush : InkFaintBrush,
                TimeAxisWidth - 10, y + 8, rightAligned: true, family: AppFont);

            if (hasTime)
            {
                AddText(sec.Label, 9, FontWeights.Normal, InkFaintBrush,
                    TimeAxisWidth - 10, y + 26, rightAligned: true, family: AppFont);
            }
        }
    }

    private void DrawGridLines(double gridW, double colW)
    {
        for (int day = 0; day <= 7; day++)
        {
            double x = TimeAxisWidth + day * colW;
            AddRect(x, HeaderHeight, 1, RowCount * RowHeight, RuleFaintBrush);
        }
        for (int i = 0; i <= RowCount; i++)
        {
            double y = HeaderHeight + i * RowHeight;
            AddRect(TimeAxisWidth, y, gridW, 1, RuleBrush);
        }
        // 午间分隔：第 5 节之后一条稍重的发丝线
        AddRect(TimeAxisWidth, HeaderHeight + 5 * RowHeight, gridW, 1, RuleStrongBrush);
    }

    // ------------------------------------------------------------- 日程块
    private void DrawEventBlocks(double gridW, double colW)
    {
        if (Events is null)
        {
            return;
        }
        var all = Events.OfType<ScheduleEvent>()
            .Where(c => c.OccursInWeek(SelectedWeek, TermStart))
            .ToList();

        foreach (int day in Enumerable.Range(1, 7))
        {
            var dayEvents = all.Where(c => c.Weekday == day)
                                .OrderBy(c => c.StartSection)
                                .ThenByDescending(c => c.EndSection)
                                .ToList();
            if (dayEvents.Count == 0) continue;

            // 贪心分栏：按开始时间扫掠，能复用空档则复用，否则开新栏
            int[] laneEnd = Array.Empty<int>();
            int laneCount = 0;
            var lanes = new int[dayEvents.Count];
            for (int k = 0; k < dayEvents.Count; k++)
            {
                ScheduleEvent c = dayEvents[k];
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
            for (int k = 0; k < dayEvents.Count; k++)
            {
                ScheduleEvent c = dayEvents[k];
                double height = Math.Max(1, (c.EndSection - c.StartSection + 1) * RowHeight - 2 * BlockGap);
                double wBlock = (colW - 2 * BlockGap - (laneCount - 1) * 2) / laneCount;
                double x = colX + BlockGap + lanes[k] * (wBlock + 2);
                double y = HeaderHeight + (c.StartSection - 1) * RowHeight + BlockGap;

                var block = CreateBlock(c, height, wBlock);
                Children.Add(block);
                SetLeft(block, x);
                SetTop(block, y);
                SetZIndex(block, 20);
            }
        }
        _ = gridW;
    }

    private FrameworkElement CreateBlock(ScheduleEvent c, double height, double width)
    {
        Brush solid = EventPalette.SolidBrush(c);
        Brush soft = EventPalette.SoftBrush(c);
        Brush edge = EventPalette.EdgeBrush(c);
        bool selected = SelectedEvent?.Id == c.Id;
        bool compact = height < RowHeight * 1.55;
        bool roomLine = height >= RowHeight * 1.85;

        var scale = new ScaleTransform(1, 1);
        var root = new Grid
        {
            Width = Math.Max(46, width - 2),
            Height = height,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Tag = c,
            ToolTip = BuildTooltip(c),
            SnapsToDevicePixels = true,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = scale,
        };

        root.Children.Add(new Border
        {
            Background = soft,
            BorderBrush = edge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
        });

        root.Children.Add(new Border
        {
            Width = 3.5,
            Background = solid,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(9, 0, 0, 9),
            Margin = new Thickness(0, 1, 0, 1),
            IsHitTestVisible = false,
        });

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = c.Title,
            FontFamily = AppFont,
            FontSize = compact ? 11.8 : 12.3,
            FontWeight = FontWeights.SemiBold,
            Foreground = InkBrush,
            TextWrapping = compact ? TextWrapping.NoWrap : TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = compact ? 17 : 35,
            LineHeight = 16.5,
        });

        if (!compact)
        {
            string time = _sectionList.TimeText(c.StartSection, c.EndSection);
            string meta = time.Length > 0
                ? $"{c.StartSection}–{c.EndSection} 节 · {time}"
                : c.SectionRangeText;
            var metaText = new TextBlock
            {
                Text = meta,
                FontFamily = AppFont,
                FontSize = 9.8,
                Foreground = InkSoftBrush,
                Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Typography.SetNumeralAlignment(metaText, FontNumeralAlignment.Tabular);
            panel.Children.Add(metaText);

            if (roomLine)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = c.DisplayLocation,
                    FontFamily = AppFont,
                    FontSize = 9.8,
                    Foreground = InkFaintBrush,
                    Margin = new Thickness(0, 1, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }
        }

        var content = new Grid
        {
            Margin = new Thickness(11, 7, 8, 6),
            Children = { panel },
        };
        root.Children.Add(content);

        if (selected)
        {
            root.Children.Add(new Border
            {
                BorderBrush = AccentBrush,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(9),
                IsHitTestVisible = false,
            });
        }

        // 悬停轻微浮起、按下即时收缩——反馈发生在 pointer-down，不等 click
        root.MouseEnter += (_, _) => SpringScale(scale, 1.012, 0.18);
        root.MouseLeave += (_, _) => SpringScale(scale, 1.0, 0.2);
        root.PreviewMouseLeftButtonDown += (_, _) => SpringScale(scale, 0.982, 0.1);
        root.PreviewMouseLeftButtonUp += (_, _) => SpringScale(scale, root.IsMouseOver ? 1.012 : 1.0, 0.18);

        root.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (e.ClickCount >= 2)
            {
                EventDoubleClicked?.Invoke(c);
            }
            else
            {
                EventClicked?.Invoke(c);
            }
        };
        return root;
    }

    private static void SpringScale(ScaleTransform scale, double to, double response)
    {
        Motion.SpringTo(scale, ScaleTransform.ScaleXProperty, to, 1.0, response, tolerance: 0.0008);
        Motion.SpringTo(scale, ScaleTransform.ScaleYProperty, to, 1.0, response, tolerance: 0.0008);
    }

    private object BuildTooltip(ScheduleEvent c)
    {
        string t = _sectionList.TimeText(c.StartSection, c.EndSection);
        string when = $"第 {WeekdayName(c.Weekday)} · 第{c.StartSection}-{c.EndSection}节";
        if (t.Length > 0) when += $"  {t}";
        return $"{c.Title}\n分类：{c.Category}\n" +
               $"参与人：{(c.DisplayTeacher.Length > 0 ? c.DisplayTeacher : "—")}\n" +
               $"地点：{(c.DisplayLocation.Length > 0 ? c.DisplayLocation : "—")}\n" +
               $"时间：{when}\n安排：{c.ScheduleText}";
    }

    // ------------------------------------------------------------- 今日时间线
    private void DrawNowLine(double colW)
    {
        if (!HighlightToday || TodayDay is < 1 or > 7) return;

        DateTime now = DateTime.Now;
        int mins = now.Hour * 60 + now.Minute;
        SectionTime? sec = _sectionList.FirstOrDefault(s =>
        {
            int a = MinutesOf(s.Start);
            int b = MinutesOf(s.End);
            return a >= 0 && b > a && mins >= a && mins < b;
        });
        if (sec is null) return;

        int start = MinutesOf(sec.Start);
        int end = MinutesOf(sec.End);
        double frac = Math.Clamp((mins - start) / (double)(end - start), 0, 1);
        double y = HeaderHeight + (sec.Index - 1) * RowHeight + frac * RowHeight;
        double x = TimeAxisWidth + (TodayDay - 1) * colW;

        var line = new Rectangle
        {
            Width = Math.Max(1, colW - 8),
            Height = 1.5,
            Fill = DangerBrush,
            RadiusX = 0.75,
            RadiusY = 0.75,
            IsHitTestVisible = false,
        };
        Children.Add(line);
        SetLeft(line, x + 4);
        SetTop(line, y - 0.75);
        SetZIndex(line, 30);

        var dot = new Ellipse { Width = 6, Height = 6, Fill = DangerBrush, IsHitTestVisible = false };
        Children.Add(dot);
        SetLeft(dot, x + 1);
        SetTop(dot, y - 3);
        SetZIndex(dot, 31);

        var tag = new Border
        {
            Background = DangerBrush,
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(6, 1.5, 6, 1.5),
            Child = new TextBlock
            {
                Text = $"现在 {now:HH:mm}",
                FontFamily = AppFont,
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
            },
            IsHitTestVisible = false,
        };
        Children.Add(tag);
        SetLeft(tag, x + 10);
        SetTop(tag, y - 10);
        SetZIndex(tag, 32);
    }

    private static int MinutesOf(string hhmm)
    {
        string[] parts = (hhmm ?? "").Split(':');
        if (parts.Length == 2 && int.TryParse(parts[0], out int h) && int.TryParse(parts[1], out int m))
        {
            return h * 60 + m;
        }
        return -1;
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

    private void AddRect(double x, double y, double w, double h, Brush fill)
    {
        var r = new Rectangle { Width = Math.Max(0.5, w), Height = Math.Max(0.5, h), Fill = fill };
        Children.Add(r);
        SetLeft(r, x);
        SetTop(r, y);
        SetZIndex(r, 1);
    }

    private void AddText(string text, double fontSize, FontWeight weight, Brush fg,
        double rightEdge, double top, bool rightAligned, FontFamily? family = null)
    {
        var fam = family ?? AppFont;
        var tb = new TextBlock
        {
            Text = text,
            FontFamily = fam,
            FontSize = fontSize,
            FontWeight = weight,
            Foreground = fg,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
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
            new Typeface(fam, FontStyles.Normal, weight, FontStretches.Normal),
            fontSize, fg, ppd);
        SetLeft(tb, rightAligned ? rightEdge - sz.Width : rightEdge);
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
