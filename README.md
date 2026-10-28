# 时间表（Timetable）

一个 Windows 桌面时间表应用：周视图日程、待办清单、常驻桌面的信息组件，以及可选的
数据源插件。核心程序不绑定任何学校或教务系统；课程同步等能力由插件提供。

当前仓库自带示例插件 **南昌大学课程源**，它把原先内置的 CAS 登录、gmsstu 课表同步、
凭据存储与 JSON 导入全部迁出到独立 DLL 中。

## 功能

**日程**
- 周视图（周一 ~ 周日 × 节次），支持时间轴、今日高亮与“现在”时间线。
- 手动新建 / 编辑 / 删除日程：标题、分类、地点、参与人、备注、颜色。
- 每周重复（支持 `1-16周`、`1-9周(单)` 等表达式）或仅一次（指定日期）。
- 空白格双击新建，日程块双击编辑；时段冲突软提示。
- 学期名称、第 1 周周一、总周数可在设置中调整。

**待办**
- 快速添加（Enter 保存）与详细编辑（截止日期、优先级、备注）。
- 未完成 / 全部 / 已完成筛选；逾期、今天、即将到期用颜色区分。
- 完成、恢复、删除、清除已完成；侧栏角标与桌面组件同步展示。

**坚果云同步**
- 通过坚果云 WebDAV 在多设备间双向同步日程与待办；应用密码以 DPAPI 密文保存在本机。
- 启动时与本地修改后自动同步（可关闭），也可在设置面板或托盘菜单手动同步。
- 以条目 Id + 更新时间做三方合并：单侧删除会传播，双方改同一条目时保留较新版本。

**其他**
- 桌面玻璃卡片：今日安排、周密度点阵、下一项与未完成待办清单；可拖动、锁定穿透、调整层级。
- 托盘常驻：打开主窗口 / 设置 / 坚果云同步，切换桌面组件显隐；组件层级、锁定与拖动在设置中调整。
- 数据本地 JSON 存储，原子写入并保留 `.bak` 备份；支持外部改动自动刷新。
- 数据源插件可整批替换“自己的”日程，不影响手动日程与待办。

## 快速开始

需要 .NET SDK 8+。

```bash
# 构建（宿主 + 插件契约 + 南昌大学课程源插件）
cd desktop
dotnet build Timetable.sln -c Debug

# 打开完整管理窗口（开发/首次配置推荐）
./NcuCourseTable.Desktop/bin/Debug/net8.0-windows/NcuCourseTable.Desktop.exe --window

# 或启动桌面组件（默认：托盘常驻 + 桌面卡片，不弹主窗口）
./NcuCourseTable.Desktop/bin/Debug/net8.0-windows/NcuCourseTable.Desktop.exe
```

Windows 下也可以使用仓库根目录脚本：

```bat
start.bat build
start.bat app --window
start.bat app
```

```bash
./start.sh build
./start.sh app --window
./start.sh app
```

命令行参数：

| 参数 | 说明 |
|---|---|
| `--data <目录>` / `-d` | 指定数据目录 |
| `--window` | 启动完整主窗口（默认是桌面组件模式） |
| `--host bottom\|wallpaper` | 桌面组件宿主层；`wallpaper` 受 WPF 限制通常不可见 |

## 数据与迁移

默认数据目录：`--data` > `TIMETABLE_DATA`（兼容 `NCU_COURSE_DATA`）> exe 同级 `data/` >
`%LocalAppData%\NcuCourseTable`。

| 文件 | 内容 |
|---|---|
| `schedule.json` | 日程、待办、学期、节次时间（主数据） |
| `courses.json` | 旧版课程数据；首次载入自动迁移到 `schedule.json`，原文件保留 |
| `widget.json` | 桌面组件位置、透明度、锁定、宿主等 |
| `credentials.bin` | 插件保存的登录凭据（DPAPI 加密，不落明文） |
| `schedule.db` | 南大课程源插件使用的 ncu_sdk 会话缓存 |
| `nutstore.json` | 坚果云 WebDAV 配置（应用密码 DPAPI 加密） |
| `nutstore-state.json` | 上次同步基线与 ETag（仅本机，用于三方合并） |

旧版数据迁移后，课程会标记为 `Source: "ncu"`；再次同步时只替换这些插件导入的课程。
启用坚果云同步后，`schedule.json` 会写入坚果云 WebDAV 的指定文件；桌面组件位置与
插件凭据仍只保存在本机。

## 插件

