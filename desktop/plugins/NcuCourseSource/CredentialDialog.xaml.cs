using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace NcuCourseSource;

/// <summary>账号配置：校验非空并交给 CredentialVault 用 DPAPI 加密保存。</summary>
public partial class CredentialDialog : Window
{
    private readonly CredentialVault _vault;

    public bool LoginAfterSave { get; private set; }

    public CredentialDialog(CredentialVault vault)
    {
        InitializeComponent();
        _vault = vault;
        if (vault.UsernameOrEmpty is { Length: > 0 } saved)
        {
            UserBox.Text = saved;
        }

        UserBox.TextChanged += (_, _) => { UpdatePlaceholders(); UpdateValidation(); };
        PassBox.PasswordChanged += (_, _) => { UpdatePlaceholders(); UpdateValidation(); };
        Loaded += (_, _) =>
        {
            UiMotion.ScaleFadeIn(Root, 0.96, 0.34);
            UpdateValidation();
            if (UserBox.Text.Length == 0) UserBox.Focus(); else PassBox.Focus();
        };
        UpdatePlaceholders();
        UpdateValidation();
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void UpdatePlaceholders()
    {
        UserPlaceholder.Visibility = UserBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        PassPlaceholder.Visibility = PassBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateValidation()
    {
        bool hasUser = !string.IsNullOrWhiteSpace(UserBox.Text);
        bool hasPass = PassBox.Password.Length > 0;
        SaveBtn.IsEnabled = hasUser && hasPass;

        if (!hasUser && !hasPass)
        {
            string saved = _vault.UsernameOrEmpty;
            HintText.Text = saved.Length > 0
                ? $"已保存账号 {saved}，可修改后重新保存。账号、密码均为必填项。"
                : "账号、密码均为必填项。密码只在本机加密保存。";
            HintText.Foreground = BrushOf("#6E6E73");
        }
        else if (!hasUser)
        {
            HintText.Text = "请填写账号（学号 / 工号）";
            HintText.Foreground = BrushOf("#FF3B30");
        }
        else if (!hasPass)
        {
            HintText.Text = "请填写密码（统一身份认证密码）";
            HintText.Foreground = BrushOf("#FF3B30");
        }
        else
        {
            HintText.Text = AutoLoginBox.IsChecked == true
                ? "校验通过：保存后将立即登录并同步课表。"
                : "校验通过：凭据将加密保存，稍后可在插件窗口执行登录。";
            HintText.Foreground = BrushOf("#6E6E73");
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
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

        LoginAfterSave = AutoLoginBox.IsChecked == true;
        DialogResult = true;
        Close();
    }

    private static SolidColorBrush BrushOf(string hex) =>
        (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
}
