#!/usr/bin/env bash
# ============================================================================
# 南昌大学课程表 —— 一键启动脚本
# 用法见下方 help()；所有路径相对本文件所在目录解析，可在任意位置调用。
# 约定与 docs/DESKTOP_EMBED.md §9、README.md 保持一致：
#   - python / SDK 路径优先读取环境变量 NCU_PYTHON / NCU_SDK_DIR
#   - 桌面组件默认使用 Release 产物（缺失时回退 Debug）
#   - 桌面数据目录默认指向仓库 data/，可用 --data <目录> 覆盖
# ============================================================================

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# 原生 Windows 程序（exe/python）需要 Windows 风格路径；MSYS 下用 cygpath 转换
if command -v cygpath >/dev/null 2>&1; then
    WIN_ROOT="$(cygpath -w "$ROOT")"
else
    WIN_ROOT="$ROOT"
fi
EXE_REL="desktop/NcuCourseTable.Desktop"
SDK_DIR="${NCU_SDK_DIR:-$ROOT/sdk}"
WIN_SDK="$( { command -v cygpath >/dev/null 2>&1 && cygpath -w "$SDK_DIR"; } 2>/dev/null || echo "$SDK_DIR")"

# ---------------------------------------------------------------- 基础工具

die() { echo "[错误] $*" >&2; exit 1; }
info() { echo "[启动] $*"; }
warn() { echo "[提示] $*" >&2; }

is_tty() { [ -t 0 ] && [ -t 1 ]; }

# 解析 Python 解释器：NCU_PYTHON > python > python3 > py(-3)
PY=""
resolve_python() {
    if [ -n "${NCU_PYTHON:-}" ]; then PY="$NCU_PYTHON"; return 0; fi
    for cand in python python3; do
        if command -v "$cand" >/dev/null 2>&1; then PY="$cand"; return 0; fi
    done
    if command -v py >/dev/null 2>&1; then PY="py"; return 0; fi
    return 1
}
run_py() { # run_py <args...>：兼容 py -3 启动器
    if [ "$PY" = "py" ]; then py -3 "$@"; else "$PY" "$@"; fi
}

# 解析 dotnet：PATH > %LOCALAPPDATA%\Microsoft\dotnet
resolve_dotnet() {
    if command -v dotnet >/dev/null 2>&1; then echo dotnet; return 0; fi
    local fallback="${LOCALAPPDATA:-}/Microsoft/dotnet/dotnet.exe"
    if [ -n "${LOCALAPPDATA:-}" ] && [ -f "$fallback" ]; then echo "$fallback"; return 0; fi
    return 1
}

# 选择桌面组件产物：Release 优先，缺失回退 Debug
pick_exe() {
    for cfg in Release Debug; do
        local exe="$ROOT/$EXE_REL/bin/$cfg/net8.0-windows/NcuCourseTable.Desktop.exe"
        if [ -f "$exe" ]; then echo "$exe"; return 0; fi
    done
    return 1
}

# 检查 ncu_sdk 运行依赖（requests），缺失时给出指引；交互终端下可一键安装
ensure_sdk_deps() {
    if run_py -c "import requests" >/dev/null 2>&1; then return 0; fi
    warn "Python 缺少依赖 requests（仓库 requirements.txt）。"
    if is_tty; then
        printf "是否现在执行 pip install -r requirements.txt 安装？[y/N] "
        read -r ans
        case "$ans" in
            y|Y|yes) run_py -m pip install -r "$WIN_ROOT/requirements.txt" || die "依赖安装失败"; return 0 ;;
            *) die "已取消。请手动执行: python -m pip install -r requirements.txt" ;;
        esac
    fi
    die "请先安装依赖: python -m pip install -r requirements.txt"
}

# ---------------------------------------------------------------- 各子命令

