using System.Runtime.InteropServices;

namespace CNM.Core;

/// <summary>
/// 全局快捷键：把 "Ctrl+Alt+C" 这种字符串解析成修饰键 + 虚拟键，并负责注册/注销。
///
/// 用 RegisterHotKey（系统级）而不是键盘钩子 —— 钩子要全局监听所有按键，杀软会疑心、
/// 也容易和别的软件打架；RegisterHotKey 只登记你点名的那几个组合，别人按别的键我们收不到。
/// </summary>
public static class Hotkeys
{
    public const int WM_HOTKEY = 0x0312;

    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;   // 按住不重复触发（连发会把模型切飞）

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>主键名 → 虚拟键码（不区分大小写）</summary>
    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BACKSPACE"] = 0x08, ["TAB"] = 0x09, ["ENTER"] = 0x0D, ["RETURN"] = 0x0D,
        ["ESC"] = 0x1B, ["ESCAPE"] = 0x1B, ["SPACE"] = 0x20,
        ["PAGEUP"] = 0x21, ["PAGEDOWN"] = 0x22, ["END"] = 0x23, ["HOME"] = 0x24,
        ["LEFT"] = 0x25, ["UP"] = 0x26, ["RIGHT"] = 0x27, ["DOWN"] = 0x28,
        ["INSERT"] = 0x2D, ["DELETE"] = 0x2E, ["DEL"] = 0x2E,
        ["`"] = 0xC0, ["-"] = 0xBD, ["="] = 0xBB, ["["] = 0xDB, ["]"] = 0xDD,
        ["\\"] = 0xDC, [";"] = 0xBA, ["'"] = 0xDE, [","] = 0xBC, ["."] = 0xBE, ["/"] = 0xBF,
    };

    /// <summary>解析 "Ctrl+Alt+C"。空串返回 false（表示这个快捷键没启用）。</summary>
    public static bool TryParse(string text, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        var t = (text ?? "").Trim();
        if (t.Length == 0) return false;

        var parts = t.Split('+', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        // 最后一段是主键，前面都是修饰键
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var m = parts[i].Trim().ToLowerInvariant();
            switch (m)
            {
                case "ctrl": case "control": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win": case "super": case "cmd": mods |= MOD_WIN; break;
                default: return false;                     // 不认识的修饰键
            }
        }

        var key = parts[^1].Trim();
        if (key.Length == 0) return false;

        if (NamedKeys.TryGetValue(key, out var named)) vk = named;
        else if (key.Length == 1)
        {
            char c = char.ToUpperInvariant(key[0]);
            if (c >= 'A' && c <= 'Z') vk = c;              // A-Z → 0x41-0x5A
            else if (c >= '0' && c <= '9') vk = c;         // 0-9 → 0x30-0x39
            else return false;
        }
        else if (key.Length >= 2 && (key[0] == 'F' || key[0] == 'f') && int.TryParse(key[1..], out var fn) && fn >= 1 && fn <= 24)
            vk = (uint)(0x70 + fn - 1);
        else return false;

        // 光按一个字母/数字当全局热键太容易误触（打字全被吞），必须有至少一个修饰键
        if (mods == 0 && !(vk >= 0x70 && vk <= 0x87)) return false;
        return vk != 0;
    }

    /// <summary>规范化显示（"ctrl+alt+c" → "Ctrl+Alt+C"）</summary>
    public static string Normalize(string text)
    {
        if (!TryParse(text, out var mods, out var vk)) return "";
        var sb = new System.Text.StringBuilder();
        if ((mods & MOD_CONTROL) != 0) sb.Append("Ctrl+");
        if ((mods & MOD_ALT) != 0) sb.Append("Alt+");
        if ((mods & MOD_SHIFT) != 0) sb.Append("Shift+");
        if ((mods & MOD_WIN) != 0) sb.Append("Win+");
        sb.Append(KeyName(vk));
        return sb.ToString();
    }

    /// <summary>虚拟键 → 显示名。注意单字符符号（` - = 等）要先查表，
    /// 否则会掉进下面的 hex 兜底，显示成 "Ctrl+Alt+0xC0" 这种鬼东西。</summary>
    private static string KeyName(uint vk)
    {
        if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
        if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
        if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x70 + 1);
        // 符号键：查表拿名字（` - = [ ] \ ; ' , . /）
        foreach (var kv in NamedKeys)
            if (kv.Value == vk && kv.Key.Length == 1) return kv.Key;
        // 长名字（Backspace / Home / Delete…）
        foreach (var kv in NamedKeys)
            if (kv.Value == vk) return kv.Key[0] + kv.Key[1..].ToLowerInvariant();
        return "0x" + vk.ToString("X2");
    }

    /// <summary>
    /// 注册。返回 false 表示这个键没生效，reason 说明原因（界面用红灯 + 悬停显示）。
    /// 返回 true 时 reason 为空。
    /// </summary>
    public static bool Register(IntPtr hwnd, int id, string text, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(text)) return false;          // 空 = 没设，不算错（界面显示灰灯）
        if (!TryParse(text, out var mods, out var vk))
        {
            reason = "写法不对（要像 Ctrl+Alt+C 这样，且至少带一个 Ctrl/Alt/Shift/Win）";
            return false;
        }
        UnregisterHotKey(hwnd, id);                                 // 先注销旧的，避免重复注册失败
        if (!RegisterHotKey(hwnd, id, mods | MOD_NOREPEAT, vk))
        {
            var err = Marshal.GetLastWin32Error();
            // 1409 = ERROR_HOTKEY_ALREADY_REGISTERED；5 = ERROR_ACCESS_DENIED
            // （有些程序用独占方式占键，报的是 5 而不是 1409，都算"被占用"）
            reason = (err == 1409 || err == 5)
                ? "已经被别的程序占用了"
                : "系统拒绝了（错误码 " + err + "）";
            return false;
        }
        return true;
    }

    public static void Unregister(IntPtr hwnd, int id)
    {
        try { UnregisterHotKey(hwnd, id); } catch { }
    }
}
