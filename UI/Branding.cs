using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using CNM.Core;

namespace CNM.UI;

/// <summary>
/// 应用图标（唯一出处）：侧栏 logo、窗口图标、托盘图标、exe 图标、用户自定义图标。
/// 所有图形都在 64×64 网格里描述，缩放靠 k = size / 64。
/// </summary>
internal static class Branding
{
    private static readonly Color C1 = Color.FromArgb(0x5B, 0x8C, 0xFF);
    private static readonly Color C2 = Color.FromArgb(0x8A, 0x5C, 0xFF);

    /// <summary>默认图形：三角网状（三点互联 = 一个模型转发给多个客户端）</summary>
    private const string DefaultMarkKey = "A";

    public static string BrandDir => Path.Combine(Settings.DataDir, "brand");
    public static string UserIcoPath => Path.Combine(BrandDir, "icon.ico");
    public static string UserPngPath => Path.Combine(BrandDir, "icon.png");

    public static bool HasUserIcon
    {
        get { try { return File.Exists(UserIcoPath); } catch { return false; } }
    }

    /* ---------------- 取图标 ---------------- */

    /// <summary>取图标：把位图转成 Icon 时把句柄一起收拾干净（GetHicon 出来的 HICON 不归 Icon 管，得自己 DestroyIcon，否则每次调用漏一个）</summary>
    public static Icon AppIcon(int size)
    {
        using var bmp = Render(size);
        var h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }   // Clone 之后 Icon 自己存了一份数据
        finally { DestroyIcon(h); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>取当前生效的应用图标（自定义优先），want = 想要的像素尺寸</summary>
    public static Icon LoadAppIcon(int want)
    {
        try
        {
            if (HasUserIcon)
            {
                var bytes = File.ReadAllBytes(UserIcoPath);
                return new Icon(new MemoryStream(bytes), new Size(want, want));
            }
        }
        catch { }
        return AppIcon(want);
    }

    /* ---------------- 画 ---------------- */

    public static Bitmap Render(int size, string markKey = null)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        var rect = new RectangleF(0.5f, 0.5f, size - 1f, size - 1f);
        using (var path = RoundedRect(rect, size * 0.235f))
        using (var brush = new LinearGradientBrush(rect, C1, C2, 45f))
            g.FillPath(brush, path);

        DrawMark(g, markKey ?? DefaultMarkKey, size / 64f, Color.White);
        return bmp;
    }

