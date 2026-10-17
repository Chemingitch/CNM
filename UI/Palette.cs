using System.Drawing;

namespace CNM.UI;

/// <summary>
/// 自绘控件（对话框、按钮）用的配色。
/// 色值和 wwwroot/style.css 里的 CSS 变量一一对应 —— 改主题时两边一起改。
/// 托盘菜单那边是 TrayMenu.Palette，也是同一套色。
/// </summary>
internal sealed class Palette
{
    public static readonly Palette Dark = new(true);
    public static readonly Palette Light = new(false);

    public bool IsDark { get; }

    public Color Bg { get; }
    public Color Bg2 { get; }
    public Color Panel { get; }
    public Color Panel2 { get; }
    public Color Line { get; }
    public Color Text { get; }
    public Color Muted { get; }
    public Color Accent { get; }
    public Color Ok { get; }
    public Color Err { get; }

    private Palette(bool dark)
    {
        IsDark = dark;
        Bg = Mix(dark, "0e1014", "eef1f6");
        Bg2 = Mix(dark, "12151b", "f6f8fc");
        Panel = Mix(dark, "161a22", "ffffff");
        Panel2 = Mix(dark, "1b202a", "f4f7fb");
        Line = Mix(dark, "242b38", "e2e7f0");
        Text = Mix(dark, "e7ebf3", "1a1f2b");
        Muted = Mix(dark, "8b93a6", "6b7488");
        Accent = Color.FromArgb(0x5b, 0x8c, 0xff);   // 亮暗两套主题共用同一个蓝
        Ok = Color.FromArgb(0x35, 0xc9, 0x8a);
        Err = Color.FromArgb(0xff, 0x6b, 0x6b);
    }

    public static Palette For(bool dark) => dark ? Dark : Light;

    public static Palette ForTheme(string theme)
        => For(!string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase));

    /// <summary>把颜色朝目标色混一点，用来做 hover / pressed 态</summary>
    public static Color Blend(Color a, Color b, float t)
    {
        if (t < 0f) t = 0f;
        if (t > 1f) t = 1f;
        return Color.FromArgb(
            (int)(a.A + (b.A - a.A) * t),
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }

    private static Color Mix(bool dark, string darkHex, string lightHex)
    {
        var s = dark ? darkHex : lightHex;
        return Color.FromArgb(
            Convert.ToInt32(s.Substring(0, 2), 16),
            Convert.ToInt32(s.Substring(2, 2), 16),
            Convert.ToInt32(s.Substring(4, 2), 16));
    }
}
