using System;

namespace NcuCourseTable.Desktop.Interop;

/// <summary>
/// 桌面宿主解析 —— 逻辑移植自 Rainmeter Library/System.cpp（GetDefaultShellWindow /
/// ShouldUseShellWindowAsDesktopIconsHost / GetDesktopIconsHostWindow，见 references/rainmeter）。
///
/// Windows 桌面窗口层级（Spy++ 实测，Win10/11 ≤23H2）：
///   Progman "Program Manager"
///      └ SHELLDLL_DefView          （桌面图标，SysListView32）
///   WorkerW A                      （同一 Explorer 进程，图标宿主）
///      └ SHELLDLL_DefView
///   WorkerW B                      （壁纸承载层，Wallpaper Engine 的 SetParent 目标）
/// Windows 11 24H2 起层级被重排：DefView 直接挂在 Progman 下，壁纸 WorkerW 移到 Progman 子树内。
/// 因此 Rainmeter 探测 user32 是否导出 GetCurrentMonitorTopologyId 判定 24H2，从而选择正确的宿主。
/// </summary>
public enum DesktopHostKind
{
    /// <summary>Rainmeter 式：普通置底顶层窗口（图标之上、普通窗口之下），无 SetParent。</summary>
    BottomWindow,

    /// <summary>Wallpaper Engine 式：SetParent 到壁纸 WorkerW（壁纸之上、图标之下）。24H2 自动降级。</summary>
    WallpaperWorkerW,
}

public static class DesktopHostKindExtensions
{
    public static DesktopHostKind ParseHost(string? value) =>
        string.Equals(value, "wallpaper", StringComparison.OrdinalIgnoreCase)
            ? DesktopHostKind.WallpaperWorkerW
            : DesktopHostKind.BottomWindow;

    public static string HostName(this DesktopHostKind kind) =>
        kind == DesktopHostKind.WallpaperWorkerW ? "wallpaper" : "bottom";
}

public static class DesktopHost
{
    private static IntPtr _shellWindow; // Progman 缓存

    /// <summary>Explorer 默认 shell 窗口（Progman）。</summary>
    public static IntPtr GetDefaultShellWindow()
    {
        if (_shellWindow != IntPtr.Zero && Native.IsWindow(_shellWindow))
        {
            return _shellWindow;
        }
        IntPtr w = Native.FindWindow(Native.ClassProgman, null);
        _shellWindow = Native.IsClass(w, Native.ClassProgman) ? w : IntPtr.Zero;
        return _shellWindow;
    }

    /// <summary>Win11 24H2+ 层级重排检测（同 Rainmeter：探测 GetCurrentMonitorTopologyId）。</summary>
    public static bool IsNewShellLayout() =>
        Native.User32Exports("GetCurrentMonitorTopologyId");

    /// <summary>shell 窗口内是否直接含有 DefView（24H2 布局特征）。</summary>
    public static bool ShellHasDefView(IntPtr shellW) =>
        shellW != IntPtr.Zero &&
        Native.FindWindowEx(shellW, IntPtr.Zero, Native.ClassDefView, null) != IntPtr.Zero;

    /// <summary>找桌面图标宿主窗口：SHELLDLL_DefView 的父窗口（Rainmeter 术语 icons host）。</summary>
    public static IntPtr GetDesktopIconsHost()
    {
        IntPtr shellW = GetDefaultShellWindow();
        if (shellW == IntPtr.Zero) return IntPtr.Zero;

        if (IsNewShellLayout())
        {
            // 24H2+：DefView 直接在 Progman 下 → 图标宿主就是 Progman
            return ShellHasDefView(shellW) ? shellW : IntPtr.Zero;
        }

        // 老布局：遍历顶层 WorkerW，找同 Explorer 进程且含 DefView 者
        IntPtr defView = Native.FindWindowEx(shellW, IntPtr.Zero, Native.ClassDefView, null);
        if (defView != IntPtr.Zero)
        {
            IntPtr parent = Native.GetAncestor(defView, Native.GA_PARENT);
            if (Native.IsClass(parent, Native.ClassWorkerW))
            {
                return parent;
            }
            return IntPtr.Zero; // DefView 直接挂 Progman（未触发过 WorkerW 拆分）
        }

        uint explorerPid = Native.ProcessIdOf(shellW);
        foreach (IntPtr h in Native.EnumTopLevelWindows())
        {
            if (Native.IsClass(h, Native.ClassWorkerW) &&
                Native.ProcessIdOf(h) == explorerPid &&
                Native.FindWindowEx(h, IntPtr.Zero, Native.ClassDefView, null) != IntPtr.Zero)
            {
                return h;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// 找壁纸承载 WorkerW（不含 DefView 的那个）。找不到（或 24H2 新布局）时回退 Progman。
    /// 先向 Progman 发送 0x052C 让 Explorer 拆分出壁纸层（Wallpaper Engine 同款触发）。
    /// </summary>
    public static IntPtr GetWallpaperHost()
    {
        IntPtr shellW = GetDefaultShellWindow();
        if (shellW == IntPtr.Zero) return IntPtr.Zero;

        // 触发壁纸层拆分（幂等；老布局下 Explorer 才需要）
        if (!IsNewShellLayout())
        {
            Native.SendMessageTimeout(shellW, Native.WM_SPAWN_WORKERW,
                new IntPtr(0x0D), IntPtr.Zero, Native.SMTO_NORMAL | Native.SMTO_ABORTIFHUNG,
                1000, out _);
        }

        if (IsNewShellLayout())
        {
            // 24H2：壁纸 WorkerW 是 Progman 的子窗口
            IntPtr h = IntPtr.Zero;
            while ((h = Native.FindWindowEx(shellW, h, Native.ClassWorkerW, null)) != IntPtr.Zero)
            {
                if (Native.IsWindowVisible(h) &&
                    Native.FindWindowEx(h, IntPtr.Zero, Native.ClassDefView, null) == IntPtr.Zero)
                {
                    return h;
                }
            }
            return shellW; // 回退：直接贴 Progman
        }

        // 老布局：顶层 WorkerW 中，与 Explorer 同进程且不含 DefView 者 = 壁纸层
        uint explorerPid = Native.ProcessIdOf(shellW);
        foreach (IntPtr h in Native.EnumTopLevelWindows())
        {
            if (Native.IsClass(h, Native.ClassWorkerW) &&
                Native.ProcessIdOf(h) == explorerPid &&
                Native.FindWindowEx(h, IntPtr.Zero, Native.ClassDefView, null) == IntPtr.Zero)
            {
                return h;
            }
        }

        // 找不到壁纸层（如“无壁纸/纯色”优化路径）→ 图标宿主
        return GetDesktopIconsHost();
    }

    /// <summary>把窗口嵌到指定宿主（SetParent），保持位置尺寸并刷新边框。返回是否成功。</summary>
    public static bool EmbedInto(IntPtr hwnd, IntPtr host)
    {
        if (hwnd == IntPtr.Zero || host == IntPtr.Zero) return false;
        if (Native.SetParent(hwnd, host) == IntPtr.Zero)
        {
            return false;
        }
        // 子窗口化后重算非客户区，保持可见
        Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER |
            Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED | Native.SWP_SHOWWINDOW);
        return true;
    }

    /// <summary>解除嵌入，恢复为顶层窗口（进程退出前可省略）。</summary>
    public static void Detach(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero && Native.GetParent(hwnd) != IntPtr.Zero)
        {
            Native.SetParent(hwnd, IntPtr.Zero);
        }
    }
}
