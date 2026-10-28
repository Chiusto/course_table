using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Timetable.Plugins;

namespace NcuCourseSource;

/// <summary>插件主界面：账号配置、登录同步、JSON 导入与移除已导入课程。</summary>
public partial class NcuSourceWindow : Window
{
    private readonly NcuSourcePlugin _plugin;
    private readonly CredentialVault _vault;
    private readonly LoginService _login;
    private bool _busy;

    public NcuSourceWindow(NcuSourcePlugin plugin)
    {
        InitializeComponent();
        _plugin = plugin;
        _vault = new CredentialVault(plugin.Host.DataDir);
        _login = new LoginService(plugin.Host.DataDir);
        Loaded += (_, _) =>
        {
            UiMotion.ScaleFadeIn(Root, 0.97, 0.32);
            RefreshState();
        };
        Closing += (_, e) =>
        {
            if (_busy)
            {
                e.Cancel = true;
                SetStatus("同步进行中，请稍候…", "#007AFF");
            }
        };
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (!_busy) Close();
    }

    private void RefreshState()
    {
        string account = _vault.UsernameOrEmpty;
        bool hasAccount = account.Length > 0;
        AccountText.Text = hasAccount ? $"已配置账号：{Mask(account)}" : "未配置账号";
        AccountHint.Text = hasAccount
            ? "凭据已 DPAPI 加密保存，可一键登录并同步课表"
            : "配置统一身份认证账号后即可同步真课表";

        int count = _plugin.Host.CountEvents(NcuSourcePlugin.PluginId);
        ImportCountText.Text = count > 0 ? $"已导入 {count} 门课程" : "尚未导入课程";
        RemoveBtn.IsEnabled = count > 0 && !_busy;

        LoginBtn.IsEnabled = !_busy;
        LoginBtn.Content = _busy ? "正在同步…" : (hasAccount ? "登录并同步课表" : "配置并登录");
    }

    private async void OnAccountConfig(object sender, RoutedEventArgs e)
    {
        bool login = ShowAccountDialog();
        if (login) await LoginAsync();
    }

    private bool ShowAccountDialog()
    {
        var dialog = new CredentialDialog(_vault)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true) return false;
        RefreshState();
        SetStatus("凭据已加密保存。", "#34C759");
        return dialog.LoginAfterSave;
    }

    private async void OnLogin(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_vault.Read() is null)
        {
            if (ShowAccountDialog()) await LoginAsync();
            else SetStatus("请先填写账号与密码。", "#FF9500");
            return;
        }
        await LoginAsync();
    }

    private async Task LoginAsync()
    {
        if (_busy) return;
        (string Username, string Password)? cred = _vault.Read();
        if (cred is null)
        {
            SetStatus("尚未配置账号，无法登录。", "#FF9500");
            return;
        }

        SetBusy(true);
        SetStatus("正在验证账号并同步课表，首次登录可能需要十几秒，请稍候…", "#007AFF");
        try
        {
            SyncOutcome outcome = await _login.LoginAndSyncAsync(cred.Value.Username, cred.Value.Password);
            if (!outcome.Ok)
            {
                SetStatus($"{outcome.Message}\n{outcome.Detail}", "#FF3B30");
                _plugin.Host.Notify($"课程同步失败：{outcome.Message}");
                return;
            }

            _plugin.Host.ReplaceEvents(NcuSourcePlugin.PluginId, outcome.Events, outcome.Termcode);
            _plugin.Host.RequestRefresh();
            SetStatus(outcome.Detail + "  可在时间表中查看。", "#34C759");
            _plugin.Host.Notify("课程已同步：" + outcome.Detail);
        }
        catch (Exception ex)
        {
            SetStatus("登录过程发生异常：" + ex.Message, "#FF3B30");
        }
        finally
        {
            SetBusy(false);
            RefreshState();
        }
    }

    private void OnImportJson(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var picker = new OpenFileDialog
        {
            Title = "导入 ncu_sdk 课程 JSON",
            Filter = "课程 JSON (*.json)|*.json|所有文件 (*.*)|*.*",
        };
        if (picker.ShowDialog(this) != true) return;

        try
        {
            CourseJsonImporter.ImportResult result = CourseJsonImporter.ReadFile(picker.FileName);
            if (result.Events.Count == 0)
            {
                SetStatus("文件中没有可导入的课程。", "#FF9500");
                return;
            }
            _plugin.Host.ReplaceEvents(NcuSourcePlugin.PluginId, result.Events,
                result.Termcode.Length > 0 ? result.Termcode : null);
            _plugin.Host.RequestRefresh();
            SetStatus($"已导入 {result.Events.Count} 门课程。", "#34C759");
            _plugin.Host.Notify($"已导入 {result.Events.Count} 门课程（南昌大学课程源）");
            RefreshState();
        }
        catch (Exception ex)
        {
            SetStatus("导入失败：" + ex.Message, "#FF3B30");
        }
    }

    private void OnRemoveImported(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (MessageBox.Show(this,
                "移除本插件导入的全部课程？\n手动创建的日程与待办不受影响。",
                "移除已导入课程", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        _plugin.Host.ReplaceEvents(NcuSourcePlugin.PluginId, Array.Empty<PluginEvent>());
        _plugin.Host.RequestRefresh();
        SetStatus("已移除本插件导入的课程。", "#6E6E73");
        RefreshState();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        AccountText.Opacity = busy ? 0.6 : 1;
        AccountBtn.IsEnabled = !busy;
        ImportBtn.IsEnabled = !busy;
        RemoveBtn.IsEnabled = !busy && _plugin.Host.CountEvents(NcuSourcePlugin.PluginId) > 0;
        LoginBtn.IsEnabled = !busy;
        LoginBtn.Content = busy ? "正在同步…" : (_vault.Read() is null ? "配置并登录" : "登录并同步课表");
        CloseBtn.IsEnabled = !busy;
    }

    private void SetStatus(string text, string colorHex)
    {
        HintText.Text = text;
        HintText.Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString(colorHex)!;
    }

    private static string Mask(string account) =>
        account.Length <= 4 ? account : account[..4] + "…" + account[^2..];
}
