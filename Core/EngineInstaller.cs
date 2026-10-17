using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace CNM.Core;

/// <summary>
/// 下载 / 安装 / 升级推理引擎（llama.cpp 的 Windows 构建）。
/// 界面上的「下载并安装引擎」和命令行的 <c>--install-engine</c> 共用这一份逻辑：
///   · 加速前缀：先走镜像，不行自动回落直连；
///   · 断点续传：进度写 <c>%TEMP%\cnm-engine-&lt;后端&gt;.zip.part</c>，用 Range 接着下 ——
///     慢网、断线都不用从零再来（这也是原来最要命的地方：60 秒总超时会把慢速下载掐死）；
///   · 失败重试：每个地址 3 次；
///   · 本地 zip 安装：完全绕开网络。
/// </summary>
public static class EngineInstaller
{
    /// <summary>支持的 llama.cpp Windows 后端</summary>
    public static readonly string[] Variants = { "vulkan", "cpu", "rocm", "cuda-12.4", "cuda-13.4" };

    /// <summary>没下完的临时文件（变化时文件名固定，才能续传）</summary>
    public static string PartPath(string variant) =>
        Path.Combine(Path.GetTempPath(), "cnm-engine-" + (variant.Length > 0 ? variant : "local") + ".zip.part");

    /// <param name="variant">vulkan / cpu / rocm / cuda-12.4 / cuda-13.4</param>
    /// <param name="localZip">给了就用本地包，不走网络</param>
    /// <param name="mirror">GitHub 加速前缀，空 = 直连</param>
    /// <param name="log">进度回调（界面推到日志面板，命令行写 stdout）</param>
    /// <param name="targetRoot">装到哪个目录下的 engine\llama（默认 exe 所在目录）</param>
    public static async Task<(bool ok, string message, string enginePath)> InstallAsync(
        string variant, string localZip, string mirror, Action<string> log, string targetRoot = null)
    {
        try
        {
            string zip;
            string cudartZip = null;
            string tag = "";
            if (!string.IsNullOrEmpty(localZip))
            {
                zip = localZip;
                log("[安装] 用本地包：" + Path.GetFileName(zip) + "（" + (new FileInfo(zip).Length / 1048576.0).ToString("0.0") + " MB）");
            }
            else
            {
                if (!Variants.Contains(variant)) variant = "vulkan";

                log($"[安装] 正在查询 llama.cpp 最新版本（{variant}）…");
                var asset = await FindAssetAsync(variant);
                if (asset.url == null)
                    return (false, $"没找到 {variant} 的 Windows x64 构建（也可能查不到发布列表）—— 可以改用本地 zip 安装", "");

                tag = asset.tag ?? "";
                log($"[安装] 目标：{asset.name}（{asset.size / 1048576.0:0.0} MB）" + (tag.Length > 0 ? "，版本 " + tag : ""));
                zip = await DownloadAsync(asset.url, asset.name, variant, mirror, log);

                // CUDA 版光有引擎跑不起来：还得配套的 cudart 运行库（另一个 400 MB 级的包）
                if (variant.StartsWith("cuda", StringComparison.OrdinalIgnoreCase))
                {
                    log("[安装] CUDA 版还需要配套运行库 cudart，接着下（这个包比较大，慢慢来）…");
                    var cd = await FindAssetAsync(variant, true);
                    if (cd.url == null)
                        log("[安装] 没找到配套的 cudart 包 —— CUDA 引擎可能起不来（先用 Vulkan 版也行）");
                    else
                        cudartZip = await DownloadAsync(cd.url, cd.name, variant + "-cudart", mirror, log);
                }
            }

            var target = Path.Combine(targetRoot ?? AppContext.BaseDirectory, "engine", "llama");

            var e1 = InstallZip(zip, target, log, cleanExtra: true, requireServer: true);
            if (e1 != null) return (false, e1, "");

            if (cudartZip != null)
            {
                var e2 = InstallZip(cudartZip, target, log, cleanExtra: false, requireServer: false);
                if (e2 != null) log("[安装] cudart 没铺好：" + e2 + "（CUDA 可能起不来）");
            }

            // 装好了就把断点文件清掉，别占着 30~250MB
            if (string.IsNullOrEmpty(localZip))
            {
                TryDelete(PartPath(variant));
                TryDelete(PartPath(variant + "-cudart"));
            }

            log("[安装] 完成 → " + target + (tag.Length > 0 ? "（" + variant + " " + tag + "）" : ""));
            return (true, "引擎安装完成", target);
        }
        catch (Exception ex)
        {
            log("[安装失败] " + ex.Message);
            return (false, "安装引擎失败：" + ex.Message + "（网不通就直接用本地 zip 安装）", "");
        }
    }

