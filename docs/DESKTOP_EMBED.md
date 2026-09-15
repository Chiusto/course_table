# 桌面嵌入实现与验证

> 目标：把课程表组件**真正嵌入**到 Windows 桌面层——默认 **bottom 模式**（置底、图标之上、不占任务栏、真实可见）已落地；wallpaper 模式代码路径完整但** 因 WPF 渲染限制屏幕上不可见**（详见 §3），仅作断言层面通过。

---

## 1. 目标形态

```
┌──────────────────── 显示器桌面 ────────────────────┐
│                                                    │
│   ┌──────────── 桌面层小组件（深色玻璃卡片）────┐  │  ← 本组件绘制层
│   │  2026-2027学年第1学…  第 6 / 20 周          │  │  bottom：桌面最底层 · 图标之上，真实可见
│   │  今日课程              共 1 节                │  │  wallpaper：壁纸之上 · 图标之下（实验性）
│   │  今日课程              共 1 节                │  │
│   │  ▎组合数学A            09:50-12:10           │  │
│   │    研究生院505 · 幸玮                        │  │
│   │                                            │  │
│   │  ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━  │  │
│   │  正在上：组合数学A · 研究生院505 · 幸玮     │  │
│   └────────────────────────────────────────────┘  │
│                                                    │
│  （壁纸 / 普通应用窗口 / 图标）                    │
└────────────────────────────────────────────────────┘
   ↑ 鼠标单击穿透
   ↑ 任务栏 / Alt-Tab 中均无本组件的按钮或条目
   ↑ 关闭主窗口不会退出，进程常驻托盘
```

关键约束：

1. **不占任务栏**：用 `WS_EX_TOOLWINDOW`（去掉 AppWindow 标志），Win11 任务栏不再显示按钮。
2. **不弹独立窗口**：默认启动不创建任何应用窗口；管理窗口（编辑课程、查看周表）**单击桌面组件卡片**即可打开。
3. **常驻后台**：进程常驻 System Tray，单实例互斥锁（`Local\NcuCourseTable.Widget.SingleInstance`）防止重复启动。
4. **与桌面融为一体**：组件直接绘制在系统桌面层级；鼠标可穿透（锁定后开 `WS_EX_TRANSPARENT`）；未锁定时单击卡片打开管理窗口、左键拖拽移动位置（控制菜单统一收在系统托盘）。
5. **跨周自动切换**：30s 心跳 + 学期起始日计算当前周次；学期外显示完整周表。
6. **外部数据自动同步**：`FileSystemWatcher` + 350ms 去抖，`ncu_sdk sync/import` 一导出，组件即时刷新。

---

## 2. 与之前“独立窗口版”的差异

### 2.1 代码层

| 维度 | 独立窗口版（旧） | 桌面嵌入版（新） |
|---|---|---|
| 启动模式 | `MainWindow.xaml` 直接 `Show()`，关窗即退出 | 默认进入组件模式：`ShutdownMode=OnExplicitShutdown`；只创建 `WidgetWindow` + `NotifyIcon` |
| 单实例 | 无 | `Local\…SingleInstance` 互斥锁，重复启动弹提示并退出 |
| 窗口层级 | 普通 Topmost 窗口，父=本进程隐藏 owner | ① wallpaper：`SetParent(WorkerW)`（实验性，见 §3）；② **bottom（默认）：置底非 Topmost 顶层窗口**；二者均脱离应用窗口 Z 序 |
| 样式 | 普通 WPF Window | `WindowStyle=None` + `WS_EX_TOOLWINDOW` 去任务栏；bottom 用透明玻璃层（`AllowsTransparency`），wallpaper 用不透明底 + DWM 圆角（避开分层子窗口不合成问题） |
| 鼠标 | 默认命中 | `WS_EX_TRANSPARENT` 锁定后穿透；未锁定时响应单击（开管理窗口）/ 左键拖拽（控制统一走托盘） |
| 数据刷新 | 仅手动 `ReloadFromDisk` | `FileSystemWatcher(_store.Dir, courses.json)` + 350ms 去抖 + 30s 心跳；UI 线程 DispatcherTimer 修复（详见 §5.3） |
| 进程退出 | 用户关主窗 | 托盘“退出” / 任务栏右键“关闭窗口” / `App.ExitAll()`；常驻到显式退出 |
| 互操作 | 无 | 新增 `Interop/Native.cs`、`Interop/DesktopHost.cs`、`Interop/WindowChrome.cs` 三组 P/Invoke + WorkerW 查找算法 |
| 数据目录 | 同 | 同 `JsonStore`（兼容 ncu_sdk `to_dict`）；`--data` 覆盖目录 |
| 开机自启 | 无 | `WidgetConfig.AutoStart` + `HKCU\…\Run` 写入 |

