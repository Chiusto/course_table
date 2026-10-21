using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NcuCourseTable.Desktop.Interop;
using NcuCourseTable.Desktop.Models;
using NcuCourseTable.Desktop.ViewModels;

namespace NcuCourseTable.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly TranslateTransform _boardShift = new();
    private readonly TranslateTransform _detailShift = new();
    private int _lastWeek;

    /// <summary>工具栏/空态“登录并同步课表”被点击（由 App 层执行真实登录）。</summary>
    public event Action? LoginRequested;

    /// <summary>工具栏“设置…”被点击（由 App 层打开设置面板）。</summary>
    public event Action? SettingsRequested;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        _lastWeek = vm.SelectedWeek;

        // 周视图 / 详情侧栏使用可中断的弹簧位移（从当前屏幕值出发）
        BoardHost.RenderTransform = _boardShift;
        DetailContent.RenderTransform = _detailShift;

        // VM 事件 -> 视图实现
        _vm.AddRequested += draft => ShowCourseDialog(draft, "新增课程");
        _vm.EditRequested += draft => ShowCourseDialog(draft, "编辑课程");
        _vm.ImportJsonDialogRequested += OpenImportPicker;
        _vm.TopmostChanged += on => Topmost = on;
        _vm.PropertyChanged += OnViewModelPropertyChanged;

        // 周视图交互
        Board.CourseClicked += c => _vm.Select(c);
        Board.CourseDoubleClicked += c => _vm.RequestEdit(c);
        Board.SlotDoubleClicked += (day, section) => _vm.StartAdd(day, section);

        Loaded += (_, _) =>
        {
            Motion.ScaleFadeIn(Root, 0.988, 0.4);
            _vm.StartAutoRefresh();
        };
        Closed += (_, _) => _vm.StopAutoRefresh();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedWeek))
        {
            int week = _vm.SelectedWeek;
            int direction = week >= _lastWeek ? 1 : -1;
            _lastWeek = week;
            AnimateWeek(direction);
        }
        else if (e.PropertyName == nameof(MainViewModel.SelectedCourse))
        {
            // 等绑定先把 Visibility 切到 Visible，再做入场动画
            Dispatcher.BeginInvoke(new Action(AnimateDetail), DispatcherPriority.Loaded);
        }
    }

    /// <summary>翻周：新一周从行进方向滑入；连续点击会保留当前速度重定向。</summary>
    private void AnimateWeek(int direction)
    {
        if (Motion.ReducedMotion)
        {
            _boardShift.X = 0;
            BoardHost.Opacity = 1;
            return;
        }

        if (!Motion.IsAnimating(_boardShift, TranslateTransform.XProperty))
        {
            _boardShift.X = direction * 24;
            BoardHost.Opacity = 0.55;
        }

        Motion.SpringTo(_boardShift, TranslateTransform.XProperty, 0,
            damping: 1.0, response: 0.34, tolerance: 0.12);
        Motion.SpringTo(BoardHost, UIElement.OpacityProperty, 1,
            damping: 1.0, response: 0.32, tolerance: 0.004);
    }

    /// <summary>详情卡入场：轻微右移 + 淡入，像材质浮到侧栏。</summary>
    private void AnimateDetail()
    {
        if (_vm.SelectedCourse is null || DetailContent.Visibility != Visibility.Visible) return;

        if (Motion.ReducedMotion)
        {
            _detailShift.X = 0;
            DetailContent.Opacity = 1;
            return;
        }

        if (!Motion.IsAnimating(DetailContent, UIElement.OpacityProperty))
        {
            _detailShift.X = 16;
            DetailContent.Opacity = 0;
        }

        Motion.SpringTo(_detailShift, TranslateTransform.XProperty, 0,
            damping: 1.0, response: 0.34, tolerance: 0.12);
        Motion.SpringTo(DetailContent, UIElement.OpacityProperty, 1,
            damping: 1.0, response: 0.3, tolerance: 0.004);
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
        LoginBtn.Content = has ? $"同步课表（{Mask(account)}）" : "登录并同步课表";
        LoginBtn.ToolTip = has
            ? "使用已保存账号登录并同步当前学期课表；如需更换账号请打开“设置…”"
            : "首次使用：打开“设置…”填写校园网账号密码（加密保存），或直接点本按钮完成配置并登录";
    }

    /// <summary>账号脱敏：123456789012 → 1234…12。</summary>
    private static string Mask(string account) =>
        account.Length <= 4 ? account : account[..4] + "…" + account[^2..];
}
