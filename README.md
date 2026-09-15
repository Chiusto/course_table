# 南昌大学课程表 SDK（ncu_sdk）

依据 `docs/API_SPEC.md`（南昌大学课程表接口说明）实现的自研课程表数据层。
覆盖：CAS 统一认证、融合门户 portal-api、研究生系统 gmsstu 真课表、本地
SQLite 缓存与查询、ICS 日历导出。

> 接口约定全部来自 `docs/API_SPEC.md`，本文“对应文档”列即出处。

## 目录结构

```
course_table/
├── docs/                          # 参考文档（需求与接口约定）
│   ├── API_SPEC.md                # ★ 接口规范：CAS / portal-api / gmsstu
│   ├── RESEARCH.md / test.md      # 占位文档（无实质内容）
├── sdk/
│   └── ncu_sdk/
│       ├── __init__.py            # 包入口，导出全部公开 API
│       ├── config.py              # 端点常量与 Settings（文档 2/3/4 节）
│       ├── errors.py              # 分层异常体系
│       ├── models.py              # Course / Section / CalendarEvent / TeachingWeek
│       ├── weeks.py               # 周次表达式解析（"5-15周"、"6-6周"、单双周…）
│       ├── parser.py              # gmsstu 单元格解析 + 连续节次合并
│       ├── cas.py                 # CASClient（文档第 2 节）
│       ├── portal.py              # PortalClient（文档第 3 节）
│       ├── gmsstu.py              # GmsstuClient（文档第 4 节 ★ 真课表）
│       ├── schedule.py            # NCUClient 顶层编排（登录/同步/查询/教学周）
│       ├── storage.py             # SQLite（文档第 6 节建议）
│       ├── ics.py                 # iCalendar 导出
│       ├── demo.py                # 内置样例数据（取自 API_SPEC.md）
│       └── cli.py                 # 命令行入口
├── tests/
│   ├── fixtures/                  # 文档原文样例（JSON）
│   └── test_*.py                  # 52 个离线单元测试
└── README.md
```

## 与参考文档的对应关系

| docs/API_SPEC.md | 实现 |
|---|---|
| 1. 总览（两套系统勿混用） | `portal.py` 与 `gmsstu.py` 各自独立，不做混用 |
| 2. CAS 认证（publicKey / execution / ticket） | `cas.py`：抽取隐藏域→POST→302 取 ticket，支持 RSA 加密回退 |
| 3.1 请求头 `x-id-token` 等 | `portal.py::headers`；无 token 返回 `code:-1` → `PermissionError_` |
| 3.2-3.5 七个端点 | `PortalClient.get_events / get_section / get_week_of_teaching / get_schedule / get_calendar / get_personal_calendar / get_subscribed` |
| 4.1 gmsstu Cookie 认证 | `GmsstuClient.require_session`（`cn_com_southsoft_gmis_stu`） |
| 4.2 混淆 URL / kblx=xs / termcode | `config.py::GMSSTU_OBFUSCATED_PATH`；termcode 生成/解析；解析正则原样保留在 `parser.py::_STRICT_RE` |
| 4.2 单元格正则 + 实测课表 | `parse_cell`；`demo.py` 内建实测数据，测试逐条断言 |
| 5. Python SDK 示例 | `NCUClient` 即示例的工程化封装 |
| 6. 开发建议 | SQLite 索引 `(termcode, weekday, start_section, end_section)`；`getSection` 本地缓存；周次以 gmsstu 为准、portal 仅校准 |

## 快速开始

依赖：Python ≥ 3.10、`requests`（RSA 加密登录时才需要 `cryptography`）。

```bash
pip install -r requirements.txt

# 1) 凭据（建议环境变量，勿写死在代码里）
export NCU_USERNAME=你的学号
export NCU_PASSWORD='你的密码'
export NCU_ID_TOKEN=eyJ...        # 可选：跳过登录，直接用浏览器 localStorage 的 token

# 2) 登录验证
python -m ncu_sdk.cli --db schedule.db login

# 3) 同步课表（termcode 缺省按当前日期推算）
python -m ncu_sdk.cli --db schedule.db sync --term 202620271

# 4) 查询：第 3 周课表 / 今日课程 / 节次时间表
python -m ncu_sdk.cli --db schedule.db show  --term 202620271 --week 3
python -m ncu_sdk.cli --db schedule.db today
python -m ncu_sdk.cli --db schedule.db sections

# 5) 导出 .ics / .json（ics 需先指定学期第一周周一）
python -m ncu_sdk.cli --db schedule.db \
       --term-start 202620271=2026-08-31 export --term 202620271 --format ics

# 6) 无网络离线演示（使用文档中的实测数据）
python -m ncu_sdk.cli --db demo.db demo
```

注意：SDK 包在 `sdk/ncu_sdk`，运行 CLI 需先 `cd sdk` 或设置
`PYTHONPATH=sdk`（示例中已包含）。

### 代码方式

```python
from ncu_sdk import NCUClient
from ncu_sdk.config import Settings

client = NCUClient(
    username="你的学号",
    password="***",
    settings=Settings(db_path="schedule.db",
                      term_start_dates={"202620271": "2026-08-31"}),
)
client.sync("202620271")                  # 拉 gmsstu + 缓存节次表
week, courses = client.today_schedule()   # (第几周, 今天的课)
for c in courses:
    print(c.describe(client.sections()))
```

## 核心逻辑要点

