using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NcuCourseTable.Desktop.Interop;
using NcuCourseTable.Desktop.Models;
using NcuCourseTable.Desktop.Services;

namespace NcuCourseTable.Desktop.Views;

/// <summary>
/// 桌面小组件 —— 真正“长在桌面上”的那块玻璃卡片。
///
/// 与 MainWindow（应用窗口）的关系：MainWindow 是全功能“管理窗口”（编辑/导入/周视图），
/// 供需要时打开；WidgetWindow 是常驻桌面层的信息组件，二者共享同一份 courses.json。
///
/// 宿主策略（构造参数 hostKind）：
///  - BottomWindow   ：Rainmeter 式置底顶层窗口。无任务栏、无 Alt+Tab、非置顶；
///                     显示在桌面图标之上、所有普通窗口之下；可单击打开管理窗口 / 左键拖拽定位。
///                     控制入口已统一收进托盘（右键菜单已移除，见文件底部说明）。
///  - WallpaperWorkerW：Wallpaper Engine 式。窗口 SetParent 到壁纸承载 WorkerW，
///                     画在壁纸之上、图标之下；天生不抢鼠标（点击穿透），控制走托盘。
/// </summary>
public partial class WidgetWindow : Window
{
    public const string WindowTitle = "NcuCourseTable.DesktopWidget";
    private static readonly string[] DayNames = { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };

    private static readonly Brush Accent = BrushOf("#4E7FFF");
    private static readonly Brush OnGoing = BrushOf("#FF9F43");
    private static readonly Brush RowBarNone = BrushOf("#2A3548");
    private static readonly Brush TextMain = BrushOf("#FFFFFF");
    private static readonly Brush TextSub = BrushOf("#9FB0C8");
    private static readonly Brush TextDim = BrushOf("#7C8CA3");

    private readonly JsonStore _store;
    private readonly WidgetConfig _cfg;
    private readonly DesktopHostKind _hostKind;
    private readonly Dictionary<int, SectionTime> _sectionMap;

    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _ticker;
    private DispatcherTimer? _debounce;
    private readonly Dispatcher _uiDispatcher = Dispatcher.CurrentDispatcher;
    private IntPtr _hwnd;
    private bool _embedded;

    // 拖动状态
    private Point _dragOrigin;
    private bool _dragging;
    private bool _dragMoved;   // 本次按下是否发生过真实拖动（位移超阈值）

    // “显示桌面 / 最小化 / 隐藏 / DWM 遮蔽”防消失守卫（bottom 模式）
    private const int GuardIntervalMs = 120;
    private HwndSource? _hookSource;
    private DispatcherTimer? _guard;
    private bool _userHidden;              // 托盘“隐藏”期间不自动还原
    private static WidgetWindow? _winEventSink;            // 单实例：WinEvent 回调目标
    private static Native.WinEventProc? _winEventProc;     // 持有引用防 GC
    private IntPtr _winEventHook;

    // 计算缓存（每次 Reload 后刷新）
    private bool _inTerm;
    private int _currentWeek = 1;
    private int _termDaysOffset;
    private DateTime _termStartDate;
    private int _todayWeekday = 1;

    /// <summary>请求打开管理窗口（由 App 订阅）。</summary>
    public event Action? ManageRequested;

    public bool IsWallpaperMode => _hostKind == DesktopHostKind.WallpaperWorkerW;

