using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using NcuCourseTable.Desktop.Common;
using NcuCourseTable.Desktop.Models;
using NcuCourseTable.Desktop.Services;

namespace NcuCourseTable.Desktop.ViewModels;

/// <summary>
/// 主窗口 VM：学期/周导航、周视图数据、课程详情、课程增删改、自动刷新、
/// 外部 JSON 热更新、点击跳转。
/// 数据一律经 <see cref="JsonStore"/> 落盘（本地 JSON + .bak 备份）。
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly JsonStore _store;
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _ticker;
    private DateTime _lastExternalChange = DateTime.MinValue;
    private bool _saving;

    // ---- 展示状态
    private int _selectedWeek = 1;
    private int _selectedDay = DateTime.Today.DayOfWeek == DayOfWeek.Sunday ? 7
        : (int)DateTime.Today.DayOfWeek; // 周一=1..周日=7
    private bool _autoFollowToday = true;
    private bool _alwaysOnTop;
    private bool _hasCourses;
    private Course? _selectedCourse;
    private string _statusText = "";

    public MainViewModel(JsonStore store)
    {
        _store = store;
        _store.Load();
        ApplyTerm();
        UpdateToday();
    }

    public JsonStore Store => _store;

    // ------------------------------------------------------------- 学期与周
    public string Termcode => _store.Data.Termcode;

    public string TermTitle => _store.Data.TermName.Length > 0
        ? _store.Data.TermName
        : (_store.Data.Termcode.Length > 0 ? $"学期 {_store.Data.Termcode}" : "未命名学期");

    public DateTime? TermStart => DateTime.TryParseExact(_store.Data.TermStart, "yyyy-MM-dd",
        CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    public int TotalWeeks => _store.Data.TotalWeeks > 0 ? _store.Data.TotalWeeks : 20;

    /// <summary>今天是第几周（1..TotalWeeks；未开学/已结束取 1）。</summary>
    public int CurrentWeek { get; private set; } = 1;

    public bool InTermNow { get; private set; } = true;

    public int SelectedWeek
    {
        get => _selectedWeek;
        set
        {
            value = Math.Clamp(value, 1, TotalWeeks);
            if (Set(ref _selectedWeek, value))
            {
                OnPropertyChanged(nameof(WeekLabelText));
                OnPropertyChanged(nameof(WeekDatesText));
                OnPropertyChanged(nameof(HighlightToday));
                OnPropertyChanged(nameof(TodayHint));
            }
        }
    }

    public string WeekLabelText => $"{_selectedWeek} / {TotalWeeks}";

    public string WeekDatesText
    {
        get
        {
            DateTime? start = TermStart;
            if (start is null) return "";
            DateTime monday = start.Value.AddDays((_selectedWeek - 1) * 7);
            DateTime sunday = monday.AddDays(6);
            return $"{monday:MM-dd} ~ {sunday:MM-dd}";
        }
    }

    public bool HighlightToday => InTermNow && SelectedWeek == CurrentWeek;

    public string TodayHint => InTermNow ? $"今天是第 {CurrentWeek} 周" : "当前不在教学周内";

    // ------------------------------------------------------------- 选中日（周视图列高亮 / 新建课程默认日）
    public int SelectedDay
    {
        get => _selectedDay;
        set
        {
            value = Math.Clamp(value, 1, 7);
            Set(ref _selectedDay, value);
        }
    }

    public int TodayDay { get; private set; }

    // ------------------------------------------------------------- 选中课程详情
    public Course? SelectedCourse
    {
        get => _selectedCourse;
        set
        {
            if (Set(ref _selectedCourse, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(DetailTime));
            }
        }
    }

    public bool HasSelection => SelectedCourse is not null;

    public string DetailTime
    {
        get
        {
            if (SelectedCourse is null) return "";
            var c = SelectedCourse;
            string t = c.TimeText(_store.SectionMap);
            return t.Length > 0 ? $"{c.SectionRangeText}  {t}" : c.SectionRangeText;
        }
    }

    // ------------------------------------------------------------- 数据是否为空
    public bool HasCourses
    {
        get => _hasCourses;
        private set
        {
            if (Set(ref _hasCourses, value))
            {
                OnPropertyChanged(nameof(EmptyVisible));
            }
        }
    }

    public bool EmptyVisible => !HasCourses;

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public bool AlwaysOnTop
    {
        get => _alwaysOnTop;
        set
        {
            if (Set(ref _alwaysOnTop, value))
            {
                TopmostChanged?.Invoke(value);
            }
        }
    }

    public event Action<bool>? TopmostChanged;

    // 对话框请求（由视图层实现，返回 true 表示确认）
    public event Func<Course, bool>? AddRequested;
    public event Func<Course, bool>? EditRequested;

    // ------------------------------------------------------------- 命令
    public RelayCommand PrevWeekCommand => new(_ => { AutoFollowToday = false; SelectedWeek--; });
    public RelayCommand NextWeekCommand => new(_ => { AutoFollowToday = false; SelectedWeek++; });
    public RelayCommand GotoTodayCommand => new(_ => GotoToday());
    public RelayCommand AddCourseCommand => new(_ => StartAdd(_selectedDay, 1));
    public RelayCommand EditCourseCommand => new(_ => RequestEdit(SelectedCourse!));
    public RelayCommand DeleteCourseCommand => new(_ => Delete(SelectedCourse!));
    public RelayCommand OpenMapCommand => new(_ => { if (SelectedCourse is { } c) Openers.OpenMap(c); });
    public RelayCommand OpenPortalCommand => new(_ =>
        Openers.OpenLinkFromStore(_store.Data, "portal", "https://portal.ncu.edu.cn"));
    public RelayCommand OpenDataDirCommand => new(_ => Openers.OpenDataDir(_store.Dir));
    public RelayCommand ReloadCommand => new(_ => ReloadFromDisk());
    public RelayCommand LoadDemoCommand => new(_ => LoadDemo());
    public RelayCommand ImportJsonCommand => new(_ => ImportJsonDialog());
    public event Action? ImportJsonDialogRequested;

    /// <summary>周视图画布的数据源（每次变更都发新快照以触发重绘）。</summary>
    public List<Course> Courses => _store.Data.Courses.ToList();

    public bool AutoFollowToday
    {
        get => _autoFollowToday;
        set
        {
            if (Set(ref _autoFollowToday, value) && value)
            {
                UpdateToday();
            }
        }
    }

    // ------------------------------------------------------------- 生命周期
    public void StartAutoRefresh()
    {
        try
        {
            _watcher = new FileSystemWatcher(_store.Dir, JsonStore.FileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => OnExternalFileChange();
            _watcher.Created += (_, _) => OnExternalFileChange();
        }
        catch (Exception ex)
        {
            StatusText = $"文件监视不可用：{ex.Message}";
        }

        _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _ticker.Tick += (_, _) => UpdateToday();
        _ticker.Start();
    }

    public void StopAutoRefresh()
    {
        _watcher?.Dispose();
        _watcher = null;
        _ticker?.Stop();
        _ticker = null;
    }

    /// <summary>应用当前日期：跟随今天时自动切换周。定时器 + 启动时调用。</summary>
    public void UpdateToday()
    {
        DateTime today = DateTime.Today;
        TodayDay = today.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)today.DayOfWeek;
        OnPropertyChanged(nameof(TodayDay));

        DateTime? start = TermStart;
        if (start is null)
        {
            InTermNow = false;
            CurrentWeek = 1;
        }
        else
        {
            int days = (today - start.Value.Date).Days;
            if (days < 0)
            {
                InTermNow = false; // 未开学
                CurrentWeek = 1;
            }
            else
            {
                int wk = days / 7 + 1;
                InTermNow = wk <= TotalWeeks;
                CurrentWeek = Math.Clamp(wk, 1, TotalWeeks);
            }
        }
        OnPropertyChanged(nameof(CurrentWeek));
        OnPropertyChanged(nameof(InTermNow));
        OnPropertyChanged(nameof(TodayHint));
        OnPropertyChanged(nameof(HighlightToday));

        if (AutoFollowToday)
        {
            SelectedWeek = CurrentWeek;
            SelectedDay = TodayDay;
        }
        else
        {
            OnPropertyChanged(nameof(HighlightToday));
        }
    }

    public void GotoToday()
    {
        AutoFollowToday = true;
        UpdateToday();
    }

    // ------------------------------------------------------------- 数据操作
    private void ApplyTerm()
    {
        _store.Normalize();
        HasCourses = _store.Data.Courses.Count > 0;
        OnPropertyChanged(nameof(Termcode));
        OnPropertyChanged(nameof(TermTitle));
        OnPropertyChanged(nameof(TermStart));
        OnPropertyChanged(nameof(TotalWeeks));
        OnPropertyChanged(nameof(SectionList));
        OnPropertyChanged(nameof(Courses));
        StatusText = $"已载入 {_store.Data.Courses.Count} 门课 · 数据文件：{_store.FilePath}";
    }

    public List<SectionTime> SectionList => _store.Data.Sections;

    public void Select(Course c)
    {
        SelectedCourse = c;
        if (SelectedDay != c.Weekday)
        {
            SelectedDay = c.Weekday; // 右侧联动该日
        }
    }

    public void StartAdd(int weekday, int section)
    {
        var draft = new Course
        {
            Termcode = Termcode,
            Weekday = weekday,
            StartSection = section,
            EndSection = section,
            WeeksRaw = $"1-{TotalWeeks}周",
        };
        if (AddRequested?.Invoke(draft) == true)
        {
            _store.Data.Courses.Add(draft);
            HasCourses = true;
            SelectedCourse = draft;
            Select(draft);
            Persist("已添加课程：" + draft.Name);
        }
    }

    public void RequestEdit(Course course)
    {
        if (course is null) return;
        var draft = course.Clone();
        if (EditRequested?.Invoke(draft) == true)
        {
            CopyCourse(draft, course);
            Persist("已保存课程：" + course.Name);
        }
    }

    public void Delete(Course course)
    {
        if (course is null) return;
        var msg = $"删除“{course.Name}（{course.WeekdayName()}{course.SectionRangeText}）”？";
        if (MessageBox.Show(msg, "删除课程", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        _store.Data.Courses.Remove(course);
        if (SelectedCourse == course) SelectedCourse = null;
        HasCourses = _store.Data.Courses.Count > 0;
        Persist("已删除课程：" + course.Name);
    }

    public void LoadDemo()
    {
        _store.FillDemo();
        ApplyTerm();
        UpdateToday();
        Persist("已载入演示课表（2026-2027-1 样例）");
    }

    public void ReloadFromDisk() => ReloadFromDisk(silent: false);

    private void ReloadFromDisk(bool silent)
    {
        _store.Load();
        ApplyTerm();
        UpdateToday();
        SelectedCourse = null;
        if (!silent) StatusText = "已从磁盘重新载入";
    }

    public void ImportJson(string path)
    {
        try
        {
            int n = _store.ImportNcuJson(path);
            ReloadFromDisk(silent: true);
            StatusText = $"导入完成：{n} 门课 <- {path}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导入失败：{ex.Message}", "导入 JSON");
        }
    }

    private void ImportJsonDialog() => ImportJsonDialogRequested?.Invoke();

    private void Persist(string status)
    {
        _saving = true;
        try
        {
            _store.Save();
        }
        finally
        {
            _saving = false;
        }
        StatusText = status;
        OnPropertyChanged(nameof(SectionList));
        OnPropertyChanged(nameof(Courses));
    }

    // ------------------------------------------------------------- 文件监视
    private void OnExternalFileChange()
    {
        // 防抖：自身保存也会触发事件；且跳过 400ms 内的重复事件
        DateTime now = DateTime.UtcNow;
        if (_saving || (now - _lastExternalChange).TotalMilliseconds < 400)
        {
            _lastExternalChange = now;
            return;
        }
        _lastExternalChange = now;
        Application.Current?.Dispatcher.BeginInvoke(() => ReloadFromDisk(silent: true));
    }

    private static void CopyCourse(Course from, Course to)
    {
        to.Name = from.Name;
        to.Teacher = from.Teacher;
        to.Room = from.Room;
        to.Weekday = from.Weekday;
        to.StartSection = from.StartSection;
        to.EndSection = from.EndSection;
        to.WeeksRaw = from.WeeksRaw;
        to.Raw = from.Raw;
    }
}

internal static class CourseExt
{
    public static string WeekdayName(this Course c) =>
        new[] { "周一", "周二", "周三", "周四", "周五", "周六", "周日" }[Math.Clamp(c.Weekday, 1, 7) - 1];
}
