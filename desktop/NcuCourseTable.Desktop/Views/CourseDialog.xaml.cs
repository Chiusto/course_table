using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using NcuCourseTable.Desktop.Models;

namespace NcuCourseTable.Desktop.Views;

/// <summary>
/// 课程新增/编辑对话框：直接修改调用方传入的 draft（新增时是新建对象，
/// 编辑时是原课程克隆），保存成功返回 DialogResult=true。
/// 校验：名称非空、周次表达式可解析、结束节次 &gt;= 起始节次；
/// 提示：解析出的上课周范围，以及与其他课程的时段冲突（不阻断）。
/// </summary>
public partial class CourseDialog : Window
{
    private static readonly string[] DayNames =
        { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };

    private readonly Course _draft;
    private readonly List<SectionTime> _sections;
    private readonly List<Course> _others;

    public CourseDialog(Course draft, IReadOnlyList<SectionTime> sections,
        IReadOnlyList<Course> others, string title = "课程")
    {
        InitializeComponent();
        Title = title;
        _draft = draft;
        _sections = sections.OrderBy(s => s.Index).ToList();
        _others = others.Where(o => o.Id != draft.Id).ToList();

        DayBox.ItemsSource = DayNames;
        DayBox.SelectedIndex = Math.Clamp(draft.Weekday - 1, 0, 6);

        var startItems = _sections.Select(s => s.Display).ToList();
        StartBox.ItemsSource = startItems;
        EndBox.ItemsSource = startItems;
        SelectSection(StartBox, draft.StartSection);
        SelectSection(EndBox, draft.EndSection);

        NameBox.Text = draft.Name;
        TeacherBox.Text = draft.Teacher;
        RoomBox.Text = draft.Room;
        WeeksBox.Text = draft.WeeksRaw;

        NameBox.TextChanged += (_, _) => UpdateHint();
        WeeksBox.TextChanged += (_, _) => UpdateHint();
        StartBox.SelectionChanged += (_, _) => UpdateHint();
        EndBox.SelectionChanged += (_, _) => UpdateHint();
        Loaded += (_, _) => UpdateHint();
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

    private void UpdateHint()
    {
        var sb = new System.Text.StringBuilder();
        bool ok = true;

        WeekRule? rule = null;
        try
        {
            rule = WeekRule.Parse(WeeksBox.Text);
        }
        catch (FormatException ex)
        {
            ok = false;
            sb.Append("周次格式不正确：").Append(ex.Message);
        }

        int start = SelectedSection(StartBox);
        int end = SelectedSection(EndBox);
        if (end < start)
        {
            ok = false;
            if (sb.Length > 0) sb.AppendLine();
            sb.Append("结束节次不能早于起始节次");
        }

        if (ok && rule is not null)
        {
            int count = rule.EndWeek - rule.StartWeek + 1;
            sb.Append("上课周：第").Append(rule.StartWeek).Append('-').Append(rule.EndWeek).Append("周")
              .Append(rule.OddOnly ? "（仅单周）" : rule.EvenOnly ? "（仅双周）" : "（每周）")
              .Append(" · ").Append(rule.Display);

            // 冲突提示（软性）：同一上课日、节次区间相交且周次有交集
            var clash = _others.FirstOrDefault(o =>
                o.Weekday == DayBox.SelectedIndex + 1 &&
                o.StartSection <= end && o.EndSection >= start &&
                WeeksIntersect(o, rule, start));
            if (clash is not null)
            {
                sb.AppendLine().Append("⚠ 与已有课程时段重叠：").Append(clash.Name)
                  .Append("（周").Append(clash.Weekday).Append(' ')
                  .Append(clash.StartSection).Append('-').Append(clash.EndSection)
                  .Append("节 ").Append(clash.Rule.Display).Append('）');
            }
        }

        HintText.Text = sb.ToString();
        HintText.Foreground = ok
            ? System.Windows.Media.Brushes.DarkSlateGray
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE5, 0x48, 0x4D));
        OkBtn.IsEnabled = ok;
    }

    private static bool WeeksIntersect(Course other, WeekRule rule, int _)
    {
        for (int w = 1; w <= 40; w++)
        {
            if (other.ContainsWeek(w) && rule.Contains(w))
            {
                return true;
            }
        }
        return false;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            MessageBox.Show(this, "请填写课程名称", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _draft.Name = NameBox.Text.Trim();
        _draft.Teacher = TeacherBox.Text.Trim();
        _draft.Room = RoomBox.Text.Trim();
        _draft.Weekday = DayBox.SelectedIndex + 1;
        _draft.StartSection = SelectedSection(StartBox);
        _draft.EndSection = SelectedSection(EndBox);
        _draft.WeeksRaw = WeeksBox.Text.Trim();
        DialogResult = true;
        Close();
    }
}
