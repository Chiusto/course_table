using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NcuCourseTable.Desktop.Interop;
using NcuCourseTable.Desktop.Models;
using NcuCourseTable.Desktop.Services;

namespace NcuCourseTable.Desktop.Views;

/// <summary>
/// 日程新增/编辑对话框：直接修改调用方传入的 draft，保存成功返回 DialogResult=true。
/// 支持每周重复与单次日程、分类、颜色、备注，并软性提示时段冲突。
/// </summary>
public partial class EventDialog : Window
{
    private static readonly string[] DayNames =
        { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };

    private readonly ScheduleEvent _draft;
    private readonly List<SectionTime> _sections;
    private readonly List<ScheduleEvent> _others;
    private readonly DateTime? _termStart;
    private readonly int _totalWeeks;
    private readonly int _week;
    private string _colorKey;

    public EventDialog(ScheduleEvent draft, IReadOnlyList<SectionTime> sections,
        IReadOnlyList<ScheduleEvent> others, DateTime? termStart, int totalWeeks,
        int selectedWeek, string title = "日程")
    {
        InitializeComponent();
        Title = title;
        _draft = draft;
        _sections = sections.OrderBy(s => s.Index).ToList();
        _others = others.Where(o => o.Id != draft.Id).ToList();
        _termStart = termStart;
        _totalWeeks = Math.Max(1, totalWeeks);
        _week = Math.Clamp(selectedWeek, 1, _totalWeeks);
        _colorKey = (draft.ColorKey ?? "").Trim().ToLowerInvariant();

        var categories = EventCategories.All.ToList();
        if (draft.Category.Length > 0 && !categories.Contains(draft.Category))
        {
            categories.Insert(0, draft.Category);
        }
        CategoryBox.ItemsSource = categories;
        CategoryBox.SelectedItem = categories.Contains(draft.Category)
            ? draft.Category : EventCategories.Default;

        DayBox.ItemsSource = DayNames;
        DayBox.SelectedIndex = Math.Clamp(draft.Weekday - 1, 0, 6);

        RepeatBox.ItemsSource = new[] { "每周重复", "仅一次" };
        RepeatBox.SelectedIndex = draft.IsOneOff ? 1 : 0;

        var startItems = _sections
            .Select(s => s.Start.Length > 0 ? $"第{s.Label}节 {s.Start}" : s.Display).ToList();
        var endItems = _sections
            .Select(s => s.End.Length > 0 ? $"第{s.Label}节 {s.End}" : s.Display).ToList();
        StartBox.ItemsSource = startItems;
        EndBox.ItemsSource = endItems;
        SelectSection(StartBox, draft.StartSection);
        SelectSection(EndBox, draft.EndSection);

        TitleBox.Text = draft.Title;
        LocationBox.Text = draft.Location;
        TeacherBox.Text = draft.Teacher;
        NotesBox.Text = draft.Notes;
        WeeksBox.Text = draft.WeeksRaw;
        DateBox.Text = draft.OnDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            ?? DefaultOneOffDate().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        BuildColorPanel();
        SetAdvancedExpanded(HasOptionalData(draft));
        ApplyRepeatMode();

        TitleBox.TextChanged += (_, _) => UpdateHint();
        WeeksBox.TextChanged += (_, _) => UpdateHint();
        DateBox.TextChanged += (_, _) => UpdateHint();
        StartBox.SelectionChanged += (_, _) => UpdateHint();
        EndBox.SelectionChanged += (_, _) => UpdateHint();
        DayBox.SelectionChanged += (_, _) => UpdateHint();
        RepeatBox.SelectionChanged += (_, _) => { ApplyRepeatMode(); UpdateHint(); };

        Loaded += (_, _) =>
        {
            UpdateHint();
            Motion.ScaleFadeIn(Root, 0.96, 0.34);
            TitleBox.Focus();
        };
    }

    private DateTime DefaultOneOffDate()
    {
        if (_termStart is { } start)
        {
            return start.AddDays((_week - 1) * 7 + Math.Clamp(_draft.Weekday, 1, 7) - 1);
        }
        return DateTime.Today;
    }

