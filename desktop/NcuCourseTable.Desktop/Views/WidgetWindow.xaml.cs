using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
/// 供需要时打开；WidgetWindow 是常驻桌面层的信息组件，二者共享同一份 schedule.json。
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

    private static readonly Brush Accent = BrushOf("#0A84FF");
    private static readonly Brush OnGoing = BrushOf("#FF9F0A");
    private static readonly Brush TodoDue = BrushOf("#8E8E93");
    private static readonly Brush TodoToday = BrushOf("#FFB340");
    private static readonly Brush TodoOverdue = BrushOf("#FF8A80");
    private static readonly Brush TodoPriorityUrgent = BrushOf("#FF453A");
    private static readonly Brush TodoPriorityImportant = BrushOf("#FF9F0A");
    private static readonly Brush TodoPriorityNormal = BrushOf("#636366");
    private static readonly Brush RowBarNone = BrushOf("#3A3A3C");
    private static readonly Brush TextMain = BrushOf("#FFFFFF");
    private static readonly Brush TextSub = BrushOf("#B0B0B5");
    private static readonly Brush TextDim = BrushOf("#8E8E93");

    private readonly DataStore _store;
    private readonly WidgetConfig _cfg;
    private readonly DesktopHostKind _hostKind;
    private readonly Dictionary<int, SectionTime> _sectionMap;

    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _ticker;
    private DispatcherTimer? _debounce;
    private readonly Dispatcher _uiDispatcher = Dispatcher.CurrentDispatcher;
    private IntPtr _hwnd;
    private bool _embedded;

    // 拖动状态：屏幕坐标增量保证 1:1 跟手；历史速度用于释放后的动量投影
    private Point _dragStartScreen;
    private Point _lastScreen;
    private bool _dragging;
    private bool _dragMoved;
    private bool _dragEnabled;
    private readonly List<(double X, double Y, double Ms)> _dragHistory = new();
    private readonly Stopwatch _dragClock = new();
    private readonly ScaleTransform _cardScale = new(1, 1);
    private const double DragThreshold = 6;

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
    private bool _hasTermStart;
    private int _currentWeek = 1;
    private int _termDaysOffset;
    private DateTime _termStartDate;
    private int _todayWeekday = 1;

    /// <summary>请求打开管理窗口（由 App 订阅）。</summary>
    public event Action? ManageRequested;

    public bool IsWallpaperMode => _hostKind == DesktopHostKind.WallpaperWorkerW;

    public WidgetWindow(DataStore store, WidgetConfig cfg, DesktopHostKind hostKind)
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
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));
        }
        else
        {
            AllowsTransparency = true;
            Background = Brushes.Transparent;
        }

        InitializeComponent();
        Card.RenderTransformOrigin = new Point(0.5, 0.5);
        Card.RenderTransform = _cardScale;
        _store = store;
        _cfg = cfg;
        _sectionMap = store.SectionMap as Dictionary<int, SectionTime>
            ?? store.SectionMap.ToDictionary(kv => kv.Key, kv => kv.Value);

        Title = WindowTitle;
        // 首帧布局前就设上限，避免内容多时先撑出屏幕再被夹回来
        MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 24);

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
            // 亚克力按窗口矩形着色，8px 阴影留白会变成方形光晕；
            // 这里保留半透明玻璃卡片 + 阴影，材质更干净。
        };

        Loaded += OnLoaded;
        Closed += OnClosed;
        SizeChanged += (_, _) => { if (IsLoaded) ClampToWorkArea(); };

        BuildWeekDots();

        // 数据文件外部变更（ncu_sdk sync/import 等）→ 自动重载
        try
        {
            _watcher = new FileSystemWatcher(_store.Dir, DataStore.FileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => DebouncedReload();
            _watcher.Created += (_, _) => DebouncedReload();
            // Save() 走「写 .tmp 再替换」，磁盘层面是重命名而非新建，只会触发 Renamed
            _watcher.Renamed += (_, _) => DebouncedReload();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"widget 文件监视不可用：{ex.Message}");
        }

        // 同进程保存（管理窗口/插件）不经过文件监视，直接通知即可立即刷新
        _store.Saved += OnStoreSaved;

        // 30s 心跳：跨天/跨周自动切换（开学、放假、周日->周一……）。
        // 只重算重绘、不重新读盘：Load() 会把 Data 整表换成新对象，
        // 管理窗口（MainViewModel）手里共享的条目引用会变成游离对象，
        // 之后的待办/日程编辑会写进游离副本并静默丢失。
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _ticker.Tick += (_, _) =>
        {
            Recompute();
            Render();
        };
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

    private void OnStoreSaved()
    {
        if (!IsLoaded) return;
        _uiDispatcher.BeginInvoke(new Action(() =>
        {
            if (IsLoaded) ReloadNow(keepPosition: true);
        }));
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
                if (!IsLoaded) return;
                // 本进程自己的保存已由 _store.Saved 立即刷新，这里只处理外部写入；
                // 否则每次保存都会把共享的 Data 换成新对象，管理窗口的编辑随之丢失。
                if ((DateTime.UtcNow - _store.LastProgrammaticWriteUtc).TotalMilliseconds < 1500) return;
                ReloadNow();
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
        _hasTermStart = false;
        _currentWeek = 1;
        if (DateTime.TryParseExact(d.TermStart, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime start))
        {
            _hasTermStart = true;
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
        DateTime? termStart = _hasTermStart ? _termStartDate : null;
        var hasEventThisWeek = Enumerable.Range(1, 7).Select(wd =>
            d.Events.Any(c => c.Weekday == wd && c.OccursInWeek(_currentWeek, termStart))).ToArray();
        for (int i = 0; i < 7 && i < WeekDots.Children.Count; i++)
        {
            if (WeekDots.Children[i] is Ellipse e)
            {
                bool isToday = i + 1 == _todayWeekday;
                e.Fill = isToday ? Brushes.White : (hasEventThisWeek[i] ? Accent : RowBarNone);
                e.StrokeThickness = isToday ? 1.6 : 0;
                e.Stroke = isToday ? Brushes.White : Brushes.Transparent;
                ToolTipService.SetToolTip(e, DayNames[i] + (hasEventThisWeek[i] ? " · 有安排" : ""));
            }
        }

        // ---- 今日安排
        var events = d.Events
            .Where(c => c.Weekday == _todayWeekday && c.OccursInWeek(_currentWeek, termStart))
            .OrderBy(c => c.StartSection)
            .ToList();

        TodayList.Children.Clear();
        TodayEmpty.Visibility = events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TodayCountText.Text = events.Count > 0 ? $"共 {events.Count} 项" : "";

        TimeSpan now = DateTime.Now.TimeOfDay;
        foreach (ScheduleEvent c in events)
        {
            TodayList.Children.Add(BuildEventRow(c));
        }

        // ---- 下一项 / 学期状态
        NextPill.Visibility = Visibility.Visible;
        NextText.Text = BuildNextLine(events, now);
        RenderTodos(d);
        ScheduleFit();
    }

    /// <summary>
    /// 用主体滚动区的实际溢出量校正窗口高度：内容多则增高，少则缩回，到屏幕可用高度后留滚动。
    /// 不用 SizeToContent="Height"：它在测量阶段不受 MaxHeight 约束，超高时会被裁掉而不是滚动。
    /// </summary>
    private void OnBodyScrollChanged(object? sender, ScrollChangedEventArgs e) => FitHeightFromScroll();

    private void ScheduleFit() =>
        Dispatcher.BeginInvoke(new Action(FitHeightFromScroll), DispatcherPriority.Loaded);

    private void FitHeightFromScroll()
    {
        if (!IsLoaded) return;
        double overflow = BodyScroll.ExtentHeight - BodyScroll.ViewportHeight;
        if (Math.Abs(overflow) <= 0.5) return;
        double target = Math.Clamp(Height + overflow, MinHeight, MaxHeight);
        if (Math.Abs(target - Height) > 0.5)
        {
            Height = target;
        }
    }

    private void RenderTodos(StoreData d)
    {
        var todos = d.Todos
            .Where(t => !t.Done)
            .OrderBy(t => t.HasDue ? 0 : 1)
            .ThenBy(t => t.Due ?? DateTime.MaxValue)
            .ThenByDescending(t => t.Priority)
            .ToList();

        TodoRows.Children.Clear();
        TodoCountText.Text = todos.Count > 0 ? $"{todos.Count} 项" : "";
        TodoEmpty.Visibility = todos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (todos.Count == 0) return;

        foreach (TodoItem todo in todos)
        {
            TodoRows.Children.Add(BuildTodoRow(todo));
        }
    }

    private UIElement BuildTodoRow(TodoItem todo)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Brush priorityBrush = todo.Priority switch
        {
            >= 2 => TodoPriorityUrgent,
            1 => TodoPriorityImportant,
            _ => TodoPriorityNormal,
        };
        var dot = new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = priorityBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0),
        };
        var title = new TextBlock
        {
            Text = todo.Title,
            Foreground = TextMain,
            FontSize = 11.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var priority = new Border
        {
            CornerRadius = new CornerRadius(5),
            Background = priorityBrush,
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = todo.HasPriority ? Visibility.Visible : Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = todo.PriorityText,
                Foreground = TextMain,
                FontSize = 8.8,
                FontWeight = FontWeights.SemiBold,
            },
        };
        string dueText = CompactDue(todo);
        var due = new TextBlock
        {
            Text = dueText,
            Foreground = todo.IsOverdue ? TodoOverdue
                : todo.DueState == "today" ? TodoToday : TodoDue,
            FontSize = 9.5,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = dueText.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
        };

        Grid.SetColumn(dot, 0);
        Grid.SetColumn(title, 1);
        Grid.SetColumn(priority, 2);
        Grid.SetColumn(due, 3);
        row.Children.Add(dot);
        row.Children.Add(title);
        row.Children.Add(priority);
        row.Children.Add(due);

        string extra = todo.HasPriority ? $" · {todo.PriorityText}" : "";
        row.ToolTip = $"{todo.Title}\n{(todo.HasDue ? todo.DueText : "无截止日期")}{extra}";
        return row;
    }

    private static string CompactDue(TodoItem todo)
    {
        if (todo.Due is not { } due) return "";
        if (todo.IsOverdue) return $"逾期 {(DateTime.Today - due).Days} 天";
        int days = (due - DateTime.Today).Days;
        return days switch
        {
            0 => "今天",
            1 => "明天",
            _ => $"{due.Month}/{due.Day}",
        };
    }

    private string BuildNextLine(List<ScheduleEvent> events, TimeSpan now)
    {
        if (!_inTerm)
        {
            if (_termDaysOffset < 0)
            {
                return _termDaysOffset == -1
                    ? "明天开学 🎒"
                    : $"距开学还有 {-_termDaysOffset} 天";
            }
            return "本学期安排已结束 🎉";
        }
        if (events.Count == 0)
        {
            return _todayWeekday >= 6 ? "周末愉快 🌤" : "今天没有安排 · 好好休息";
        }

        ScheduleEvent? ongoing = null;
        ScheduleEvent? upcoming = null;
        foreach (ScheduleEvent c in events)
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
            return $"正在进行：{ongoing.Title} · {Meta(ongoing)}";
        }
        if (upcoming is not null)
        {
            return $"下一项：{upcoming.Title} · {TimeOf(upcoming)} · {Meta(upcoming)}";
        }
        return "今日安排已结束";
    }

    /// <summary>单条日程一行：左侧状态色条 + 标题/时间 + 地点·参与人。</summary>
    private UIElement BuildEventRow(ScheduleEvent c)
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
            Width = 3.5,
            CornerRadius = new CornerRadius(2),
            Background = ongoing ? OnGoing : EventPalette.SolidBrush(c),
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 2, 0, 2),
        };

        var name = new TextBlock
        {
            Text = c.Title,
            Foreground = TextMain,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var time = new TextBlock
        {
            Text = TimeOf(c),
            Foreground = ongoing ? OnGoing : TextSub,
            FontSize = 10.5,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Documents.Typography.SetNumeralAlignment(
            time, System.Windows.FontNumeralAlignment.Tabular);
        var meta = new TextBlock
        {
            Text = Meta(c),
            Foreground = TextDim,
            FontSize = 10.5,
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

    private string TimeOf(ScheduleEvent c)
    {
        if (_sectionMap.TryGetValue(c.StartSection, out var s) &&
            _sectionMap.TryGetValue(c.EndSection, out var e) &&
            s.Start.Length > 0 && e.End.Length > 0)
        {
            return $"{s.Start}-{e.End}";
        }
        return c.SectionRangeText;
    }

    private string Meta(ScheduleEvent c)
    {
        string location = c.DisplayLocation;
        string who = string.IsNullOrWhiteSpace(c.DisplayTeacher) ? "" : $" · {c.DisplayTeacher}";
        if (location.Length > 0) return $"{location}{who}";
        return who.Length > 0 ? who.TrimStart(' ', '·') : (c.Category.Length > 0 ? c.Category : "未填写地点");
    }

    private static string FormatTerm(StoreData d)
    {
        // 与 DataStore.FormatTermName 同规则：规范 9 位 termcode 学期号在末位（202620271 -> 第1学期）
        string name = d.TermName.Length > 0 ? d.TermName
            : (d.Termcode.Length > 0 ? DataStore.FormatTermName(d.Termcode) : "我的时间表");

        // 宽卡片里优先显示紧凑学期名：2026-2027学年第1学期 → 2026–27 · 第1学期
        var m = Regex.Match(name, @"^(\d{4})-(\d{4})学年第(\d+)学期$");
        if (m.Success)
        {
            return $"{m.Groups[1].Value}–{m.Groups[2].Value[2..]} · 第{m.Groups[3].Value}学期";
        }
        return name.Length > 18 ? name[..18] + "…" : name;
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
                Width = 6,
                Height = 6,
                Margin = new Thickness(0, 0, 5, 0),
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

        // 材质入场：缩放 + 淡入（reduced-motion 下直接出现）
        Card.Opacity = 0;
        Motion.SpringTo(_cardScale, ScaleTransform.ScaleXProperty, 1, 1.0, 0.42, tolerance: 0.0005);
        Motion.SpringTo(_cardScale, ScaleTransform.ScaleYProperty, 1, 1.0, 0.42, tolerance: 0.0005);
        Motion.SpringTo(Card, UIElement.OpacityProperty, 1, 1.0, 0.36, tolerance: 0.002);
        UpdateCursor();

        // 透明度恒定由托盘菜单控制（_cfg.Opacity），鼠标悬停不改变。

        ScheduleFit();
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
        UpdateCursor();
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
        Rect work = SystemParameters.WorkArea;
        if (!double.IsNaN(_cfg.X) && !double.IsNaN(_cfg.Y))
        {
            Left = _cfg.X;
            Top = _cfg.Y;
        }
        else
        {
            // 默认右下角
            Left = work.Right - LayoutWidth - 40;
            Top = work.Bottom - LayoutHeight - 60;
        }
        // 组件变高或屏幕布局变化后，确保整张卡片仍在工作区内
        Left = Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - LayoutWidth));
        Top = Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - LayoutHeight));
    }

    private double LayoutWidth => ActualWidth > 0 ? ActualWidth : Width;

    private double LayoutHeight => ActualHeight > 0 ? ActualHeight
        : (double.IsNaN(Height) ? 300 : Height);

    private void ClampToWorkArea()
    {
        Rect work = SystemParameters.WorkArea;
        // 卡片随内容增高，但不能超出屏幕可用高度；到顶后主体内部滚动
        MaxHeight = Math.Max(MinHeight, work.Height - 24);
        Left = Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - LayoutWidth));
        Top = Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - LayoutHeight));
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _store.Saved -= OnStoreSaved;
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

        // 拖动滚动条时不移动窗口
        if (e.OriginalSource is DependencyObject source &&
            FindAncestor<ScrollBar>(source) is not null)
        {
            return;
        }

        // 可在动量飞行中重新抓住卡片：停掉位置弹簧，从当前屏幕位置继续
        Motion.Stop(this, Window.LeftProperty);
        Motion.Stop(this, Window.TopProperty);

        _dragging = true;
        _dragMoved = false;
        _dragEnabled = _cfg.Draggable;
        _dragStartScreen = PointToScreen(e.GetPosition(this));
        _lastScreen = _dragStartScreen;
        _dragHistory.Clear();
        _dragClock.Restart();
        _dragHistory.Add((_dragStartScreen.X, _dragStartScreen.Y, 0));
        CaptureMouse();
        SpringCardScale(1.008, 0.2);
        e.Handled = false;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed || IsClickThroughNow()) return;

        Point screen = PointToScreen(e.GetPosition(this));
        double totalX = screen.X - _dragStartScreen.X;
        double totalY = screen.Y - _dragStartScreen.Y;

        // 10px 级迟滞：先判定点击还是拖动，避免手抖吞掉单击
        if (!_dragMoved && Math.Abs(totalX) < DragThreshold && Math.Abs(totalY) < DragThreshold)
        {
            _lastScreen = screen;
            return;
        }
        _dragMoved = true;
        e.Handled = true;

        if (!_dragEnabled)
        {
            _lastScreen = screen;
            return;
        }

        // 1:1 跟手（屏幕坐标增量），窗口靠近屏幕边缘时渐进阻尼
        double dx = screen.X - _lastScreen.X;
        double dy = screen.Y - _lastScreen.Y;
        _lastScreen = screen;

        Rect work = SystemParameters.WorkArea;
        Left = RubberBand(Left + dx, work.Left, work.Right - LayoutWidth, LayoutWidth);
        Top = RubberBand(Top + dy, work.Top, work.Bottom - LayoutHeight, LayoutHeight);

        RecordDrag(screen);
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        SpringCardScale(1.0, 0.22);

        if (!_dragMoved)
        {
            // 单击（按下-原地抬起，未拖动）→ 打开管理窗口。
            // 双击的第二击会再次触发本分支，但 EnsureManager 幂等（已开则激活），不会重复开窗。
            if (e.ChangedButton == MouseButton.Left && !IsClickThroughNow())
            {
                ManageRequested?.Invoke();
            }
            return;
        }

        _dragMoved = false;
        e.Handled = true;

        if (!_dragEnabled) return;

        (double vx, double vy) = ReleaseVelocity();
        Rect work = SystemParameters.WorkArea;
        double targetLeft = Math.Clamp(Project(Left, vx), work.Left, work.Right - LayoutWidth);
        double targetTop = Math.Clamp(Project(Top, vy), work.Top, work.Bottom - LayoutHeight);

        // 释放速度交接给弹簧：掠过一小段后落位，带一点动量回弹
        Motion.SpringTo(this, Window.LeftProperty, targetLeft,
            damping: 0.9, response: 0.42, velocity: vx, tolerance: 0.5, completed: SavePosition);
        Motion.SpringTo(this, Window.TopProperty, targetTop,
            damping: 0.9, response: 0.42, velocity: vy, tolerance: 0.5, completed: SavePosition);
    }

    /// <summary>允许/禁止左键拖动（关闭后仍可单击打开主窗口）。</summary>
    public void SetDraggable(bool value)
    {
        _cfg.Draggable = value;
        _cfg.Save(_store.Dir);
        UpdateCursor();
    }

    private void UpdateCursor()
    {
        Cursor = IsClickThroughNow() ? Cursors.Arrow
            : _cfg.Draggable ? Cursors.SizeAll
            : Cursors.Hand;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null && node is not T)
        {
            if (node is not Visual) return null;
            node = VisualTreeHelper.GetParent(node);
        }
        return node as T;
    }

    private void RecordDrag(Point screen)
    {
        double now = _dragClock.Elapsed.TotalMilliseconds;
        _dragHistory.Add((screen.X, screen.Y, now));
        while (_dragHistory.Count > 2 && now - _dragHistory[0].Ms > 120)
        {
            _dragHistory.RemoveAt(0);
        }
    }

    private (double Vx, double Vy) ReleaseVelocity()
    {
        if (_dragHistory.Count < 2) return (0, 0);
        (double x0, double y0, double t0) = _dragHistory[0];
        (double x1, double y1, double t1) = _dragHistory[^1];
        double dt = (t1 - t0) / 1000.0;
        if (dt <= 0.008) return (0, 0);
        return (Math.Clamp((x1 - x0) / dt, -4000, 4000),
                Math.Clamp((y1 - y0) / dt, -4000, 4000));
    }

    /// <summary>Apple 的动量投影：指数减速下还会滑行的距离（d≈0.99 更利落）。</summary>
    private static double Project(double current, double velocity, double decelerationRate = 0.99)
        => current + velocity / 1000.0 * decelerationRate / (1 - decelerationRate);

    /// <summary>边缘渐进阻尼：越过边界后跟手距离越来越短，而不是硬停。</summary>
    private static double RubberBand(double value, double min, double max, double dimension)
    {
        const double constant = 0.55;
        if (value < min)
        {
            double over = min - value;
            return min - over * dimension * constant / (dimension + constant * Math.Abs(over));
        }
        if (value > max)
        {
            double over = value - max;
            return max + over * dimension * constant / (dimension + constant * Math.Abs(over));
        }
        return value;
    }

    private void SpringCardScale(double to, double response)
    {
        Motion.SpringTo(_cardScale, ScaleTransform.ScaleXProperty, to,
            damping: 1.0, response: response, tolerance: 0.0008);
        Motion.SpringTo(_cardScale, ScaleTransform.ScaleYProperty, to,
            damping: 1.0, response: response, tolerance: 0.0008);
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
