using System;
using System.Threading;
using System.Windows;
using NcuCourseTable.Desktop.Interop;
using NcuCourseTable.Desktop.Services;
using NcuCourseTable.Desktop.ViewModels;
using NcuCourseTable.Desktop.Views;

namespace NcuCourseTable.Desktop;

public partial class App : Application
{
    // ------------------------------------------------------------------ 全局状态
    private JsonStore? _store;
    private WidgetConfig? _cfg;
    private WidgetWindow? _widget;
    private MainWindow? _manager;
    private SettingsWindow? _settings;
    private DesktopHostKind _host = DesktopHostKind.BottomWindow;
    private Mutex? _singleInstance;
    private bool _widgetMode;

    private const string SingleInstanceName = "Local\\NcuCourseTable.Widget.SingleInstance";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 命令行：
        //   NcuCourseTable.Desktop.exe [--data <目录>] [--demo] [--import <json>] [--headless]
        //                               [--window] [--host=bottom|wallpaper]
        // 默认（无 --window）：桌面组件模式 —— 常驻托盘 + 桌面层小组件，不弹任何应用窗口。
        // --window：显式打开完整管理窗口（旧行为）；--host 仅对桌面组件模式生效。
        string? dataDir = null, importFile = null, hostArg = null;
        bool withDemo = false, headless = false, windowMode = false;
        for (int i = 0; i < e.Args.Length; i++)
        {
            switch (e.Args[i])
            {
                case "--data" or "-d" when i + 1 < e.Args.Length: dataDir = e.Args[++i]; break;
                case "--demo": withDemo = true; break;
                case "--import" when i + 1 < e.Args.Length: importFile = e.Args[++i]; break;
                case "--headless": headless = true; break;
                case "--window": windowMode = true; break;
                case "--host" when i + 1 < e.Args.Length: hostArg = e.Args[++i]; break;
            }
        }

        // 数据准备（所有模式共用）
        var store = new JsonStore(dataDir);
        if (withDemo) store.FillDemo();
        if (importFile is not null)
        {
            int n = store.ImportNcuJson(importFile);
            if (headless) System.Console.WriteLine($"imported {n} courses -> {store.FilePath}");
        }
        if (headless)
        {
            Shutdown();
            return;
        }

        _store = store;
        _cfg = WidgetConfig.Load(store.Dir);

        // 账号登录：凭据 DPAPI 加密存储（credentials.bin）；登录经本地 ncu_sdk 子进程执行
        _vault = new CredentialVault(store.Dir);
        _login = new LoginService(store.Dir);

        if (windowMode)
        {
            // 旧行为：普通应用窗口，关窗即退出
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            ShowManager();
            return;
        }

        // ---------------- 桌面组件模式 ----------------
        _widgetMode = true;
        _singleInstance = new Mutex(true, SingleInstanceName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("南昌大学课程表桌面组件已在运行。\n双击托盘图标打开设置，或从托盘菜单打开课程中心。",
                "已在运行", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _host = DesktopHostKindExtensions.ParseHost(hostArg ?? _cfg.Host);
        _cfg.Host = _host.HostName();
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        BuildTray();
        CreateWidget();
    }

    // ================================================================= 桌面小组件
    private void CreateWidget()
    {
        _widget = new WidgetWindow(_store!, _cfg!, _host)
        {
            // Opacity<1 会让 WPF 给窗口补 WS_EX_LAYERED，而分层子窗口在壁纸层不被
            // DWM 合成（屏幕上看不到）→ wallpaper 模式禁用整体透明度
            Opacity = _host == DesktopHostKind.WallpaperWorkerW ? 1d : _cfg!.Opacity,
        };
        _widget.ManageRequested += EnsureManager;
        _widget.Show();
    }

    private void CloseWidget()
    {
        if (_widget is null) return;
        _widget.ManageRequested -= EnsureManager;
        _widget.Close();
        _widget = null;
    }

    /// <summary>切换桌面层级（图标上层 / 壁纸层）：重建小组件。</summary>
    internal void SwitchHost(DesktopHostKind kind)
    {
        if (_host == kind) return;
        _host = kind;
        _cfg!.Host = kind.HostName();
        _cfg.Save(_store!.Dir);
        CloseWidget();
        CreateWidget();
    }

    // ================================================================= 设置面板
    /// <summary>打开设置面板（已开则激活）；显隐/锁定/层级/透明度/自启/刷新集中于此。</summary>
    internal void ShowSettings()
    {
        if (_settings is { IsLoaded: true })
        {
            _settings.Activate();
            _settings.RefreshState();
            return;
        }
        _settings = new SettingsWindow();
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
    }

    // ---------------- 设置面板读取的状态快照 ----------------
    internal bool HasAccount => (_vault?.UsernameOrEmpty ?? "").Length > 0;
    internal string MaskedAccount => MaskAccount(_vault?.UsernameOrEmpty ?? "");
    internal DesktopHostKind CurrentHost => _host;
    internal double WidgetOpacity => _cfg?.Opacity ?? 1.0;
    internal bool IsWidgetShown => _widget is { IsLoaded: true } w && w.IsVisible;
    internal bool IsWidgetLocked => _widget is { } w && w.IsClickThroughNow();

    // ---------------- 设置面板触发的动作（与托盘语义一致） ----------------
    internal void SetWidgetVisible(bool visible)
    {
        if (_widget is null) return;
        if (visible)
        {
            _widget.ShowFromTray();
        }
        else
        {
            _widget.HideFromTray();
        }
    }

    internal void SetWidgetLocked(bool locked)
    {
        if (_widget is null || _host == DesktopHostKind.WallpaperWorkerW) return;
        _widget.SetLocked(locked);
    }

    internal void SetWidgetOpacity(double v)
    {
        _cfg!.Opacity = v;
        _cfg.Save(_store!.Dir);
        if (_widget is { IsLoaded: true })
        {
            _widget.SetOpacity(v);
        }
    }

    internal void OpenDataDir()
    {
        Services.Openers.OpenDataDir(_store!.Dir);
    }

    // ================================================================= 管理窗口
    internal void ShowManager()
    {
        if (_manager is { IsLoaded: true })
        {
            if (_manager.WindowState == WindowState.Minimized)
            {
                _manager.WindowState = WindowState.Normal;
            }
            _manager.Activate();
            return;
        }
        _manager = new MainWindow(new MainViewModel(_store!));
        _manager.LoginRequested += LoginAndSync;
        _manager.SettingsRequested += ShowSettings;
        _manager.Closed += (_, _) =>
        {
            if (!_widgetMode)
            {
                // --window 模式：关窗即退出
                Shutdown();
            }
        };
        _manager.SetLoginAccount(_vault?.UsernameOrEmpty ?? "");
        if (_widgetMode)
        {
            // 管理窗口是普通应用窗口：允许最小化/置于普通窗口层即可
            _manager.Show();
        }
        else
        {
            _manager.Show();
            MainWindow = _manager;
        }
    }

    private void EnsureManager() => ShowManager();

    // ================================================================= 通用动作（托盘 + 面板共用）
    internal void RefreshAll()
    {
        if (_widget is { IsLoaded: true })
        {
            _widget.ReloadNow();
        }
        if (_manager is { IsLoaded: true } && _manager.DataContext is MainViewModel vm)
        {
            vm.ReloadFromDisk();
        }
    }

    private void ExitAll()
    {
        _tray?.Dispose();
        _tray = null;
        CloseWidget();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _tray = null;
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