### 2.2 视觉层

| 维度 | 独立窗口 | 桌面嵌入 |
|---|---|---|
| 用户视觉感受 | 弹出一个“程序窗口”，可被任务栏/Alt-Tab 选中 | “像壁纸上的一个小组件”，任务栏/Alt-Tab 都不出现 |
| 鼠标交互 | 全程命中 | 未锁定：单击卡片打开管理窗口 / 左键拖拽移动；锁定后完全穿透（右键菜单已并入托盘，托盘不再含“打开管理窗口”） |
| Z 序 | 始终在普通窗口之上 | bottom（默认）= 永远在最底层，真实可见；wallpaper = 壁纸之上、图标之下（实验性，屏幕上不渲染） |
| 背景 | 纯色面板 | 圆角深色玻璃卡（`#1F2A3E` 底 + `#FFFFFF` / `#9FB0C8` / `#7C8CA3` 文字层级） |
| 自我标识 | 标准 Windows 标题栏 | 无标题栏；唯一入口是系统托盘 NotifyIcon |

### 2.3 一行对比

- 旧版：**应用窗口**（Application Window）。
- 新版：**桌面小组件**（Desktop Widget / Desktop Gadget）。

---

## 3. 两套宿主方案

桌面嵌入有两条路，技术取舍如下：

| 方案 | `--host=bottom`（置底顶层窗口，**默认·推荐**） | `--host=wallpaper`（WorkerW 挂载，实验性） |
|---|---|---|
| 父窗口 | 父 = 本进程隐藏 owner，Z 序置底（HWND_BOTTOM） | `SetParent(WorkerW)`（Win11 24H2 兼容：下钻 SHELLDLL_DefView 所在 WorkerW） |
| 与桌面图标关系 | 图标之上（覆盖图标） | 图标之下（被图标覆盖） |
| 鼠标穿透 | 未锁定时可交互（单击开窗 / 拖拽），锁定后恒开 | **必须** 恒开（否则挡住图标点击） |
| 真实屏幕可见性 | ✅ 已实测（DWM 正常合成顶层窗口） | ⚠️ **WPF 渲染限制：内容不显示**（见下方说明） |
| 抗窗口激活抖动 | 定期 `SetWindowPos(HWND_BOTTOM)` 防回归 | 需监听 `EVENT_SYSTEM_FOREGROUND` + `WM_WINDOWPOSCHANGING`（参考 Rainmeter `Library/System.cpp`） |
| 用户可控性 | 中（其它置顶窗口可能压上来） | 强（一旦挂入 WorkerW，几乎无法被普通 UI 干扰） |
| 风险 | 与某些全屏游戏 / 视频播放器的置顶行为冲突 | Win11 24H2 SHELLDLL_DefView 需下钻；多壁纸/桌面扩展可能破坏层级 |
| 断言验证 | `verify_embed.py bottom`：全过 | `verify_embed.py wallpaper`：断言全过，**但屏幕上不可见** |

> **⚠️ wallpaper 模式的 WPF 硬限制（2026-09-04 真机实测确认）**
> WPF 窗口内容经 DWM 的**顶层窗口合成路径**呈现；`SetParent(WorkerW)` 后窗口变成
> shell 子窗口，该路径不再生效 → **屏幕上不渲染**。即使去掉 `WS_EX_LAYERED`
> （`AllowsTransparency=false` + 不透明底 + DWM 圆角，本轮已修复复测）依然不可见，
> 而 `PrintWindow` 抓重定向表面仍有内容——只做 PrintWindow 验证会漏判此问题。
> Wallpaper Engine（自绘 D3D）、Rainmeter（GDI 置底顶层窗口）不踩坑的原因正是
> 绕开了 WPF 合成路径 / 不挂 WorkerW。**WPF 技术栈下请使用 bottom 模式**；
> wallpaper 代码路径保留（挂载断言全过），未来换非 WPF 渲染层可启用。

