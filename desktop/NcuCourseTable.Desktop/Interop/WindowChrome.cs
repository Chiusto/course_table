using System;

namespace NcuCourseTable.Desktop.Interop;

/// <summary>
/// 窗口“桌面化”样式助手：
///  - 去掉任务栏按钮（WS_EX_APPWINDOW 复位）与 Alt+Tab 条目（WS_EX_TOOLWINDOW）；
///  - 置底 Z 序（Rainmeter 式，图标之上/普通窗口之下）；
///  - 点击穿透开关（WS_EX_TRANSPARENT + WS_EX_NOACTIVATE）；
///  - WM_MOUSEACTIVATE 无激活处理（SourceInitialized 后 hook，防止点桌面抢焦点）。
/// </summary>
public static class WindowChrome
{
    /// <summary>应用“桌面组件”外观：无任务栏按钮、无 Alt+Tab、工具窗口。</summary>
    public static void ApplyDesktopToolStyle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        long style = Native.GetWindowLong(hwnd, Native.GWL_STYLE);
        style |= Native.WS_POPUP | Native.WS_CLIPSIBLINGS | Native.WS_CLIPCHILDREN;
        Native.SetWindowLong(hwnd, Native.GWL_STYLE, style);

        long ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
        ex &= ~Native.WS_EX_APPWINDOW;          // 不占任务栏
        ex |= Native.WS_EX_TOOLWINDOW;          // 不进 Alt+Tab
        Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex);

        Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER |
            Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
    }

    /// <summary>压到 Z 序最底（HWND_BOTTOM）——保持在桌面图标层之上、所有普通窗口之下。</summary>
    public static void SendToBottom(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        Native.SetWindowPos(hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>点击穿透开/关。开启后鼠标事件直通桌面（锁定时用）。</summary>
    public static void SetClickThrough(IntPtr hwnd, bool enabled)
    {
        if (hwnd == IntPtr.Zero) return;
        long ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
        if (enabled)
        {
            ex |= Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE;
        }
        else
        {
            ex &= ~Native.WS_EX_TRANSPARENT;
            // NOACTIVATE 保留：桌面组件永远不该抢走键盘焦点
        }
        Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex);
        Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER |
            Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
    }

    public static bool IsClickThrough(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        return (Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TRANSPARENT) != 0;
    }

    /// <summary>是否带任务栏按钮（WS_EX_APPWINDOW 且无 WS_EX_TOOLWINDOW）。</summary>
    public static bool HasTaskbarButton(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        long ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
        return (ex & Native.WS_EX_APPWINDOW) != 0 && (ex & Native.WS_EX_TOOLWINDOW) == 0;
    }
}
