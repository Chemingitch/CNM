using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using CNM.Core;

namespace CNM.UI;

/// <summary>
/// 托盘右键菜单：配色跟应用主题一套（深色时不再冒出一个白底菜单）。
/// 色值和 wwwroot/style.css 里的变量对着来，改主题两边一起改。
/// </summary>
internal static class TrayMenu
{
    internal sealed class Palette : ProfessionalColorTable
    {
        private readonly bool _dark;

        public Palette(bool dark)
        {
            _dark = dark;
            UseSystemColors = false;
        }

        public Color Panel => _dark ? Color.FromArgb(0x16, 0x1A, 0x22) : Color.FromArgb(0xFF, 0xFF, 0xFF);
        public Color Hover => _dark ? Color.FromArgb(0x1B, 0x20, 0x2A) : Color.FromArgb(0xEB, 0xF1, 0xFF);
        public Color Line => _dark ? Color.FromArgb(0x24, 0x2B, 0x38) : Color.FromArgb(0xDF, 0xE5, 0xEF);
        public Color Text => _dark ? Color.FromArgb(0xE7, 0xEB, 0xF3) : Color.FromArgb(0x1A, 0x1F, 0x2B);
        public Color Dim => _dark ? Color.FromArgb(0x74, 0x7D, 0x90) : Color.FromArgb(0x8A, 0x93, 0xA6);

        public override Color ToolStripDropDownBackground => Panel;
        public override Color MenuBorder => Color.Transparent;   // 边框自己画圆角
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientMiddle => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color ImageMarginGradientBegin => Panel;
        public override Color ImageMarginGradientMiddle => Panel;
        public override Color ImageMarginGradientEnd => Panel;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Line;
        public override Color CheckBackground => Hover;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }

    /// <summary>圆角矩形路径（半径 r）。</summary>
    private static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        if (radius <= 0) { p.AddRectangle(r); return p; }
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        if (radius <= 0) { p.AddRectangle(r); return p; }
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>
    /// 手绘 20×20 矢量图标（不用字体图标：小尺寸下更清晰、可控）。
    /// 线条统一 1.6px、圆头端点；播放/停止用实心（符合惯例）。
    /// </summary>
    private static Image DrawIcon(string kind, Color color, int size = 20)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        float s = size, m = 3.6f;
        using var pen = new Pen(color, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(color);

        switch (kind)
        {
            case "show":          // 窗口：圆角框 + 标题栏横线
                using (var path = RoundRect(new RectangleF(m, m + 1, s - 2 * m, s - 2 * m - 1), 3.2f))
                    g.DrawPath(pen, path);
                g.DrawLine(pen, m + 1.6f, m + 5.6f, s - m - 1.6f, m + 5.6f);
                break;

            case "play":          // 播放：实心三角
                g.FillPolygon(brush, new[]
                {
                    new PointF(m + 1.2f, m - 0.6f),
                    new PointF(s - m - 0.4f, s / 2f),
                    new PointF(m + 1.2f, s - m + 0.6f),
                });
                break;

            case "stop":          // 停止：实心圆角方块
                using (var path = RoundRect(new RectangleF(m + 0.6f, m + 0.6f, s - 2 * m - 1.2f, s - 2 * m - 1.2f), 2.4f))
                    g.FillPath(brush, path);
                break;

            case "exit":          // 退出：电源（270° 弧 + 顶部竖线）
                float r = (s - 2 * m) / 2f;
                float cx = s / 2f, cy = s / 2f + 1f;
                g.DrawArc(pen, cx - r, cy - r, r * 2, r * 2, -48, 276);
                g.DrawLine(pen, cx, m - 0.4f, cx, cy - r * 0.45f);
                break;

            case "next":          // 切换模型：三角 + 竖线（跳到下一个）
                g.FillPolygon(brush, new[]
                {
                    new PointF(m + 0.6f, m - 0.2f),
                    new PointF(s / 2f + 1.2f, s / 2f),
                    new PointF(m + 0.6f, s - m + 0.2f),
                });
                using (var path = RoundRect(new RectangleF(s / 2f + 2.4f, m - 0.2f, 2.2f, s - 2 * m + 0.4f), 1f))
                    g.FillPath(brush, path);
                break;

            case "eject":         // 卸载：上三角 + 底座横线
                g.FillPolygon(brush, new[]
                {
                    new PointF(s / 2f, m - 0.2f),
                    new PointF(s - m + 0.2f, s / 2f + 0.6f),
                    new PointF(m - 0.2f, s / 2f + 0.6f),
                });
                using (var path = RoundRect(new RectangleF(m - 0.2f, s / 2f + 2.6f, s - 2 * m + 0.4f, 2.4f), 1.2f))
                    g.FillPath(brush, path);
                break;

            default:
                using (var path = RoundRect(new RectangleF(m, m, s - 2 * m, s - 2 * m), 3f))
                    g.DrawPath(pen, path);
                break;
        }
        return bmp;
    }