> 两套宿主通过托盘菜单 “宿主切换” 在线互切；选择持久化在 `WidgetConfig.Host`，下次启动自动恢复。

---

## 4. 参考开源实现映射

桌面嵌入不是“从零发明”，以下三套开源项目提供了关键模块范式（已 `references/` 浅克隆核对）：

| 项目 | 关键模块 | 借鉴点 | 本项目落地位置 |
|---|---|---|---|
| `TrafficMonitor` | `TrafficMonitor/TrafficMonitorDlg.cpp` 拖拽 + HWND_TOPMOST | 鼠标穿透的鼠标坐标落在底层时的“软激活”思路 | `Views/WidgetWindow.xaml.cs` 拖动前临时关穿透、松开后恢复 |
| `Rainmeter` | `Library/System.cpp`（约 584 行附近）`RainmeterCreateMeter` 路径下的 Progman/WorkerW 查找 + `EVENT_SYSTEM_FOREGROUND` 监听 | WorkerW 父窗口查找 + 抗窗口激活抖动 | `Interop/DesktopHost.cs` `FindWallpaperParent()`（含 24H2 下钻） |
| `shiguangschedule` | `app/src/main/java/.../Course.kt` 课程实体 | 字段命名（name/teacher/room/weekday/startSection/...） | `Models/Course.cs`（直接复用 SDK 字段） |

> 三个项目均已通过 `gh repo clone` 拉取到 `references/`，仅做接口核对，不复制源码到产物。

---

## 5. 实现步骤与技术难点

### 5.1 实现步骤

1. **新增 Interop 层**（`Interop/Native.cs`、`Interop/DesktopHost.cs`、`Interop/WindowChrome.cs`）
   - P/Invoke 声明 `FindWindowW` / `EnumWindows` / `EnumChildWindows` / `GetAncestor` / `SetParent` / `SetWindowPos` / `PrintWindow`。
   - `DesktopHost` 提供 `FindWallpaperParent()`：先查 Progman 子 SHELLDLL_DefView；Win11 24H2 下钻到 WorkerW。
   - `WindowChrome`：去任务栏样式 + 鼠标穿透开关。

2. **新增 WidgetWindow**（`Views/WidgetWindow.xaml(.cs)`）
   - 无标题栏 + 透明背景 + 深色玻璃卡 XAML。
   - 自持 `FileSystemWatcher`（数据文件外部变更）+ 30s `DispatcherTimer`（跨周检测）。
   - 单击开管理窗口 / 左键拖拽 / 锁定穿透 / 下一节课提示（控制统一走托盘，右键菜单已并入）。
   - 公开 `IsClickThroughNow()`、`ReloadNow()`。

3. **App 启动路由**（`App.xaml.cs`）
   - 默认进入组件模式（不弹主窗，`ShutdownMode=OnExplicitShutdown`）。
   - `--window` 走旧应用窗口（管理用）；`--host bottom|wallpaper` 仅对组件模式生效。
   - 单实例 `Mutex`：`Local\NcuCourseTable.Widget.SingleInstance`。

4. **托盘常驻**（`App.Tray.cs`，WinForms `NotifyIcon`）
   - 显示 / 锁定 / 打开管理 / 立即刷新 / 宿主切换 / 透明度 / 开机自启 / 退出。

5. **csproj 配置**（`NcuCourseTable.Desktop.csproj`）
   - `<TargetFramework>net8.0-windows</TargetFramework>`
   - `<UseWPF>true</UseWPF>` + `<UseWindowsForms>true</UseWindowsForms>`（互操作）
   - `<Using Remove="System.Drawing"/>`、`<Using Remove="System.Windows.Forms"/>` 防隐式 using 污染 WPF。
   - `<RootNamespace>NcuCourseTable.Desktop</RootNamespace>`

### 5.2 技术难点