1. **连续节次合并**：gmsstu 返回“每节一行”，如周一第 6/7/8 节单元格文本相同，
   `parser._merge_sections` 按 `(周几, 课程名, 原文)` 分组、`jcid` 连续则合并为
   `start_section=6, end_section=8`，与文档实测“周一 6-8 节机器学习”一致。
2. **周次规则**：`weeks.py` 支持 `5-15周`/`6-6周`/多段/单双周；是否某周上课
   以 gmsstu 的 weeks 字符串为准（文档第 6 节）。
3. **教学周判定**：优先 `portal getWeekOfTeaching`（文档 3.5，-1 表示未开学），
   不可用时回退本地 `term_start_dates` 推算；纯本地运行时第 3.5 节样例（-1）会
   正确触发回退。
4. **存储**：课程表按 `(termcode, weekday, start_section, end_section)` 索引
   （文档第 6 节）；节次表带有效期缓存（默认 30 天）；同步为整学期原子替换。
5. **健壮性**：gmsstu 混淆路径的 hex 尾随登录会话轮换，静态路径失效（500/404）时
   `GmsstuClient` 自动在会话内重新发现课表端点（拉首页 → 找含 kblx 的课表页 →
   提取其 POST url），无需人工抓包；CAS 开启密码加密时自动 RSA（需 cryptography）。

## 测试

全部离线（用 `tests/fixtures/*.json` 的文档原文样例 + 假 Session），不访问校园网：

```bash
cd tests
python -m unittest discover -s . -p "test_*.py"
# Ran 52 tests ... OK
```

## 说明与边界

- 账号/密码不内置：运行期通过参数或环境变量注入（SDK 内置样例仅用于离线演示）。
- 融合门户 `x-id-token` 由前端写入 localStorage，自动化登录不一定能拿到；
  失败时请从浏览器复制 token 传入（`PortalClient(token=...)` / `NCU_ID_TOKEN`）。
  课表主体在 gmsstu，portal 登录失败不影响同步。
- `demo.py` 中个别课程（数据科学与工程 / 最优化 / 自然辩证法）的周次与教室在
  文档中未给出，以标注“示例值”的占位补全，仅用于演示解析与合并逻辑。

---

## 桌面组件（Windows 桌面嵌入）

> 详见 [`docs/DESKTOP_EMBED.md`](docs/DESKTOP_EMBED.md)。本节只做入口摘要。

WPF(.NET 8) 实现，常驻托盘、把深色玻璃卡片绘制在系统桌面层（默认 bottom 模式：置底、图标之上、不占任务栏、不弹应用窗口、不进 Alt-Tab；wallpaper 模式因 WPF 渲染限制目前不可见，保留代码路径待换非 WPF 渲染层）。`ncu_sdk sync/import` 一导出，组件 < 1 秒自动重绘。

```bash
# 构建
cd desktop
"$LOCALAPPDATA/Microsoft/dotnet/dotnet.exe" build NcuCourseTable.Desktop -c Debug

# 启动组件（默认 bottom 模式；常驻托盘，真实可见）
./NcuCourseTable.Desktop/bin/Debug/net8.0-windows/NcuCourseTable.Desktop.exe --demo

# 显式打开管理窗口（按需；旧窗口行为）
./NcuCourseTable.Desktop.exe --window

# 切到 wallpaper 模式（WPF 下仅父窗口正确挂载，屏幕上不渲染）
./NcuCourseTable.Desktop.exe --host wallpaper

# 验证嵌入
python desktop/scripts/verify_embed.py wallpaper
python desktop/scripts/verify_embed.py bottom
python desktop/scripts/verify_refresh.py
python desktop/scripts/verify_both_hosts.py   # 一键 wallpaper+bottom 联合断言
```

技术选型：WPF + Win32 互操作（P/Invoke WorkerW/Progman/SHELLDLL_DefView + WS_EX_TOOLWINDOW/TRANSPARENT）。参考开源：TrafficMonitor（拖拽）、Rainmeter `Library/System.cpp`（桌面宿主挂载参考）、shiguangschedule（实体字段）。

**托盘与设置面板**：托盘菜单为「课程中心 / 设置… / 退出」，双击托盘图标 = 设置面板；
小组件显隐、锁定穿透、桌面层级、透明度、开机自启、立即刷新等开关集中在设置面板
（Views/SettingsWindow）。登录入口在设置面板与课程中心内（托盘不再放登录项）。
「课程中心」（完整管理窗口）可经托盘菜单或设置面板打开，桌面组件卡片单击亦可。
账号配置（DPAPI 加密）在设置面板与课程中心均有入口；保存凭据后一键「登录并刷新课表」。
桌面端以子进程调本地 `ncu_sdk.cli` 完成 login → sync → export → 原子导入，卡片自动刷新；
凭据经 Windows DPAPI 加密存于数据目录 `credentials.bin`（不落明文）。python / SDK 路径自动
探测（PYTHONPATH 自动并入 `<SDK根>\sdk` 与 `desktop\vendor`，无需手动设置环境变量），
亦可用 `NCU_PYTHON` / `NCU_SDK_DIR` 环境变量覆盖；校外需先连校园 VPN。详见
[`docs/DESKTOP_EMBED.md`](docs/DESKTOP_EMBED.md) §9。

> **WPF 与 WorkerW 不兼容** —— SetParent 后窗口内容不到屏幕，原因与替代方案详见 `docs/DESKTOP_EMBED.md` §3。

