using System.Runtime.InteropServices;

namespace CNM.Core;

/// <summary>
/// 读某个进程实际占用的显存。走 Windows 自带的性能计数器（GPU Process Memory），
/// 和任务管理器是同一个数据源 —— 所以界面上的数和你在任务管理器看到的对得上。
/// 用的是“英文计数器名”API，中文系统上也能查到。查不到就返回 -1，界面不显示即可。
/// </summary>
internal static class Vram
{
    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_MORE_DATA = 0x800007D2;

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public IntPtr Name;
        public PDH_FMT_COUNTERVALUE Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
    private static extern uint PdhOpenQuery(string dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    private static readonly object Gate = new();
    private static IntPtr _query, _counter;
    private static bool _dead;

    private static bool Ensure()
    {
        if (_counter != IntPtr.Zero) return true;
        if (_dead) return false;
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) { _dead = true; return false; }
        // 通配实例：一次拿到所有进程/显卡的实例名，再按 pid 挑
        if (PdhAddEnglishCounter(_query, @"\GPU Process Memory(*)\Dedicated Usage", IntPtr.Zero, out _counter) != 0)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
            _dead = true;
            return false;
        }
        return true;
    }

    /// <summary>进程的专用显存占用（MiB）。拿不到返回 -1。</summary>
    public static double ProcessMiB(int pid)
    {
        if (pid <= 0) return -1;
        lock (Gate)
        {
            IntPtr buf = IntPtr.Zero;
            try
            {
                if (!Ensure()) return -1;
                if (PdhCollectQueryData(_query) != 0) return -1;

                uint size = 0, count = 0;
                if (PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref size, ref count, IntPtr.Zero) != PDH_MORE_DATA || size == 0)
                    return -1;

                buf = Marshal.AllocHGlobal((int)size);
                if (PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref size, ref count, buf) != 0) return -1;

                var want = "pid_" + pid + "_";
                int stride = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
                double sum = 0;
                bool hit = false;
                for (int i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(IntPtr.Add(buf, i * stride));
                    var name = Marshal.PtrToStringUni(item.Name) ?? "";
                    if (name.StartsWith(want, StringComparison.OrdinalIgnoreCase) && item.Value.CStatus == 0)
                    {
                        sum += item.Value.DoubleValue;   // 字节
                        hit = true;
                    }
                }
                return hit ? sum / 1048576.0 : 0;
            }
            catch { return -1; }
            finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
        }
    }
}
