using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NcuCourseTable.Desktop.Interop;
using NcuCourseTable.Desktop.Models;

namespace NcuCourseTable.Desktop.Views;

/// <summary>笔记新增/编辑对话框：正文、置顶开关、便签配色；#标签 由正文解析。</summary>
public partial class NoteDialog : Window
{
    private readonly NoteItem _draft;
    private string _color = "";

    public NoteDialog(NoteItem draft, string title = "新建笔记")
    {
        InitializeComponent();
        Title = title;
        _draft = draft;

        ContentBox.Text = draft.Content;
        PinCheck.IsChecked = draft.Pinned;
        _color = draft.Color ?? "";
        ApplyColorSelection();

        ContentBox.TextChanged += (_, _) => UpdateHint();
        Loaded += (_, _) =>
        {
            UpdateHint();
            Motion.ScaleFadeIn(Root, 0.96, 0.34);
            ContentBox.Focus();
            ContentBox.CaretIndex = ContentBox.Text.Length;
        };
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnColorPick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement item && item.Tag is string key)
        {
            _color = key;
            ApplyColorSelection();
        }
    }

    private void ApplyColorSelection()
    {
        (string Key, Border Swatch)[] swatches =
        {
            ("", SwatchDefault),
            ("yellow", SwatchYellow),
            ("green", SwatchGreen),
            ("blue", SwatchBlue),
            ("pink", SwatchPink),
            ("purple", SwatchPurple),
        };
        Brush active = (Brush)FindResource("AccentBrush");
        Brush idle = (Brush)FindResource("SeparatorStrongBrush");
        foreach ((string k, Border swatch) in swatches)
        {
            bool isActive = string.Equals(k, _color, StringComparison.OrdinalIgnoreCase);
            swatch.BorderThickness = new Thickness(isActive ? 2 : 1);
            swatch.BorderBrush = isActive ? active : idle;
        }
    }

    private void UpdateHint()
    {
        IReadOnlyList<string> tags = NoteItem.ParseTags(ContentBox.Text);
        HintText.Text = tags.Count == 0
            ? "用 #标签 自动归类，例如：#想法 #读书"
            : "标签：" + string.Join("  ", tags);
        OkBtn.IsEnabled = !string.IsNullOrWhiteSpace(ContentBox.Text);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        string content = ContentBox.Text.Trim();
        if (content.Length == 0)
        {
            ContentBox.Focus();
            return;
        }
        _draft.Content = content;
        _draft.Pinned = PinCheck.IsChecked == true;
        _draft.Color = _color;
        DialogResult = true;
    }
}
