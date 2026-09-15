using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NcuCourseTable.Desktop.Models;
using NcuCourseTable.Desktop.ViewModels;

namespace NcuCourseTable.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    /// <summary>工具栏/空态“登录并同步课表”被点击（由 App 层执行真实登录）。</summary>
    public event Action? LoginRequested;

    /// <summary>工具栏“设置…”被点击（由 App 层打开设置面板）。</summary>
    public event Action? SettingsRequested;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        // VM 事件 -> 视图实现
        _vm.AddRequested += draft => ShowCourseDialog(draft, "新增课程");
        _vm.EditRequested += draft => ShowCourseDialog(draft, "编辑课程");
        _vm.ImportJsonDialogRequested += OpenImportPicker;
        _vm.TopmostChanged += on => Topmost = on;

        // 周视图交互
        Board.CourseClicked += c => _vm.Select(c);
        Board.CourseDoubleClicked += c => _vm.RequestEdit(c);
        Board.SlotDoubleClicked += (day, section) => _vm.StartAdd(day, section);

        Loaded += (_, _) => _vm.StartAutoRefresh();
        Closed += (_, _) => _vm.StopAutoRefresh();
    }

    private bool ShowCourseDialog(Course draft, string title)
    {
        var dialog = new CourseDialog(draft, _vm.SectionList, _vm.Courses, title)
        {
            Owner = this,
        };
        return dialog.ShowDialog() == true;
    }

    private void OpenImportPicker()
    {
        var picker = new OpenFileDialog
        {
            Title = "导入课表 JSON",
            Filter = "课表 JSON (*.json)|*.json|所有文件 (*.*)|*.*",
        };
        if (picker.ShowDialog(this) == true)
        {
            _vm.ImportJson(picker.FileName);
        }
    }

    // ============================================================ 账号 / 登录（转发到 App 层）
    private void OnLoginClick(object sender, RoutedEventArgs e) => LoginRequested?.Invoke();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    /// <summary>按已保存账号刷新登录按钮文案（空串 = 未配置）。</summary>
    public void SetLoginAccount(string account)
    {
        bool has = account.Length > 0;
        LoginBtn.Content = has ? $"登录并同步课表（{Mask(account)}）" : "登录并同步课表";
        LoginBtn.ToolTip = has
            ? "使用已保存账号登录并同步当前学期课表；如需更换账号请打开“设置…”"
            : "首次使用：打开“设置…”填写校园网账号密码（加密保存），或直接点本按钮完成配置并登录";
    }

    /// <summary>账号脱敏：123456789012 → 1234…12。</summary>
    private static string Mask(string account) =>
        account.Length <= 4 ? account : account[..4] + "…" + account[^2..];
}
