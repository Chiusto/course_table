using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using NcuCourseTable.Desktop.Interop;

namespace NcuCourseTable.Desktop;

/// <summary>
/// 托盘常驻（widget 模式）—— 与主界面一致的 Apple 浅色材质快捷入口：
/// 状态头（周次 / 待办）→ 主窗口 / 插件 / 设置 → 组件显隐 / 锁定 / 层级 → 退出。
/// 双击图标仍打开设置面板；菜单打开时统一回刷文案与可用状态。
/// </summary>
public partial class App
{
    private NotifyIcon? _tray;

    private ToolStripMenuItem? _traySubheader;
    private ToolStripMenuItem? _traySync;
    private ToolStripMenuItem? _trayWidget;

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "NcuCourseTable.Widget";

    // 与 WPF 端一致的 Apple 浅色材质
    private static readonly Color TrayPaper = Color.FromArgb(0xF7, 0xF7, 0xFA);
    private static readonly Color TrayPaperDeep = Color.FromArgb(0xE8, 0xE8, 0xED);
    private static readonly Color TrayInk = Color.FromArgb(0x1D, 0x1D, 0x1F);
    private static readonly Color TrayInkFaint = Color.FromArgb(0x86, 0x86, 0x8B);
    private static readonly Color TrayAccent = Color.FromArgb(0x00, 0x7A, 0xFF);
    private static readonly Color TrayAccentDeep = Color.FromArgb(0x00, 0x62, 0xCC);
    private static readonly Color TrayDanger = Color.FromArgb(0xFF, 0x3B, 0x30);
    private static readonly Color TrayRule = Color.FromArgb(0xE5, 0xE5, 0xEA);

    private void BuildTray()
    {
        ToolStripManager.Renderer = new AppleMenuRenderer();

        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            ShowCheckMargin = false,
            Font = new Font("Segoe UI", 9.5f),
            BackColor = TrayPaper,
            ForeColor = TrayInk,
            Padding = new Padding(0, 6, 0, 6),
            MinimumSize = new Size(240, 0),
        };
        menu.Opening += (_, _) =>
        {
            WindowAppearance.RoundPopupMenu(menu.Handle);
            RefreshTrayChecks();
        };

