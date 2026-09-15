using System;
using System.Windows;
using NcuCourseTable.Desktop.Services;
using NcuCourseTable.Desktop.Views;

namespace NcuCourseTable.Desktop;

/// <summary>
/// 账号配置 / 登录（托盘 + 管理窗口共用入口）—— 桌面组件的“身份与数据”控制器。
/// 入口：托盘“打开配置…”/双击托盘图标、管理窗口“登录并同步课表”“账号配置…”按钮。
/// “打开配置…”弹出账号配置对话框（必填校验、DPAPI 加密保存、保存后自动登录并明确反馈）；
/// “登录并刷新课表”用已保存凭据执行 ncu_sdk 登录 → 同步 → 导出 → 导入，成功后刷新卡片/管理窗口。
/// </summary>
public partial class App
{
    private CredentialVault? _vault;
    private LoginService? _login;
    private bool _loggingIn;

    internal void ShowCredentialDialog()
    {
        if (_vault is null || _login is null) return;
        if (_loggingIn)
        {
            MessageBox.Show("登录正在进行中，请稍候…", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new CredentialDialog(_vault, _login);
        if (_manager is { IsLoaded: true })
        {
            dlg.Owner = _manager;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        dlg.LoginSucceeded += RefreshAll;
        dlg.ShowDialog();
        RefreshAccountUi();
    }

    /// <summary>凭据/登录态变更后统一刷新：托盘菜单与图标提示、管理窗口登录按钮文案。</summary>
    private void RefreshAccountUi()
    {
        if (_tray is not null) RefreshTrayChecks();
        if (_manager is { IsLoaded: true })
        {
            _manager.SetLoginAccount(_vault?.UsernameOrEmpty ?? "");
        }
    }

    /// <summary>“登录并刷新课表”（托盘菜单 / 设置面板 / 管理窗口按钮共用）：用已保存凭据登录同步；未配置则引导先去配置。</summary>
    internal async void LoginAndSync()
    {
        if (_vault is not { } vault || _login is null) return;
        if (_loggingIn)
        {
            MessageBox.Show("登录正在进行中，请稍候…", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (vault.Read() is not { } cred)
        {
            MessageBox.Show("尚未配置账号凭据。\n请点击“账号配置…”填写账号与密码。",
                "未配置账号", MessageBoxButton.OK, MessageBoxImage.Information);
            ShowCredentialDialog();
            return;
        }

        _loggingIn = true;
        try
        {
            LoginOutcome outcome = await _login.LoginAndSyncAsync(cred.Username, cred.Password);
            if (outcome.Ok)
            {
                MessageBox.Show($"登录成功：{outcome.Detail}", "登录成功",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshAll();
            }
            else
            {
                MessageBox.Show($"{outcome.Message}\n\n{outcome.Detail}", "登录失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("登录过程发生异常：" + ex.Message, "登录失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _loggingIn = false;
            RefreshAccountUi();
        }
    }

    /// <summary>账号脱敏（托盘菜单 / 按钮 / 提示展示用）：123456789012 → 1234…12。</summary>
    private static string MaskAccount(string account) =>
        account.Length <= 4 ? account : account[..4] + "…" + account[^2..];
}
