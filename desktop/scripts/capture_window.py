"""用 PrintWindow(PW_RENDERFULLCONTENT) 抓取被遮挡窗口的内容。

用法: python capture_window.py <窗口标题> <输出.png> [超宽] [超高]
"""
import ctypes
import ctypes.wintypes as wt
import sys

u = ctypes.windll.user32
g = ctypes.windll.gdi32
PW_RENDERFULLCONTENT = 0x00000002


def capture(hwnd, out_png):
    r = wt.RECT()
    u.GetWindowRect(hwnd, ctypes.byref(r))
    w, h = r.right - r.left, r.bottom - r.top
    if w <= 0 or h <= 0:
        print("bad rect", w, h)
        return False

    hdc = u.GetWindowDC(hwnd)
    mem = g.CreateCompatibleDC(hdc)
    bmp = g.CreateCompatibleBitmap(hdc, w, h)
    old = g.SelectObject(mem, bmp)
    ok = u.PrintWindow(hwnd, mem, PW_RENDERFULLCONTENT)

    class BMIH(ctypes.Structure):
        _fields_ = [("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG),
                    ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
                    ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG),
                    ("biYPelsPerMeter", wt.LONG), ("biClrUsed", wt.DWORD),
                    ("biClrImportant", wt.DWORD)]

    class BMI(ctypes.Structure):
        _fields_ = [("bmiHeader", BMIH), ("colors", wt.DWORD * 3)]

    bmi = BMI()
    bmi.bmiHeader.biSize = ctypes.sizeof(BMIH)
    bmi.bmiHeader.biWidth = w
    bmi.bmiHeader.biHeight = -h  # top-down
    bmi.bmiHeader.biPlanes = 1
    bmi.bmiHeader.biBitCount = 32
    bmi.bmiHeader.biCompression = 0  # BI_RGB
    buf = ctypes.create_string_buffer(w * h * 4)
    got = g.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bmi), 0)

    g.SelectObject(mem, old)
    g.DeleteObject(bmp)
    g.DeleteDC(mem)
    u.ReleaseDC(hwnd, hdc)

    if not ok or got == 0:
        print("PrintWindow failed", ok, got)
        return False

    from PIL import Image
    img = Image.frombuffer("RGBA", (w, h), buf.raw, "raw", "BGRA", 0, 1)
    img.convert("RGB").save(out_png)
    print("saved", out_png, w, h)
    return True


if __name__ == "__main__":
    u.SetProcessDPIAware()
    hwnd = u.FindWindowW(None, sys.argv[1])
    if not hwnd:
        print("window not found:", sys.argv[1])
        sys.exit(1)
    sys.exit(0 if capture(hwnd, sys.argv[2]) else 1)