    /// <summary>
    /// 把一个引擎 zip 铺到 <paramref name="target"/>。返回 null = 成功，否则是人话错误。
    /// <paramref name="cleanExtra"/> 只在装主包时为真：换后端（cuda → vulkan）时旧包里多出来的
    /// ggml-cuda.dll 之类会留在原地，新旧混装引擎直接起不来，所以先把「新包里没有」的旧文件删掉。
    /// </summary>
    private static string InstallZip(string zip, string target, Action<string> log, bool cleanExtra, bool requireServer)
    {
        var staging = Path.Combine(Path.GetTempPath(),
            "cnm-engine-unzip-" + Environment.TickCount.ToString() + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            ZipFile.ExtractToDirectory(zip, staging);

            // 官方包外面套一层目录（llama-b1234-bin-win-…\），展平它
            var srcDir = staging;
            var subs = Directory.GetDirectories(staging);
            if (subs.Length == 1 && Directory.GetFiles(staging).Length == 0) srcDir = subs[0];

            if (requireServer && !Directory.GetFiles(srcDir, "llama-server.exe", SearchOption.AllDirectories).Any())
                return "这个 zip 里没有 llama-server.exe —— 确认下是不是引擎包（别选成 cudart 之类的附加包）";

            Directory.CreateDirectory(target);

            var incoming = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
                incoming.Add(Path.GetRelativePath(srcDir, f));

            if (cleanExtra)
            {
                foreach (var old in Directory.GetFiles(target, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(target, old);
                    if (!incoming.Contains(rel))
                    {
                        try { File.Delete(old); log("[清理] 旧文件 " + rel); } catch { }
                    }
                }
            }

            foreach (var f in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(srcDir, f);
                var dst = Path.Combine(target, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(f, dst, true);
            }
            return null;
        }
        finally { try { Directory.Delete(staging, true); } catch { } }
    }

    /// <summary>命令行入口：CNM.exe --install-engine [后端] / --install-engine-zip &lt;zip&gt; [--engine-dir &lt;目录&gt;]</summary>
    public static async Task<int> RunCliAsync(string variant, string localZip, string mirror, string targetRoot)
    {
        var r = await InstallAsync(variant, localZip, mirror, line => Console.WriteLine(line), targetRoot);
        Console.WriteLine(r.ok ? "OK → " + r.enginePath : "失败：" + r.message);
        return r.ok ? 0 : 1;
    }

    /// <summary>查官方最新发布里对应后端的 Windows x64 包（cudart 那种附加包不算）</summary>
    private static async Task<(string url, string name, long size, string tag)> FindAssetAsync(string variant, bool cudart = false)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CNM/0.1");
        var relTxt = await http.GetStringAsync("https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=8");
        if (JsonNode.Parse(relTxt) is not JsonArray rels) return (null, null, 0, null);

        foreach (var r in rels)
        {
            if (r?["assets"] is not JsonArray assets) continue;
            var tag = r["tag_name"]?.GetValue<string>() ?? "";
            foreach (var a in assets)
            {
                var an = a?["name"]?.GetValue<string>() ?? "";
                if (!MatchAsset(an, variant, cudart)) continue;
                return (a["browser_download_url"]?.GetValue<string>(), an, a["size"]?.GetValue<long>() ?? 0, tag);
            }
        }
        return (null, null, 0, null);
    }

    /// <summary>
    /// 官方资产名的匹配规则。注意几个坑：
    ///   · 只要 x64（还有 arm64 的包，名字很像）；
    ///   · ROCm 包实际叫 <c>llama-b11714-bin-win-rocm-10.0-x64.zip</c> —— 版本号会变，不能用固定串匹配；
    ///   · cudart 是 CUDA 版必需的配套运行库，单独一个包，别当成引擎包。
    /// </summary>
    private static bool MatchAsset(string name, string variant, bool cudart)
    {
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Contains("arm64", StringComparison.OrdinalIgnoreCase)) return false;

        if (cudart)
            return name.StartsWith("cudart", StringComparison.OrdinalIgnoreCase)
                   && name.Contains(variant, StringComparison.OrdinalIgnoreCase);

        if (name.StartsWith("cudart", StringComparison.OrdinalIgnoreCase)) return false;
        if (variant == "cpu") return name.Contains("-win-cpu-x64");
        if (variant == "rocm") return name.Contains("-win-rocm") && name.Contains("-x64");
        return name.Contains("-win-" + variant + "-x64");
    }

