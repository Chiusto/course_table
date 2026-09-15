using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using NcuCourseTable.Desktop.Services;

namespace NcuCourseTable.Desktop.Views;

/// <summary>
/// 账号配置对话框：填写南昌大学统一身份认证账号 / 密码。
/// 校验与提示：账号、密码任一为空即禁用“保存”并给出红色提示，输入框内带占位提示；
/// 保存：凭据经 CredentialVault（Windows DPAPI）加密落盘 credentials.bin（不落明文）；
/// 随后按勾选默认立即登录同步——成功关窗并通知刷新，失败留在窗口内给出原因（可改后重试）。
/// </summary>
public partial class CredentialDialog : Window
{
    private readonly CredentialVault _vault;
    private readonly LoginService _login;
    private bool _busy;
    private bool _initialized;   // InitializeComponent 期间事件早触发守卫

    /// <summary>登录成功后触发（由 App 订阅，用于刷新桌面卡片 / 管理窗口）。</summary>
    public event Action? LoginSucceeded;

    public CredentialDialog(CredentialVault vault, LoginService login)
    {
        InitializeComponent();
        _vault = vault;
        _login = login;

        // 只回显账号；密码一律重新输入，避免明文出现在控件里
        string? saved = vault.UsernameOrEmpty;
        if (!string.IsNullOrEmpty(saved))
        {
            UserBox.Text = saved;
        }

        UserBox.TextChanged += (_, _) => { UpdatePlaceholders(); UpdateValidation(); };
        PassBox.PasswordChanged += (_, _) => { UpdatePlaceholders(); UpdateValidation(); };
        Loaded += (_, _) =>
        {
            UpdateValidation();
            if (UserBox.Text.Length == 0) UserBox.Focus(); else PassBox.Focus();
        };
        UpdatePlaceholders();
        _initialized = true;   // 此后所有事件处理器可安全访问控件与 _vault
        UpdateValidation();     // 用真实状态刷新一次（按钮文案 / 提示文本）
    }

    private void UpdatePlaceholders()
    {
        UserPlaceholder.Visibility = UserBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        PassPlaceholder.Visibility = PassBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateValidation()
    {
        if (!_initialized) return;   // InitializeComponent 期间 _vault/控件尚未就绪
        if (_busy) return;
        bool hasUser = !string.IsNullOrWhiteSpace(UserBox.Text);
        bool hasPass = PassBox.Password.Length > 0;
        SaveBtn.IsEnabled = hasUser && hasPass;

        if (!hasUser && !hasPass)
        {
            string saved = _vault.UsernameOrEmpty;
            HintText.Text = saved.Length > 0
                ? $"已保存账号 {saved}，可修改后重新保存。账号、密码均为必填项。"
                : "账号、密码均为必填项。密码只在本机加密保存，用于登录并同步课表。";
            HintText.Foreground = BrushOf("#6B707A");
        }
        else if (!hasUser)
        {
            HintText.Text = "请填写账号（学号 / 工号）";
            HintText.Foreground = BrushOf("#E5484D");
        }
        else if (!hasPass)
        {
            HintText.Text = "请填写密码（统一身份认证密码）";
            HintText.Foreground = BrushOf("#E5484D");
        }
        else
        {
            HintText.Text = AutoLoginBox.IsChecked == true
                ? "校验通过：保存后将立即登录并同步课表。"
                : "校验通过：凭据将加密保存，稍后可经托盘“登录并刷新课表”执行登录。";
            HintText.Foreground = BrushOf("#6B707A");
        }
    }

    private void OnAutoLoginChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;   // InitializeComponent 期间 SaveBtn 尚未创建
        SaveBtn.Content = AutoLoginBox.IsChecked == true ? "保存并登录" : "保存";
        UpdateValidation();
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        string user = UserBox.Text.Trim();
        string pass = PassBox.Password;
        if (user.Length == 0 || pass.Length == 0)
        {
            UpdateValidation();
            MessageBox.Show(this, "账号与密码为必填项，请补充完整。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            _vault.Save(user, pass);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"凭据加密保存失败：{ex.Message}", "保存失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (AutoLoginBox.IsChecked != true)
        {
            MessageBox.Show(this, "凭据已加密保存。\n可稍后通过托盘菜单“登录并刷新课表”执行登录。",
                "已保存", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
            return;
        }
        await LoginAsync(user, pass);
    }

    private async Task LoginAsync(string user, string pass)
    {
        SetBusy(true);
        try
        {
            LoginOutcome outcome = await _login.LoginAndSyncAsync(user, pass);
            if (outcome.Ok)
            {
                LoginSucceeded?.Invoke();
                MessageBox.Show(this, $"登录成功：{outcome.Detail}", "登录成功",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
                Close();
                return;
            }

            HintText.Text = $"{outcome.Message}\n{outcome.Detail}";
            HintText.Foreground = BrushOf("#E5484D");
            MessageBox.Show(this, $"{outcome.Message}\n\n{outcome.Detail}", "登录失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            HintText.Text = "登录过程发生异常：" + ex.Message;
            HintText.Foreground = BrushOf("#E5484D");
            MessageBox.Show(this, "登录过程发生异常：" + ex.Message, "登录失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UserBox.IsEnabled = !busy;
        PassBox.IsEnabled = !busy;
        AutoLoginBox.IsEnabled = !busy;
        CancelBtn.IsEnabled = !busy;
        SaveBtn.IsEnabled = !busy;
        SaveBtn.Content = busy ? "正在登录…" : (AutoLoginBox.IsChecked == true ? "保存并登录" : "保存");
        if (busy)
        {
            HintText.Text = "正在验证账号并同步课表，首次登录可能需要十几秒，请稍候…";
            HintText.Foreground = BrushOf("#2F6FED");
        }
    }

    private static SolidColorBrush BrushOf(string hex) =>
        (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
}
