using System.Drawing;

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

        public Color Panel => _dark ? Color.FromArgb(0x17, 0x1B, 0x23) : Color.FromArgb(0xFF, 0xFF, 0xFF);
        public Color Hover => _dark ? Color.FromArgb(0x25, 0x2E, 0x40) : Color.FromArgb(0xEB, 0xF1, 0xFF);
        public Color Line => _dark ? Color.FromArgb(0x2A, 0x32, 0x42) : Color.FromArgb(0xDF, 0xE5, 0xEF);
        public Color Text => _dark ? Color.FromArgb(0xE7, 0xEB, 0xF3) : Color.FromArgb(0x1A, 0x1F, 0x2B);
        public Color Dim => _dark ? Color.FromArgb(0x74, 0x7D, 0x90) : Color.FromArgb(0x8A, 0x93, 0xA6);

        public override Color ToolStripDropDownBackground => Panel;
        public override Color MenuBorder => Line;
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

    internal sealed class Renderer : ToolStripProfessionalRenderer
    {
        private readonly Palette _p;

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

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var b = new SolidBrush(_p.Panel);
            e.Graphics.FillRectangle(b, new Rectangle(Point.Empty, e.ToolStrip.Size));
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            // 不要左边那条渐变/竖线，跟界面一样干净
        }
    }

    /// <summary>建一套托盘菜单（界面和预览共用，保证两处长得一样）</summary>
    public static ContextMenuStrip Create(out ToolStripMenuItem miShow, out ToolStripMenuItem miToggle, out ToolStripMenuItem miExit)
    {
        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            Font = new Font("Microsoft YaHei UI", 9f)
        };

        miShow = new ToolStripMenuItem("显示主界面");
        miToggle = new ToolStripMenuItem("启动服务");
        miExit = new ToolStripMenuItem("退出 CNM");

        menu.Items.Add(miShow);
        menu.Items.Add(miToggle);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miExit);
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
        }
    }
}
