using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
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
    private TodoItem? _priorityTodo;
    private NoteItem? _noteColorNote;

    /// <summary>工具栏 / 空态“插件”入口（由 App 层打开插件管理窗口）。</summary>
    public event Action? PluginsRequested;

    /// <summary>工具栏“设置”入口（由 App 层打开设置面板）。</summary>
    public event Action? SettingsRequested;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        _lastWeek = vm.SelectedWeek;

        BoardHost.RenderTransform = _boardShift;
        DetailContent.RenderTransform = _detailShift;

        _vm.AddRequested += draft => ShowEventDialog(draft, "新建日程");
        _vm.EditRequested += draft => ShowEventDialog(draft, "编辑日程");
        _vm.TodoEditRequested += draft => ShowTodoDialog(draft,
            string.IsNullOrWhiteSpace(draft.Title) ? "新建待办" : "编辑待办");
        _vm.NoteEditRequested += draft => ShowNoteDialog(draft,
            string.IsNullOrWhiteSpace(draft.Content) ? "新建笔记" : "编辑笔记");
        _vm.TopmostChanged += on => Topmost = on;
        _vm.PropertyChanged += OnViewModelPropertyChanged;

        Board.EventClicked += e => _vm.Select(e);
        Board.EventDoubleClicked += e => _vm.RequestEdit(e);
        Board.SlotDoubleClicked += (day, section) => _vm.StartAdd(day, section);

        TodoQuickBox.TextChanged += (_, _) => UpdateTodoPlaceholder();

        Loaded += (_, _) =>
        {
            Motion.ScaleFadeIn(Root, 0.988, 0.4);
            _vm.StartAutoRefresh();
            UpdateTodoPlaceholder();
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
        else if (e.PropertyName == nameof(MainViewModel.SelectedEvent))
        {
            Dispatcher.BeginInvoke(new Action(AnimateDetail), DispatcherPriority.Loaded);
        }
        else if (e.PropertyName == nameof(MainViewModel.Sidebar))
        {
            if (_vm.IsDetailsMode) Dispatcher.BeginInvoke(new Action(AnimateDetail), DispatcherPriority.Loaded);
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

    private void AnimateDetail()
    {
        if (_vm.SelectedEvent is null || !_vm.IsDetailsMode || DetailContent.Visibility != Visibility.Visible)
        {
            return;
        }

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

    private bool ShowEventDialog(ScheduleEvent draft, string title)
    {
        var dialog = new EventDialog(draft, _vm.SectionList, _vm.Events,
            _vm.TermStart, _vm.TotalWeeks, _vm.SelectedWeek, title)
        {
            Owner = this,
        };
        return dialog.ShowDialog() == true;
    }

    private bool ShowTodoDialog(TodoItem draft, string title)
    {
        var dialog = new TodoDialog(draft, title) { Owner = this };
        return dialog.ShowDialog() == true;
    }

    private bool ShowNoteDialog(NoteItem draft, string title)
    {
        var dialog = new NoteDialog(draft, title) { Owner = this };
        return dialog.ShowDialog() == true;
    }

    private void OnNoteInputKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0) return;
        _vm.AddNote();
        e.Handled = true;
    }

    private void OnNoteCardMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2) return;
        if (FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) is not null) return;
        if ((sender as FrameworkElement)?.DataContext is NoteItem note)
        {
            _vm.EditNote(note);
            e.Handled = true;
        }
    }

    private void OnNoteColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement source || source.DataContext is not NoteItem note) return;
        _noteColorNote = note;
        string key = note.Color ?? "";
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
            bool isActive = string.Equals(k, key, StringComparison.OrdinalIgnoreCase);
            swatch.BorderThickness = new Thickness(isActive ? 2 : 1);
            swatch.BorderBrush = isActive ? active : idle;
        }
        NoteColorMenu.PlacementTarget = source;
        NoteColorMenu.IsOpen = true;
    }

    private void OnNoteColorPick(object sender, RoutedEventArgs e)
    {
        if (_noteColorNote is { } note && sender is FrameworkElement item && item.Tag is string key)
        {
            _vm.SetNoteColor(note, key);
        }
        NoteColorMenu.IsOpen = false;
        _noteColorNote = null;
    }

    private void OnTodoQuickKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        _vm.AddQuickTodo();
        e.Handled = true;
    }

    private void OnTodoPriorityClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement source || source.DataContext is not TodoItem todo) return;
        _priorityTodo = todo;
        int level = Math.Clamp(todo.Priority, 0, 2);
        PriorityCheck0.Visibility = level == 0 ? Visibility.Visible : Visibility.Collapsed;
        PriorityCheck1.Visibility = level == 1 ? Visibility.Visible : Visibility.Collapsed;
        PriorityCheck2.Visibility = level == 2 ? Visibility.Visible : Visibility.Collapsed;
        PriorityMenu.PlacementTarget = source;
        PriorityMenu.IsOpen = true;
    }

    private void OnPriorityMenuPick(object sender, RoutedEventArgs e)
    {
        if (_priorityTodo is { } todo && sender is FrameworkElement item
            && int.TryParse(item.Tag?.ToString(), out int level))
        {
            _vm.SetTodoPriority(todo, level);
        }
        PriorityMenu.IsOpen = false;
        _priorityTodo = null;
    }

    private void OnTodoRowMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2) return;
        if (FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }
        if ((sender as FrameworkElement)?.DataContext is TodoItem todo)
        {
            _vm.EditTodo(todo);
            e.Handled = true;
        }
    }

    private void UpdateTodoPlaceholder()
    {
        TodoPlaceholder.Visibility = TodoQuickBox.Text.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
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

    private void OnPluginsClick(object sender, RoutedEventArgs e) => PluginsRequested?.Invoke();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
}