    private bool HasOptionalData(ScheduleEvent draft) =>
        draft.Category != EventCategories.Default ||
        !string.IsNullOrWhiteSpace(draft.ColorKey) ||
        !string.IsNullOrWhiteSpace(draft.Location) ||
        !string.IsNullOrWhiteSpace(draft.Teacher) ||
        !string.IsNullOrWhiteSpace(draft.Notes) ||
        (!string.IsNullOrWhiteSpace(draft.WeeksRaw) &&
         draft.WeeksRaw != $"1-{_totalWeeks}周");

    private void SetAdvancedExpanded(bool expanded)
    {
        AdvancedPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        AdvancedBtn.Content = expanded ? "收起选项" : "更多选项";
    }

    private void OnToggleAdvanced(object sender, RoutedEventArgs e) =>
        SetAdvancedExpanded(AdvancedPanel.Visibility != Visibility.Visible);

    private bool IsOneOff => RepeatBox.SelectedIndex == 1;

    private void ApplyRepeatMode()
    {
        bool once = IsOneOff;
        WeeklyDayPanel.Visibility = once ? Visibility.Collapsed : Visibility.Visible;
        WeeksPanel.Visibility = once ? Visibility.Collapsed : Visibility.Visible;
        OnceDatePanel.Visibility = once ? Visibility.Visible : Visibility.Collapsed;
        if (once && DateTime.TryParseExact(DateBox.Text, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d))
        {
            DayBox.SelectedIndex = ScheduleEvent.WeekdayOf(d) - 1;
        }
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnTodayDate(object sender, RoutedEventArgs e)
    {
        DateBox.Text = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private void SelectSection(ComboBox box, int sectionIndex)
    {
        int pos = _sections.FindIndex(s => s.Index == sectionIndex);
        box.SelectedIndex = pos >= 0 ? pos : 0;
    }

    private int SelectedSection(ComboBox box) =>
        box.SelectedIndex >= 0 && box.SelectedIndex < _sections.Count
            ? _sections[box.SelectedIndex].Index
            : 1;

    private void BuildColorPanel()
    {
        ColorPanel.Children.Clear();
        ColorPanel.Children.Add(MakeSwatch("", "自动", null));
        foreach (EventPalette.Swatch swatch in EventPalette.Swatches)
        {
            ColorPanel.Children.Add(MakeSwatch(swatch.Key, swatch.Name, swatch.Solid));
        }
    }

    private FrameworkElement MakeSwatch(string key, string name, string? solidHex)
    {
        bool selected = string.Equals(key, _colorKey, StringComparison.OrdinalIgnoreCase);
        var host = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            BorderThickness = new Thickness(selected ? 2 : 1),
            BorderBrush = selected
                ? (Brush)FindResource("LabelPrimaryBrush")
                : (Brush)FindResource("SeparatorStrongBrush"),
            Margin = new Thickness(0, 0, 4, 4),
            Cursor = Cursors.Hand,
            ToolTip = name,
            Background = Brushes.Transparent,
        };
        var swatch = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (solidHex is null)
        {
            swatch.Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromRgb(0x00, 0x7A, 0xFF), 0),
                    new(Color.FromRgb(0xAF, 0x52, 0xDE), 0.5),
                    new(Color.FromRgb(0xFF, 0x95, 0x00), 1),
                }, 45);
        }
        else
        {
            swatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(solidHex));
        }
        host.Child = swatch;
        host.MouseLeftButtonDown += (_, _) =>
        {
            _colorKey = key;
            BuildColorPanel();
        };
        return host;
    }

    private void UpdateHint()
    {
        var sb = new System.Text.StringBuilder();
        bool ok = true;

        int start = SelectedSection(StartBox);
        int end = SelectedSection(EndBox);
        if (end < start)
        {
            ok = false;
            sb.Append("结束节次不能早于起始节次");
        }

        WeekRule? rule = null;
        int clashWeek = -1;
        string? onceError = null;
        if (IsOneOff)
        {
            if (!DateTime.TryParseExact(DateBox.Text.Trim(), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
            {
                ok = false;
                sb.Append("日期格式应为 yyyy-MM-dd");
            }
            else
            {
                clashWeek = WeekOf(date, out onceError);
                if (onceError is not null)
                {
                    ok = false;
                    sb.Append(onceError);
                }
                else
                {
                    sb.Append($"{date:yyyy年M月d日}（{ScheduleEvent.WeekdayName(ScheduleEvent.WeekdayOf(date))}）")
                      .Append(" · 仅此一次");
                    if (clashWeek > 0) sb.Append(" · 第 ").Append(clashWeek).Append(" 周");
                }
            }
        }
        else
        {
            try
            {
                rule = WeekRule.Parse(WeeksBox.Text);
                int endWeek = Math.Max(rule.EndWeek, _totalWeeks);
                if (rule.StartWeek < 1 || endWeek > 60)
                {
                    ok = false;
                    sb.Append("周次超出合理范围");
                }
                else
                {
                    sb.Append("安排：第 ").Append(rule.StartWeek).Append('-').Append(rule.EndWeek).Append(" 周")
                      .Append(rule.OddOnly ? "（仅单周）" : rule.EvenOnly ? "（仅双周）" : "（每周）");
                }
            }
            catch (FormatException ex)
            {
                ok = false;
                sb.Append("周次格式不正确：").Append(ex.Message);
            }
        }

        if (ok && end >= start)
        {
            ScheduleEvent? clash = _others.FirstOrDefault(o =>
                o.Weekday == DayBox.SelectedIndex + 1 &&
                o.StartSection <= end && o.EndSection >= start &&
                Overlaps(o, rule, clashWeek));
            if (clash is not null)
            {
                sb.AppendLine().Append("⚠ 与已有日程时段重叠：").Append(clash.Title)
                  .Append("（周").Append(clash.Weekday).Append(' ')
                  .Append(clash.StartSection).Append('-').Append(clash.EndSection)
                  .Append("节 ").Append(clash.ScheduleText).Append('）');
            }
        }

        HintText.Text = sb.ToString();
        HintText.Foreground = ok
            ? new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x73))
            : new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30));
        OkBtn.IsEnabled = ok && !string.IsNullOrWhiteSpace(TitleBox.Text);
    }

    private int WeekOf(DateTime date, out string? error)
    {
        error = null;
        if (_termStart is null)
        {
            return -1;
        }
        int days = (date.Date - _termStart.Value.Date).Days;
        if (days < 0)
        {
            error = "日期早于学期开始日";
            return -1;
        }
        int week = days / 7 + 1;
        if (week > _totalWeeks)
        {
            error = $"日期超出学期总周数（{_totalWeeks} 周）";
        }
        return week;
    }

    private bool Overlaps(ScheduleEvent other, WeekRule? rule, int onceWeek)
    {
        if (onceWeek > 0)
        {
            return other.OccursInWeek(onceWeek, _termStart);
        }
        if (rule is null) return false;
        for (int w = 1; w <= 40; w++)
        {
            if (other.OccursInWeek(w, _termStart) && rule.Contains(w)) return true;
        }
        return false;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            MessageBox.Show(this, "请填写日程标题", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _draft.Title = TitleBox.Text.Trim();
        _draft.Category = CategoryBox.SelectedItem as string ?? EventCategories.Default;
        _draft.Location = LocationBox.Text.Trim();
        _draft.Teacher = TeacherBox.Text.Trim();
        _draft.Notes = NotesBox.Text.Trim();
        _draft.ColorKey = _colorKey;
        _draft.StartSection = SelectedSection(StartBox);
        _draft.EndSection = SelectedSection(EndBox);
        _draft.Source = _draft.Source.Length > 0 ? _draft.Source : "local";

        if (IsOneOff)
        {
            if (!DateTime.TryParseExact(DateBox.Text.Trim(), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
            {
                UpdateHint();
                return;
            }
            _draft.Date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            _draft.Weekday = ScheduleEvent.WeekdayOf(date);
            if (string.IsNullOrWhiteSpace(_draft.WeeksRaw))
            {
                _draft.WeeksRaw = $"1-{_totalWeeks}周";
            }
        }
        else
        {
            _draft.Date = null;
            _draft.Weekday = DayBox.SelectedIndex + 1;
            _draft.WeeksRaw = WeeksBox.Text.Trim();
        }

        DialogResult = true;
        Close();
    }
}
