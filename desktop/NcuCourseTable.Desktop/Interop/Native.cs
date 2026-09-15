using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace NcuCourseTable.Desktop.Interop;

/// <summary>
/// Win32 P/Invoke 最小集 —— 桌面嵌入所需。
/// 常量与签名取自 user32.dll 官方文档；逻辑组织参考 Rainmeter Library/System.cpp。
/// </summary>
internal static class Native
{
    // ---------------------------------------------------------------- 窗口类
    public const string ClassProgman = "Progman";
    public const string ClassWorkerW = "WorkerW";
    public const string ClassDefView = "SHELLDLL_DefView";

    // ---------------------------------------------------------------- 窗口样式
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    public const long WS_POPUP = 0x80000000L;
    public const long WS_VISIBLE = 0x10000000L;
    public const long WS_CLIPSIBLINGS = 0x04000000L;
    public const long WS_CLIPCHILDREN = 0x02000000L;

    public const long WS_EX_TOOLWINDOW = 0x00000080L;   // 不出现在 Alt+Tab
    public const long WS_EX_APPWINDOW = 0x00040000L;    // 出现在任务栏（要去掉）
    public const long WS_EX_LAYERED = 0x00080000L;      // 分层窗口（透明度）
    public const long WS_EX_TRANSPARENT = 0x00000020L;  // 点击穿透
    public const long WS_EX_NOACTIVATE = 0x08000000L;   // 点击不激活

    // ---------------------------------------------------------------- Z 序 / 消息
    public static readonly IntPtr HWND_BOTTOM = new(1);
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_TOP = new(0);

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public const int GW_HWNDPREV = 3;
    public const int GW_HWNDNEXT = 2;
    public const uint GA_PARENT = 1;
    public const uint GA_ROOT = 2;

    /// <summary>Progman 广播消息：让 Explorer 重建壁纸 WorkerW（Wallpaper Engine 同款触发）。</summary>
    public const uint WM_SPAWN_WORKERW = 0x052C;

    public const uint SMTO_NORMAL = 0x0000;
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    // ---------------------------------------------------------------- P/Invoke
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindowEx(
        IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern long GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern long SetWindowLong(IntPtr hWnd, int nIndex, long dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    // ---------------------------------------------------------------- WinEvent（前台变化 → 即时感知“显示桌面”）
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    public delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventProc pfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public const int SW_HIDE = 0;
    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_RESTORE = 9;

    // ---------------------------------------------------------------- 消息 / 最小化
    public const uint WM_SYSCOMMAND = 0x0112;
    public const uint WM_SIZE = 0x0005;
    public const long SC_MINIMIZE = 0xF020;
    public const long SIZE_MINIMIZED = 1;

    // ---------------------------------------------------------------- DWM（遮蔽检测）
    public const int DWMWA_CLOAKED = 14;

    // ---------------------------------------------------------------- 工具方法
    public static string ClassOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    public static uint ProcessIdOf(IntPtr hwnd) =>
        GetWindowThreadProcessId(hwnd, out uint pid) != 0 ? pid : 0;

    /// <summary>类名是否匹配（忽略空类名）。</summary>
    public static bool IsClass(IntPtr hwnd, string className) =>
        hwnd != IntPtr.Zero && string.Equals(ClassOf(hwnd), className, StringComparison.Ordinal);

    /// <summary>user32 是否导出指定函数（用于 Win11 24H2 特性探测，同 Rainmeter）。</summary>
    public static bool User32Exports(string procName)
    {
        try
        {
            IntPtr h = GetModuleHandle("user32.dll");
            return h != IntPtr.Zero && GetProcAddress(h, procName) != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>枚举可见顶层窗口（回调返回 true 继续）。</summary>
    public static List<IntPtr> EnumTopLevelWindows()
    {
        var result = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h))
            {
                result.Add(h);
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
}
