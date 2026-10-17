using System.Drawing;
using System.Runtime.InteropServices;

namespace CNM.UI;

/// <summary>截图/自检用的小工具（把窗口内容画进 Bitmap）</summary>
internal static class Native
{
    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    /// <summary>
    /// 确保主窗口真的显示出来。从脚本 / 计划任务 / 被隐藏启动的父进程拉起时，
    /// 子进程会继承 STARTUPINFO 的 SW_HIDE —— 界面"启动了但看不见"，这里补一刀。
    /// </summary>
    public static void EnsureVisible(Form f)
    {
        try
        {
            if (f.WindowState == FormWindowState.Minimized) f.WindowState = FormWindowState.Normal;
            ShowWindow(f.Handle, 5);   // SW_SHOW
            if (!f.Visible) f.Show();
        }
        catch { }
    }

    /// <summary>PW_RENDERFULLCONTENT，能抓到 WebView 这类自绘内容</summary>
    public static bool PrintWindow(IntPtr hwnd, Bitmap target)
    {
        try
        {
            using var g = Graphics.FromImage(target);
            var hdc = g.GetHdc();
            try { return PrintWindow(hwnd, hdc, 2); }
            finally { g.ReleaseHdc(hdc); }
        }
        catch { return false; }
    }

    public static bool DrawToBitmap(Control c, Bitmap target)
    {
        try
        {
            c.DrawToBitmap(target, new Rectangle(0, 0, target.Width, target.Height));
            return true;
        }
        catch { return false; }
    }

    /// <summary>整张图只有一种颜色 = 没画出来</summary>
    public static bool IsBlank(Bitmap b)
    {
        try
        {
            var first = b.GetPixel(0, 0);
            for (int y = 0; y < b.Height; y += 2)
                for (int x = 0; x < b.Width; x += 2)
                    if (b.GetPixel(x, y).ToArgb() != first.ToArgb()) return false;
            return true;
        }
        catch { return true; }
    }

    /// <summary>抓控件：先 PrintWindow，画不出来再退回 DrawToBitmap</summary>
    public static bool Capture(Control c, string path)
    {
        var size = c.Size;
        if (size.Width <= 0 || size.Height <= 0) return false;

        using (var bmp = new Bitmap(size.Width, size.Height))
        {
            PrintWindow(c.Handle, bmp);
            if (IsBlank(bmp)) DrawToBitmap(c, bmp);
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return true;
        }
    }
}