- **Win11 24H2 SHELLDLL_DefView 不再直接挂 Progman**：传统 `FindWindow("Progman")` → `FindWindowEx("SHELLDLL_DefView")` 拿不到；必须先 `EnumChildWindows(Progman)` 找到那个 SHELLDLL_DefView，再 `GetAncestor(GA_PARENT)` 倒推真实 `WorkerW`。详见 `DesktopHost.FindWallpaperParent()`。
- **嵌入后 `FindWindowW` 失效**：`SetParent(WorkerW)` 后 WPF 顶层窗口变成 WorkerW 子窗口，`FindWindowW`（仅查顶层）拿不到。验证脚本改用 `EnumWindows + EnumChildWindows` 全树搜索。
- **`GetParent` 误判**：WPF 顶层窗口有隐藏 owner，`GetParent` 永远返回本进程隐藏 HWND（≠ 嵌入父）。改用 `GetAncestor(GA_PARENT)` 取真实父窗口。
- **任务栏按钮去不掉**：必须显式清掉 `WS_EX_APPWINDOW`（默认会被加回）。`WindowChrome` 在 `SourceInitialized` 阶段一次性设好所有 exstyle。
- **WS_EX_TRANSPARENT 与 IsLoaded**：置底模式在“锁定”后开启穿透；未锁定时可直接响应鼠标（单击开管理窗口 / 拖拽移动），锁定后需先经托盘解锁。
- **Z 序被其它置顶窗口顶上来**：置底模式每帧用 `SetWindowPos(HWND_BOTTOM)` 防回归（Win11 24H2 行为变化）。
- **FileSystemWatcher 在 FSW 线程触发**：`DispatcherTimer` 默认绑到当前线程（FSW 线程）—— 该线程无消息泵，Tick 永不触发，刷新其实从未生效。修复：捕获 UI `Dispatcher.CurrentDispatcher` 并通过 `BeginInvoke` 创建 timer。
- **csproj WinForms / WPF 同进程**：必须显式 `<Using Remove="System.Windows.Forms"/>`，否则 `System.Windows.Rectangle` 与 `System.Drawing.Rectangle` 冲突（CS0104）。
- **Run Binding 默认 TwoWay 触发崩溃**：所有 WPF `Run` 标签加 `Mode=OneWay`。
- **WS_EX_TOOLWINDOW 组件遇“显示桌面”会凭空消失**：普通应用被显示桌面/最小化后有任务栏按钮可还原；本组件无任务栏按钮、不进 Alt+Tab，一旦被最小化/隐藏/DWM 遮蔽就“消失”（2026-09-04 用户实测：点任务栏右下角细条后组件不见，要点任务栏里的任务恢复焦点才回来）。对策见 §6.5 守卫。

### 5.3 文件清单（新增 / 修改）

```
desktop/NcuCourseTable.Desktop/
├── Interop/
│   ├── Native.cs              # P/Invoke 声明
│   ├── DesktopHost.cs         # Progman/WorkerW 查找 + SetParent + 24H2 下钻
│   └── WindowChrome.cs        # 去任务栏 + 鼠标穿透
├── Services/
│   ├── WidgetConfig.cs        # 位置/透明度/锁定/Host/AutoStart 持久化
│   ├── CredentialVault.cs     # 账号凭据 DPAPI 加密存储（credentials.bin，不落明文）
│   └── LoginService.cs        # 子进程调 ncu_sdk：login→sync→export→原子导入
├── Views/
│   ├── WidgetWindow.xaml      # 深色玻璃卡片
│   ├── WidgetWindow.xaml.cs   # 单击开窗/拖动/锁定/FileSystemWatcher（控制入口在托盘）
│   └── CredentialDialog.xaml  # 账号配置：必填校验/输入提示/保存后自动登录
├── App.xaml                   # 多窗口资源合并
├── App.xaml.cs                # 启动路由 + 单实例 + 宿主切换
├── App.Tray.cs                # NotifyIcon 托盘菜单
└── App.Account.cs             # 托盘“打开配置 / 登录并刷新课表”入口与状态
```

`desktop/sample_data/courses.json`、`desktop/scripts/verify_embed.py`、`desktop/scripts/capture_window.py`、`desktop/scripts/verify_refresh.py` 为新增验证脚本与样本数据。

---

## 6. 验证步骤与实测结果

### 6.1 验证工具链

