using System;
using System.IO;
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
    private DataStore? _store;
    private WidgetConfig? _cfg;
    private WidgetWindow? _widget;
    private MainWindow? _manager;
    private SettingsWindow? _settings;
    private PluginManagerWindow? _pluginsWindow;
    private PluginManager? _pluginManager;
    private PluginHost? _pluginHost;
    private ApiServer? _apiServer;
    private DesktopHostKind _host = DesktopHostKind.BottomWindow;
    private Mutex? _singleInstance;
    private bool _widgetMode;

    private const string SingleInstanceName = "Local\\Timetable.Widget.SingleInstance";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                string dir = _store?.Dir ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "timetable-error.log"),
                    $"{DateTime.Now:O}  {args.Exception}{Environment.NewLine}{Environment.NewLine}");
            }
            catch
            {
                // 日志失败不阻断错误提示
            }
            MessageBox.Show("时间表遇到未处理的错误：\n" + args.Exception.Message,
                "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // 命令行：Timetable.exe [--data <目录>] [--window] [--host=bottom|wallpaper]
        // 默认：桌面组件模式（常驻托盘 + 桌面卡片）；--window 打开完整管理窗口。
        string? dataDir = null, hostArg = null;
        bool windowMode = false;
        for (int i = 0; i < e.Args.Length; i++)
        {
            switch (e.Args[i])
            {
                case "--data" or "-d" when i + 1 < e.Args.Length: dataDir = e.Args[++i]; break;
                case "--window": windowMode = true; break;
                case "--host" when i + 1 < e.Args.Length: hostArg = e.Args[++i]; break;
            }
        }

        _store = new DataStore(dataDir);
        _store.Load();
        _cfg = WidgetConfig.Load(_store.Dir);
        InitializeNutstore();

        _pluginManager = new PluginManager();
        _pluginManager.LoadAll();
        _pluginHost = new PluginHost(_store, RefreshAll, PluginNotify);
        InitializePlugins();
        StartApiServer();

        if (windowMode)
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            ShowManager();
            ScheduleStartupNutstoreSync();
            return;
        }

        // ---------------- 桌面组件模式 ----------------
        _widgetMode = true;
        _singleInstance = new Mutex(true, SingleInstanceName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("时间表桌面组件已在运行。\n双击托盘图标打开设置，或从托盘菜单打开主窗口。",
                "已在运行", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _host = DesktopHostKindExtensions.ParseHost(hostArg ?? _cfg.Host);
        _cfg.Host = _host.HostName();
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        BuildTray();
        CreateWidget();
        ScheduleStartupNutstoreSync();
    }

    private void InitializePlugins()
    {
        if (_pluginManager is null || _pluginHost is null) return;
        foreach (LoadedPlugin loaded in _pluginManager.Plugins)
        {
            try
            {
                loaded.Instance.Initialize(_pluginHost);
            }
            catch (Exception ex)
            {
                _pluginManager.ReportError(loaded.AssemblyPath,
                    $"{loaded.Instance.Name} 初始化失败：{ex.Message}");
            }
        }
    }

    /// <summary>启动本地 HTTP API（环境变量 TIMETABLE_API_DISABLED=1 可关闭）。</summary>
    private void StartApiServer()
    {
        if (ApiServer.IsDisabled() || _store is null) return;
        try
        {
            _apiServer = new ApiServer(_store, RefreshAll);
            _apiServer.Start();
            System.Diagnostics.Debug.WriteLine($"[api] 本地 API 已启动：{_apiServer.BaseUrl}");
        }
        catch (Exception ex)
        {
            _apiServer = null;
            System.Diagnostics.Debug.WriteLine($"[api] 启动失败：{ex.Message}");
        }
    }

    // ================================================================= 桌面小组件
    private void CreateWidget()
    {
        _widget = new WidgetWindow(_store!, _cfg!, _host)
        {
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
    internal void ShowSettings()
    {
        if (_settings is { IsLoaded: true })
        {
            BringToFront(_settings);
            _settings.RefreshState();
            return;
        }
        _settings = new SettingsWindow();
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
        BringToFront(_settings);
    }

    private static void BringToFront(Window w)
    {
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Show();
        w.Activate();
        bool top = w.Topmost;
        w.Topmost = true;
        w.Topmost = false;
        w.Topmost = top;
        w.Focus();
    }

    // ---------------- 设置面板读取的状态快照 ----------------
    internal DesktopHostKind CurrentHost => _host;
    internal double WidgetOpacity => _cfg?.Opacity ?? 1.0;
    internal bool IsWidgetShown => _widget is { IsLoaded: true } w && w.IsVisible;
    internal bool IsWidgetLocked => _widget is { } w && w.IsClickThroughNow();
    internal bool IsWidgetDraggable => _cfg?.Draggable ?? true;
    internal DataStore Store => _store!;
    internal int PendingTodoCount => _store?.Data.Todos.Count(t => !t.Done) ?? 0;

    // ---------------- 设置面板触发的动作 ----------------
    internal void SetWidgetVisible(bool visible)
    {
        if (_widget is null) return;
        if (visible) _widget.ShowFromTray();
        else _widget.HideFromTray();
    }

    internal void SetWidgetLocked(bool locked)
    {
        if (_widget is null || _host == DesktopHostKind.WallpaperWorkerW) return;
        _widget.SetLocked(locked);
    }

    internal void SetWidgetDraggable(bool draggable)
    {
        if (_cfg is null) return;
        if (_widget is not null)
        {
            _widget.SetDraggable(draggable);
            return;
        }
        _cfg.Draggable = draggable;
        _cfg.Save(_store!.Dir);
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

    internal void UpdateTerm(string name, string startMonday, int totalWeeks)
    {
        if (_store is null) return;
        _store.Data.TermName = (name ?? "").Trim();
        _store.Data.TermStart = (startMonday ?? "").Trim();
        _store.Data.TotalWeeks = Math.Clamp(totalWeeks, 1, 60);
        _store.Save();
        RefreshAll();
        RefreshTrayChecks();
    }

    // ================================================================= 管理窗口
    internal void ShowManager()
    {
        if (_manager is { IsLoaded: true })
        {
            BringToFront(_manager);
            return;
        }
        _manager = new MainWindow(new MainViewModel(_store!));
        _manager.PluginsRequested += ShowPluginManager;
        _manager.SettingsRequested += ShowSettings;
        _manager.Closed += (_, _) =>
        {
            if (!_widgetMode)
            {
                Shutdown();
            }
        };
        _manager.Show();
        if (!_widgetMode) MainWindow = _manager;
        BringToFront(_manager);
    }

    private void EnsureManager() => ShowManager();

    // ================================================================= 通用动作
    internal void RefreshAll()
    {
        if (_widget is { IsLoaded: true })
        {
            _widget.ReloadNow();
        }
        if (_manager is { IsLoaded: true } && _manager.DataContext is MainViewModel vm)
        {
            vm.ReloadFromDisk(silent: true);
        }
        RefreshTrayChecks();
    }

    internal void PluginNotify(string message)
    {
        if (_manager is { IsLoaded: true } && _manager.DataContext is MainViewModel vm)
        {
            vm.SetStatus(message);
        }
        System.Diagnostics.Debug.WriteLine("[plugin] " + message);
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
        _apiServer?.Dispose();
        _apiServer = null;
        ShutdownNutstore();
        _tray?.Dispose();
        _tray = null;
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
