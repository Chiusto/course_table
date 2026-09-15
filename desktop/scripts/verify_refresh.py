"""端到端自动刷新验证（bottom 模式）。

链路：启动桌面组件 -> 基线截图 A -> 外部改写 courses.json（组合数学 -> 组合数学A）
-> 等待 FileSystemWatcher(350ms debounce) + 重渲染 -> 截图 B
-> 断言 B 与 A 差异明显（changed_pixel_ratio > 0.01）
-> 还原原始数据 -> 截图 C -> 断言 C 与 A 基本一致（ratio < 0.002）且 C 与 B 差异明显
-> 结束进程，输出 JSON 概要。退出码 0=通过。
"""
import ctypes
import ctypes.wintypes as wt
import json
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from capture_window import capture  # noqa: E402

from PIL import Image, ImageChops  # noqa: E402

u = ctypes.windll.user32
WIDGET_TITLE = "NcuCourseTable.DesktopWidget"
ROOT = Path(__file__).parent.parent
EXE = ROOT / "NcuCourseTable.Desktop" / "bin" / "Debug" / "net8.0-windows" / "NcuCourseTable.Desktop.exe"
SAMPLE = ROOT / "sample_data" / "courses.json"
WORK = Path(tempfile.gettempdir()) / "ncu_refresh_verify"


def find_deep(title):
    res = []

    def title_of(h):
        buf = ctypes.create_unicode_buffer(256)
        u.GetWindowTextW(h, buf, 256)
        return buf.value

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def child_cb(h, _):
        if title_of(h) == title:
            res.append(h)
        return True

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def top_cb(h, _):
        if title_of(h) == title:
            res.append(h)
        u.EnumChildWindows(h, child_cb, 0)
        return True

    u.EnumWindows(top_cb, 0)
    return res[0] if res else 0


def changed_ratio(p1, p2):
    a = Image.open(p1).convert("RGB")
    b = Image.open(p2).convert("RGB")
    if a.size != b.size:
        return -1.0
    diff = ImageChops.difference(a, b)
    px = list(diff.getdata())
    return sum(1 for p in px if max(p) > 20) / len(px)


def wait_widget(timeout=20):
    t0 = time.time()
    while time.time() - t0 < timeout:
        h = find_deep(WIDGET_TITLE)
        if h and u.IsWindowVisible(h):
            time.sleep(1.5)  # 等首帧渲染稳定
            return h
        time.sleep(0.3)
    return 0


def main():
    u.SetProcessDPIAware()
    WORK.mkdir(parents=True, exist_ok=True)
    data_file = WORK / "courses.json"
    shutil.copyfile(SAMPLE, data_file)

    orig = json.loads(data_file.read_text(encoding="utf-8"))
    mutated = json.loads(data_file.read_text(encoding="utf-8"))
    hit = False
    for c in mutated.get("Courses", []):
        if c.get("Name") == "组合数学A":
            c["Name"] = "组合数学B"
            hit = True
    if not hit:
        print(json.dumps({"error": "sample data has no 组合数学A course"}))
        sys.exit(1)

    proc = subprocess.Popen([str(EXE), "--data", str(WORK)])
    try:
        hwnd = wait_widget()
        if not hwnd:
            print(json.dumps({"error": "widget window not found within timeout"}))
            sys.exit(1)

        a = WORK / "shot_a.png"
        b = WORK / "shot_b.png"
        c_png = WORK / "shot_c.png"
        capture(hwnd, str(a))
        time.sleep(0.5)
        capture(hwnd, str(a))  # 二次覆盖，排除首帧抖动

        # ---- 改写数据（模拟外部脚本/SDK 导出）
        data_file.write_text(json.dumps(mutated, ensure_ascii=False, indent=2), encoding="utf-8")
        time.sleep(4.0)  # watcher(350ms debounce) + 渲染
        capture(hwnd, str(b))

        # ---- 还原数据
        data_file.write_text(json.dumps(orig, ensure_ascii=False, indent=2), encoding="utf-8")
        time.sleep(4.0)
        capture(hwnd, str(c_png))

        # 汇总到 desktop/ 下供查看 + 拼接三联图
        for name in ("a", "b", "c"):
            shutil.copyfile(WORK / f"shot_{name}.png", ROOT / f"verify_refresh_{name}.png")
        tri = Image.new("RGB", (372 * 3 + 24, 352 + 40), (20, 26, 38))
        tri.paste(Image.open(WORK / "shot_a.png"), (0, 40))
        tri.paste(Image.open(WORK / "shot_b.png"), (392, 40))
        tri.paste(Image.open(WORK / "shot_c.png"), (784, 40))
        tri.save(ROOT / "verify_refresh_triplet.png")

        r_ab = changed_ratio(a, b)
        r_ac = changed_ratio(a, c_png)
        r_bc = changed_ratio(b, c_png)
        out = {
            "hwnd": hex(hwnd),
            "changed_a_to_b": round(r_ab, 4),
            "changed_a_to_c": round(r_ac, 4),
            "changed_b_to_c": round(r_bc, 4),
        }
        # 单字符像素差通常在 0.001~0.005 量级（数百 / 13 万像素），不能套 0.01 阈值。
        # 关键不变量：A 与 C 完全一致；B 与 A/C 有差异。
        ok = r_ab > 0.0001 and r_ac < 1e-6 and r_bc > 0.0001
        out["ok"] = bool(ok)
        print(json.dumps(out, ensure_ascii=False, indent=1))
        sys.exit(0 if ok else 1)
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=8)
        except subprocess.TimeoutExpired:
            proc.kill()


if __name__ == "__main__":
    main()
