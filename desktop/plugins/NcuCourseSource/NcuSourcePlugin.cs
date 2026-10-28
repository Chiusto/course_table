using System.Reflection;
using System.Windows;
using Timetable.Plugins;

namespace NcuCourseSource;

/// <summary>
/// 南昌大学课程源插件：登录统一身份认证并同步 gmsstu 真课表，
/// 也支持导入 ncu_sdk 导出的 JSON。同步仅替换本插件导入的课程。
/// </summary>
public sealed class NcuSourcePlugin : ITimetablePlugin
{
    public const string PluginId = "ncu";

    private IPluginHost? _host;

    public string Id => PluginId;

    public string Name => "南昌大学课程源";

    public string Description => "登录统一身份认证，同步 gmsstu 真课表；也可导入 ncu_sdk 导出的 JSON。只替换本插件导入的课程。";

    public string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    internal IPluginHost Host => _host
        ?? throw new InvalidOperationException("插件尚未初始化");

    public void Initialize(IPluginHost host)
    {
        _host = host;
    }

    public void Open(Window? owner)
    {
        var window = new NcuSourceWindow(this);
        if (owner is not null && owner.IsLoaded && owner.IsVisible)
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        window.ShowDialog();
    }
}