| 工具 | 用途 |
|---|---|
| `verify_embed.py bottom` | 置底模式断言：无任务栏、非 Topmost、置底、可控穿透 |
| `verify_embed.py wallpaper` | 壁纸层断言：`parent_class ∈ {WorkerW, Progman}`、恒穿透、无任务栏 |
| `verify_embed.py window` | 旧应用窗口模式仍能正常 Show() |
| `verify_refresh.py` | 端到端自动刷新：启动 → 改 `courses.json` → 等 watcher → 截屏比对 → 还原 → 二次截屏 |
| `capture_window.py` | 通用 `PrintWindow(PW_RENDERFULLCONTENT)` 抓图（被遮挡窗口也能抓） |
| `ImageChops.difference` | 像素级差分（threshold=20 抗反走样） |

### 6.2 桌面嵌入断言（已通过）

`verify_embed.py wallpaper` 输出（裁剪）：

```json
{
  "hwnd": "0x…",
  "ancestor_parent": "0x…",
  "parent_class": "WorkerW",
  "no_taskbar_button": true,
  "click_through": true,
  "ok": true
}
```

`verify_embed.py bottom` 输出（裁剪）：

```json
{
  "parent_is_self_process": true,
  "no_taskbar_button": true,
  "toolwindow": true,
  "not_click_through": true,
  "not_topmost": true,
  "ok": true
}
```

`verify_refresh.py` 输出（实际跑过）：

```json
{
  "hwnd": "0x140eb0",
  "changed_a_to_b": 0.0009,
  "changed_a_to_c": 0.0,
  "changed_b_to_c": 0.0009,
  "ok": true
}
```

含义：A=基线（组合数学A）→ 改文件 → B=组合数学B（FileSystemWatcher 触发重载）→ 还原 → C=组合数学A（与 A 像素级一致）。
三联图见 `desktop/verify_refresh_triplet.png`：

![三联图：左 A 组合数学A / 中 B 组合数学B / 右 C 组合数学A](../desktop/verify_refresh_triplet.png)

### 6.2.1 双宿主联合断言（修复 FileSystemWatcher 后最新复跑）

`desktop/scripts/verify_both_hosts.py` 一次性拉起 wallpaper / bottom 两种模式跑 `verify_embed.py` 断言：

```text
wallpaper: {
  "ancestor_parent": "0x203ec",
  "parent_class": "WorkerW",
  "no_taskbar_button": true,
  "click_through": true,
  "mean_rgb": [28, 35, 49],
  "bright_ratio": 0.012,   # 白色文字
  "dark_ratio": 0.973,     # 深色卡片底
  "ok": true
}
bottom: {
  "parent_is_self_process": true,
  "parent_class": "HwndWrapper[NcuCourseTable.Desktop;;…]",
  "no_taskbar_button": true,
  "toolwindow": true,
  "not_click_through": true,
  "not_topmost": true,
  "mean_rgb": [28, 35, 49],
  "bright_ratio": 0.012,
  "dark_ratio": 0.973,
  "ok": true
}
```

结论：bottom 模式断言全部通过 + 真实屏幕可见（`desktop/desktop_live_fullscreen.png`）；
wallpaper 模式断言全部通过（`desktop/verify_widget_wallpaper.png`），**但 WPF 渲染限制导致屏幕上不显示**——以全屏 `desktop/desktop_live_fullscreen.png` 实拍为准。

### 6.3 手动验证脚本

```bash
# 1. 启动组件（默认 bottom 模式；真实可见）
./NcuCourseTable.Desktop.exe --data %LOCALAPPDATA%/NcuCourseTable --demo

# 2. 启动管理窗口（按需，旧行为）
./NcuCourseTable.Desktop.exe --window

# 3. 切换到 wallpaper 模式（仅父窗口/样式断言通过，WPF 渲染限制屏幕上不可见）
./NcuCourseTable.Desktop.exe --host wallpaper --data %LOCALAPPDATA%/NcuCourseTable

# 4. 验证嵌入（需另开终端）
python desktop/scripts/verify_embed.py wallpaper
python desktop/scripts/verify_embed.py bottom
python desktop/scripts/verify_refresh.py
python desktop/scripts/verify_both_hosts.py   # 一键串起 wallpaper+bottom 联合断言
```

### 6.4 实测用户视角表现