- 插件契约：`desktop/Timetable.Plugins.Abstractions/`
- 插件目录：`<exe>/plugins/<插件目录>/*.dll`
- 详细说明与新插件模板：[`docs/PLUGINS.md`](docs/PLUGINS.md)

内置插件 **南昌大学课程源**：

1. 打开主窗口 → 顶部「插件」→「南昌大学课程源」→「打开」。
2. 「账号配置…」填写统一身份认证账号（DPAPI 加密保存）。
3. 「登录并同步课表」：调用本地 Python `ncu_sdk` 完成 CAS 登录 → gmsstu 同步 →
   JSON 导出 → 插件导入；也可直接「导入 JSON…」。
4. 同步只替换本插件导入的课程，手动日程 / 待办不受影响。

插件依赖本机 Python 与仓库 `sdk/ncu_sdk`；带 `desktop/vendor` 时无需手动
`pip install`。环境变量：`NCU_PYTHON`、`NCU_SDK_DIR`、`NCU_VENDOR_DIR`。
校外需先连接校园 VPN。

## 坚果云同步

1. 在坚果云网页端「账户信息 → 安全选项 → 第三方应用管理」添加应用，获取**应用密码**
   （不是网页登录密码）。
2. 主窗口 →「设置」→「同步」：填写坚果云账号邮箱与应用密码；WebDAV 地址默认
   `https://dav.jianguoyun.com/dav/`，远程文件默认 `Timetable/schedule.json`
   （需要修改时展开「高级设置」）。
3. 「保存」→「测试连接」→「立即同步」。启用「自动同步」后，启动时与本地修改后会自动
   同步；托盘菜单也可随时触发。

合并策略：首次同步把云端与本地条目按 Id 取并集；之后用本地保存的同步基线做三方合并，
单侧新增 / 修改 / 删除会传播，双方改同一条目时按 `UpdatedAt` 保留较新版本，删除与修改
冲突时优先保留修改后的内容。同步失败不会覆盖本地数据；坚果云文件历史版本可找回旧档。
仅支持 HTTPS WebDAV，也可填写其他兼容 WebDAV 服务的地址。

## 目录结构

```
course_table/
├── desktop/
│   ├── Timetable.sln
│   ├── NcuCourseTable.Desktop/       # 时间表宿主（WPF / .NET 8）
│   │   ├── Models/                  # ScheduleEvent / TodoItem / StoreData
│   │   ├── Services/                # DataStore / 插件宿主 / 坚果云同步与三方合并
│   │   ├── ViewModels/              # MainViewModel
│   │   └── Views/                   # 周视图、详情/待办、对话框、桌面组件
│   ├── Timetable.Plugins.Abstractions/
│   └── plugins/
│       └── NcuCourseSource/         # 南大课程源插件（登录/同步/JSON 导入）
├── sdk/ncu_sdk/                     # 课程数据 SDK（插件子进程调用）
├── tests/                           # ncu_sdk 离线单元测试
├── docs/
│   ├── API_SPEC.md                  # 南大教务接口约定
│   ├── PLUGINS.md                   # 插件契约与开发指南
│   └── DESKTOP_EMBED.md             # 桌面组件嵌入实现
└── start.sh / start.bat
```

## 南大课程 SDK（插件后端）

`ncu_sdk` 覆盖 CAS 认证、融合门户 portal-api、gmsstu 真课表、SQLite 缓存与 ICS 导出，
接口依据 [`docs/API_SPEC.md`](docs/API_SPEC.md)。

```bash
pip install -r requirements.txt
export NCU_USERNAME=学号 NCU_PASSWORD='密码'   # 或 NCU_ID_TOKEN
python -m ncu_sdk.cli --db schedule.db login
python -m ncu_sdk.cli --db schedule.db sync --term 202620271
python -m ncu_sdk.cli --db schedule.db today
python -m ncu_sdk.cli --db schedule.db export --term 202620271 --format json
```

SDK 位于 `sdk/ncu_sdk`，运行 CLI 需 `cd sdk` 或设置 `PYTHONPATH=sdk`。
离线单测：

```bash
cd tests
python -m unittest discover -s . -p "test_*.py"
```

## 说明与边界

- 除南昌大学课程源插件外，核心程序不包含任何学校专属逻辑。
- 插件是普通 .NET 程序，拥有与宿主相同的本机权限；只安装可信来源的插件。
- 桌面组件 `wallpaper` 模式在 WPF 下受渲染限制可能不可见，推荐默认 `bottom`。
- 默认所有数据保存在本机；启用坚果云同步后，`schedule.json` 会上传到你所配置的坚果云
  WebDAV 文件。应用密码只以 DPAPI 密文保存在本机，不会上传。
