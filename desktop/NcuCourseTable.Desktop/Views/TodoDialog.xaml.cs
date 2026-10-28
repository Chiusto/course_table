using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NcuCourseTable.Desktop.Interop;
using NcuCourseTable.Desktop.Models;

namespace NcuCourseTable.Desktop.Views;

/// <summary>待办新增/编辑对话框：标题、优先级、可选截止日期与备注。</summary>
public partial class TodoDialog : Window
{
    private readonly TodoItem _draft;

    public TodoDialog(TodoItem draft, string title = "新建待办")
    {
        InitializeComponent();
        Title = title;
        _draft = draft;

        TitleBox.Text = draft.Title;
        NotesBox.Text = draft.Notes;
        DueBox.Text = draft.DueDate;
        PriorityNormal.IsChecked = draft.Priority <= 0;
        PriorityImportant.IsChecked = draft.Priority == 1;
        PriorityUrgent.IsChecked = draft.Priority >= 2;

        TitleBox.TextChanged += (_, _) => UpdateHint();
        DueBox.TextChanged += (_, _) => UpdateHint();
        Loaded += (_, _) =>
        {
            UpdateHint();
            Motion.ScaleFadeIn(Root, 0.96, 0.34);
            TitleBox.Focus();
        };
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnToday(object sender, RoutedEventArgs e)
    {
        DueBox.Text = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private void UpdateHint()
    {
        var sb = new System.Text.StringBuilder();
        bool ok = true;
        bool hasTitle = !string.IsNullOrWhiteSpace(TitleBox.Text);
        if (!hasTitle)
        {
            ok = false;
            sb.Append("填写待办内容后即可保存。");
        }

        string dueText = DueBox.Text.Trim();
        if (dueText.Length > 0)
        {
            if (!DateTime.TryParseExact(dueText, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTime due))
            {
                ok = false;
                if (sb.Length > 0) sb.AppendLine();
                sb.Append("截止日期格式应为 yyyy-MM-dd，或留空");
            }
            else
            {
                int days = (due.Date - DateTime.Today).Days;
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(days switch
                {
                    0 => "今天截止",
                    > 0 => $"{due:yyyy年M月d日} · 还有 {days} 天",
                    _ => $"{due:yyyy年M月d日} · 已逾期 {-days} 天",
                });
            }
        }
        else if (sb.Length == 0)
        {
            sb.Append("保存后会出现在待办列表，建议补充截止日期以免遗忘。");
        }

        HintText.Text = sb.ToString();
        HintText.Foreground = ok || !hasTitle
            ? new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x73))
            : new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30));
        OkBtn.IsEnabled = ok;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            UpdateHint();
            return;
        }
        _draft.Title = TitleBox.Text.Trim();
        _draft.Notes = NotesBox.Text.Trim();
        _draft.Priority = PriorityUrgent.IsChecked == true ? 2
            : PriorityImportant.IsChecked == true ? 1 : 0;

        string due = DueBox.Text.Trim();
        if (due.Length > 0 && DateTime.TryParseExact(due, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
        {
            _draft.DueDate = parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        else
        {
            _draft.DueDate = "";
        }

        DialogResult = true;
        Close();
    }
}
