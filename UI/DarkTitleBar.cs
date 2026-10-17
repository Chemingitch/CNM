using System.Runtime.InteropServices;

namespace CNM.UI;

/// <summary>
/// 让系统标题栏跟随应用主题（Windows 10 20H1+ / Windows 11）。
/// Win11 上还能把标题栏底色 / 文字色 / 边框色都设成和界面一致，做到无缝。
/// </summary>
internal static class DarkTitleBar
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;   // Win10 18985+ / Win11
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19; // 更老的 Win10 预览版
    private const int DWMWA_BORDER_COLOR = 34;              // Win11
    private const int DWMWA_CAPTION_COLOR = 35;             // Win11
    private const int DWMWA_TEXT_COLOR = 36;                // Win11

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Apply(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int flag = dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref flag, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref flag, sizeof(int));

            // Win11：把标题栏染成和界面同一底色，文字/边框也一起对上（旧系统调用会失败，忽略即可）
            int caption = dark ? ColorRef(0x0e, 0x10, 0x14) : ColorRef(0xee, 0xf1, 0xf6);
            int text = dark ? ColorRef(0xe7, 0xeb, 0xf3) : ColorRef(0x1a, 0x1f, 0x2b);
            int border = dark ? ColorRef(0x24, 0x2b, 0x38) : ColorRef(0xe2, 0xe7, 0xf0);

            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }
        catch { }
    }

    /// <summary>DWM 用的是 COLORREF：0x00BBGGRR</summary>
    private static int ColorRef(byte r, byte g, byte b) => r | (g << 8) | (b << 16);
}