cmd_app() {
    # 启动 Windows 桌面组件：常驻托盘 + 桌面层卡片（默认 bottom 模式，真实可见）
    local exe
    exe="$(pick_exe)" || die "未找到编译产物。请先执行: $0 build"
    info "桌面组件: $exe"

    # 未显式指定 --data/-d 时，注入仓库 data/ 作为数据目录（覆盖 %LocalAppData%\NcuCourseTable）
    local has_data=no prev=""
    for a in "$@"; do
        if [ "$prev" = "--data" ] || [ "$prev" = "-d" ]; then has_data=yes; fi
        prev="$a"
    done
    [ "$prev" = "--data" ] || [ "$prev" = "-d" ] && has_data=yes   # 末尾缺值的 --data 也不注入

    local args=() data_dir="$WIN_ROOT\\data"
    if [ "$has_data" = no ]; then args+=("-d" "$data_dir"); fi
    args+=("$@")

    info "数据目录: $data_dir（可用 --data <目录> 覆盖）"
    info "若课表为空: 右键托盘 →「登录并刷新课表」拉取真课表；"
    info "或先执行: $0 app --demo 查看内置样例。"
    exec "$exe" "${args[@]}"
}

cmd_demo() {
    # SDK 离线演示：内置样例课表，无需账号 / 校园网
    resolve_python || die "未找到 Python，请设置环境变量 NCU_PYTHON 指定解释器"
    ensure_sdk_deps
    mkdir -p "$ROOT/data"
    info "运行离线演示（样例取自 docs/API_SPEC.md 实测课表）..."
    PYTHONPATH="$WIN_SDK" run_py -m ncu_sdk.cli --db "$WIN_ROOT\\data\\demo.db" demo "$@"
}

cmd_sdk() {
    # 透传 ncu_sdk.cli 命令：login / sync / show / today / sections / export
    [ $# -ge 1 ] || die "缺少子命令。示例: $0 sdk today   或   $0 sdk sync --term 202620271"
    resolve_python || die "未找到 Python，请设置环境变量 NCU_PYTHON 指定解释器"
    ensure_sdk_deps
    info "ncu_sdk.cli $*  （SDK: $WIN_SDK）"
    PYTHONPATH="$WIN_SDK" run_py -m ncu_sdk.cli "$@"
}

cmd_build() {
    # 构建桌面组件；默认 Release，可用 --debug 切换
    local cfg=Release
    if [ "${1:-}" = "--debug" ]; then cfg=Debug; shift; fi
    local dn
    dn="$(resolve_dotnet)" || die "未找到 dotnet（.NET SDK ≥ 8），请安装或加入 PATH"
    info "dotnet build -c $cfg"
    "$dn" build "$ROOT/$EXE_REL/NcuCourseTable.Desktop.csproj" -c "$cfg" "$@"
}

help() {
    cat <<'EOF'
南昌大学课程表 —— 一键启动脚本

用法:
  ./start.sh [app] [参数...]   启动 Windows 桌面组件（默认动作，参数原样透传）
  ./start.sh app --demo        以内置样例数据启动桌面组件（无需账号/网络）
  ./start.sh app --window      打开完整管理窗口（旧行为；默认是桌面组件模式）
  ./start.sh app --host bottom 指定宿主层（bottom=图标之上[默认]；wallpaper=壁纸层[WPF 下不可见]）
  ./start.sh demo              SDK 离线演示：内置样例课表（无需登录）
  ./start.sh sdk <命令...>     透传 SDK 命令行，如: ./start.sh sdk today / sync --term 202620271
  ./start.sh build [--debug]   构建桌面组件（默认 Release）
  ./start.sh help              显示本帮助

说明:
  * 桌面组件常驻托盘；退出请用托盘菜单。重复启动会自动提示"已在运行"。
  * 桌面数据目录默认指向仓库 data/；可用 --data <目录> 或环境变量 NCU_COURSE_DATA 覆盖。
  * Python/SDK 路径约定: NCU_PYTHON / NCU_SDK_DIR 环境变量优先，其次 PATH 探测。
  * SDK 网络登录需校园网/VPN，凭据走 NCU_USERNAME / NCU_PASSWORD 环境变量。
EOF
}

# ---------------------------------------------------------------- 入口分发

case "${1:-app}" in
    app|--app)      shift; cmd_app "$@" ;;
    demo)           shift; cmd_demo "$@" ;;
    sdk)            shift; cmd_sdk "$@" ;;
    build)          shift; cmd_build "$@" ;;
    help|-h|--help) help ;;
    *) die "未知命令: $1（./start.sh help 查看用法）" ;;
esac
