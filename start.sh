#!/usr/bin/env bash
# ============================================================================
# 时间表 —— 一键启动脚本
# 用法见下方 help()；所有路径相对本文件所在目录解析，可在任意位置调用。
#   - 默认启动桌面组件（托盘 + 桌面卡片），--window 打开完整主窗口
#   - 数据目录默认仓库 data/，可用 --data <目录> 或 TIMETABLE_DATA 覆盖
#   - 南大课程同步由 plugins/NcuCourseSource 插件提供
# ============================================================================

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if command -v cygpath >/dev/null 2>&1; then
    WIN_ROOT="$(cygpath -w "$ROOT")"
else
    WIN_ROOT="$ROOT"
fi
EXE_REL="desktop/NcuCourseTable.Desktop"
SDK_DIR="${NCU_SDK_DIR:-$ROOT/sdk}"
WIN_SDK="$( { command -v cygpath >/dev/null 2>&1 && cygpath -w "$SDK_DIR"; } 2>/dev/null || echo "$SDK_DIR")"

die() { echo "[错误] $*" >&2; exit 1; }
info() { echo "[启动] $*"; }
warn() { echo "[提示] $*" >&2; }

is_tty() { [ -t 0 ] && [ -t 1 ]; }

PY=""
resolve_python() {
    if [ -n "${NCU_PYTHON:-}" ]; then PY="$NCU_PYTHON"; return 0; fi
    for cand in python python3; do
        if command -v "$cand" >/dev/null 2>&1; then PY="$cand"; return 0; fi
    done
    if command -v py >/dev/null 2>&1; then PY="py"; return 0; fi
    return 1
}
run_py() {
    if [ "$PY" = "py" ]; then py -3 "$@"; else "$PY" "$@"; fi
}

resolve_dotnet() {
    if command -v dotnet >/dev/null 2>&1; then echo dotnet; return 0; fi
    local fallback="${LOCALAPPDATA:-}/Microsoft/dotnet/dotnet.exe"
    if [ -n "${LOCALAPPDATA:-}" ] && [ -f "$fallback" ]; then echo "$fallback"; return 0; fi
    return 1
}

pick_exe() {
    for cfg in Release Debug; do
        local exe="$ROOT/$EXE_REL/bin/$cfg/net8.0-windows/NcuCourseTable.Desktop.exe"
        if [ -f "$exe" ]; then echo "$exe"; return 0; fi
    done
    return 1
}

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

cmd_app() {
    local exe
    exe="$(pick_exe)" || die "未找到编译产物。请先执行: $0 build"
    info "时间表: $exe"

    local has_data=no prev=""
    for a in "$@"; do
        if [ "$prev" = "--data" ] || [ "$prev" = "-d" ]; then has_data=yes; fi
        prev="$a"
    done
    [ "$prev" = "--data" ] || [ "$prev" = "-d" ] && has_data=yes

    local args=() data_dir="$WIN_ROOT\\data"
    if [ "$has_data" = no ]; then args+=("-d" "$data_dir"); fi
    args+=("$@")

    info "数据目录: $data_dir（可用 --data <目录> 覆盖）"
    info "默认是桌面组件模式；打开主窗口请加 --window。"
    info "课程同步：主窗口 →「插件」→ 南昌大学课程源 → 登录并同步。"
    exec "$exe" "${args[@]}"
}

cmd_demo() {
    # SDK 离线演示（生成的 demo.db 仅供 SDK 验证，不会写入时间表数据）
    resolve_python || die "未找到 Python，请设置环境变量 NCU_PYTHON 指定解释器"
    ensure_sdk_deps
    mkdir -p "$ROOT/data"
    info "运行 ncu_sdk 离线演示（SDK 样例，不导入时间表）..."
    PYTHONPATH="$WIN_SDK" run_py -m ncu_sdk.cli --db "$WIN_ROOT\\data\\demo.db" demo "$@"
}

cmd_sdk() {
    [ $# -ge 1 ] || die "缺少子命令。示例: $0 sdk today   或   $0 sdk sync --term 202620271"
    resolve_python || die "未找到 Python，请设置环境变量 NCU_PYTHON 指定解释器"
    ensure_sdk_deps
    info "ncu_sdk.cli $*  （SDK: $WIN_SDK）"
    PYTHONPATH="$WIN_SDK" run_py -m ncu_sdk.cli "$@"
}

cmd_build() {
    local cfg=Release
    if [ "${1:-}" = "--debug" ]; then cfg=Debug; shift; fi
    local dn
    dn="$(resolve_dotnet)" || die "未找到 dotnet（.NET SDK ≥ 8），请安装或加入 PATH"
    info "dotnet build desktop/Timetable.sln -c $cfg"
    "$dn" build "$ROOT/desktop/Timetable.sln" -c "$cfg" "$@"
}

help() {
    cat <<'EOF'
时间表 —— 一键启动脚本

用法:
  ./start.sh [app] [参数...]    启动桌面组件（默认动作，参数原样透传）
  ./start.sh app --window       打开完整主窗口（周视图 / 日程 / 待办）
  ./start.sh app --host bottom  指定桌面组件宿主层（bottom=图标之上[默认]；wallpaper 实验性）
  ./start.sh demo               ncu_sdk 离线演示（SDK 样例，不写入时间表数据）
  ./start.sh sdk <命令...>      透传 SDK 命令行，如: ./start.sh sdk today
  ./start.sh build [--debug]    构建宿主 + 插件（默认 Release）
  ./start.sh help               显示本帮助

说明:
  * 桌面组件常驻托盘；退出请用托盘菜单。重复启动会提示“已在运行”。
  * 数据目录默认指向仓库 data/；旧 courses.json 会自动迁移为 schedule.json。
  * 课程同步由内置插件“南昌大学课程源”提供，需要 Python + sdk/ncu_sdk + 校园网/VPN。
  * 插件依赖路径: NCU_PYTHON / NCU_SDK_DIR / NCU_VENDOR_DIR；插件目录可用 TIMETABLE_PLUGINS_DIR 覆盖。
EOF
}

case "${1:-app}" in
    app|--app)      shift; cmd_app "$@" ;;
    demo)           shift; cmd_demo "$@" ;;
    sdk)            shift; cmd_sdk "$@" ;;
    build)          shift; cmd_build "$@" ;;
    help|-h|--help) help ;;
    *) die "未知命令: $1（./start.sh help 查看用法）" ;;
esac
