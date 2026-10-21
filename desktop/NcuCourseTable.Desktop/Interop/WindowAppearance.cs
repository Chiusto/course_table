using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace NcuCourseTable.Desktop.Interop;

/// <summary>
/// 让系统窗口融入 Apple 风格的浅色材质：
///  - Win11：标题栏 / 边框颜色跟随窗口底色，并启用系统圆角；
///  - 桌面组件：启用深色亚克力（真实的桌面模糊），失败时退回半透明玻璃；
///  - 托盘菜单：Win11 圆角 + 自定义渲染器。
/// 所有调用都在 Win10 上安全失败，不影响功能。
/// </summary>
public static class WindowAppearance
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    private const int DwmwcpRound = 2;

    private enum AccentState
    {
        Disabled = 0,
        EnableGradient = 1,
        EnableTransparentGradient = 2,
        EnableBlurBehind = 3,
        EnableAcrylicBlurBehind = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    // ---------------------------------------------------------------- 浅色窗口 chrome
    public static readonly DependencyProperty LightChromeProperty = DependencyProperty.RegisterAttached(
        "LightChrome", typeof(bool), typeof(WindowAppearance),
        new PropertyMetadata(false, OnLightChromeChanged));

    public static bool GetLightChrome(DependencyObject d) => (bool)d.GetValue(LightChromeProperty);

    public static void SetLightChrome(DependencyObject d, bool value) => d.SetValue(LightChromeProperty, value);

    private static void OnLightChromeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window || e.NewValue is not true) return;
        if (window.IsInitialized)
        {
            ApplyLightChrome(window);
        }
        else
        {
            window.SourceInitialized += (_, _) => ApplyLightChrome(window);
        }
    }

    public static void ApplyLightChrome(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int light = 0;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref light, sizeof(int));

        int caption = 0x00F7F5F5;   // COLORREF BGR：#F5F5F7
        int text = 0x001F1D1D;      // #1D1D1F
        int border = 0x00EAE5E5;    // #E5E5EA
        _ = DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref text, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));

        int round = DwmwcpRound;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
    }

    // ---------------------------------------------------------------- 亚克力材质
    /// <summary>
    /// 在窗口背后启用深色亚克力模糊。仅用于已允许分层透明的桌面组件；
    /// 壁纸层（非分层子窗口）请勿调用。
    /// </summary>
    public static bool EnableDarkAcrylic(IntPtr hwnd, byte tintAlpha = 0xC8)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            var policy = new AccentPolicy
            {
                AccentState = AccentState.EnableAcrylicBlurBehind,
                AccentFlags = 2,
                // AABBGGRR：近黑蓝灰，保留玻璃感
                GradientColor = (tintAlpha << 24) | (0x24 << 16) | (0x1F << 8) | 0x1A,
            };

            int size = Marshal.SizeOf<AccentPolicy>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, ptr, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = 19, // WCA_ACCENT_POLICY
                    Data = ptr,
                    SizeOfData = size,
                };
                return SetWindowCompositionAttribute(hwnd, ref data) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>为 WinForms 托盘菜单启用 Win11 系统圆角。</summary>
    public static void RoundPopupMenu(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        int round = DwmwcpRound;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
        int border = 0x00EAE5E5;
        _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));
    }
}
