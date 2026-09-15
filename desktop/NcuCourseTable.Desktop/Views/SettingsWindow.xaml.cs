using System;
using System.Windows;
using System.Windows.Controls;
using NcuCourseTable.Desktop.Interop;

namespace NcuCourseTable.Desktop.Views;

/// <summary>
/// 设置面板（widget 模式）—— 原托盘菜单中各开关的集中归宿：
/// 账号配置 / 登录、小组件显隐、锁定穿透、桌面层级、透明度、开机自启、立即刷新。
/// 状态读写都经 App（控制中枢），本窗口只做展示与触发；
/// 托盘或其它入口改变状态后由 App 调 RefreshState() 回刷。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly App _app;
    private bool _updating; // 程序性赋值期间抑制事件，避免回写

    public SettingsWindow()
    {
        InitializeComponent();
        _app = (App)Application.Current;
        Loaded += (_, _) => RefreshState();
    }

    /// <summary>按 App 当前状态回刷全部控件（幂等，可反复调用）。</summary>
    public void RefreshState()
    {
        _updating = true;
        try
        {
            // ---- 账号 ----
            bool hasAccount = _app.HasAccount;
            AccountText.Text = hasAccount ? $"已配置账号：{_app.MaskedAccount}" : "未配置账号";
            AccountHint.Text = hasAccount
                ? "凭据已 DPAPI 加密保存，可一键登录并同步课表"
                : "配置统一身份认证账号后即可一键同步真课表";
            LoginBtn.IsEnabled = hasAccount;

            // ---- 小组件 ----
            WidgetVisibleBox.IsChecked = _app.IsWidgetShown;
            bool wallpaper = _app.CurrentHost == DesktopHostKind.WallpaperWorkerW;
            LockBox.IsChecked = !wallpaper && _app.IsWidgetLocked;
            LockBox.IsEnabled = !wallpaper;

            HostBottomRadio.IsChecked = !wallpaper;
            HostWallpaperRadio.IsChecked = wallpaper;

            double opacity = _app.WidgetOpacity;
            Opacity60.IsChecked = Math.Abs(opacity - 0.6) < 0.001;
            Opacity80.IsChecked = Math.Abs(opacity - 0.8) < 0.001;
            Opacity100.IsChecked = Math.Abs(opacity - 1.0) < 0.001;
            // 壁纸层不合成透明（Opacity<1 会让 WPF 补 WS_EX_LAYERED 导致卡片不可见）
            Opacity60.IsEnabled = !wallpaper;
            Opacity80.IsEnabled = !wallpaper;

            // ---- 系统 ----
            AutoStartBox.IsChecked = _app.IsAutoStartEnabled();
        }
        finally
        {
            _updating = false;
        }
    }

    // ---------------------------------------------------------------- 事件

    private void OnLogin(object sender, RoutedEventArgs e) => _app.LoginAndSync();

    private void OnAccountConfig(object sender, RoutedEventArgs e)
    {
        _app.ShowCredentialDialog();
        RefreshState(); // 配置对话框关闭后账号文案立即更新
    }

    private void OnWidgetVisibleChanged(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _app.SetWidgetVisible(WidgetVisibleBox.IsChecked == true);
    }

    private void OnLockChanged(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _app.SetWidgetLocked(LockBox.IsChecked == true);
    }

    private void OnHostChanged(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        DesktopHostKind kind = HostWallpaperRadio.IsChecked == true
            ? DesktopHostKind.WallpaperWorkerW
            : DesktopHostKind.BottomWindow;
        _app.SwitchHost(kind);      // 内部同值时为 no-op；切换后小组件被重建
        RefreshState();             // 壁纸层会连带禁用锁定/透明度
    }

    private void OnOpacityChanged(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        double v = Opacity60.IsChecked == true ? 0.6
                 : Opacity80.IsChecked == true ? 0.8
                 : 1.0;
        _app.SetWidgetOpacity(v);
    }

    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _app.SetAutoStart(AutoStartBox.IsChecked == true);
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _app.RefreshAll();

    private void OnOpenCourseCenter(object sender, RoutedEventArgs e) => _app.ShowManager();

    private void OnOpenDataDir(object sender, RoutedEventArgs e) => _app.OpenDataDir();
}