    public WidgetWindow(JsonStore store, WidgetConfig cfg, DesktopHostKind hostKind)
    {
        // 必须在 InitializeComponent（窗口句柄创建）之前按宿主决定分层与否：
        // WPF AllowsTransparency=True 即 WS_EX_LAYERED 分层窗口，而分层窗口被
        // SetParent 进 WorkerW 当子窗口后 DWM 不再合成 → 屏幕上看不到（PrintWindow
        // 却仍能抓到内容，纯 PrintWindow 验证会漏掉此问题）。因此：
        //   wallpaper 模式 → 非透明窗口（不进 DWM 分层合成路径）+ DWM 圆角
        //   bottom    模式 → 透明玻璃窗口（顶层分层窗口正常合成）
        _hostKind = hostKind;
        if (_hostKind == DesktopHostKind.WallpaperWorkerW)
        {
            AllowsTransparency = false;
            Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x24, 0x34));
        }
        else
        {
            AllowsTransparency = true;
            Background = Brushes.Transparent;
        }

        InitializeComponent();
        _store = store;
        _cfg = cfg;
        _sectionMap = store.SectionMap as Dictionary<int, SectionTime>
            ?? store.SectionMap.ToDictionary(kv => kv.Key, kv => kv.Value);

        Title = WindowTitle;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            // 桌面组件样式：不占任务栏、不进 Alt+Tab
            WindowChrome.ApplyDesktopToolStyle(_hwnd);
            // 永不抢键盘焦点（点桌面/点组件都不打断用户输入）
            Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER |
                Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
            // 消息钩子：拦截“最小化”，检测外部最小化/隐藏/遮蔽后立即自恢复
            _hookSource = HwndSource.FromHwnd(_hwnd);
            _hookSource?.AddHook(WndProc);
            // 非透明窗口用 Win11 DWM 圆角补足圆角观感
            if (_hostKind == DesktopHostKind.WallpaperWorkerW)
            {
                int pref = 2; // DWMWCP_ROUND
                Native.DwmSetWindowAttribute(_hwnd, 33, ref pref, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE
            }
        };

        Loaded += OnLoaded;
        Closed += OnClosed;

        BuildWeekDots();

        // 数据文件外部变更（ncu_sdk sync/import 等）→ 自动重载
        try
        {
            _watcher = new FileSystemWatcher(_store.Dir, JsonStore.FileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => DebouncedReload();
            _watcher.Created += (_, _) => DebouncedReload();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"widget 文件监视不可用：{ex.Message}");
        }

        // 30s 心跳：跨天/跨周自动切换（开学、放假、周日->周一……）
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _ticker.Tick += (_, _) => ReloadNow(keepPosition: true);
        _ticker.Start();

        ReloadNow(keepPosition: true);
    }

    // ================================================================= 载入与渲染
    /// <summary>从磁盘重载数据并重算/重绘（外部变更或心跳触发）。</summary>
    public void ReloadNow(bool keepPosition = true)
    {
        _store.Load();
        Recompute();
        Render();
    }

    private void DebouncedReload()
    {
        // FSW 在其内部线程触发该回调：必须把 DispatcherTimer 绑回 UI 线程，
        // 否则 DefaultDispatcher 会跑到 FSW 线程的 Dispatcher（无消息泵、Tick 永不触发）。
        _uiDispatcher.BeginInvoke(() =>
        {
            _debounce?.Stop();
            _debounce = new DispatcherTimer(DispatcherPriority.Background, _uiDispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(350),
            };
            _debounce.Tick += (_, _) =>
            {
                _debounce.Stop();
                _debounce = null;
                if (IsLoaded) ReloadNow();
            };
            _debounce.Start();
        });
    }

    private void Recompute()
    {
        StoreData d = _store.Data;
        DateTime today = DateTime.Today;
        _todayWeekday = today.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)today.DayOfWeek;

        _inTerm = false;
        _currentWeek = 1;
        if (DateTime.TryParseExact(d.TermStart, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime start))
        {
            _termStartDate = start.Date;
            _termDaysOffset = (today - _termStartDate).Days;
            if (_termDaysOffset >= 0)
            {
                int wk = _termDaysOffset / 7 + 1;
                _inTerm = wk <= d.TotalWeeks;
                _currentWeek = Math.Clamp(wk, 1, d.TotalWeeks);
            }
        }
    }

    private void Render()
    {
        StoreData d = _store.Data;
        int total = d.TotalWeeks > 0 ? d.TotalWeeks : 20;

        // ---- 头部
        TermText.Text = FormatTerm(d);
        DateText.Text = $"{DateTime.Today.Month}月{DateTime.Today.Day}日 {DayNames[_todayWeekday - 1]}";
        if (_inTerm)
        {
            WeekPill.Visibility = Visibility.Visible;
            WeekText.Text = $"第 {_currentWeek} / {total} 周";
        }
        else
        {
            WeekPill.Visibility = Visibility.Collapsed;
        }

        // ---- 本周点阵
        var hasCourseThisWeek = Enumerable.Range(1, 7).Select(wd =>
            d.Courses.Any(c => c.Weekday == wd && c.ContainsWeek(_currentWeek))).ToArray();
        for (int i = 0; i < 7 && i < WeekDots.Children.Count; i++)
        {
            if (WeekDots.Children[i] is Ellipse e)
            {
                bool isToday = i + 1 == _todayWeekday;
                e.Fill = isToday ? Brushes.White : (hasCourseThisWeek[i] ? Accent : RowBarNone);
                e.StrokeThickness = isToday ? 1.6 : 0;
                e.Stroke = isToday ? Brushes.White : Brushes.Transparent;
                ToolTipService.SetToolTip(e, DayNames[i] + (hasCourseThisWeek[i] ? " · 有课" : ""));
            }
        }

        // ---- 今日课程
        var courses = d.Courses
            .Where(c => c.Weekday == _todayWeekday && c.ContainsWeek(_currentWeek))
            .OrderBy(c => c.StartSection)
            .ToList();

        TodayList.Children.Clear();
        TodayEmpty.Visibility = courses.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TodayCountText.Text = courses.Count > 0 ? $"共 {courses.Count} 节" : "";

        TimeSpan now = DateTime.Now.TimeOfDay;
        foreach (Course c in courses)
        {
            TodayList.Children.Add(BuildCourseRow(c));
        }

        // ---- 下一节 / 学期状态
        NextPill.Visibility = Visibility.Visible;
        NextText.Text = BuildNextLine(courses, now);
    }

    private string BuildNextLine(List<Course> courses, TimeSpan now)
    {
        if (!_inTerm)
        {
            if (_termDaysOffset < 0)
            {
                return _termDaysOffset == -1
                    ? "明天开学 🎒"
                    : $"距开学还有 {-_termDaysOffset} 天";
            }
            return "本学期课程已结束 🎉";
        }
        if (courses.Count == 0)
        {
            return _todayWeekday >= 6 ? "周末愉快 🌤" : "今日无课 · 好好休息";
        }

        // 正在上的课（开始时间 <= now <= 结束时间）
        Course? ongoing = null;
        Course? upcoming = null;
        foreach (Course c in courses)
        {
            string st = _sectionMap.TryGetValue(c.StartSection, out var s) ? s.Start : "";
            string en = _sectionMap.TryGetValue(c.EndSection, out var e) ? e.End : "";
            if (TimeOnly.TryParse(st, out TimeOnly s0) && TimeOnly.TryParse(en, out TimeOnly e0))
            {
                if (s0.ToTimeSpan() <= now && now <= e0.ToTimeSpan())
                {
                    ongoing ??= c;
                }
                else if (s0.ToTimeSpan() > now)
                {
                    upcoming ??= c;
                }
            }
        }
        if (ongoing is not null)
        {
            return $"正在上：{ongoing.Name} · {Meta(ongoing)}";
        }
        if (upcoming is not null)
        {
            return $"下一节：{upcoming.Name} · {TimeOf(upcoming)} · {RoomOf(upcoming)}";
        }
        return "今日课程已结束";
    }

    /// <summary>单门课一行：左侧状态色条 + 名称/时间 + 地点·教师。</summary>
    private UIElement BuildCourseRow(Course c)
    {
        TimeSpan now = DateTime.Now.TimeOfDay;
        bool ongoing = false;
        if (_sectionMap.TryGetValue(c.StartSection, out var s) &&
            _sectionMap.TryGetValue(c.EndSection, out var e) &&
            TimeOnly.TryParse(s.Start, out TimeOnly s0) &&
            TimeOnly.TryParse(e.End, out TimeOnly e0))
        {
            ongoing = s0.ToTimeSpan() <= now && now <= e0.ToTimeSpan();
        }

        var bar = new Border
        {
            Width = 4,
            CornerRadius = new CornerRadius(2),
            Background = ongoing ? OnGoing : Accent,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 2, 0, 2),
        };

        var name = new TextBlock
        {
            Text = c.Name,
            Foreground = TextMain,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var time = new TextBlock
        {
            Text = TimeOf(c),
            Foreground = ongoing ? OnGoing : TextSub,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var meta = new TextBlock
        {
            Text = Meta(c),
            Foreground = TextDim,
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(name, 0);
        Grid.SetColumn(time, 1);
        top.Children.Add(name);
        top.Children.Add(time);

        var body = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        body.Children.Add(top);
        body.Children.Add(meta);

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 9) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(bar, 0);
        Grid.SetColumn(body, 1);
        grid.Children.Add(bar);
        grid.Children.Add(body);

        return grid;
    }

    private string TimeOf(Course c)
    {
        if (_sectionMap.TryGetValue(c.StartSection, out var s) &&
            _sectionMap.TryGetValue(c.EndSection, out var e) &&
            s.Start.Length > 0 && e.End.Length > 0)
        {
            return $"{s.Start}-{e.End}";
        }
        return c.SectionRangeText;
    }

    private string Meta(Course c)
    {
        string room = RoomOf(c);
        string who = string.IsNullOrWhiteSpace(c.Teacher) ? "" : $" · {c.Teacher}";
        return room.Length > 0 ? $"{room}{who}" : (who.Length > 0 ? who.TrimStart(' ', '·') : "");
    }

    private static string RoomOf(Course c) =>
        string.IsNullOrWhiteSpace(c.Room) ? "地点待定" : c.Room;

    private static string FormatTerm(StoreData d)
    {
        if (d.TermName.Length > 0)
        {
            return d.TermName.Length > 14 ? d.TermName[..14] + "…" : d.TermName;
        }
        // 与 JsonStore.FormatTermName 同规则：规范 9 位 termcode 学期号在末位（202620271 -> 第1学期）
        string name = JsonStore.FormatTermName(d.Termcode);
        return name == "未命名学期" ? "我的课表" : name;
    }

    private static Brush BrushOf(string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ================================================================= 点阵初始化
    private void BuildWeekDots()
    {
        WeekDots.Children.Clear();
        for (int i = 0; i < 7; i++)
        {
            WeekDots.Children.Add(new Ellipse
            {
                Width = 7,
                Height = 7,
                Margin = new Thickness(0, 0, 6, 0),
                Fill = RowBarNone,
            });
        }
    }

    // ================================================================= 桌面宿主
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RestorePosition();
        Opacity = _cfg.Opacity;

        if (_hostKind == DesktopHostKind.WallpaperWorkerW)
        {
            // 真壁纸层嵌入：壁纸之上、图标之下，天生穿透
            IntPtr host = DesktopHost.GetWallpaperHost();
            _embedded = host != IntPtr.Zero && DesktopHost.EmbedInto(_hwnd, host);
            WindowChrome.SetClickThrough(_hwnd, true);
        }
        else
        {
            // Rainmeter 式置底
            _embedded = false;
            WindowChrome.SetClickThrough(_hwnd, _cfg.Locked);
            SendToBottomSoon();
            StartSurvivalGuard();
        }

        // 透明度恒定由托盘菜单控制（_cfg.Opacity），鼠标悬停不改变。
    }

    public bool IsClickThroughNow() =>
        _hostKind == DesktopHostKind.WallpaperWorkerW ||
        (_hwnd != IntPtr.Zero && WindowChrome.IsClickThrough(_hwnd));

    /// <summary>锁定（点击穿透）/ 解锁 —— 托盘入口。壁纸层模式恒穿透，忽略此调用。</summary>
    public void SetLocked(bool locked)
    {
        if (_hostKind == DesktopHostKind.WallpaperWorkerW) return;
        WindowChrome.SetClickThrough(_hwnd, locked);
        _cfg.Locked = locked;
        _cfg.Save(_store.Dir);
        Opacity = locked ? 0.75 : _cfg.Opacity;
    }

    private void SendToBottomSoon()
    {
        // Show() 后 WPF 会把窗口放到 Z 序顶部，这里压回最底；
        // 延迟到 ContentRendered 之后再压一次，保证稳定处于普通窗口之下。
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            if (IsLoaded && _hwnd != IntPtr.Zero) WindowChrome.SendToBottom(_hwnd);
        });
        ContentRendered += (_, _) =>
        {
            if (_hwnd != IntPtr.Zero) WindowChrome.SendToBottom(_hwnd);
        };
    }

    private void RestorePosition()
    {
        if (!double.IsNaN(_cfg.X) && !double.IsNaN(_cfg.Y))
        {
            Left = _cfg.X;
            Top = _cfg.Y;
            return;
        }
        // 默认右下角
        double wa = SystemParameters.WorkArea.Width;
        double ha = SystemParameters.WorkArea.Height;
        Left = Math.Max(0, wa - Width - 40);
        Top = Math.Max(0, ha - Height - 60);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _watcher?.Dispose();
        _watcher = null;
        _ticker?.Stop();
        _ticker = null;
        _debounce?.Stop();
        StopSurvivalGuard();
        _hookSource?.RemoveHook(WndProc);
        _hookSource = null;
        if (_embedded && _hwnd != IntPtr.Zero)
        {
            DesktopHost.Detach(_hwnd);
        }
    }

    // ================================================================= 交互：单击开管理窗口 / 左键拖动定位
    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsClickThroughNow()) return;
        _dragging = true;
        _dragMoved = false;
        _dragOrigin = e.GetPosition(this);
        CaptureMouse();
        e.Handled = false;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed || IsClickThroughNow()) return;
        Point p = e.GetPosition(this);
        double dx = p.X - _dragOrigin.X;
        double dy = p.Y - _dragOrigin.Y;
        if (Math.Abs(dx) < 2 && Math.Abs(dy) < 2) return;
        _dragMoved = true;      // 位移超阈值 → 判定为拖动而非单击
        Left = Math.Round(Left + dx);
        Top = Math.Round(Top + dy);
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        if (_dragMoved)
        {
            _dragMoved = false;
            SavePosition();
            return;
        }
        // 单击（按下-原地抬起，未拖动）→ 打开管理窗口。
        // 双击的第二击会再次触发本分支，但 EnsureManager 幂等（已开则激活），不会重复开窗。
        if (e.ChangedButton == MouseButton.Left && !IsClickThroughNow())
        {
            ManageRequested?.Invoke();
        }
    }

    private void SavePosition()
    {
        _cfg.X = Math.Round(Left);
        _cfg.Y = Math.Round(Top);
        _cfg.Save(_store.Dir);
    }

    // ================================================================= 防“显示桌面/最小化”消失
    // Windows“显示桌面”（任务栏右下角细条 / Win+D / Win+M）会把 Progman（桌面整层）从 Z 序
    // 最底抬升到任务栏正下方，让桌面壁纸/图标盖住所有置底窗口——普通窗口靠任务栏按钮还原，
    // 本组件是 WS_EX_TOOLWINDOW、无任务栏按钮、不进 Alt+Tab，一旦被盖住就“凭空消失”。
    // 对策（bottom 模式）：
    //  ① 消息钩子吞掉 SC_MINIMIZE（Win+M / 标题栏最小化命令根本到不了 DefWindowProc）；
    //  ② WM_SIZE 最小化即还原（SW_SHOWNOACTIVATE + 沉底，不抢焦点）；
    //  ③ 120ms 看门狗：被最小化/隐藏/遮蔽时自恢复；
    //  ④ Z 序不变量：组件永远紧贴 Progman 正上方——“显示桌面”把 Progman 抬到最顶时
    //     组件跟着浮到桌面之上保持可见，桌面恢复后随 Progman 一起沉回底部。
    //     （前台切入桌面即触发重贴，事件 + 轮询双保险；用户从托盘主动“隐藏”除外。）
    private void StartSurvivalGuard()
    {
        if (_guard is not null || _hostKind == DesktopHostKind.WallpaperWorkerW) return;
        _guard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(GuardIntervalMs) };
        _guard.Tick += (_, _) => GuardTick();
        _guard.Start();
        // 前台窗口变化（落到桌面 = “显示桌面”/点桌面）→ 立即重贴 Z 序，不等下一次轮询
        _winEventSink = this;
        _winEventProc = OnShellWinEvent;
        _winEventHook = Native.SetWinEventHook(
            Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _winEventProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
    }

    private void StopSurvivalGuard()
    {
        _guard?.Stop();
        _guard = null;
        if (_winEventHook != IntPtr.Zero)
        {
            Native.UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }
        _winEventProc = null;
        if (ReferenceEquals(_winEventSink, this)) _winEventSink = null;
    }

    /// <summary>托盘“隐藏桌面小组件”：隐藏期间守卫不自动还原。</summary>
    public void HideFromTray()
    {
        _userHidden = true;
        Hide();
    }

    /// <summary>托盘“显示桌面小组件”。</summary>
    public void ShowFromTray()
    {
        _userHidden = false;
        Show();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 系统最小化命令（Win+M / 标题栏菜单等 SC_MINIMIZE）→ 直接吞掉，组件不允许最小化
        if (msg == Native.WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == Native.SC_MINIMIZE)
        {
            handled = true;
            return IntPtr.Zero;
        }
        // 已被外部最小化（“显示桌面”等渠道）→ 消息处理完后立即还原，避免消失
        if (msg == Native.WM_SIZE && wParam.ToInt64() == Native.SIZE_MINIMIZED)
        {
            _uiDispatcher.BeginInvoke(DispatcherPriority.Send, new Action(RestoreFromSuppression));
        }
        return IntPtr.Zero;
    }

    private void GuardTick()
    {
        if (_userHidden || !IsLoaded || _hwnd == IntPtr.Zero || IsWallpaperMode) return;
        bool needRestore = false;

        if (Native.IsIconic(_hwnd) || !Native.IsWindowVisible(_hwnd))
        {
            needRestore = true;
        }
        else
        {
            int cloaked = 0;
            Native.DwmGetWindowAttribute(_hwnd, Native.DWMWA_CLOAKED, ref cloaked, sizeof(int));
            if (cloaked == 1)
            {
                // “显示桌面”若走 DWM 遮蔽路径：自己解除遮蔽（本进程可解除自己窗口的 cloak）
                cloaked = 0;
                Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_CLOAKED, ref cloaked, sizeof(int));
                needRestore = true;
            }
        }

        if (needRestore)
        {
            RestoreFromSuppression();
        }
        // Z 序不变量：无论桌面层（Progman）被抬到哪，组件都紧贴其上方 → 显示桌面不再盖住组件
        EnsureAboveProgman();
    }

    // ------------------------------------------------------------ “显示桌面”Z 序不变量
    /// <summary>
    /// 组件永远位于 Progman（桌面整层）正上方。正常时 Progman 在最底 → 组件在置底位；
    /// “显示桌面”把 Progman 抬到任务栏下方 → 组件随之浮到桌面之上，壁纸/图标盖不住它。
    /// </summary>
    private void EnsureAboveProgman()
    {
        if (_userHidden || !IsLoaded || _hwnd == IntPtr.Zero || IsWallpaperMode) return;
        IntPtr progman = Native.FindWindow(Native.ClassProgman, null);
        if (progman == IntPtr.Zero) return;                          // 桌面层缺失时不动
        if (Native.GetWindow(progman, Native.GW_HWNDNEXT) == _hwnd) return;  // 已紧贴 Progman 上方
        IntPtr anchor = Native.GetWindow(progman, Native.GW_HWNDPREV);        // Progman 上方那扇窗
        Native.SetWindowPos(_hwnd, anchor, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private static void OnShellWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject,
        int idChild, uint thread, uint time)
    {
        if (evt != Native.EVENT_SYSTEM_FOREGROUND) return;
        WidgetWindow? w = _winEventSink;
        if (w is null) return;
        w._uiDispatcher.BeginInvoke(w.OnShellForegroundChanged);
    }

    private void OnShellForegroundChanged()
    {
        if (_userHidden || !IsLoaded || _hwnd == IntPtr.Zero || IsWallpaperMode) return;
        EnsureAboveProgman();
    }

    private void RestoreFromSuppression()
    {
        if (_userHidden || !IsLoaded || _hwnd == IntPtr.Zero || IsWallpaperMode) return;
        if (Visibility != Visibility.Visible)
        {
            Visibility = Visibility.Visible;
        }
        // SW_SHOWNOACTIVATE：还原但不抢焦点（保持“永不激活”的桌面组件语义）
        Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
        WindowChrome.SendToBottom(_hwnd);
    }

    // ================================================================= 右键菜单 / 点击交互记录
    // （2026-09-04：组件右键菜单功能与托盘完全重合——打开管理窗口 / 立即刷新 /
    //   锁定穿透 / 透明度 / 退出在托盘均有同名项，且在“锁定穿透 / 壁纸层”模式下组件不接收
    //   鼠标，右键本就是死入口。为消除双入口不一致，右键菜单整体移除，所有控制统一走托盘。
    //   同日：改为“单击卡片（未拖动）打开管理窗口”，托盘菜单移除“打开管理窗口”项；
    //   组件在锁定穿透 / 壁纸层模式下仍不接收鼠标，需先经托盘解锁或切换层级后再单击。）
    // 交互约定：未锁定（bottom 层可交互）时——
    //   单击 = 打开管理窗口（位移 <2px 视为单击，见 OnDragEnd）；
    //   左键拖动 = 移动卡片位置（位移 ≥2px 标记为拖动，结束保存位置）。

    public void SetOpacity(double v)
    {
        _cfg.Opacity = v;
        _cfg.Save(_store.Dir);
        // wallpaper 模式禁用整体透明度：Opacity<1 会触发 WS_EX_LAYERED，
        // 分层子窗口在壁纸层不被 DWM 合成（屏幕上看不到）
        if (!IsWallpaperMode) Opacity = v;
    }
}