- **任务栏**：组件运行期间无按钮（`WS_EX_TOOLWINDOW` 生效）。
- **Alt-Tab**：不出现本组件条目。
- **托盘**：出现 NotifyIcon（NcuCourseTable 图标），右键菜单含“打开配置 / 登录并刷新课表 / 显示 / 锁定 / 宿主切换 / 透明度 / 开机自启 / 退出”（无“打开管理窗口”，改由单击组件卡片打开）；双击托盘图标直接打开“账号配置”（§9）。
- **鼠标交互**：未锁定（bottom 可交互）时单击卡片 → 打开管理窗口，左键拖拽 → 移动位置；锁定 / 壁纸层模式下完全穿透，先经托盘“锁定到桌面”解锁或切换层级；组件右键不再弹菜单（控制已统一并入托盘）。
- **数据外部更新**：执行 `ncu_sdk sync` 或手动编辑 `courses.json` 后，< 1 秒内卡片自动刷新。

### 6.5 防“显示桌面 / 最小化”消失守卫（bottom 模式，2026-09-04 实测）

**问题**：点任务栏右下角“显示桌面”细条 / Win+D / Win+M 会对所有顶层窗口最小化或 DWM 遮蔽。
普通应用能靠任务栏按钮还原，而本组件是 `WS_EX_TOOLWINDOW`、无任务栏按钮、不进 Alt+Tab——
一旦被抑制就“凭空消失”，只能靠托盘或重启找回（用户实测触发：任务栏右下角细条；
恢复路径：点任务栏中其它任务夺回前台时组件才跟着回来）。

**对策（三层防御，代码在 `Views/WidgetWindow.xaml.cs` “防‘显示桌面/最小化’消失”区）**：

1. **吞 `SC_MINIMIZE`**：`HwndSource.AddHook` 挂 WndProc，`WM_SYSCOMMAND & 0xFFF0 == SC_MINIMIZE` 直接 `handled=true`（Win+M / 标题栏最小化等命令根本到不了 DefWindowProc）。
2. **`WM_SIZE SIZE_MINIMIZED` 即还原**：已被外部渠道最小化时（如“显示桌面”走的 ShowWindow 路径），消息处理完后 `DispatcherPriority.Send` 立即 `RestoreFromSuppression()`（`SW_SHOWNOACTIVATE` + `SendToBottom`，还原但不抢焦点）。
3. **300ms 看门狗兜底**：`DispatcherTimer` 周期检查 `IsIconic || !IsWindowVisible || DwmGetWindowAttribute(DWMWA_CLOAKED)==1`，命中即解除遮蔽并还原；托盘主动“隐藏”置 `_userHidden` 除外（`HideFromTray/ShowFromTray` 由 `App.Tray.cs` 调用，守卫尊重用户意愿）。

wallpaper 模式不需守卫（窗口挂 WorkerW 后不参与显示桌面最小化），代码按 `IsWallpaperMode` 跳过。

**实测（`.probe_winD/probe9_guard.py` + `probe10_blip.py`，真机新构建 PID 32576，bottom 模式）**：

| 威胁 | 手段 | 结果 |
|---|---|---|
| A 系统最小化命令 | `PostMessage(WM_SYSCOMMAND, SC_MINIMIZE)` | ✅ 被钩子吞掉，全程 vis=1 min=0 cloak=0 |
| B 外部强制最小化 | `ShowWindow(SW_MINIMIZE)`（绕过 SC_MINIMIZE） | ✅ 0ms 出现 min=1 真实最小化，**~30ms 内被看门狗还原**，rect 不变 |
| C 真实“显示桌面” | 物理点击任务栏右下角细条 x=1919（确认桌面态已切换） | ✅ 窗口全程存活，rect `[1508,620,1880,972]` 不变 |

→ 三类威胁全部 PASS：组件在“显示桌面”类操作下不再消失，且还原过程不抢前台焦点。

---

## 7. 已知限制

