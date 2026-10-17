using Microsoft.Win32;

namespace CNM.Core;

/// <summary>
/// 认显卡：直接读注册表里的显示适配器（比 WMI 快，也不引第三方库），
/// 顺便给出「该装哪个 llama.cpp 后端」的建议 —— 省得用户在一堆 CUDA / ROCm / Vulkan 里猜。
/// </summary>
public static class GpuInfo
{
    private const string DisplayClass =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public sealed class Adapter
    {
        public string Name = "";
        public string Vendor = "";
        public long VramBytes;
    }

    /// <summary>本机的显示适配器列表（认不到就返回空表，不抛异常）</summary>
    public static List<Adapter> Detect()
    {
        var list = new List<Adapter>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(DisplayClass);
            if (root == null) return list;

            foreach (var sub in root.GetSubKeyNames())
            {
                if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;   // 只认 0000 / 0001 … 这几个槽位
                try
                {
                    using var k = root.OpenSubKey(sub);
                    var name = k?.GetValue("DriverDesc") as string;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var a = new Adapter
                    {
                        Name = name.Trim(),
                        Vendor = (k.GetValue("ProviderName") as string ?? "").Trim()
                    };

                    var mem = k.GetValue("HardwareInformation.qwMemorySize");
                    if (mem is long l && l > 0) a.VramBytes = l;
                    else if (k.GetValue("HardwareInformation.MemorySize") is int i && i > 0) a.VramBytes = i;
                    if (a.VramBytes > (1L << 40)) a.VramBytes = 0;    // 有的驱动往这儿塞垃圾值

                    if (!list.Any(x => x.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase)))
                        list.Add(a);
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    /// <summary>建议的 llama.cpp 后端 + 一句人话理由</summary>
    public static (string variant, string reason) Recommend(List<Adapter> adapters)
    {
        adapters ??= new List<Adapter>();
        string all = string.Join(" | ", adapters.Select(a => (a.Name + " " + a.Vendor).ToLowerInvariant()));

        bool basic = adapters.Count == 0 || adapters.All(a =>
            a.Name.Contains("microsoft basic", StringComparison.OrdinalIgnoreCase) ||
            a.Name.Contains("remote display", StringComparison.OrdinalIgnoreCase));

        if (all.Contains("nvidia"))
            return ("cuda-13.4", "NVIDIA 显卡：CUDA 版通常比 Vulkan 快一截（驱动太老就选 CUDA 12.4）。" +
                                 "注意 CUDA 版要连配套运行库一起下，加起来 400 MB 左右。");

        if (all.Contains("advanced micro devices") || all.Contains("amd") || all.Contains("radeon"))
            return ("vulkan", "AMD 显卡：Windows 上用 Vulkan 最省事（包里已经带了，开箱即用）。" +
                              "想再快一点可以试 ROCm 10.0 版（约 250 MB）。");

        if (all.Contains("intel"))
            return ("vulkan", "Intel 显卡（Arc / 核显）：走 Vulkan。");

        if (basic)
            return ("cpu", "没认到显卡驱动：先装 CPU 版，慢但一定能跑起来。");

        return ("vulkan", "认到显卡但型号不确定：Vulkan 兼容面最广。");
    }
}
