using System.Drawing;
using System.Drawing.Drawing2D;
using System.Media;
using CNM.Core;

namespace CNM.UI;

/// <summary>
/// 跟随亮暗主题的自绘提示框。
/// 原生 MessageBox 由系统画：深色界面下会突然冒出一块白的，所以这里自己画一个
/// （标题栏也跟着染，和主窗口一套色）。用法：
///     ThemedDialog.Info("...")              纯提示
///     ThemedDialog.Error("...", "CNM", true) 深色错误框
/// 不传 dark 就按设置里的主题走。
/// </summary>
internal static class ThemedDialog
{
    internal enum Kind { Info, Error }

    public static void Info(string message, string title = "CNM", bool? dark = null, IWin32Window owner = null)
        => Show(message, title, Kind.Info, dark, owner);

    public static void Error(string message, string title = "CNM", bool? dark = null, IWin32Window owner = null)
        => Show(message, title, Kind.Error, dark, owner);

    private static void Show(string message, string title, Kind kind, bool? dark, IWin32Window owner)
    {
        bool isDark = dark ?? ThemeIsDark();
        try
        {
            using var f = new Box(message ?? "", title ?? "CNM", kind, isDark, owner != null);
            try { (kind == Kind.Error ? SystemSounds.Hand : SystemSounds.Asterisk).Play(); } catch { }
            if (owner != null && owner.Handle != IntPtr.Zero) f.ShowDialog(owner);
            else f.ShowDialog();
        }
        catch
        {
            // 自绘这条路出意外的话，宁可难看也不能丢提示
            try
            {
                MessageBox.Show(message, title, MessageBoxButtons.OK,
                    kind == Kind.Error ? MessageBoxIcon.Error : MessageBoxIcon.Information);
            }
            catch { }
        }
    }

    private static bool ThemeIsDark()
    {
        try { return !string.Equals(Settings.Load().Theme, "light", StringComparison.OrdinalIgnoreCase); }
        catch { return true; }
    }

    /// <summary>对话框本体：一个圆图标 + 会换行的正文 + 右下角一个按钮</summary>
    private sealed class Box : Form
    {
        private readonly Palette _p;
        private readonly Kind _kind;
        private readonly string _msg;
        private readonly DlgButton _ok;

        private float _s = 1f;
        private Rectangle _icon;
        private Rectangle _text;

        public Box(string msg, string title, Kind kind, bool dark, bool hasOwner)
        {
            _p = Palette.For(dark);
            _kind = kind;
            _msg = string.IsNullOrWhiteSpace(msg) ? "（没有内容）" : msg;

            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ShowIcon = false;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            AutoScaleMode = AutoScaleMode.None;      // 尺寸自己按 DPI 算，别让 WinForms 再缩一遍
            StartPosition = hasOwner ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen;
            BackColor = _p.Bg;
            ForeColor = _p.Text;
            DoubleBuffered = true;
            TopMost = true;

            _ok = new DlgButton("确定", _p) { DialogResult = DialogResult.OK };
            Controls.Add(_ok);
            AcceptButton = _ok;
            CancelButton = _ok;      // Esc 也能关
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            DarkTitleBar.Apply(Handle, _p.IsDark);
            LayoutBox();
        }

        private void LayoutBox()
        {
            _s = DeviceDpi / 96f;
            Font = new Font("Microsoft YaHei UI", 9.5f * _s, FontStyle.Regular, GraphicsUnit.Point);
            _ok.Font = new Font("Microsoft YaHei UI", 9f * _s, FontStyle.Regular, GraphicsUnit.Point);

            int pad = Px(20);
            int icon = Px(30);
            int gap = Px(14);
            int maxText = Px(430);
            int minWidth = Px(300);
            int btnH = Px(30);

            const TextFormatFlags wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;
            var size = TextRenderer.MeasureText(_msg, Font, new Size(maxText, int.MaxValue), wrap);
            int textW = Math.Max(Px(200), Math.Min(size.Width, maxText));
            int textH = Math.Max(size.Height, Px(22));

            int width = Math.Max(minWidth, pad * 2 + icon + gap + textW);
            int height = pad + Math.Max(icon, textH) + pad + btnH + pad;

            // 文字按宽算出来的实际换行高度可能比 MeasureText 高一两行，留一手
            ClientSize = new Size(width, height);

            int textX = pad + icon + gap;
            _icon = new Rectangle(pad, pad, icon, icon);
            _text = new Rectangle(textX, pad, ClientSize.Width - textX - pad, textH);

            int btnW = Math.Max(Px(86), TextRenderer.MeasureText("确定", _ok.Font).Width + Px(30));
            _ok.Size = new Size(btnW, btnH);
            _ok.Location = new Point(ClientSize.Width - pad - btnW, ClientSize.Height - pad - btnH);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var b = new SolidBrush(_p.Bg)) g.FillRectangle(b, ClientRectangle);

            // 图标：实心圆 + 白色字形
            using (var b = new SolidBrush(_kind == Kind.Error ? _p.Err : _p.Accent)) g.FillEllipse(b, _icon);
            using (var f = new Font("Microsoft YaHei UI", 12.5f * _s, FontStyle.Bold, GraphicsUnit.Point))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (var wb = new SolidBrush(Color.White))
                g.DrawString(_kind == Kind.Error ? "!" : "i", f, wb, _icon, sf);

            // 正文（GDI 文字，小字号下比 GDI+ 清楚）
            TextRenderer.DrawText(g, _msg, Font, _text, _p.Text,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);
        }

        private int Px(int dip) => (int)Math.Round(dip * _s);
    }

    /// <summary>对话框上的按钮：圆角、跟主题，hover / 按下有自己的状态</summary>
    private sealed class DlgButton : Button
    {
        private readonly Palette _p;
        private bool _hot;
        private bool _down;

        public DlgButton(string text, Palette p)
        {
            _p = p;
            Text = text;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            TabStop = false;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hot = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var fill = _p.Accent;
            if (_down) fill = Palette.Blend(_p.Accent, Color.Black, 0.18f);
            else if (_hot) fill = Palette.Blend(_p.Accent, Color.White, 0.10f);

            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Rounded(r, Math.Max(4, (int)(Height * 0.34))))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                using (var pen = new Pen(Palette.Blend(fill, _p.Bg, 0.55f), 1f)) g.DrawPath(pen, path);
            }

            TextRenderer.DrawText(g, Text, Font, r, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            if (d <= 0 || d > r.Height || d > r.Width) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