    /// <summary>在 64×64 网格里画图形；k = 目标尺寸 / 64</summary>
    private static void DrawMark(Graphics g, string key, float k, Color color)
    {
        using var pen = new Pen(color, 4.6f * k)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var fill = new SolidBrush(color);

        switch (key)
        {
            case "A": // 三角网状
                pen.Width = 4.4f * k;
                g.DrawLine(pen, A(k, 32, 14), A(k, 13, 46));
                g.DrawLine(pen, A(k, 32, 14), A(k, 51, 46));
                g.DrawLine(pen, A(k, 13, 46), A(k, 51, 46));
                Dot(g, fill, k, 32, 14, 6.4f);
                Dot(g, fill, k, 13, 46, 6.4f);
                Dot(g, fill, k, 51, 46, 6.4f);
                break;

            case "B": // 两端对接（透镜）
                pen.Width = 4.4f * k;
                g.DrawBezier(pen, A(k, 15, 32), A(k, 15, 15), A(k, 49, 15), A(k, 49, 32));
                g.DrawBezier(pen, A(k, 49, 32), A(k, 49, 49), A(k, 15, 49), A(k, 15, 32));
                Dot(g, fill, k, 15, 32, 6f);
                Dot(g, fill, k, 49, 32, 6f);
                break;

            case "C": // 一源扇出
                pen.Width = 4f * k;
                g.DrawLine(pen, A(k, 17, 32), A(k, 45, 14));
                g.DrawLine(pen, A(k, 17, 32), A(k, 45, 32));
                g.DrawLine(pen, A(k, 17, 32), A(k, 45, 50));
                Dot(g, fill, k, 15, 32, 6.6f);
                Dot(g, fill, k, 47, 14, 4.6f);
                Dot(g, fill, k, 47, 32, 4.6f);
                Dot(g, fill, k, 47, 50, 4.6f);
                break;

            case "D": // 上行折线
                pen.Width = 4.6f * k;
                var pts = new[] { A(k, 11, 48), A(k, 25, 39), A(k, 38, 25), A(k, 53, 15) };
                g.DrawLines(pen, pts);
                Dot(g, fill, k, 11, 48, 5.4f);
                Dot(g, fill, k, 38, 25, 5f);
                Dot(g, fill, k, 53, 15, 5.8f);
                break;

            case "E": // 轨道环绕
                pen.Width = 4.4f * k;
                g.DrawEllipse(pen, (32 - 17) * k, (32 - 17) * k, 34 * k, 34 * k);
                Dot(g, fill, k, 32, 32, 4.4f);
                Dot(g, fill, k, 44, 20, 6.2f);
                break;

            case "F": // 一笔 C + 两端节点
                pen.Width = 5.4f * k;
                g.DrawArc(pen, (32 - 19) * k, (32 - 19) * k, 38 * k, 38 * k, 40f, 280f);
                Dot(g, fill, k, 46.6f, 19.8f, 5.6f);
                Dot(g, fill, k, 46.6f, 44.2f, 5.6f);
                break;

            case "G": // 信号广播
                pen.Width = 4.6f * k;
                Dot(g, fill, k, 18, 32, 6.4f);
                g.DrawArc(pen, (18 - 12) * k, (32 - 12) * k, 24 * k, 24 * k, -45f, 90f);
                g.DrawArc(pen, (18 - 20) * k, (32 - 20) * k, 40 * k, 40 * k, -45f, 90f);
                break;

            case "H": // 两点一线
                pen.Width = 4.6f * k;
                g.DrawLine(pen, A(k, 15, 32), A(k, 49, 32));
                Dot(g, fill, k, 14, 32, 6.4f);
                Dot(g, fill, k, 32, 32, 3.6f);
                Dot(g, fill, k, 50, 32, 6.4f);
                break;

            case "I": // 双环相扣
                pen.Width = 4.4f * k;
                g.DrawEllipse(pen, (25 - 13) * k, (32 - 13) * k, 26 * k, 26 * k);
                g.DrawEllipse(pen, (39 - 13) * k, (32 - 13) * k, 26 * k, 26 * k);
                break;
        }
    }

    private static PointF A(float k, float x, float y) => new(x * k, y * k);

    private static void Dot(Graphics g, Brush b, float k, float x, float y, float r)
        => g.FillEllipse(b, (x - r) * k, (y - r) * k, 2 * r * k, 2 * r * k);

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /* ---------------- 生成文件 ---------------- */

    /// <summary>多尺寸 .ico（PNG 压缩，Vista+ 支持）</summary>
    public static void SaveIcon(string path)
    {
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        var frames = new List<Bitmap>();
        foreach (var s in sizes) frames.Add(Render(s));
        PackIco(frames, path);
        foreach (var f in frames) f.Dispose();
    }

    /// <summary>用用户图片生成 icon.ico（多尺寸）+ icon.png（界面用）</summary>
    public static void SaveUserIcon(Bitmap src)
    {
        Directory.CreateDirectory(BrandDir);
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };

