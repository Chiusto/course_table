# 插件机制

时间表核心只负责周视图、日程、待办和本地存储。任何「外部数据源」都可以作为插件接入，
例如从教务系统同步课程、导入其他日历，或连接团队日程服务。

目前内置示例插件是 `NcuCourseSource`（南昌大学课程源），它已经从核心程序集中完全移出。

## 目录与加载

```
desktop/
├── Timetable.Plugins.Abstractions/   # 插件契约（ITimetablePlugin / IPluginHost）
├── NcuCourseTable.Desktop/           # 时间表宿主（WPF）
└── plugins/
    └── NcuCourseSource/              # 插件项目，构建后自动部署到宿主插件目录
```

宿主启动时扫描：

```
<NcuCourseTable.Desktop.exe 所在目录>/plugins/<插件目录>/*.dll
```

也可以通过环境变量 `TIMETABLE_PLUGINS_DIR` 指定其他插件目录。每个 DLL 载入独立
`AssemblyLoadContext`；契约程序集 `Timetable.Plugins.Abstractions.dll` 始终与宿主共享。
载入失败会在「插件」窗口的「载入失败」区域显示原因。

## 契约

```csharp
public interface ITimetablePlugin
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    string Version { get; }
    void Initialize(IPluginHost host);
    void Open(Window? owner);
}
```

插件通过 `IPluginHost` 读写宿主数据：

| 成员 | 作用 |
|---|---|
| `DataDir` | 当前数据目录（插件可在此保存自己的凭据/缓存） |
| `ActiveTermcode` / `TotalWeeks` | 当前学期信息 |
| `CountEvents(pluginId)` | 统计该插件已导入的日程数 |
| `ReplaceEvents(pluginId, events, termcode?, termName?)` | **整批替换**该插件来源的日程；不会影响手动日程与待办 |
| `SetTerm(...)` | 更新学期名称 / 第一周周一 |
| `Notify(...)` / `RequestRefresh()` | 状态栏提示 / 请求所有窗口刷新 |

`PluginEvent` 是插件提交给宿主的通用日程 DTO，字段包括标题、分类、参与人、地点、备注、
颜色键、周几、起止节次、周次表达式、单次日期与原始文本。单次日程设置 `Date=yyyy-MM-dd` 即可。

## 编写一个新插件

1. 新建 `net8.0-windows` + `UseWPF` 类库，引用 `Timetable.Plugins.Abstractions`
   （`Private=false`，避免把契约 DLL 复制进插件目录）。
2. 实现 `ITimetablePlugin`，在 `Open(owner)` 中显示插件自己的窗口。
3. 调用 `host.ReplaceEvents(Id, events, termcode)` 导入数据，再调用
   `host.RequestRefresh()`。
4. 在 `csproj` 中把构建产物复制到宿主的 `plugins/<AssemblyName>/`，或手动放入该目录。
5. 重启时间表，在「插件」窗口打开。

`NcuCourseSource.csproj` 的 `CopyPluginToHost` Target 可以直接作为模板。

## 南大课程源插件

- 项目：`desktop/plugins/NcuCourseSource/`
- 功能：账号配置（Windows DPAPI 加密）、登录统一身份认证、同步 gmsstu 真课表、
  导入 ncu_sdk 导出的 JSON、移除本插件导入的课程。
- 账号密码保存在共享数据目录的 `credentials.bin`（沿用旧版熵，已保存的凭据可直接继续使用）。
- 同步只替换 `Source == "ncu"` 的日程；手动日程和待办不受影响。
- 运行依赖本机 Python 与仓库内的 `sdk/ncu_sdk`；`desktop/vendor` 会自动并入
  `PYTHONPATH`。可用 `NCU_PYTHON` / `NCU_SDK_DIR` / `NCU_VENDOR_DIR` 覆盖。
- 校外登录需先连接校园 VPN。

## 安全说明

插件是普通 .NET 程序，拥有与宿主相同的本机文件访问权限，**只安装可信来源的插件**。
宿主不会自动下载或安装插件。
