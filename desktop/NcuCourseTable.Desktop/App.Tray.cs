using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using NcuCourseTable.Desktop.Interop;

namespace NcuCourseTable.Desktop;

/// <summary>
/// 托盘常驻（widget 模式）—— 极简入口：课程中心 / 设置… / 退出；双击图标 = 设置面板。
/// 登录在设置面板与课程中心触发；小组件显隐、锁定穿透、桌面层级、透明度、开机自启、
/// 立即刷新等开关都在设置面板（Views/SettingsWindow），状态回刷经 RefreshTrayChecks。
/// （2026-09-06：菜单去掉“打开”字眼并移除登录项——设置/课程中心内均有登录按钮。）
/// </summary>
public partial class App
{
    private NotifyIcon? _tray;

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "NcuCourseTable.Widget";

    private void BuildTray()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false };

        var center = new ToolStripMenuItem("课程中心");
        center.Click += (_, _) => ShowManager();
        center.ToolTipText = "完整课表管理窗口（周视图 / 增删改课程 / 登录同步）";
        menu.Items.Add(center);

        var settings = new ToolStripMenuItem("设置…");
        settings.Click += (_, _) => ShowSettings();
        settings.ToolTipText = "账号登录、显隐、锁定、桌面层级、透明度、开机自启";
        menu.Items.Add(settings);

        menu.Items.Add(new ToolStripSeparator());

        var exit = new ToolStripMenuItem("退出");
        exit.Click += (_, _) => ExitAll();
        menu.Items.Add(exit);

        menu.Opening += (_, _) => RefreshTrayChecks();

        _tray = new NotifyIcon
        {
            Icon = MakeTrayIcon(),
            Text = "南昌大学课程表（桌面组件）",
            ContextMenuStrip = menu,
            Visible = true,
        };
        // 双击托盘图标 = 打开设置面板（账号登录在其首卡片内）
        _tray.DoubleClick += (_, _) => ShowSettings();

        RefreshTrayChecks(); // 启动即按凭据状态刷新菜单文案 / 图标提示
    }

    /// <summary>托盘提示文案 + 设置面板（若开着）状态回刷。</summary>
    private void RefreshTrayChecks()
    {
        string saved = _vault?.UsernameOrEmpty ?? "";
        if (_tray is not null)
        {
            _tray.Text = saved.Length > 0
                ? $"南昌大学课程表 · 账号 {MaskAccount(saved)}（双击打开设置）"
                : "南昌大学课程表（双击打开设置，配置账号后同步课表）";
        }

        _settings?.RefreshState();
    }

    // ------------------------------------------------------------------ 开机自启
    internal bool IsAutoStartEnabled()
    {
        try
        {
            using RegistryKey? k = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return k?.GetValue(RunValueName) is not null;
        }
        catch
        {
            return false;
        }
    }

    internal void SetAutoStart(bool enabled)
    {
        try
        {
            using RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enabled)
            {
                string cmd = $"\"{Environment.ProcessPath}\" --data \"{_store!.Dir}\" --host {_host.HostName()}";
                k.SetValue(RunValueName, cmd);
            }
            else
            {
                k.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"设置开机自启失败：{ex.Message}",
                "南昌大学课程表", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }

    // ------------------------------------------------------------------ 托盘图标
    private static System.Drawing.Icon MakeTrayIcon()
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Color.FromArgb(255, 62, 120, 255));
            g.FillEllipse(bg, 0, 0, 15, 15);
            using var f = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold, GraphicsUnit.Pixel);
            TextRenderer.DrawText(g, "课", f, new Rectangle(0, -1, 16, 16), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        IntPtr h = bmp.GetHicon();
        try
        {
            using var tmp = System.Drawing.Icon.FromHandle(h);
            return (System.Drawing.Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(h);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
