using System.Windows;

namespace Timetable.Plugins;

/// <summary>
/// 数据源插件契约。宿主从程序目录 plugins/&lt;插件目录&gt;/ 载入实现了本接口的 DLL，
/// 插件通过 <see cref="IPluginHost"/> 读写时间表数据，不直接依赖宿主程序集。
/// </summary>
public interface ITimetablePlugin
{
    string Id { get; }

    string Name { get; }

    string Description { get; }

    string Version { get; }

    void Initialize(IPluginHost host);

    /// <summary>打开插件自己的配置 / 同步界面。</summary>
    void Open(Window? owner);
}

/// <summary>插件可用的宿主能力。所有替换操作只影响该插件自己导入的日程。</summary>
public interface IPluginHost
{
    string DataDir { get; }

    string ActiveTermcode { get; }

    int TotalWeeks { get; }

    int CountEvents(string pluginId);

    /// <summary>
    /// 整批替换该插件来源的日程（不影响手动创建的日程）。termcode 非空时会同步学期信息。
    /// </summary>
    void ReplaceEvents(string pluginId, IReadOnlyList<PluginEvent> events,
        string? termcode = null, string? termName = null);

    void SetTerm(string termcode, string termName, string? firstMonday = null);

    void Notify(string message);

    void RequestRefresh();
}

/// <summary>插件导入的一条周次日程。</summary>
public sealed class PluginEvent
{
    public string Title { get; set; } = "";

    public string Category { get; set; } = "课程";

    public string Teacher { get; set; } = "";

    public string Location { get; set; } = "";

    public string Notes { get; set; } = "";

    public string ColorKey { get; set; } = "";

    /// <summary>1=周一 ... 7=周日。</summary>
    public int Weekday { get; set; } = 1;

    public int StartSection { get; set; } = 1;

    public int EndSection { get; set; } = 1;

    public string WeeksRaw { get; set; } = "1-16周";

    /// <summary>单次日程日期（yyyy-MM-dd）；为空表示按周重复。</summary>
    public string? Date { get; set; }

    public string? Raw { get; set; }
}
