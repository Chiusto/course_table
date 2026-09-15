"""桌面嵌入验证：bottom / wallpaper / window 三模式断言 + PrintWindow 截图。

用法: python verify_embed.py <bottom|wallpaper|window>
断言输出 JSON 概要；退出码 0=通过 1=失败。
"""
import ctypes
import ctypes.wintypes as wt
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from capture_window import capture  # noqa: E402

from PIL import Image  # noqa: E402

u = ctypes.windll.user32

WIDGET_TITLE = "NcuCourseTable.DesktopWidget"
MANAGER_TITLE = "南昌大学课程表组件"
WS_EX_APPWINDOW = 0x00040000
WS_EX_TOOLWINDOW = 0x00000080
WS_EX_TRANSPARENT = 0x00000020
WS_EX_TOPMOST = 0x00000008
GWL_EXSTYLE = -20


def class_of(hwnd):
    buf = ctypes.create_unicode_buffer(256)
    u.GetClassNameW(hwnd, buf, 256)
    return buf.value


def exstyle(hwnd):
    return u.GetWindowLongW(hwnd, GWL_EXSTYLE)


def find(title):
    return u.FindWindowW(None, title)


def find_deep(title):
    """全窗口树查找（嵌入桌面层后窗口不再是顶层，FindWindow 找不到）。"""
    res = []

    def make_title_reader(h):
        buf = ctypes.create_unicode_buffer(256)
        u.GetWindowTextW(h, buf, 256)
        return buf.value

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def child_cb(h, _):
        if make_title_reader(h) == title:
            res.append(h)
        return True

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def top_cb(h, _):
        if make_title_reader(h) == title:
            res.append(h)
        u.EnumChildWindows(h, child_cb, 0)
        return True

    u.EnumWindows(top_cb, 0)
    return res[0] if res else 0


def pid_of(hwnd):
    p = wt.DWORD()
    u.GetWindowThreadProcessId(hwnd, ctypes.byref(p))
    return p.value


def pixel_stats(path):
    """深色卡片 + 白色文字的特征统计（原尺寸，避免缩放摊薄文字像素）。"""
    img = Image.open(path).convert("RGB")
    px = list(img.getdata())
    n = len(px)
    mean = tuple(sum(c[i] for c in px) // n for i in range(3))
    bright = sum(1 for r, g, b in px if r > 170 and g > 170 and b > 170) / n
    dark = sum(1 for r, g, b in px if r < 90 and g < 90 and b < 110) / n
    return list(mean), round(bright, 3), round(dark, 3)


def capture_widget(out_png):
    hwnd = find_deep(WIDGET_TITLE)
    return hwnd, capture(hwnd, out_png)


def main():
    u.SetProcessDPIAware()
    mode = sys.argv[1] if len(sys.argv) > 1 else "bottom"
    shot_dir = Path(__file__).parent.parent
    out = {}
    code = 1

    if mode == "bottom":
        hwnd, ok_cap = capture_widget(str(shot_dir / "verify_widget_bottom.png"))
        if not hwnd:
            out["error"] = "widget window not found"
        else:
            own_pid = pid_of(hwnd)
            par = u.GetParent(hwnd)
            par_pid = pid_of(par)
            # WPF 顶层窗口的 GetParent 是本进程隐藏 owner —— 即“未嵌入外部宿主”
            out["parent_is_self_process"] = par_pid == own_pid
            out["parent_class"] = class_of(par) if par else "(top-level)"
            ex = exstyle(hwnd)
            out["no_taskbar_button"] = bool(ex & WS_EX_APPWINDOW == 0)
            out["toolwindow"] = bool(ex & WS_EX_TOOLWINDOW)
            out["not_click_through"] = bool(ex & WS_EX_TRANSPARENT == 0)
            out["not_topmost"] = bool(ex & WS_EX_TOPMOST == 0)
            if ok_cap:
                mean, bright, dark = pixel_stats(shot_dir / "verify_widget_bottom.png")
                out["mean_rgb"] = mean
                out["bright_ratio"] = bright   # 白色文字
                out["dark_ratio"] = dark       # 深色卡片底
            ok = (
                out["parent_is_self_process"] and out["no_taskbar_button"]
                and out["toolwindow"] and out["not_click_through"] and out["not_topmost"]
                and ok_cap and dark > 0.3 and bright > 0.005
            )
            code = 0 if ok else 1
            out["ok"] = bool(ok)

    elif mode == "wallpaper":
        hwnd, ok_cap = capture_widget(str(shot_dir / "verify_widget_wallpaper.png"))
        if not hwnd:
            out["error"] = "widget window not found"
        else:
            # SetParent 后窗口挂入桌面 shell 树；owner 关系仍指向 WPF 隐藏窗口，
            # 因此用 GetAncestor(GA_PARENT) 取真实父窗口。
            GA_PARENT = 1
            par = u.GetAncestor(hwnd, GA_PARENT)
            out["ancestor_parent"] = hex(par)
            out["parent_class"] = class_of(par)
            ex = exstyle(hwnd)
            out["no_taskbar_button"] = bool(ex & WS_EX_APPWINDOW == 0)
            out["click_through"] = bool(ex & WS_EX_TRANSPARENT)  # 壁纸层恒穿透
            if ok_cap:
                mean, bright, dark = pixel_stats(shot_dir / "verify_widget_wallpaper.png")
                out["mean_rgb"] = mean
                out["bright_ratio"] = bright
                out["dark_ratio"] = dark
            ok = (
                par != 0 and out["parent_class"] in ("WorkerW", "Progman")
                and out["no_taskbar_button"] and out["click_through"]
            )
            code = 0 if ok else 1
            out["ok"] = bool(ok)

    elif mode == "window":
        hwnd = find(MANAGER_TITLE)
        if not hwnd:
            out["error"] = "manager window not found"
        else:
            out["hwnd"] = hex(hwnd)
            out["visible"] = bool(u.IsWindowVisible(hwnd))
            out["ok"] = True
            code = 0

    print(json.dumps(out, ensure_ascii=False, indent=1))
    sys.exit(code)


if __name__ == "__main__":
    main()
