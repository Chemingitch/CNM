# CNM 安装包物料：干净克制版（亮/暗各一套，跟随系统）
# 只画一个 logo + 字标，没有星星、光环、辉光、地址胶囊这些装饰。
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

public static class CnmArt
{
    // 亮色主题
    public static readonly Color L1 = Color.FromArgb(0x3B, 0x6B, 0xF0);   // 蓝
    public static readonly Color L2 = Color.FromArgb(0x7C, 0x4D, 0xF0);   // 紫
    public static readonly Color LBg1 = Color.FromArgb(0xFF, 0xFF, 0xFF);
    public static readonly Color LBg2 = Color.FromArgb(0xEE, 0xF2, 0xF9);
    public static readonly Color LTitle = Color.FromArgb(0x14, 0x1B, 0x2D);
    public static readonly Color LSub = Color.FromArgb(0x7B, 0x86, 0x98);
    public static readonly Color LTag = Color.FromArgb(0x9A, 0xA5, 0xB8);

    // 暗色主题（中性深灰，不偏蓝紫，和系统暗色控件更搭）
    public static readonly Color D1 = Color.FromArgb(0x7B, 0xA0, 0xFF);
    public static readonly Color D2 = Color.FromArgb(0xA9, 0x8C, 0xFF);
    public static readonly Color DBg1 = Color.FromArgb(0x18, 0x1A, 0x1F);
    public static readonly Color DBg2 = Color.FromArgb(0x0E, 0x10, 0x13);
    public static readonly Color DTitle = Color.FromArgb(0xF2, 0xF4, 0xF8);
    public static readonly Color DSub = Color.FromArgb(0x8A, 0x93, 0xA6);
    public static readonly Color DTag = Color.FromArgb(0x76, 0x80, 0x94);

    static void Text(Graphics g, string s, string family, float px, FontStyle style, Color c,
                     float cx, float cy, float w)
    {
        using (Font f = new Font(family, px, style, GraphicsUnit.Pixel))
        using (SolidBrush b = new SolidBrush(c))
        using (StringFormat sf = new StringFormat())
        {
            sf.Alignment = StringAlignment.Center;
            sf.LineAlignment = StringAlignment.Center;
            sf.FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
            g.DrawString(s, f, b, new RectangleF(cx - w / 2f, cy - px, w, px * 2f), sf);
        }
    }

    static void Init(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    }

    // 三角网状（和 Branding.cs 的 "A" 同一套 64x64 坐标），纯渐变描边、无发光
    public static void Mark(Graphics g, float k, float ox, float oy, Color c1, Color c2)
    {
        RectangleF box = new RectangleF(ox, oy, 64f * k, 64f * k);
        using (LinearGradientBrush br = new LinearGradientBrush(box, c1, c2, 45f))
        {
            using (Pen pen = new Pen(br, 4.5f * k))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                g.DrawLine(pen, ox + 32f * k, oy + 14f * k, ox + 13f * k, oy + 46f * k);
                g.DrawLine(pen, ox + 32f * k, oy + 14f * k, ox + 51f * k, oy + 46f * k);
                g.DrawLine(pen, ox + 13f * k, oy + 46f * k, ox + 51f * k, oy + 46f * k);
            }
            // 节点：同色渐变实心
            float[] xs = new float[] { 32f, 13f, 51f };
            float[] ys = new float[] { 14f, 46f, 46f };
            for (int i = 0; i < 3; i++)
            {
                float cx = ox + xs[i] * k, cy = oy + ys[i] * k;
                g.FillEllipse(br, cx - 6.3f * k, cy - 6.3f * k, 12.6f * k, 12.6f * k);
            }
        }
    }

    // 向导左大图：极简（logo + 字标 + 一行小字）
    public static void Big(int w, int h, bool dark, string path)
    {
        float k = h / 314f;
        float W = 164f * k, H = 314f * k;
        using (Bitmap bmp = new Bitmap((int)Math.Round(W), (int)Math.Round(H)))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            Init(g);
            RectangleF all = new RectangleF(0f, 0f, bmp.Width, bmp.Height);
            using (LinearGradientBrush bg = new LinearGradientBrush(all,
                       dark ? DBg1 : LBg1, dark ? DBg2 : LBg2, 90f))
                g.FillRectangle(bg, all);

            float mk = 1.36f;
            float ox = 82f * k - 32f * mk * k;
            float oy = 104f * k - 32f * mk * k;
            Mark(g, mk * k, ox, oy, dark ? D1 : L1, dark ? D2 : L2);

            Text(g, "CNM", "Segoe UI", 28f * k, FontStyle.Bold,
                 dark ? DTitle : LTitle, 82f * k, 182f * k, 160f * k);
            Text(g, "Chemingitch China Model", "Segoe UI", 9f * k, FontStyle.Regular,
                 dark ? DSub : LSub, 82f * k, 204f * k, 160f * k);
            Text(g, "本地模型 · 云端中转", "Microsoft YaHei UI", 9.5f * k, FontStyle.Regular,
                 dark ? DTag : LTag, 82f * k, 226f * k, 164f * k);

            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    // 向导右上小图：只有那个 logo（PNG 透明底，跟页头底色自然融合）
    public static void Small(int s, bool dark, string path)
    {
        using (Bitmap bmp = new Bitmap(s, s))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            Init(g);
            float k = s / 64f * 0.86f;
            float ox = (s - 64f * k) / 2f, oy = (s - 64f * k) / 2f;
            Mark(g, k, ox, oy, dark ? D1 : L1, dark ? D2 : L2);
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
    }
}
'@

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$assets = Join-Path $dir 'assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null

# 旧的深空风资源清掉，免得混淆
Get-ChildItem $assets -Filter 'back-*.png' -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem $assets -Filter 'wiz-big-*.png' -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem $assets -Filter 'wiz-small-*.png' -ErrorAction SilentlyContinue | Remove-Item -Force

foreach ($dark in @($false, $true)) {
  $t = if ($dark) { 'dark' } else { 'light' }
  [CnmArt]::Big(202, 386, $dark, (Join-Path $assets "big-$t-202x386.png"))
  [CnmArt]::Big(336, 643, $dark, (Join-Path $assets "big-$t-336x643.png"))
  [CnmArt]::Big(430, 824, $dark, (Join-Path $assets "big-$t-430x824.png"))
  [CnmArt]::Small(58, $dark, (Join-Path $assets "small-$t-58x58.png"))
  [CnmArt]::Small(97, $dark, (Join-Path $assets "small-$t-97x97.png"))
  [CnmArt]::Small(124, $dark, (Join-Path $assets "small-$t-124x124.png"))
}

Get-ChildItem $assets -Filter '*.png' | Sort-Object Name | ForEach-Object { "  " + $_.Name.PadRight(26) + $_.Length + " B" }