    /// <summary>只查最新版信息，不下载（给界面上的「检查更新」用）</summary>
    public static async Task<(string tag, string name, long size, string error)> LatestAsync(string variant)
    {
        try
        {
            if (!Variants.Contains(variant)) variant = "vulkan";
            var a = await FindAssetAsync(variant);
            if (a.url == null) return (null, null, 0, "查不到发布列表（网不通，或 GitHub API 到这个网络都不通）");
            return (a.tag, a.name, a.size, null);
        }
        catch (Exception ex) { return (null, null, 0, ex.Message); }
    }

    /// <summary>下载：镜像优先 → 直连兜底；每个地址重试 3 次；用 Range 续传</summary>
    private static async Task<string> DownloadAsync(string url, string name, string variant, string mirror, Action<string> log)
    {
        var part = PartPath(variant);
        var candidates = new List<string>();
        mirror = (mirror ?? "").Trim();
        if (mirror.Length > 0 && !mirror.Contains("github.com/ggml-org"))
        {
            var m = mirror.EndsWith("/") ? mirror : mirror + "/";
            candidates.Add(m + url);                          // 「前缀 + 完整地址」最常见
            candidates.Add(m + url.Replace("https://", ""));   // 少数镜像要「前缀 + 不带协议」
        }
        candidates.Add(url);                                   // 兜底：直连

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };  // 慢网不能设总时限
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CNM/0.1");

        Exception last = null;
        foreach (var u in candidates)
        {
            bool isMirror = !u.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase);
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    long have = File.Exists(part) ? new FileInfo(part).Length : 0;
                    using var req = new HttpRequestMessage(HttpMethod.Get, u);
                    if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);

                    using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                    bool resuming = have > 0 && resp.StatusCode == HttpStatusCode.PartialContent;
                    if (have > 0 && !resuming)
                    {
                        log("[安装] 服务器不接受续传，从头下");
                        TryDelete(part); have = 0;
                    }
                    resp.EnsureSuccessStatusCode();

                    long total = have + (resp.Content.Headers.ContentLength ?? 0);
                    if (isMirror) log("[安装] 走加速前缀下载…");
                    else if (candidates.Count > 1 && u == candidates[candidates.Count - 1]) log("[安装] 镜像不行，回落直连…");

                    var sw = Stopwatch.StartNew();
                    long logged = 0;
                    using (var fs = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var src = await resp.Content.ReadAsStreamAsync())
                    {
                        var buf = new byte[1 << 20];
                        long done = have, got = 0;
                        while (true)
                        {
                            int n = await src.ReadAsync(buf);
                            if (n <= 0) break;
                            await fs.WriteAsync(buf, 0, n);
                            done += n; got += n;
                            if (done - logged >= 4 * 1048576 || (got > 0 && sw.Elapsed.TotalSeconds > 5))
                            {
                                logged = done; sw.Restart();
                                log($"[安装] {(have > 0 ? "续传 " : "")}{done / 1048576.0:0.0} / {(total > 0 ? (total / 1048576.0).ToString("0.0") : "?")} MB");
                            }
                        }
                    }

                    long size = new FileInfo(part).Length;
                    if (total > 0 && size < total)
                        throw new IOException($"只下到 {size / 1048576.0:0.0} / {total / 1048576.0:0.0} MB");

                    log($"[安装] 下载完成：{name}（{size / 1048576.0:0.0} MB）");
                    return part;
                }
                catch (Exception ex)
                {
                    last = ex;
                    log($"[安装] 下载中断（第 {attempt} 次）：{ex.Message} —— 停 1.5 秒接着续传…");
                    await Task.Delay(1500 * attempt);
                }
            }
        }
        throw last ?? new Exception("下载失败");
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