        var square = ToSquare(src);
        var frames = new List<Bitmap>();
        try
        {
            foreach (var s in sizes) frames.Add(Scale(square, s));
            PackIco(frames, UserIcoPath);
            using (var big = Scale(square, 256)) big.Save(UserPngPath, ImageFormat.Png);
        }
        finally
        {
            foreach (var f in frames) f.Dispose();
            if (!ReferenceEquals(square, src)) square.Dispose();
        }
    }

    public static void ClearUserIcon()
    {
        try { if (File.Exists(UserIcoPath)) File.Delete(UserIcoPath); } catch { }
        try { if (File.Exists(UserPngPath)) File.Delete(UserPngPath); } catch { }
    }

    /// <summary>从字节流解码图片（png/jpg/bmp/gif/ico 都行）</summary>
    public static Bitmap Decode(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, false);
        // 先当普通位图读；ICO 走 Icon 通道
        try { return new Bitmap(ms); }
        catch
        {
            ms.Position = 0;
            using var ico = new Icon(ms, new Size(256, 256));
            return ico.ToBitmap();
        }
    }

    private static Bitmap ToSquare(Bitmap src)
    {
        int side = Math.Min(src.Width, src.Height);
        var bmp = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        g.DrawImage(src, new Rectangle(0, 0, side, side),
            new Rectangle((src.Width - side) / 2, (src.Height - side) / 2, side, side), GraphicsUnit.Pixel);
        return bmp;
    }

    private static Bitmap Scale(Bitmap src, int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        g.DrawImage(src, new Rectangle(0, 0, size, size));
        return bmp;
    }

    private static void PackIco(List<Bitmap> frames, string path)
    {
        var blobs = new List<byte[]>();
        foreach (var b in frames)
            blobs.Add(b.Width >= 256 ? PngBlob(b) : DibBlob(b));

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write((ushort)0);
        bw.Write((ushort)1);                 // 1 = icon
        bw.Write((ushort)frames.Count);
        int offset = 6 + 16 * frames.Count;
        for (int i = 0; i < frames.Count; i++)
        {
            int s = frames[i].Width;
            bw.Write((byte)(s >= 256 ? 0 : s));
            bw.Write((byte)(s >= 256 ? 0 : s));
            bw.Write((byte)0);
            bw.Write((byte)0);
            bw.Write((ushort)1);
            bw.Write((ushort)32);
            bw.Write((uint)blobs[i].Length);
            bw.Write((uint)offset);
            offset += blobs[i].Length;
        }
        foreach (var p in blobs) bw.Write(p);
    }

    private static byte[] PngBlob(Bitmap b)
    {
        using var ms = new MemoryStream();
        b.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>
    /// 32 位 DIB 帧 = BITMAPINFOHEADER + XOR 位图 + AND 掩码。
    /// 小尺寸必须用 DIB：PNG 压缩帧只有 256 这档是各版本通吃的，
    /// 用 PNG 塞 16/32/48 会让 Icon.ToBitmap() / 托盘在某些机器上直接报错。
    /// </summary>
    private static byte[] DibBlob(Bitmap src)
    {
        int w = src.Width, h = src.Height;
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.DrawImage(src, new Rectangle(0, 0, w, h));
        }

        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var pixels = new byte[data.Stride * h];
        Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        bmp.UnlockBits(data);

        int maskStride = ((w + 31) / 32) * 4;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(40);               // biSize
        bw.Write(w);                // biWidth
        bw.Write(h * 2);            // biHeight：XOR 图 + AND 掩码
        bw.Write((short)1);         // biPlanes
        bw.Write((short)32);        // biBitCount
        bw.Write(0);                // BI_RGB
        bw.Write(w * h * 4);        // biSizeImage
        bw.Write(0);                // 横向分辨率
        bw.Write(0);                // 纵向分辨率
        bw.Write(0);                // 调色板
        bw.Write(0);
        for (int y = h - 1; y >= 0; y--) bw.Write(pixels, y * data.Stride, w * 4);  // DIB 自下而上（BGRA）
        var zero = new byte[maskStride];
        for (int y = 0; y < h; y++) bw.Write(zero);                                 // 透明靠 alpha，掩码留空
        bw.Flush();
        return ms.ToArray();
    }

    /* ---------------- 候选图对比表（设计用，随 exe 一起） ---------------- */

    public static void CandidateSheet(string path)
    {
        string[] keys = { "A", "B", "C", "D", "E", "F", "G", "H", "I" };
        string[] names =
        {
            "A 三角网状", "B 两端对接", "C 一源扇出",
            "D 上行折线", "E 轨道环绕", "F 一笔C+节点",
            "G 信号广播", "H 两点一线", "I 双环相扣"
        };

        const int tile = 128, cellW = 148, labelH = 26, pad = 18, cols = 3;
        int rows = (int)Math.Ceiling(keys.Length / (double)cols);
        int w = pad + cols * (cellW + pad);
        int h = pad + rows * (tile + labelH + pad);

        using var sheet = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(sheet);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.FromArgb(0x0E, 0x10, 0x14));

        using var font = new Font("Microsoft YaHei UI", 10f);
        using var label = new SolidBrush(Color.FromArgb(0xC3, 0xCB, 0xDB));

        for (int i = 0; i < keys.Length; i++)
        {
            int col = i % cols, row = i / cols;
            int x = pad + col * (cellW + pad);
            int y = pad + row * (tile + labelH + pad);

            using (var bmp = Render(tile, keys[i]))
                g.DrawImage(bmp, x, y);

            // 侧栏实际尺寸预览（34px 方块 + 20px 图形）
            using (var small = Render(34, keys[i]))
                g.DrawImage(small, x + tile + 8, y + 14);
            using (var tiny = Render(16, keys[i]))
                g.DrawImage(tiny, x + tile + 8, y + 60);

            g.DrawString(names[i], font, label, x, y + tile + 4);
        }

        sheet.Save(path, ImageFormat.Png);
    }
}