- Win11 24H2 部分多壁纸/桌面扩展（Wallpaper Engine、Memoji 桌面背景）会破坏 WorkerW 层级，此时回退到 `bottom` 模式。
- Win10 LTSC 早期版本不保证 `SHELLDLL_DefView` 位置；本组件以 Win10 22H2 / Win11 23H2+ 为目标。
- 鼠标在组件“画布”外不会触发穿透关闭；锁定后单击无响应属预期，可经托盘“锁定到桌面”解锁后再单击卡片打开管理窗口。
- “显示桌面”守卫针对 bottom 模式所有已知抑制渠道（SC_MINIMIZE / ShowWindow 最小化 / DWM cloak）做了兜底（§6.5）；若未来系统新增未覆盖的隐藏渠道，组件可能瞬时闪烁后自动恢复（300ms 看门狗）。
- 组件与 WSL2 / Linux 子系统桌面无直接关系，仅 Windows。

---

## 8. 交付物清单

| 类别 | 路径 |
|---|---|
| 工程 | `desktop/NcuCourseTable.Desktop/`（WPF / .NET 8 / MVVM） |
| 编译产物 | `desktop/NcuCourseTable.Desktop/bin/Debug/net8.0-windows/NcuCourseTable.Desktop.exe` |
| 样本数据 | `desktop/sample_data/courses.json` |
| 验证脚本 | `desktop/scripts/verify_embed.py` / `verify_refresh.py` / `verify_both_hosts.py` / `capture_window.py` |
| 验证截图 | `desktop/verify_refresh_triplet.png` / `desktop/verify_widget_*.png` |
| 设计 / 选型 | `docs/DESKTOP_EMBED.md`（本文件） |
| 数据接口 | `docs/API_SPEC.md` + `sdk/ncu_sdk/` |

---

## 9. 托盘账号配置与登录（新增）

桌面卡片只做展示，网络登录在 Python SDK（ncu_sdk）。托盘新增“身份与数据”入口，
让账号/密码只填一次、加密保存，之后可随时一键登录并刷新课表：

- **入口**：托盘菜单“打开配置…” / “登录并刷新课表”，双击托盘图标 = 打开配置。
- **配置对话框**（`Views/CredentialDialog.xaml`）：账号 + 密码两个必填输入框（占位输入提示），
  任一为空即禁用保存并红字提示；默认勾选“保存后立即登录并同步课表”。
- **保存凭据**（`Services/CredentialVault.cs`）：`{账号, 密码}` JSON 后经 Windows DPAPI
  （`CryptProtectData`，当前用户作用域 + 应用熵）加密写入数据目录 `credentials.bin`，
  **不落明文**；文件拷走或换用户无法解密，损坏/异常时容错视为“未配置”。
- **登录**（`Services/LoginService.cs`）：子进程调本地 `ncu_sdk.cli`，凭据只经环境变量
  `NCU_USERNAME / NCU_PASSWORD` 传递（不进命令行）：
  `login`（CAS 验证）→ `sync`（当前学期 gmsstu 真课表落库 schedule.db）→
  `export --format json`（临时文件）→ `JsonStore` 整学期原子导入 `courses.json`
  （桌面卡片 FileSystemWatcher 自动重绘）。成功 / 失败均有明确弹窗与原因归类
  （账号密码错误 / 无法连接校园网 / 缺 Python 依赖 / SDK 未找到等）。
- **环境解析**：python 与 SDK 路径按 `NCU_PYTHON` / `NCU_SDK_DIR` 环境变量 > PATH 探测 /
  从可执行目录向上探测仓库布局的顺序解析；未找到时给出可操作提示，不静默。
  **Python 依赖随仓库自带**：`desktop\vendor`（`pip install --target` 产物，requests 等）
  会被自动并入子进程 `PYTHONPATH`（顺序 vendor → sdk → 既有值），用户**无需**手动
  `pip install -r requirements.txt`；解释器探测亦经 vendor 自检 `import requests`，
  缺依赖的解释器不会被误选。可用 `NCU_VENDOR_DIR` 覆盖 vendor 位置。
  校外环境登录需先连接校园 VPN。
- **代码位置**：`App.Account.cs`（托盘入口与状态）、`App.Tray.cs`（菜单项）、
  `Views/CredentialDialog.xaml(.cs)`（对话框）、`Services/CredentialVault.cs` +
  `Services/LoginService.cs`（存储与执行）。
- **限制**：`--window`（旧独立窗口）模式不建托盘、不使用本功能；登录成败依赖
  本机 Python + ncu_sdk + 校园网可达性，属于桌面组件对 SDK 的“遥控”，不是内嵌重实现。
