using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using NcuCourseTable.Desktop.Common;
using NcuCourseTable.Desktop.Models;
using NcuCourseTable.Desktop.Services;

namespace NcuCourseTable.Desktop.ViewModels;

public enum SidebarMode
{
    Details,
    Todos,
    Notes,
}

/// <summary>
/// 主窗口 VM：学期/周导航、周视图日程、详情、日程增删改、待办管理、
/// 外部文件热更新与点击跳转。数据一律经 <see cref="DataStore"/> 落盘。
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly DataStore _store;
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _ticker;
    private DateTime _lastExternalChange = DateTime.MinValue;
    private bool _saving;

    private int _selectedWeek = 1;
    private int _selectedDay = DateTime.Today.DayOfWeek == DayOfWeek.Sunday ? 7
        : (int)DateTime.Today.DayOfWeek;
    private bool _autoFollowToday = true;
    private bool _alwaysOnTop;
    private bool _hasEvents;
    private ScheduleEvent? _selectedEvent;
    private SidebarMode _sidebar = SidebarMode.Details;
    private string _todoFilter = "pending";
    private string _newTodoText = "";
    private string _newNoteText = "";
    private string _noteSearch = "";
    private string? _noteTagFilter;
    private string _statusText = "";

    public MainViewModel(DataStore store)
    {
        _store = store;
        _store.Load();
        ApplyData();
        UpdateToday();
        RefreshTodos();
        RefreshNotes();
        StatusText = $"已载入 {_store.Data.Events.Count} 项日程 · {PendingTodoCount} 项待办";
    }

    public DataStore Store => _store;

    // ------------------------------------------------------------- 学期与周
    public string Termcode => _store.Data.Termcode;

    public string TermTitle => _store.Data.TermName.Length > 0
        ? _store.Data.TermName
        : (_store.Data.Termcode.Length > 0 ? DataStore.FormatTermName(_store.Data.Termcode) : "我的时间表");

    /// <summary>排版用学期标题：2026-2027学年第1学期 → 2026—2027 学年 · 第 1 学期。</summary>
    public string TermTitleDisplay
    {
        get
        {
            string t = TermTitle;
            if (!t.Contains("学年第", StringComparison.Ordinal)) return t;
            t = t.Replace("-", "—");
            t = t.Replace("学年第", " 学年 · 第 ");
            t = t.Replace("学期", " 学期");
            return t.Replace("  ", " ").Trim();
        }
    }

    public DateTime? TermStart => DateTime.TryParseExact(_store.Data.TermStart, "yyyy-MM-dd",
        CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    public int TotalWeeks => _store.Data.TotalWeeks > 0 ? _store.Data.TotalWeeks : 20;

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
            return $"{monday:MM.dd} — {sunday:MM.dd}";
        }
    }

    public bool HighlightToday => InTermNow && SelectedWeek == CurrentWeek;

    public string TodayHint => TermStart is null
        ? "未设置学期开始日"
        : InTermNow ? $"今天是第 {CurrentWeek} 周" : "当前不在教学周内";

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

    // ------------------------------------------------------------- 侧栏
    public SidebarMode Sidebar
    {
        get => _sidebar;
        set
        {
            if (Set(ref _sidebar, value))
            {
                OnPropertyChanged(nameof(IsDetailsMode));
                OnPropertyChanged(nameof(IsTodosMode));
                OnPropertyChanged(nameof(IsNotesMode));
            }
        }
    }

    public bool IsDetailsMode
    {
        get => _sidebar == SidebarMode.Details;
        set
        {
            if (value) Sidebar = SidebarMode.Details;
        }
    }

    public bool IsTodosMode
    {
        get => _sidebar == SidebarMode.Todos;
        set
        {
            if (value) Sidebar = SidebarMode.Todos;
        }
    }

    public bool IsNotesMode
    {
        get => _sidebar == SidebarMode.Notes;
        set
        {
            if (value) Sidebar = SidebarMode.Notes;
        }
    }

    // ------------------------------------------------------------- 选中日程
    public ScheduleEvent? SelectedEvent
    {
        get => _selectedEvent;
        set
        {
            if (!Set(ref _selectedEvent, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(DetailTime));
            OnPropertyChanged(nameof(DetailSchedule));
            OnPropertyChanged(nameof(SelectedEventColor));
            OnPropertyChanged(nameof(SelectedSourceNote));
            OnPropertyChanged(nameof(HasSourceNote));
            OnPropertyChanged(nameof(HasNotes));
            OnPropertyChanged(nameof(HasLocation));
            OnPropertyChanged(nameof(HasTeacher));
            OnPropertyChanged(nameof(DisplayLocation));
            OnPropertyChanged(nameof(DisplayTeacher));
            OnPropertyChanged(nameof(DisplayNotes));
        }
    }

    public bool HasSelection => SelectedEvent is not null;

    public Brush SelectedEventColor => SelectedEvent is { } e
        ? EventPalette.SolidBrush(e)
        : Brushes.Transparent;

    public string DetailTime
    {
        get
        {
            if (SelectedEvent is null) return "";
            var e = SelectedEvent;
            string t = e.TimeText(_store.SectionMap);
            return t.Length > 0 ? $"{e.SectionRangeText}  {t}" : e.SectionRangeText;
        }
    }

    public string DetailSchedule => SelectedEvent?.ScheduleText ?? "";

    public bool HasNotes => !string.IsNullOrWhiteSpace(SelectedEvent?.Notes);

    public bool HasLocation => !string.IsNullOrWhiteSpace(DisplayLocation);

    public string DisplayLocation => SelectedEvent?.DisplayLocation ?? "";

    public string DisplayTeacher => SelectedEvent?.DisplayTeacher ?? "";

    public bool HasTeacher => DisplayTeacher.Length > 0;

    public string DisplayNotes => SelectedEvent?.Notes ?? "";

    public bool HasSourceNote => SelectedEvent is { Source: not ("local" or "") };

    public string SelectedSourceNote => HasSourceNote
        ? "此日程由插件导入，重新同步时会覆盖这里的修改。"
        : "";

    // ------------------------------------------------------------- 数据是否为空
    public bool HasEvents
    {
        get => _hasEvents;
        private set
        {
            if (Set(ref _hasEvents, value))
            {
                OnPropertyChanged(nameof(EmptyVisible));
            }
        }
    }

    public bool EmptyVisible => !HasEvents;

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
    public event Func<ScheduleEvent, bool>? AddRequested;
    public event Func<ScheduleEvent, bool>? EditRequested;
    public event Func<TodoItem, bool>? TodoEditRequested;
    public event Func<NoteItem, bool>? NoteEditRequested;

    // ------------------------------------------------------------- 命令
    public RelayCommand PrevWeekCommand => new(_ => { AutoFollowToday = false; SelectedWeek--; });
    public RelayCommand NextWeekCommand => new(_ => { AutoFollowToday = false; SelectedWeek++; });
    public RelayCommand GotoTodayCommand => new(_ => GotoToday());
    public RelayCommand AddEventCommand => new(_ => StartAdd(_selectedDay, 1));
    public RelayCommand EditEventCommand => new(_ => RequestEdit(SelectedEvent!));
    public RelayCommand DeleteEventCommand => new(_ => Delete(SelectedEvent!));
    public RelayCommand OpenMapCommand => new(_ => { if (SelectedEvent is { } e) Openers.OpenMap(e); });
    public RelayCommand OpenDataDirCommand => new(_ => Openers.OpenDataDir(_store.Dir));
    public RelayCommand ReloadCommand => new(_ => ReloadFromDisk());
    public RelayCommand AddTodoCommand => new(_ => AddQuickTodo());
    public RelayCommand AddTodoDetailedCommand => new(_ => AddTodoDetailed());
    public RelayCommand ToggleTodoCommand => new(p => { if (p is TodoItem t) ToggleTodo(t); });
    public RelayCommand DeleteTodoCommand => new(p => { if (p is TodoItem t) DeleteTodo(t); });
    public RelayCommand ClearCompletedCommand => new(_ => ClearCompleted());
    public RelayCommand SetTodoFilterCommand => new(p => TodoFilter = p as string ?? "pending");
    public RelayCommand AddNoteCommand => new(_ => AddNote());
    public RelayCommand DeleteNoteCommand => new(p => { if (p is NoteItem n) DeleteNote(n); });
    public RelayCommand ToggleNotePinCommand => new(p => { if (p is NoteItem n) ToggleNotePin(n); });
    public RelayCommand SetNoteTagCommand => new(p => { if (p is string tag) SetNoteTagFilter(tag); });
    public RelayCommand ClearNoteFilterCommand => new(_ => ClearNoteFilters());

    public List<ScheduleEvent> Events => _store.Data.Events.ToList();

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

    // ------------------------------------------------------------- 待办
    public string NewTodoText
    {
        get => _newTodoText;
        set
        {
            if (Set(ref _newTodoText, value)) OnPropertyChanged(nameof(HasNewTodoText));
        }
    }

    public bool HasNewTodoText => !string.IsNullOrWhiteSpace(_newTodoText);

    public string TodoFilter
    {
        get => _todoFilter;
        set
        {
            value = value switch { "all" => "all", "done" => "done", _ => "pending" };
            if (!Set(ref _todoFilter, value)) return;
            OnPropertyChanged(nameof(IsPendingFilter));
            OnPropertyChanged(nameof(IsAllFilter));
            OnPropertyChanged(nameof(IsDoneFilter));
            RefreshTodos();
        }
    }

    public bool IsPendingFilter
    {
        get => _todoFilter == "pending";
        set { if (value) TodoFilter = "pending"; }
    }

    public bool IsAllFilter
    {
        get => _todoFilter == "all";
        set { if (value) TodoFilter = "all"; }
    }

    public bool IsDoneFilter
    {
        get => _todoFilter == "done";
        set { if (value) TodoFilter = "done"; }
    }

    public List<TodoItem> VisibleTodos { get; private set; } = new();

    public int PendingTodoCount => _store.Data.Todos.Count(t => !t.Done);

    public int CompletedTodoCount => _store.Data.Todos.Count(t => t.Done);

    public bool HasPendingTodos => PendingTodoCount > 0;

    public bool HasVisibleTodos => VisibleTodos.Count > 0;

    public string PendingTodoBadge => PendingTodoCount > 0 ? PendingTodoCount.ToString() : "";

    public string TodoFooter =>
        $"共 {_store.Data.Todos.Count} 项 · 未完成 {PendingTodoCount} 项 · 已完成 {CompletedTodoCount} 项";

    public bool CanClearCompleted => CompletedTodoCount > 0;

    // ------------------------------------------------------------- 生命周期
    public void StartAutoRefresh()
    {
        try
        {
            _watcher = new FileSystemWatcher(_store.Dir, DataStore.FileName)
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

    public void UpdateToday()
    {
        DateTime today = DateTime.Today;
        TodayDay = ScheduleEvent.WeekdayOf(today);
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
                InTermNow = false;
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
    private void ApplyData()
    {
        _store.Normalize();
        HasEvents = _store.Data.Events.Count > 0;
        OnPropertyChanged(nameof(Termcode));
        OnPropertyChanged(nameof(TermTitle));
        OnPropertyChanged(nameof(TermTitleDisplay));
        OnPropertyChanged(nameof(TermStart));
        OnPropertyChanged(nameof(TotalWeeks));
        OnPropertyChanged(nameof(SectionList));
        OnPropertyChanged(nameof(Events));
        OnPropertyChanged(nameof(WeekDatesText));
        OnPropertyChanged(nameof(TodayHint));
        if (_selectedWeek > TotalWeeks)
        {
            _selectedWeek = TotalWeeks;
            OnPropertyChanged(nameof(SelectedWeek));
            OnPropertyChanged(nameof(WeekLabelText));
            OnPropertyChanged(nameof(WeekDatesText));
        }
        OnPropertyChanged(nameof(PendingTodoCount));
        OnPropertyChanged(nameof(CompletedTodoCount));
        OnPropertyChanged(nameof(HasPendingTodos));
        OnPropertyChanged(nameof(PendingTodoBadge));
        OnPropertyChanged(nameof(TodoFooter));
        OnPropertyChanged(nameof(CanClearCompleted));
    }

    public List<SectionTime> SectionList => _store.Data.Sections;

    public void Select(ScheduleEvent e)
    {
        SelectedEvent = e;
        Sidebar = SidebarMode.Details;
        if (SelectedDay != e.Weekday && !e.IsOneOff)
        {
            SelectedDay = e.Weekday;
        }
    }

    public void StartAdd(int weekday, int section)
    {
        var draft = new ScheduleEvent
        {
            Category = EventCategories.Default,
            Weekday = weekday,
            StartSection = section,
            EndSection = section,
            WeeksRaw = $"1-{TotalWeeks}周",
            Source = "local",
        };
        if (AddRequested?.Invoke(draft) == true)
        {
            _store.Data.Events.Add(draft);
            HasEvents = true;
            SelectedEvent = draft;
            Select(draft);
            Persist("已添加日程：" + draft.Title);
        }
    }

    public void RequestEdit(ScheduleEvent scheduleEvent)
    {
        if (scheduleEvent is null) return;
        var draft = scheduleEvent.Clone();
        if (EditRequested?.Invoke(draft) == true)
        {
            scheduleEvent.CopyFrom(draft);
            Persist("已保存日程：" + scheduleEvent.Title);
            RaiseSelectionChanged();
        }
    }

    public void Delete(ScheduleEvent scheduleEvent)
    {
        if (scheduleEvent is null) return;
        string extra = scheduleEvent.Source is "local" or "" ? "" : "\n（插件日程会在下次同步时恢复）";
        string msg = $"删除“{scheduleEvent.Title}”？{extra}";
        if (MessageBox.Show(msg, "删除日程", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        _store.Data.Events.Remove(scheduleEvent);
        if (SelectedEvent == scheduleEvent) SelectedEvent = null;
        HasEvents = _store.Data.Events.Count > 0;
        Persist("已删除日程：" + scheduleEvent.Title);
    }

    public void ReloadFromDisk() => ReloadFromDisk(silent: false);

    public void ReloadFromDisk(bool silent)
    {
        _store.Load();
        ApplyData();
        UpdateToday();
        SelectedEvent = null;
        RefreshTodos();
        RefreshNotes();
        if (!silent) StatusText = "已从磁盘重新载入";
    }

    public void SetStatus(string status) => StatusText = status;

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
        HasEvents = _store.Data.Events.Count > 0;
        OnPropertyChanged(nameof(SectionList));
        OnPropertyChanged(nameof(Events));
        OnPropertyChanged(nameof(TermTitle));
        OnPropertyChanged(nameof(TermTitleDisplay));
        OnPropertyChanged(nameof(TermStart));
        OnPropertyChanged(nameof(TotalWeeks));
        OnPropertyChanged(nameof(SelectedEventColor));
        RaiseSelectionChanged();
        RefreshTodoCounts();
    }

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(DetailTime));
        OnPropertyChanged(nameof(DetailSchedule));
        OnPropertyChanged(nameof(SelectedEventColor));
        OnPropertyChanged(nameof(SelectedSourceNote));
        OnPropertyChanged(nameof(HasSourceNote));
        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(HasLocation));
        OnPropertyChanged(nameof(HasTeacher));
        OnPropertyChanged(nameof(DisplayLocation));
        OnPropertyChanged(nameof(DisplayTeacher));
        OnPropertyChanged(nameof(DisplayNotes));
    }

    // ------------------------------------------------------------- 待办操作
    public void AddQuickTodo()
    {
        string title = (NewTodoText ?? "").Trim();
        if (title.Length == 0) return;
        _store.Data.Todos.Add(new TodoItem { Title = title });
        NewTodoText = "";
        TodoFilter = "pending";
        Sidebar = SidebarMode.Todos;
        PersistToday("已添加待办：" + title);
    }

    private void AddTodoDetailed()
    {
        var draft = new TodoItem();
        Sidebar = SidebarMode.Todos;
        if (TodoEditRequested?.Invoke(draft) != true) return;
        _store.Data.Todos.Add(draft);
        TodoFilter = "pending";
        PersistToday("已添加待办：" + draft.Title);
    }

    public void EditTodo(TodoItem todo)
    {
        if (todo is null) return;
        var draft = todo.Clone();
        if (TodoEditRequested?.Invoke(draft) != true) return;
        TodoItem target = ResolveTodo(todo);
        target.CopyFrom(draft);
        PersistToday("已保存待办：" + target.Title);
    }

    public void ToggleTodo(TodoItem todo)
    {
        if (todo is null) return;
        TodoItem target = ResolveTodo(todo);
        target.Toggle();
        PersistToday(target.Done ? "已完成待办：" + target.Title : "已恢复待办：" + target.Title);
    }

    public void DeleteTodo(TodoItem todo)
    {
        if (todo is null) return;
        TodoItem target = ResolveTodo(todo);
        _store.Data.Todos.Remove(target);
        PersistToday("已删除待办：" + target.Title);
    }

    public void SetTodoPriority(TodoItem todo, int level)
    {
        if (todo is null) return;
        TodoItem target = ResolveTodo(todo);
        level = Math.Clamp(level, 0, 2);
        if (target.Priority == level) return;
        target.Priority = level;
        PersistToday($"已将「{target.Title}」设为{target.PriorityLabel}");
    }

    /// <summary>
    /// 按 Id 取当前数据里的同一条待办。弹窗/弹出菜单期间数据可能被整体替换
    /// （同步 Replace、外部文件重载），列表里持有的旧引用会变成游离对象，
    /// 直接改它会写进副本、保存后看不到变化。
    /// </summary>
    private TodoItem ResolveTodo(TodoItem todo) =>
        _store.Data.Todos.FirstOrDefault(t => t.Id == todo.Id) ?? todo;

    public void ClearCompleted()
    {
        int n = _store.Data.Todos.RemoveAll(t => t.Done);
        if (n == 0) return;
        PersistToday($"已清除 {n} 项已完成待办");
    }

    private void PersistToday(string status)
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
        RefreshTodoCounts();
        RefreshTodos();
    }

    private void RefreshTodoCounts()
    {
        OnPropertyChanged(nameof(PendingTodoCount));
        OnPropertyChanged(nameof(CompletedTodoCount));
        OnPropertyChanged(nameof(HasPendingTodos));
        OnPropertyChanged(nameof(PendingTodoBadge));
        OnPropertyChanged(nameof(TodoFooter));
        OnPropertyChanged(nameof(CanClearCompleted));
    }

    private void RefreshTodos()
    {
        IEnumerable<TodoItem> query = _store.Data.Todos;
        query = _todoFilter switch
        {
            "done" => query.Where(t => t.Done)
                .OrderByDescending(t => t.CompletedAt),
            "all" => query.OrderBy(t => t.Done)
                .ThenBy(t => t.HasDue ? 0 : 1)
                .ThenBy(t => t.Due ?? DateTime.MaxValue)
                .ThenByDescending(t => t.Priority)
                .ThenByDescending(t => t.CreatedAt),
            _ => query.Where(t => !t.Done)
                .OrderBy(t => t.HasDue ? 0 : 1)
                .ThenBy(t => t.Due ?? DateTime.MaxValue)
                .ThenByDescending(t => t.Priority)
                .ThenByDescending(t => t.CreatedAt),
        };
        VisibleTodos = query.ToList();
        OnPropertyChanged(nameof(VisibleTodos));
        OnPropertyChanged(nameof(HasVisibleTodos));
        RefreshTodoCounts();
    }

    // ------------------------------------------------------------- 笔记
    public string NewNoteText
    {
        get => _newNoteText;
        set
        {
            if (Set(ref _newNoteText, value)) OnPropertyChanged(nameof(HasNewNoteText));
        }
    }

    public bool HasNewNoteText => !string.IsNullOrWhiteSpace(_newNoteText);

    public string NoteSearch
    {
        get => _noteSearch;
        set
        {
            if (Set(ref _noteSearch, value)) RefreshNotes();
        }
    }

    public string? NoteTagFilter
    {
        get => _noteTagFilter;
        private set
        {
            if (Set(ref _noteTagFilter, value))
            {
                OnPropertyChanged(nameof(IsAllNotesTag));
                OnPropertyChanged(nameof(HasNoteFilter));
            }
        }
    }

    public bool IsAllNotesTag => string.IsNullOrEmpty(_noteTagFilter);

    public bool HasNoteFilter => !IsAllNotesTag || (_noteSearch ?? "").Length > 0;

    public List<NoteItem> VisibleNotes { get; private set; } = new();

    public List<NoteTagChip> NoteTags { get; private set; } = new();

    public bool HasVisibleNotes => VisibleNotes.Count > 0;

    public bool HasAnyNotes => _store.Data.Notes.Count > 0;

    public string NoteEmptyText => HasAnyNotes ? "没有匹配的笔记" : "还没有笔记，写下第一条想法";

    public string NoteFooter =>
        $"共 {_store.Data.Notes.Count} 条 · 置顶 {_store.Data.Notes.Count(n => n.Pinned)} 条";

    public void AddNote()
    {
        string content = (NewNoteText ?? "").Trim();
        if (content.Length == 0) return;
        _store.Data.Notes.Add(new NoteItem { Content = content });
        NewNoteText = "";
        Sidebar = SidebarMode.Notes;
        PersistNotes("已保存笔记");
    }

    public void EditNote(NoteItem note)
    {
        if (note is null) return;
        var draft = note.Clone();
        if (NoteEditRequested?.Invoke(draft) != true) return;
        ResolveNote(note).CopyFrom(draft);
        PersistNotes("已更新笔记");
    }

    public void DeleteNote(NoteItem note)
    {
        if (note is null) return;
        _store.Data.Notes.Remove(ResolveNote(note));
        PersistNotes("已删除笔记");
    }

    public void ToggleNotePin(NoteItem note)
    {
        if (note is null) return;
        NoteItem target = ResolveNote(note);
        target.Pinned = !target.Pinned;
        target.Touch();
        PersistNotes(target.Pinned ? "已置顶笔记" : "已取消置顶");
    }

    public void SetNoteTagFilter(string? tag)
    {
        bool same = string.Equals(tag, _noteTagFilter, StringComparison.OrdinalIgnoreCase);
        NoteTagFilter = same ? null : tag;
        RefreshNotes();
    }

    public void SetNoteColor(NoteItem note, string? color)
    {
        if (note is null) return;
        string key = color ?? "";
        NoteItem target = ResolveNote(note);
        if (target.Color == key) return;
        target.Color = key;
        target.Touch();
        PersistNotes("已更换笔记颜色");
    }

    public void ClearNoteFilters()
    {
        _noteSearch = "";
        OnPropertyChanged(nameof(NoteSearch));
        NoteTagFilter = null;
        RefreshNotes();
    }

    /// <summary>按 Id 取当前数据里的同一条笔记（数据可能被同步整体替换）。</summary>
    private NoteItem ResolveNote(NoteItem note) =>
        _store.Data.Notes.FirstOrDefault(n => n.Id == note.Id) ?? note;

    private void PersistNotes(string status)
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
        RefreshNotes();
    }

    private void RefreshNotes()
    {
        IEnumerable<NoteItem> query = _store.Data.Notes;
        if (!string.IsNullOrEmpty(_noteTagFilter))
        {
            query = query.Where(n => n.Tags.Contains(_noteTagFilter, StringComparer.OrdinalIgnoreCase));
        }

        string search = (_noteSearch ?? "").Trim().TrimStart('#');
        if (search.Length > 0)
        {
            query = query.Where(n =>
                n.Content.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                n.Tags.Any(t => t.Contains(search, StringComparison.OrdinalIgnoreCase)));
        }

        VisibleNotes = query
            .OrderByDescending(n => n.Pinned)
            .ThenByDescending(n => n.CreatedAt)
            .ToList();

        NoteTags = _store.Data.Notes
            .SelectMany(n => n.Tags)
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new NoteTagChip
            {
                Tag = g.Key,
                Count = g.Count(),
                IsActive = string.Equals(g.Key, _noteTagFilter, StringComparison.OrdinalIgnoreCase),
            })
            .ToList();

        OnPropertyChanged(nameof(VisibleNotes));
        OnPropertyChanged(nameof(NoteTags));
        OnPropertyChanged(nameof(HasVisibleNotes));
        OnPropertyChanged(nameof(HasAnyNotes));
        OnPropertyChanged(nameof(NoteEmptyText));
        OnPropertyChanged(nameof(NoteFooter));
        OnPropertyChanged(nameof(HasNoteFilter));
    }

    // ------------------------------------------------------------- 文件监视
    private void OnExternalFileChange()
    {
        DateTime now = DateTime.UtcNow;
        bool justReplaced = (now - _store.LastProgrammaticWriteUtc).TotalMilliseconds < 1500;
        if (_saving || justReplaced || (now - _lastExternalChange).TotalMilliseconds < 400)
        {
            _lastExternalChange = now;
            return;
        }
        _lastExternalChange = now;
        Application.Current?.Dispatcher.BeginInvoke(() => ReloadFromDisk(silent: true));
    }
}

/// <summary>标签筛选 chip：#标签 + 该标签下的笔记数量。</summary>
public sealed class NoteTagChip
{
    public string Tag { get; init; } = "";

    public int Count { get; init; }

    public bool IsActive { get; init; }

    public string Label => $"#{Tag} {Count}";
}