    internal sealed class Renderer : ToolStripProfessionalRenderer
    {
        private readonly Palette _p;
        private const int MenuRadius = 10;

        public Renderer(Palette p) : base(p)
        {
            _p = p;
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? _p.Text : _p.Dim;
            base.OnRenderItemText(e);
        }

        /// <summary>菜单背景：圆角填充（替代系统方角背景）。</summary>
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Math.Max(1, e.ToolStrip.Width - 1), Math.Max(1, e.ToolStrip.Height - 1));
            using var path = RoundRect(r, MenuRadius);
            using var b = new SolidBrush(_p.Panel);
            g.FillPath(b, path);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            // 不画描边：系统阴影 + 圆角已经能分出边界，再画一圈深色线就成了"黑框"
        }

        /// <summary>条目 hover：圆角胶囊，不再是整条方角高亮。</summary>
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(5, 1, e.Item.Width - 10, e.Item.Height - 2);
            if (r.Width <= 0 || r.Height <= 0) return;
            using var path = RoundRect(r, 7);
            using var b = new SolidBrush(_p.Hover);
            g.FillPath(b, path);
        }

        /// <summary>分隔线：两侧留白，短一点更精致。</summary>
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var g = e.Graphics;
            var r = e.Item.Bounds;
            using var pen = new Pen(_p.Line);
            g.DrawLine(pen, 12, r.Height / 2, Math.Max(13, r.Width - 12), r.Height / 2);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            // 不要左边那条渐变/竖线，跟界面一样干净
        }
    }

    /// <summary>给弹出窗口套圆角 Region —— 不然四角还是方的（Win10 兜底）。</summary>
    private static void ApplyRegion(ContextMenuStrip menu)
    {
        try
        {
            if (menu.Width <= 0 || menu.Height <= 0) return;
            using var path = RoundRect(new Rectangle(0, 0, menu.Width, menu.Height), 10);
            menu.Region?.Dispose();
            menu.Region = new Region(path);
        }
        catch { }
    }

    // ---------- Win11：让 DWM 自己给菜单窗口加圆角（阴影也跟着圆，不会露黑角） ----------
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;
    private static bool IsWin11 => Environment.OSVersion.Version.Build >= 22000;

    private static void ApplyDwmRound(IntPtr hwnd)
    {
        try
        {
            int pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch { }
    }

    /// <summary>建一套托盘菜单（界面和预览共用，保证两处长得一样）</summary>
    public static ContextMenuStrip Create(out ToolStripMenuItem miShow, out ToolStripMenuItem miToggle,
        out ToolStripMenuItem miNextModel, out ToolStripMenuItem miUnload, out ToolStripMenuItem miExit)
    {
        var menu = new ContextMenuStrip
        {
            ShowImageMargin = true,
            ImageScalingSize = new Size(20, 20),
            Padding = new Padding(4, 5, 4, 5),
            DropShadowEnabled = true,
            Font = new Font("Microsoft YaHei UI", 9f),
        };

        miShow = new ToolStripMenuItem("显示主界面") { Padding = new Padding(6, 5, 14, 5), Tag = "show" };
        miToggle = new ToolStripMenuItem("启动服务") { Padding = new Padding(6, 5, 14, 5), Tag = "toggle" };
        miNextModel = new ToolStripMenuItem("切换到下一个模型") { Padding = new Padding(6, 5, 14, 5), Tag = "nextmodel" };
        miUnload = new ToolStripMenuItem("卸载模型") { Padding = new Padding(6, 5, 14, 5), Tag = "unload" };
        miExit = new ToolStripMenuItem("退出 CNM") { Padding = new Padding(6, 5, 14, 5), Tag = "exit" };

        menu.Items.Add(miShow);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miToggle);
        menu.Items.Add(miNextModel);
        menu.Items.Add(miUnload);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miExit);

        // 先按深色画一版图标，ApplyTheme 会按当前主题重画
        var c0 = Color.FromArgb(0xE7, 0xEB, 0xF3);
        miShow.Image = DrawIcon("show", c0);
        miToggle.Image = DrawIcon("play", c0);
        miNextModel.Image = DrawIcon("next", c0);
        miUnload.Image = DrawIcon("eject", c0);
        miExit.Image = DrawIcon("exit", c0);

        if (IsWin11)
        {
            // Win11：DWM 圆角 —— 阴影会跟着圆，四角不会露黑边
            menu.DropShadowEnabled = true;
            menu.HandleCreated += (_, _) => ApplyDwmRound(menu.Handle);
            if (menu.IsHandleCreated) ApplyDwmRound(menu.Handle);
        }
        else
        {
            // Win10：只能靠 Region 裁圆角；阴影是方的会露黑角，干脆关掉
            menu.DropShadowEnabled = false;
            menu.Opening += (_, _) => ApplyRegion(menu);
            menu.Layout += (_, _) => ApplyRegion(menu);
        }
        return menu;
    }

    /// <summary>按主题上色（深/浅都走自己的配色，别交给系统）</summary>
    public static void ApplyTheme(ContextMenuStrip menu, bool dark)
    {
        if (menu == null) return;
        var p = new Palette(dark);
        menu.Renderer = new Renderer(p);
        menu.BackColor = p.Panel;
        menu.ForeColor = p.Text;
        foreach (ToolStripItem it in menu.Items)
        {
            it.ForeColor = p.Text;
            it.BackColor = Color.Transparent;
            if (it is ToolStripMenuItem mi && mi.Tag is string tag)
            {
                string kind = tag switch
                {
                    "show" => "show",
                    "exit" => "exit",
                    "nextmodel" => "next",
                    "unload" => "eject",
                    "toggle" => mi.Text.Contains("停止") ? "stop" : "play",
                    _ => null,
                };
                if (kind != null) { mi.Image?.Dispose(); mi.Image = DrawIcon(kind, p.Dim); }
            }
        }
        if (IsWin11) { if (menu.IsHandleCreated) ApplyDwmRound(menu.Handle); }
        else ApplyRegion(menu);
    }

    /// <summary>服务开/关切换时换图标（▶ 启动 / ■ 停止）。</summary>
    public static void SetToggleState(ToolStripMenuItem mi, bool running)
    {
        if (mi == null) return;
        mi.Image?.Dispose();
        var c = mi.ForeColor;
        var dim = Color.FromArgb(150, c.R, c.G, c.B);   // 图标比文字暗一档，不抢眼
        mi.Image = DrawIcon(running ? "stop" : "play", dim);
    }

    /// <summary>在菜单项右边显示这个动作的快捷键（没设就不显示）</summary>
    public static void SetShortcut(ToolStripMenuItem mi, string hotkey)
    {
        if (mi == null) return;
        var norm = Hotkeys.Normalize(hotkey ?? "");
        mi.ShortcutKeyDisplayString = norm.Length > 0 ? norm : null;
    }
}