        menu.Items.Add(new ToolStripMenuItem("时间表")
        {
            Enabled = false,
            Tag = "header",
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Regular),
            Padding = new Padding(8, 2, 8, 2),
        });

        _traySubheader = new ToolStripMenuItem("")
        {
            Enabled = false,
            Tag = "subheader",
            Font = new Font("Segoe UI", 8.5f),
            Padding = new Padding(8, 1, 8, 2),
        };
        menu.Items.Add(_traySubheader);
        menu.Items.Add(new ToolStripSeparator());

        var center = new ToolStripMenuItem("打开主窗口")
        {
            Font = new Font(menu.Font, FontStyle.Bold),
            ToolTipText = "完整时间表：周视图、日程增删改、待办管理",
        };
        center.Click += (_, _) => ShowManager();
        menu.Items.Add(center);

        var settings = new ToolStripMenuItem("设置…")
        {
            ToolTipText = "学期、桌面组件、插件、坚果云同步与开机自启",
        };
        settings.Click += (_, _) => ShowSettings();
        menu.Items.Add(settings);

        _traySync = new ToolStripMenuItem("立即同步到坚果云");
        _traySync.Click += async (_, _) => await SyncNutstoreNowAsync(announce: true);
        menu.Items.Add(_traySync);
        menu.Items.Add(new ToolStripSeparator());

        _trayWidget = new ToolStripMenuItem("隐藏桌面组件");
        _trayWidget.Click += (_, _) =>
        {
            SetWidgetVisible(!IsWidgetShown);
            RefreshTrayChecks();
        };
        menu.Items.Add(_trayWidget);

        menu.Items.Add(new ToolStripSeparator());
        var exit = new ToolStripMenuItem("退出") { Tag = "danger" };
        exit.Click += (_, _) => ExitAll();
        menu.Items.Add(exit);

        ApplyMenuPadding(menu.Items);

        _tray = new NotifyIcon
        {
            Icon = MakeTrayIcon(),
            Text = "时间表",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowSettings();

        RefreshTrayChecks();
    }

    private static void ApplyMenuPadding(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items)
        {
            if (item is not ToolStripMenuItem menuItem) continue;
            menuItem.Padding = new Padding(8, 2, 8, 2);
            ApplyMenuPadding(menuItem.DropDownItems);
        }
    }

    /// <summary>托盘状态文案 + 菜单动态项 + 设置面板回刷。</summary>
    private void RefreshTrayChecks()
    {
        int pending = _store?.Data.Todos.Count(t => !t.Done) ?? 0;
        string todo = pending > 0 ? $"待办 {pending}" : "暂无待办";
        string term = TermProgressText();

        if (_traySubheader is not null)
        {
            _traySubheader.Text = $"{term}  ·  {todo}";
        }

        if (_traySync is not null)
        {
            bool configured = NutstoreConfigured;
            _traySync.Visible = configured;
            _traySync.Text = NutstoreBusy ? "坚果云同步中…" : "立即同步到坚果云";
            _traySync.Enabled = configured && !NutstoreBusy;
            _traySync.ToolTipText = string.IsNullOrWhiteSpace(NutstoreLastSyncText)
                ? "通过 WebDAV 与坚果云双向同步"
                : NutstoreLastSyncText;
        }

        if (_trayWidget is not null)
        {
            _trayWidget.Text = IsWidgetShown ? "隐藏桌面组件" : "显示桌面组件";
        }

        if (_tray is not null)
        {
            string tip = $"时间表 · {term} · {todo}（双击设置）";
            _tray.Text = tip.Length <= 63 ? tip : tip[..63];
        }

        _settings?.RefreshState();
    }

    /// <summary>设置面板报头用：第 N 周 · 周X / 未开学 / 学期已结束。</summary>
    internal string TermStatusText => TermProgressText();

    private string TermProgressText()
    {
        if (_store is null) return "未设置学期";
        if (!DateTime.TryParseExact(_store.Data.TermStart, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime start))
        {
            return "未设置学期";
        }

        int days = (DateTime.Today - start.Date).Days;
        if (days < 0) return "未开学";
        int total = _store.Data.TotalWeeks > 0 ? _store.Data.TotalWeeks : 20;
        int week = days / 7 + 1;
        if (week > total) return "学期已结束";

        string[] names = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
        return $"第 {week} 周 · {names[(int)DateTime.Today.DayOfWeek]}";
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
                "时间表", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }

    // ------------------------------------------------------------------ 托盘图标
    private static Icon MakeTrayIcon()
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            using var shape = RoundedRect(new RectangleF(0.5f, 0.5f, 15f, 15f), 4.5f);
            using var bg = new SolidBrush(TrayAccent);
            g.FillPath(bg, shape);
            using var border = new Pen(TrayAccentDeep, 1f);
            g.DrawPath(border, shape);

            using var f = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold, GraphicsUnit.Pixel);
            TextRenderer.DrawText(g, "时", f, new Rectangle(0, 0, 16, 16), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        IntPtr h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(h);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ------------------------------------------------------------------ 菜单渲染
    private sealed class AppleColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => TrayPaper;
        public override Color ImageMarginGradientBegin => TrayPaper;
        public override Color ImageMarginGradientMiddle => TrayPaper;
        public override Color ImageMarginGradientEnd => TrayPaper;
        public override Color MenuBorder => TrayRule;
        public override Color MenuItemBorder => TrayAccent;
        public override Color MenuItemSelected => TrayPaperDeep;
        public override Color MenuItemSelectedGradientBegin => TrayPaperDeep;
        public override Color MenuItemSelectedGradientEnd => TrayPaperDeep;
        public override Color MenuItemPressedGradientBegin => TrayPaperDeep;
        public override Color MenuItemPressedGradientEnd => TrayPaperDeep;
        public override Color SeparatorDark => TrayRule;
        public override Color SeparatorLight => TrayRule;
    }

    private sealed class AppleMenuRenderer : ToolStripProfessionalRenderer
    {
        public AppleMenuRenderer() : base(new AppleColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var b = new SolidBrush(TrayPaper);
            e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var p = new Pen(TrayRule, 1);
            e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var rect = new Rectangle(Point.Empty, e.Item.Size);
            using var paper = new SolidBrush(TrayPaper);
            e.Graphics.FillRectangle(paper, rect);

            if (e.Item.Selected && e.Item.Enabled)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var hoverBounds = new RectangleF(4, 1, Math.Max(1, rect.Width - 8), Math.Max(1, rect.Height - 2));
                using GraphicsPath path = RoundedRect(hoverBounds, 6);
                using var hover = new SolidBrush(TrayPaperDeep);
                e.Graphics.FillPath(hover, path);
            }
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using var p = new Pen(TrayRule, 1);
            e.Graphics.DrawLine(p, 12, y, e.Item.Width - 12, y);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            Color color = (e.Item.Tag as string) switch
            {
                "header" => TrayInk,
                "subheader" => TrayInkFaint,
                "danger" => TrayDanger,
                _ => !e.Item.Enabled ? TrayInkFaint : TrayInk,
            };
            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, color, e.TextFormat);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item is { Selected: true } ? TrayInk : TrayInkFaint;
            base.OnRenderArrow(e);
        }
    }
}
