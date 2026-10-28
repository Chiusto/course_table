using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using NcuCourseTable.Desktop.Interop;
using NcuCourseTable.Desktop.Services;

namespace NcuCourseTable.Desktop.Views;

/// <summary>
/// 设置面板：学期、桌面组件、插件、系统开关。状态读写都经 App（控制中枢），
/// 本窗口只做展示与触发；托盘或其它入口改变状态后由 App 调 RefreshState() 回刷。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly App _app;
    private bool _updating;
    private string _nutstoreMessage = "";
    private bool _nutstoreMessageOk = true;

    public SettingsWindow()
    {
        InitializeComponent();
        _app = (App)Application.Current;
        MaxHeight = Math.Max(440, SystemParameters.WorkArea.Height - 24);
        Loaded += (_, _) =>
        {
            LoadTermFields();
            LoadNutstoreFields();
            RefreshState();
            Motion.ScaleFadeIn(Root, 0.975, 0.34);
        };
    }

    /// <summary>按 App 当前状态回刷控件（幂等，可反复调用）。</summary>
    public void RefreshState()
    {
        _updating = true;
        try
        {
            int pending = _app.PendingTodoCount;
            HeaderStatus.Text = $"{_app.TermStatusText}  ·  "
                + (pending > 0 ? $"待办 {pending} 项" : "暂无待办");

            WidgetVisibleBox.IsChecked = _app.IsWidgetShown;
            bool wallpaper = _app.CurrentHost == DesktopHostKind.WallpaperWorkerW;
            bool locked = !wallpaper && _app.IsWidgetLocked;
            LockBox.IsChecked = locked;
            LockBox.IsEnabled = !wallpaper;

            DraggableBox.IsChecked = !wallpaper && !locked && _app.IsWidgetDraggable;
            DraggableBox.IsEnabled = !wallpaper;

            HostBottomRadio.IsChecked = !wallpaper;
            HostWallpaperRadio.IsChecked = wallpaper;

            double opacity = _app.WidgetOpacity;
            Opacity60.IsChecked = !wallpaper && Math.Abs(opacity - 0.6) < 0.001;
            Opacity80.IsChecked = !wallpaper && Math.Abs(opacity - 0.8) < 0.001;
            Opacity100.IsChecked = wallpaper || Math.Abs(opacity - 1.0) < 0.001;
            Opacity60.IsEnabled = !wallpaper;
            Opacity80.IsEnabled = !wallpaper;

            AutoStartBox.IsChecked = _app.IsAutoStartEnabled();
            RefreshPluginSummary();
            RefreshNutstoreState();
        }
        finally
        {
            _updating = false;
        }
    }

    private void LoadTermFields()
    {
        _updating = true;
        try
        {
            StoreDataBoxes();
        }
        finally
        {
            _updating = false;
        }
    }

    private void StoreDataBoxes()
    {
        var data = _app.Store.Data;
        TermNameBox.Text = data.TermName;
        TermStartBox.Text = data.TermStart;
        TotalWeeksBox.Text = data.TotalWeeks.ToString(CultureInfo.InvariantCulture);
        TermHint.Text = "第 1 周周一用于周视图日期与“当前第几周”；可随时调整。";
        TermHint.Foreground = (Brush)FindResource("LabelTertiaryBrush");
    }

    private void LoadNutstoreFields()
    {
        bool previous = _updating;
        _updating = true;
        try
        {
            NutstoreSettings settings = _app.NutstoreSettings;
            NutstoreServerBox.Text = settings.Server;
            NutstoreUserBox.Text = settings.Username;
            NutstorePasswordBox.Password = settings.Password;
            NutstorePathBox.Text = settings.RemotePath;
            NutstoreAutoBox.IsChecked = settings.AutoSync;
        }
        finally
        {
            _updating = previous;
        }
    }

    private void RefreshNutstoreState()
    {
        bool busy = _app.NutstoreBusy;
        if (busy) _nutstoreMessage = "";
        NutstoreSyncButton.IsEnabled = !busy;
        NutstoreTestButton.IsEnabled = !busy;
        NutstoreSaveButton.IsEnabled = !busy;
        NutstoreSyncButton.Content = busy ? "处理中…" : "立即同步";

        bool ok = busy || (_nutstoreMessage.Length > 0 ? _nutstoreMessageOk : _app.NutstoreStatusOk);
        NutstoreStatusText.Text = busy
            ? _app.NutstoreStatusText
            : _nutstoreMessage.Length > 0 ? _nutstoreMessage : _app.NutstoreStatusText;
        NutstoreStatusText.Foreground =
            (Brush)FindResource(ok ? "LabelTertiaryBrush" : "DangerBrush");
        NutstoreStatusDot.Background =
            (Brush)FindResource(ok ? (_app.NutstoreConfigured ? "SuccessBrush" : "LabelQuaternaryBrush")
                                   : "DangerBrush");
        NutstoreLastSyncText.Text = _app.NutstoreLastSyncText;
    }

    private void ShowNutstoreMessage(string message, bool ok)
    {
        _nutstoreMessage = message;
        _nutstoreMessageOk = ok;
        RefreshNutstoreState();
    }

    private void ClearNutstoreMessage() => _nutstoreMessage = "";

    private bool TrySaveNutstoreFields(out string message) =>
        _app.SaveNutstoreSettings(
            NutstoreServerBox.Text,
            NutstoreUserBox.Text,
            NutstorePasswordBox.Password,
            NutstorePathBox.Text,
            NutstoreAutoBox.IsChecked == true,
            out message);

    private void RefreshPluginSummary()
    {
        int count = _app.Plugins.Count;
        PluginCountText.Text = count > 0 ? $"已载入 {count} 个插件" : "未安装插件";
        PluginNames.Children.Clear();
        foreach (LoadedPlugin plugin in _app.Plugins)
        {
            var row = new TextBlock
            {
                Text = $"· {plugin.Instance.Name}  —  {plugin.Instance.Description}",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("LabelSecondaryBrush"),
                Margin = new Thickness(0, 3, 0, 0),
            };
            PluginNames.Children.Add(row);
        }
        if (_app.PluginErrors.Count > 0)
        {
            PluginNames.Children.Add(new TextBlock
            {
                Text = $"有 {_app.PluginErrors.Count} 个插件载入失败，详见“管理插件…”",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("DangerBrush"),
                Margin = new Thickness(0, 4, 0, 0),
            });
        }
    }

    // ---------------------------------------------------------------- 导航

    private void OnSettingsTabChanged(object sender, RoutedEventArgs e)
    {
        if (TabGeneral is null || TabDesktop is null || TabSync is null ||
            PanelGeneral is null || PanelDesktop is null || PanelSync is null)
        {
            return;
        }

        bool general = TabGeneral.IsChecked == true;
        bool desktop = TabDesktop.IsChecked == true;
        PanelGeneral.Visibility = general ? Visibility.Visible : Visibility.Collapsed;
        PanelDesktop.Visibility = desktop ? Visibility.Visible : Visibility.Collapsed;
        PanelSync.Visibility = !general && !desktop ? Visibility.Visible : Visibility.Collapsed;
        SettingsScroll?.ScrollToTop();

        if (!IsLoaded) return;
        FrameworkElement panel = general ? PanelGeneral : desktop ? PanelDesktop : PanelSync;
        Motion.ScaleFadeIn(panel, 0.99, 0.22);
    }

    private void OnNutstoreAdvanced(object sender, RoutedEventArgs e)
    {
        bool expand = NutstoreAdvancedPanel.Visibility != Visibility.Visible;
        NutstoreAdvancedPanel.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        NutstoreAdvancedButton.Content = expand ? "收起高级设置 ▴" : "高级设置 ▾";
    }

    // ---------------------------------------------------------------- 事件

    private void OnSaveTerm(object sender, RoutedEventArgs e)
    {
        string name = TermNameBox.Text.Trim();
        string start = TermStartBox.Text.Trim();
        if (!DateTime.TryParseExact(start, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime startDate))
        {
            TermHint.Text = "第 1 周周一格式应为 yyyy-MM-dd（例如 2026-08-31）";
            TermHint.Foreground = (Brush)FindResource("DangerBrush");
            return;
        }
        if (startDate.DayOfWeek != DayOfWeek.Monday)
        {
            TermHint.Text = "第 1 周开始日必须是周一（周视图按周一 ~ 周日排列）";
            TermHint.Foreground = (Brush)FindResource("DangerBrush");
            return;
        }
        if (!int.TryParse(TotalWeeksBox.Text.Trim(), out int total) || total is < 1 or > 60)
        {
            TermHint.Text = "总周数应为 1 - 60 之间的整数";
            TermHint.Foreground = (Brush)FindResource("DangerBrush");
            return;
        }

        _app.UpdateTerm(name, startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), total);
        TermHint.Text = "已保存学期设置，周视图与桌面组件已刷新。";
        TermHint.Foreground = (Brush)FindResource("SuccessBrush");
        RefreshState();
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
        RefreshState();
    }

    private void OnDraggableChanged(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        bool on = DraggableBox.IsChecked == true;
        if (on && _app.IsWidgetLocked)
        {
            _app.SetWidgetLocked(false);
        }
        _app.SetWidgetDraggable(on);
        RefreshState();
    }

    private void OnHostChanged(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        DesktopHostKind kind = HostWallpaperRadio.IsChecked == true
            ? DesktopHostKind.WallpaperWorkerW
            : DesktopHostKind.BottomWindow;
        _app.SwitchHost(kind);
        RefreshState();
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

    // ---------------------------------------------------------------- 坚果云同步

    private void OnNutstoreSave(object sender, RoutedEventArgs e)
    {
        if (!TrySaveNutstoreFields(out string message))
        {
            ShowNutstoreMessage(message, false);
            return;
        }
        LoadNutstoreFields();
        ShowNutstoreMessage(message, true);
    }

    private async void OnNutstoreTest(object sender, RoutedEventArgs e)
    {
        if (!TrySaveNutstoreFields(out string message))
        {
            ShowNutstoreMessage(message, false);
            return;
        }
        LoadNutstoreFields();
        ClearNutstoreMessage();
        RefreshNutstoreState();

        ConnectionTestResult result = await _app.TestNutstoreAsync();
        ShowNutstoreMessage(result.Message, result.Success);
    }

    private async void OnNutstoreSync(object sender, RoutedEventArgs e)
    {
        if (!TrySaveNutstoreFields(out string saveMessage))
        {
            ShowNutstoreMessage(saveMessage, false);
            return;
        }
        LoadNutstoreFields();
        ClearNutstoreMessage();
        RefreshNutstoreState();

        SyncOutcome outcome = await _app.SyncNutstoreNowAsync(announce: false);
        if (outcome.AppliedLocal) LoadTermFields();
        ShowNutstoreMessage(outcome.Message, outcome.Success);
    }

    private void OnNutstoreAutoChanged(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        if (!TrySaveNutstoreFields(out string message))
        {
            ShowNutstoreMessage(message, false);
            return;
        }
        ShowNutstoreMessage(message, true);
    }

    private void OnNutstoreHelp(object sender, RequestNavigateEventArgs e)
    {
        Openers.OpenUrl(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private void OnOpenMainWindow(object sender, RoutedEventArgs e) => _app.ShowManager();

    private void OnManagePlugins(object sender, RoutedEventArgs e) => _app.ShowPluginManager();

    private void OnOpenDataDir(object sender, RoutedEventArgs e) => _app.OpenDataDir();
}
