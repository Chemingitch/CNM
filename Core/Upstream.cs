using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CNM.Core;

/// <summary>一个云端上游：别家的 OpenAI 兼容服务（API 中转站、自建网关…）</summary>
public sealed class UpstreamCfg
{
    public string Name { get; set; } = "";
    /// <summary>例：https://api.xxx.com/v1（没写 /v1 会自动试）</summary>
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    /// <summary>
    /// 代理。留空/直连 = 只直连；<b>auto/自动 = 自动检测</b>（并发试「直连 → 系统代理 → 本机常见端口」，哪个通用哪个）；
    /// system = 跟随系统代理；也可以写死 http://127.0.0.1:7890 / socks5://127.0.0.1:1080。
    /// </summary>
    public string Proxy { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

/// <summary>上游的探测结果（真正能用的根地址、抓到的模型列表、报错）</summary>
public sealed class UpstreamInfo
{
    public UpstreamCfg Cfg { get; set; } = new();
    public string Root { get; set; } = "";
    public List<string> Models { get; set; } = new();
    public string Error { get; set; } = "";
    public DateTime Seen { get; set; } = DateTime.MinValue;
    /// <summary>自动检测选中的代理（空 = 直连）；转发的请求就按它走</summary>
    public string ProxyUsed { get; set; } = "";
    /// <summary>自动检测的过程（给人看的：谁通了、谁超时了）</summary>
    public string ProxyDetail { get; set; } = "";
    public bool Ok => Root.Length > 0 && Error.Length == 0;
}

/// <summary>
/// 云端上游：把 /v1/* 转发到网上的 OpenAI 兼容服务。
/// 有了它，本机所有 agent 只认 CNM 一个地址 —— 本地 GGUF 和云端模型都能用，
/// 也能让「所有 agent 都用同一朵云的那个模型」（默认上游 + 强制模型名）。
/// </summary>
public sealed class UpstreamHub
{
    private readonly Settings _s;
    private readonly Dictionary<string, UpstreamInfo> _cache = new(StringComparer.OrdinalIgnoreCase);

    // 流式转发不能有总时限 —— 所以这里不设 HttpClient.Timeout，改用每次请求自己的 ct。
    private static readonly HttpClient Direct = CreateClient("");
    private static readonly Dictionary<string, HttpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    public event Action<string> Log;

    public UpstreamHub(Settings s) { _s = s; }

    /// <summary>每个代理写法一个 client（直连 = 不走代理，不偷偷用系统代理）</summary>
    private static HttpClient GetClient(string proxy)
    {
        var p = NormalizeProxy(proxy);
        if (p.Length == 0) return Direct;
        if (p.Equals("auto", StringComparison.OrdinalIgnoreCase)) return Direct;   // 还没探出来前先按直连
        lock (_clients)
        {
            if (_clients.TryGetValue(p, out var c)) return c;
            c = CreateClient(p);
            _clients[p] = c;
            return c;
        }
    }

    /// <summary>代理写法宽容点：只写 127.0.0.1:7890 也行；socks5h 当成 socks5；auto = 自动检测</summary>
    public static string NormalizeProxy(string proxy)
    {
        var p = (proxy ?? "").Trim();
        if (p.Length == 0) return "";
        if (p.Equals("auto", StringComparison.OrdinalIgnoreCase) || p == "自动" || p == "自动检测") return "auto";
        if (p.Equals("direct", StringComparison.OrdinalIgnoreCase) || p == "直连" || p == "none") return "";
        if (p.Equals("system", StringComparison.OrdinalIgnoreCase) || p == "系统" || p == "系统代理") return "system";
        if (p.StartsWith("socks5h://", StringComparison.OrdinalIgnoreCase)) p = "socks5://" + p["socks5h://".Length..];
        if (!Regex.IsMatch(p, "^[a-z0-9+.-]+://", RegexOptions.IgnoreCase)) p = "http://" + p;
        return p;
    }

    /// <summary>这个上游配的是不是「自动检测」</summary>
    public static bool IsAuto(string proxy) => NormalizeProxy(proxy).Equals("auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>自动检测要试的写法：直连 → 跟随系统 → 本机常见代理端口（Clash / v2ray / 其它）</summary>
    public static List<string> AutoProxies()
    {
        var list = new List<string> { "", "system" };
        foreach (var port in new[] { 7890, 7897, 10809, 10808, 1080, 2080, 8889, 8118, 20171 })
        {
            list.Add("http://127.0.0.1:" + port);
            list.Add("socks5://127.0.0.1:" + port);
        }
        return list;
    }

    /// <summary>给界面/日志看的代理名字</summary>
    public static string Label(string proxy)
    {
        var p = NormalizeProxy(proxy);
        if (p.Length == 0) return "直连";
        if (p.Equals("system", StringComparison.OrdinalIgnoreCase)) return "系统代理";
        if (p.Equals("auto", StringComparison.OrdinalIgnoreCase)) return "自动";
        return p;
    }

    /// <summary>
    /// 把长长的上游报错压成一句能给界面看的短话。
    ///
    /// 中转站的报错经常是一整坨 JSON（HTTP 401 Invalid token 带 request id，
    /// 一屏都放不下），直接塞进卡片会把界面撑得很难看。
    /// 这里只留「状态码 + 一句人话」：能看懂发生了什么就够了，
    /// 完整原文仍然写进日志（Write 那行），要查细节去日志里找。
    /// </summary>
    public static string Short(string err)
    {
        if (string.IsNullOrWhiteSpace(err)) return "连不上";
        var s = err.Trim().Replace("\r", " ").Replace("\n", " ");
        // 401/403 这类认证错，抽出一句人话就够
        if (s.Contains("401")) return "401 认证失败（Key 不对或过期）";
        if (s.Contains("403")) return "403 被拒绝（Key 没权限）";
        if (s.Contains("404")) return "404 地址不对（检查 Base URL）";
        if (s.Contains("429")) return "429 请求太频繁，稍后再试";
        if (s.Contains("timeout") || s.Contains("超时") || s.Contains("timed out")) return "连接超时（网络或代理问题）";
        if (s.Contains("refused") || s.Contains("拒绝")) return "连接被拒绝（地址或端口不对）";
        if (s.Contains("Name or service") || s.Contains("no such host") || s.Contains("DNS")) return "域名解析不了（检查地址）";
        if (s.Contains("SSL") || s.Contains("certificate") || s.Contains("证书")) return "HTTPS 证书有问题";
        // 其它情况截断，别让界面被撑破
        return s.Length <= 48 ? s : s.Substring(0, 48) + "…";
    }

    /// <summary>测速结果缓存：key = 上游名 + \0 + 模型名 → 首字延迟毫秒。
    /// 放后端（不是前端内存）：刷新模型列表后自动重测，重画卡片也不丢，重启前一直有效。</summary>
    private static readonly Dictionary<string, int> _pingCache = new(StringComparer.Ordinal);
    private static readonly object _pingLock = new();

    public static string PingKey(string up, string model) => up + "\0" + model;

    public static Dictionary<string, int> PingSnapshot()
    {
        lock (_pingLock) return new Dictionary<string, int>(_pingCache, StringComparer.Ordinal);
    }

    /// <summary>
    /// 拉完模型列表后，后台给所有模型测一遍速（并发 3，慢慢来）。
    /// 目的是让用户一打开「外地模型」就能看到延迟，不用自己一个个点。
    /// 只在列表变了才测，同一批不重复测。
    /// </summary>
    private void WarmUpPings(UpstreamCfg cfg, List<string> models)
    {
        if (models.Count == 0) return;
        var need = new List<string>();
        lock (_pingLock)
            foreach (var m in models)
                if (!_pingCache.ContainsKey(PingKey(cfg.Name, m))) need.Add(m);
        if (need.Count == 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var gate = new SemaphoreSlim(3);
                var tasks = need.Select(async m =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        var r = await PingModelAsync(cfg, m, CancellationToken.None);
                        lock (_pingLock) _pingCache[PingKey(cfg.Name, m)] = r.ok ? r.firstMs : -1;
                    }
                    finally { gate.Release(); }
                });
                await Task.WhenAll(tasks);
                Write($"[上游] {cfg.Name} 延迟测完（{need.Count} 个模型）");
                PingUpdated?.Invoke();
            }
            catch { }
        });
    }

    /// <summary>测速结果更新了（界面收到后重画外地模型卡片）</summary>
    public event Action PingUpdated;

    /// <summary>把缓存的测速结果读出来给界面用（-1 = 测过但失败）</summary>
    public void PushPingsToUi() => PingUpdated?.Invoke();

    private static HttpClient CreateClient(string proxy)
    {
        var p = NormalizeProxy(proxy);
        var h = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.None,   // SSE 要原样透传，别让它偷偷解压
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(5),                        // 直连被卡住时别干等，5 秒就认输
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        if (p.Length == 0) { h.UseProxy = false; h.Proxy = null; }           // 直连
        else if (p.Equals("system", StringComparison.OrdinalIgnoreCase)) { } // 保持默认 = 跟随系统
        else { h.Proxy = new System.Net.WebProxy(p); h.UseProxy = true; }

        var c = new HttpClient(h) { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "CNM/1.0 (local gateway)");
        return c;
    }

    /// <summary>真正该用哪个代理：auto 就用上次探到的（还没探过先直连，失败会自动探）</summary>
    public string ResolveProxy(UpstreamCfg cfg)
    {
        var p = NormalizeProxy(cfg.Proxy);
        if (!p.Equals("auto", StringComparison.OrdinalIgnoreCase)) return p;
        var used = Peek(cfg.Name)?.ProxyUsed;
        return string.IsNullOrWhiteSpace(used) ? "" : used;
    }

    private void Write(string line) => Log?.Invoke(line);

    // ---------------- 地址 ----------------

    /// <summary>规范化：没写协议就补 https；去掉尾巴上的斜杠；用户常把 /chat/completions 一起粘进来</summary>
    public static string Clean(string url)
    {
        var u = (url ?? "").Trim();
        if (u.Length == 0) return "";
        if (!Regex.IsMatch(u, "^https?://", RegexOptions.IgnoreCase)) u = "https://" + u;
        u = u.TrimEnd('/');
        foreach (var tail in new[] { "/chat/completions", "/completions", "/embeddings", "/models" })
        {
            if (u.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) { u = u[..^tail.Length].TrimEnd('/'); break; }
        }
        return u;
    }

    /// <summary>先按用户写的那串试，再试补一个 /v1</summary>
    private static List<string> Candidates(string root)
    {
        var list = new List<string> { root };
        if (!Regex.IsMatch(root, @"/v\d+(/|$)", RegexOptions.IgnoreCase) && !root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            list.Add(root + "/v1");
        return list;
    }

    private static void Auth(HttpRequestMessage req, UpstreamCfg cfg)
    {
        var key = (cfg.ApiKey ?? "").Trim();
        if (key.Length == 0) return;
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
    }

    private static string Snippet(string s) =>
        string.IsNullOrWhiteSpace(s) ? "空响应" : (s.Length <= 160 ? s.Trim() : s.Trim()[..160] + "…");

    // ---------------- 抓模型列表 ----------------

    /// <summary>用一个指定的代理试一次：通了就把根地址和模型列表带回来</summary>
    private static async Task<(bool ok, string root, List<string> models, string err)> TryModelsAsync(
        UpstreamCfg cfg, string root, string proxy, int seconds, CancellationToken ct)
    {
        string last = "";
        foreach (var cand in Candidates(root))
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(seconds));
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, cand + "/models");
                Auth(req, cfg);
                using var res = await GetClient(proxy).SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
                var txt = await res.Content.ReadAsStringAsync(cts.Token);
                if (!res.IsSuccessStatusCode) { last = $"HTTP {(int)res.StatusCode} {Snippet(txt)}"; continue; }
                var ids = ParseModels(txt);
                if (ids.Count == 0) { last = "地址通了，但没返回模型列表：" + Snippet(txt); continue; }
                return (true, cand, ids.Take(400).ToList(), "");
            }
            catch (OperationCanceledException) { last = $"{seconds} 秒没连上（超时）"; }
            catch (Exception ex) { last = ex.Message; }
        }
        return (false, "", new List<string>(), last.Length > 0 ? last : "连不上");
    }

    /// <summary>
    /// 自动检测：先单独试「直连」——能直连就用直连（最快、最稳，也不依赖用户代理软件开着）；
    /// 直连不通才并发试「系统代理 / 本机常见端口」，谁先通就用谁。
    /// 以前所有候选并发抢答，谁先回算谁，经常把「系统代理」写进配置，用户以为那是残留的旧值。
    /// </summary>
    public async Task<(bool ok, string proxy, string root, List<string> models, string error, string detail)> DetectProxyAsync(
        UpstreamCfg cfg, CancellationToken ct)
    {
        var root = Clean(cfg.BaseUrl);
        if (root.Length == 0) return (false, "", "", new List<string>(), "地址是空的", "");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(40));      // 全部候选的总上限

        // 第一步：只试直连（8 秒）。通了就直接定案，不再碰代理候选。
        try
        {
            var sw0 = Stopwatch.StartNew();
            var (dok, droot, dmodels, derr) = await TryModelsAsync(cfg, root, "", 8, cts.Token);
            if (dok) return (true, "", droot, dmodels, "", $"直连 ✓ {sw0.Elapsed.TotalSeconds:0.0}s · {dmodels.Count} 个模型");
            if (derr.Length > 0) { /* 记下失败原因，最后兜底提示用 */ }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }

        // 第二步：直连不行，才并发试系统代理 / 本机端口（直连已经从候选里剔除，别重复试）
        var proxies = AutoProxies().Where(p => p.Length > 0).ToList();
        var notes = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var win = new TaskCompletionSource<(bool ok, string proxy, string root, List<string> models)>(TaskCreationOptions.RunContinuationsAsynchronously);
        notes["直连"] = "直连 ✗ 不通";

        foreach (var raw in proxies)
        {
            var p = raw;
            _ = Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    var (ok, r, models, err) = await TryModelsAsync(cfg, root, p, 8, cts.Token);
                    if (ok)
                    {
                        notes[Label(p)] = $"{Label(p)} ✓ {sw.Elapsed.TotalSeconds:0.0}s";
                        win.TrySetResult((true, p, r, models));
                        return;
                    }
                    notes[Label(p)] = $"{Label(p)} ✗ {Brief(err)}";
                }
                catch (OperationCanceledException) { notes[Label(p)] = $"{Label(p)} ✗ 超时"; }
                catch (Exception ex) { notes[Label(p)] = $"{Label(p)} ✗ {Brief(ex.Message)}"; }
            }, CancellationToken.None);
        }

        var done = await Task.WhenAny(win.Task, Task.Delay(TimeSpan.FromSeconds(40), CancellationToken.None));
        bool okDone = done == win.Task && win.Task.IsCompletedSuccessfully && win.Task.Result.ok;
        try { cts.Cancel(); } catch { }

        // 详情里把「直连」也带上：用户最想知道的就是「直连到底行不行」
        var detail = string.Join(" ｜ ", new[] { "直连" }.Concat(proxies.Select(Label))
                                                .Select(l => notes.TryGetValue(l, out var n) ? n : null)
                                                .Where(x => x != null)
                                                .Distinct());
        if (okDone)
        {
            var w = win.Task.Result;
            return (true, w.proxy, w.root, w.models, "", detail);
        }
        var first = notes.Values.FirstOrDefault(x => x.Contains("✗"));
        return (false, "", "", new List<string>(), first != null ? first : "40 秒内没有一条路能连上（检查网络 / 地址 / Key）", detail);
    }

    private static string Brief(string s)
    {
        var t = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (t.Length == 0) return "失败";
        // 常见的一长串 .NET 异常压短点，界面里好读
        if (t.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("超时", StringComparison.Ordinal) ||
            t.Contains("Timeout", StringComparison.OrdinalIgnoreCase)) return "超时";
        if (t.Contains("refused", StringComparison.OrdinalIgnoreCase)) return "端口没人监听";
        if (t.Contains("name or service not known", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("No such host", StringComparison.OrdinalIgnoreCase)) return "域名解析不了";
        if (t.Contains("SSL", StringComparison.OrdinalIgnoreCase) || t.Contains("certificate", StringComparison.OrdinalIgnoreCase)) return "HTTPS 证书问题";
        return t.Length <= 40 ? t : t[..40] + "…";
    }

    /// <summary>连一次上游，把模型列表抓回来（顺便校验 key）。结果进缓存，/v1/models 直接用它。</summary>
    public async Task<UpstreamInfo> RefreshAsync(UpstreamCfg cfg, CancellationToken ct)
    {
        var info = new UpstreamInfo { Cfg = cfg, Seen = DateTime.Now };
        var root = Clean(cfg.BaseUrl);
        if (root.Length == 0)
        {
            info.Error = "地址是空的";
        }
        else if (IsAuto(cfg.Proxy))
        {
            // 自动检测：不用人告诉它要不要代理
            var d = await DetectProxyAsync(cfg, ct);
            info.ProxyUsed = d.proxy;
            info.ProxyDetail = d.detail;
            info.Error = d.ok ? "" : d.error;
            if (d.ok) { info.Root = d.root; info.Models = d.models; }
            Write(info.Ok
                ? $"[上游] {cfg.Name} 连上了：{info.Models.Count} 个模型 · 自动选 {Label(d.proxy)}（{info.Root}）"
                : $"[上游] {cfg.Name} 连不上：{info.Error}　自动检测：{d.detail}");
        }
        else
        {
            var (ok, r, models, err) = await TryModelsAsync(cfg, root, cfg.Proxy, 20, ct);
            if (ok) { info.Root = r; info.Models = models; info.ProxyUsed = NormalizeProxy(cfg.Proxy); }
            // 界面上只留一句短的（原始报错能占半个屏幕，之前塞在卡片里很难看）。
            // 给用户的建议写进 logs 的 detail 里，需要时查日志。
            else info.Error = Short(err) + (IsDirect(cfg.Proxy) ? "　（代理建议填「自动」）" : "");
            Write(info.Ok
                ? $"[上游] {cfg.Name} 连上了：{info.Models.Count} 个模型（{info.Root}）"
                : $"[上游] {cfg.Name} 连不上：{info.Error}"
                  + (IsDirect(cfg.Proxy) ? "。如果这台机器直连不了这个站，把代理那格填「自动」，让 CNM 自己找条能通的路" : ""));
        }

        lock (_cache) _cache[cfg.Name] = info;
        if (info.Ok) WarmUpPings(cfg, info.Models);      // 后台测延迟，界面一打开就有数
        return info;
    }

    private static bool IsDirect(string proxy) => NormalizeProxy(proxy).Length == 0;

    public async Task RefreshAllAsync(CancellationToken ct)
    {
        List<UpstreamCfg> list;
        try { list = _s.Upstreams.ToList(); } catch { return; }
        foreach (var u in list)
        {
            if (!u.Enabled) continue;
            try { await RefreshAsync(u, ct); } catch { }
        }
    }

    /// <summary>容错解析：{"data":[{"id":…}]} / {"models":[…]} / ["id", …] 都认</summary>
    private static List<string> ParseModels(string txt)
    {
        var ids = new List<string>();
        try
        {
            var node = JsonNode.Parse(txt);
            void Take(JsonNode n)
            {
                if (n == null) return;
                if (n is JsonArray a) { foreach (var it in a) Take(it); return; }
                if (n is JsonObject o)
                {
                    foreach (var key in new[] { "data", "models", "result" })
                        if (o.TryGetPropertyValue(key, out var v) && v is JsonArray) { Take(v); return; }
                    foreach (var key in new[] { "id", "name", "model" })
                        if (o.TryGetPropertyValue(key, out var v) && v is JsonValue)
                        {
                            var s = v.ToString().Trim();
                            if (s.Length > 0) ids.Add(s);
                            return;
                        }
                    return;
                }
                var t = n.ToString().Trim();
                if (t.Length > 0) ids.Add(t);
            }
            Take(node);
        }
        catch { }
        return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---------------- 查表 ----------------

    public UpstreamInfo Peek(string name)
    {
        lock (_cache) return _cache.TryGetValue((name ?? "").Trim(), out var v) ? v : null;
    }

    public List<UpstreamInfo> Snapshot()
    {
        lock (_cache) return _cache.Values.ToList();
    }

    /// <summary>哪个上游有这个模型名（"" = 都没有）</summary>
    public string UpstreamOf(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return "";
        var m = model.Trim();
        lock (_cache)
        {
            foreach (var kv in _cache)
            {
                if (!kv.Value.Cfg.Enabled || !kv.Value.Ok) continue;
                if (kv.Value.Models.Any(x => string.Equals(x, m, StringComparison.OrdinalIgnoreCase)))
                    return kv.Value.Cfg.Name;
            }
        }
        return "";
    }

    /// <summary>
    /// 这次请求该转给谁：1) 模型名命中某个上游 → 转它；2) 本地没有、又设了默认上游 → 转默认；
    /// 3) 否则（含本地模型）不转，交回本地引擎。
    /// </summary>
    public UpstreamCfg Route(string model, Func<string, bool> isLocal)
    {
        bool local = isLocal?.Invoke(model ?? "") ?? false;
        var name = UpstreamOf(model);
        if (name.Length > 0 && !local)
            return _s.Upstreams.FirstOrDefault(u => u.Enabled && string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase));

        if (local || string.IsNullOrWhiteSpace(model)) return null;

        var def = (_s.UpstreamDefault ?? "").Trim();
        if (def.Length == 0) return null;
        return _s.Upstreams.FirstOrDefault(u => u.Enabled && string.Equals(u.Name, def, StringComparison.OrdinalIgnoreCase));
    }

    // ---------------- 转发 ----------------

    /// <summary>拼出要打的上游地址（缓存里的 Root 优先，否则现场规范化）</summary>
    public string BuildUrl(UpstreamCfg cfg, string path)
    {
        var root = Peek(cfg.Name)?.Root;
        if (string.IsNullOrWhiteSpace(root)) root = Clean(cfg.BaseUrl);
        if (string.IsNullOrWhiteSpace(root)) return "";
        return root.TrimEnd('/') + path;
    }

    /// <summary>真发一条最小的对话过去，看通不通（也能看出路径、鉴权、代理哪里不对）</summary>
    public async Task<(bool ok, string detail)> ChatProbeAsync(UpstreamCfg cfg, string model, CancellationToken ct)
    {
        var url = BuildUrl(cfg, "/chat/completions");
        if (url.Length == 0) return (false, "上游地址是空的");

        var use = (model ?? "").Trim();
        if (use.Length == 0) use = (_s.UpstreamModel ?? "").Trim();
        if (use.Length == 0) use = Peek(cfg.Name)?.Models.FirstOrDefault() ?? "";
        if (use.Length == 0)
            return (false, "还不知道这条上游有哪些模型 —— 先点「测试」或「自动检测」把模型列表拉回来，或指定一条兜底模型名");

        var body = new JsonObject
        {
            ["model"] = use,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "只回一个字：好" }),
            ["max_tokens"] = 16
        }.ToJsonString();

        var proxy = ResolveProxy(cfg);
        var (ok, detail) = await PostOnceAsync(cfg, url, body, proxy, ct);
        if (!ok && IsAuto(cfg.Proxy))
        {
            // 自动模式：没通就先探一条能走的路（可能该走代理），再发一次
            var d = await DetectProxyAsync(cfg, ct);
            if (d.ok && !NormalizeProxy(d.proxy).Equals(NormalizeProxy(proxy), StringComparison.OrdinalIgnoreCase))
            {
                var again = await PostOnceAsync(cfg, url, body, d.proxy, ct);
                return (again.ok, $"先按 {Label(proxy)} 没通，自动改用 {Label(d.proxy)} 再发：\n{again.detail}\n（自动检测：{d.detail}）");
            }
            return (ok, detail + "\n（自动检测：" + d.detail + "）");
        }
        return (ok, detail);
    }

    /// <summary>
    /// 给单个模型测速：真发一次最小流式请求，量到「首字延迟」为止（agent 场景最关心这个）。
    /// 返回 (ok, 首字毫秒, 总毫秒, 错误)。超时 15 秒 —— 超过这个数对选模型已经没意义了。
    /// </summary>
    public async Task<(bool ok, int firstMs, int totalMs, string error)> PingModelAsync(
        UpstreamCfg cfg, string model, CancellationToken ct)
    {
        var url = BuildUrl(cfg, "/chat/completions");
        if (url.Length == 0) return (false, 0, 0, "上游地址是空的");
        var use = (model ?? "").Trim();
        if (use.Length == 0) return (false, 0, 0, "没给模型名");

        var body = new JsonObject
        {
            ["model"] = use,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }),
            ["max_tokens"] = 1,
            ["stream"] = true
        }.ToJsonString();

        var proxy = ResolveProxy(cfg);
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            Auth(req, cfg);

            using var res = await GetClient(proxy).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var headMs = (int)sw.ElapsedMilliseconds;                      // 响应头到达 = 网络 + 排队
            if ((int)res.StatusCode >= 400)
            {
                sw.Stop();
                return (false, headMs, headMs, $"HTTP {(int)res.StatusCode}");
            }

            using var src = await res.Content.ReadAsStreamAsync(cts.Token);
            var buf = new byte[4096];
            int firstMs = -1;
            while (true)
            {
                int n = await src.ReadAsync(buf, 0, buf.Length, cts.Token);
                if (n <= 0) break;
                if (firstMs < 0) firstMs = (int)sw.ElapsedMilliseconds;     // 第一个字节 = 首字延迟
                // 只要拿到第一个数据块就够了，剩下的不读了（省上游额度）
                break;
            }
            sw.Stop();
            if (firstMs < 0) return (false, headMs, (int)sw.ElapsedMilliseconds, "上游没回数据");
            return (true, firstMs, (int)sw.ElapsedMilliseconds, "");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return (false, 0, (int)sw.ElapsedMilliseconds, "15 秒没回应");
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (false, 0, (int)sw.ElapsedMilliseconds, Brief(ex.Message));
        }
    }

    private async Task<(bool ok, string detail)> PostOnceAsync(UpstreamCfg cfg, string url, string body, string proxy, CancellationToken ct)
    {
        try
        {
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            Auth(req, cfg);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            using var res = await GetClient(proxy).SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
            var txt = await res.Content.ReadAsStringAsync(cts.Token);
            if (txt.Length > 400) txt = txt[..400] + "…";
            return (res.IsSuccessStatusCode, $"POST {url}（走 {Label(proxy)}）→ HTTP {(int)res.StatusCode}\n{txt}");
        }
        catch (OperationCanceledException) { return (false, $"POST {url}（走 {Label(proxy)}）→ 60 秒超时"); }
        catch (Exception ex) { return (false, $"POST {url}（走 {Label(proxy)}）→ {Brief(ex.Message)}"); }
    }

    /// <summary>转发一次的结果（token 从响应尾巴里抠出来的，流式也认）</summary>
    private sealed class OneShot
    {
        public int Status;
        public long Bytes;
        public long PromptTok;
        public long ComplTok;
        public bool Retryable;
        public string Error = "";
    }

    /// <summary>把 /chat/completions 这类请求原样转给上游，响应（含 SSE 流式）按 chunked 边收边发。</summary>
    public async Task<(int status, long bytes, long pt, long ct)> ForwardAsync(UpstreamCfg cfg, string path, byte[] body,
                                                            NetworkStream client, CancellationToken ct,
                                                            string forceModel = null)
    {
        var url = BuildUrl(cfg, path);
        if (url.Length == 0) { var (s0, b0) = await FailAsync(client, "上游地址是空的", ct); return (s0, b0, 0, 0); }

        // forceModel："" = 原样转发；null = 看全局设置；有值 = 换成这个模型名再发
        var useModel = forceModel == null ? (_s.UpstreamModel ?? "").Trim() : forceModel.Trim();
        if (useModel.Length > 0) body = RewriteModel(body, useModel);

        var proxy = ResolveProxy(cfg);
        var r = await ForwardOnceAsync(cfg, url, body, client, ct, proxy);

        // 连接池里可能躺着对端已经关掉的连接（中转站常 60 秒空闲就断）——
        // 这种情况下一个字节都没发出去，换条新连接原地重试一次，别把抖动直接抛给客户端。
        if (r.Retryable && !ct.IsCancellationRequested)
        {
            Write($"[上游] {cfg.Name} 第一次没发出去（{Brief(r.Error)}），换新连接重试一次…");
            r = await ForwardOnceAsync(cfg, url, body, client, ct, proxy, forceNewConnection: true);
        }

        // 自动模式：这条路还没通（连头都没发出去）→ 现场探一遍再发，用户什么都不用配
        if (r.Retryable && IsAuto(cfg.Proxy))
        {
            Write($"[上游] {cfg.Name} 用 {Label(proxy)} 没连上，自动检测中…");
            var d = await DetectProxyAsync(cfg, ct);
            lock (_cache) if (_cache.TryGetValue(cfg.Name, out var inf)) inf.ProxyUsed = d.proxy;
            if (d.ok)
            {
                Write($"[上游] {cfg.Name} 自动改用 {Label(d.proxy)}（{d.detail}）");
                r = await ForwardOnceAsync(cfg, url, body, client, ct, d.proxy);
                proxy = d.proxy;
            }
            else Write($"[上游] {cfg.Name} 自动检测也没找到能通的路：{d.detail}");
        }

        if (r.Status == 0)
        {
            var msg = "转给上游失败：" + r.Error;
            if (IsAuto(cfg.Proxy))
                msg += "。自动检测了「直连 / 系统代理 / 本机常见端口」都不通 —— 检查网址和 Key，或把代理那格填成你自己的代理地址";
            else
                msg += $"（走 {Label(proxy)} 连不上上游；试试把代理那格填「自动」，让 CNM 自己找条能通的路）";
            var (s1, b1) = await FailAsync(client, msg, ct);
            return (s1, b1, 0, 0);
        }
        return (r.Status, r.Bytes, r.PromptTok, r.ComplTok);
    }

    private async Task<OneShot> ForwardOnceAsync(UpstreamCfg cfg, string url, byte[] body,
                                                 NetworkStream client, CancellationToken ct, string proxy,
                                                 bool forceNewConnection = false)
    {
        var one = new OneShot();
        HttpRequestMessage req = null;
        try
        {
            var content = new ByteArrayContent(body);
            content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            if (forceNewConnection) req.Headers.ConnectionClose = true;   // 别复用可能已死的连接
            Auth(req, cfg);

            using var res = await GetClient(proxy).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            int status = (int)res.StatusCode;
            one.Status = status;
            if (status >= 400)
            {
                var sent = body.Length == 0 ? "(空)" : Encoding.UTF8.GetString(body);
                if (sent.Length > 240) sent = sent[..240] + "…";
                Write($"[上游] {cfg.Name} POST {url}（走 {Label(proxy)}） → {status}；发过去的是 {sent}");
            }

            var ctype = res.Content.Headers.ContentType?.ToString() ?? "application/json";
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(string.IsNullOrEmpty(res.ReasonPhrase) ? "OK" : res.ReasonPhrase).Append("\r\n");
            sb.Append("Content-Type: ").Append(ctype).Append("\r\n");
            sb.Append("Transfer-Encoding: chunked\r\n");
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("Connection: close\r\n");
            sb.Append("X-CNM-Upstream: ").Append(cfg.Name).Append("\r\n\r\n");
            await client.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);
            await client.FlushAsync(ct);
            one.Retryable = false;              // 头已经出去了，后面再出错也不能换条路重来

            long outBytes = 0;
            var tail = new TailCheck();
            using var src = await res.Content.ReadAsStreamAsync(ct);
            var buf = new byte[32 * 1024];
            var crlf = new byte[] { 13, 10 };

            // 卡住检测：上游「不发也不关」（中转站排队/被限流）时，别傻等到 30 分钟超时。
            // 90 秒一个字节都没有 = 这条流已经废了，主动掐断并报错。
            long lastByteTick = Environment.TickCount64;
            bool upstreamBroke = false, clientGone = false;
            string brokeMsg = "";
            using var stall = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stall.Token);
            var wt = linked.Token;
            var watchdog = Task.Run(async () =>
            {
                while (!linked.IsCancellationRequested)
                {
                    try { await Task.Delay(5000, linked.Token); } catch { return; }
                    if (Environment.TickCount64 - Interlocked.Read(ref lastByteTick) > 90_000)
                    {
                        try { stall.Cancel(); } catch { }
                        return;
                    }
                }
            });

            while (true)
            {
                int n;
                try { n = await src.ReadAsync(buf, 0, buf.Length, wt); }
                catch (OperationCanceledException) when (stall.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    upstreamBroke = true;
                    brokeMsg = "上游 90 秒没有任何数据（中转站把连接吊住了）";
                    break;
                }
                catch (OperationCanceledException) { throw; }          // 客户端自己取消：交给外层
                catch (Exception ex) { upstreamBroke = true; brokeMsg = Brief(ex.Message); break; }

                if (n <= 0) break;                                     // 上游正常结束
                Interlocked.Exchange(ref lastByteTick, Environment.TickCount64);
                tail.Add(buf, n);

                try
                {
                    await client.WriteAsync(Encoding.ASCII.GetBytes(n.ToString("x") + "\r\n"), ct);
                    await client.WriteAsync(buf.AsMemory(0, n), ct);
                    await client.WriteAsync(crlf, ct);
                    await client.FlushAsync(ct);
                }
                catch { clientGone = true; break; }                    // 客户端（agent）先走了
                outBytes += n;
            }

            // 客户端断开：直接收工，不发终止块（发了也没人收）
            if (clientGone)
            {
                one.Status = 499;
                one.Error = "客户端断开";
                one.Bytes = outBytes;
                Write($"[上游] {cfg.Name} 客户端已断开，停止转发（已转 {FmtBytes(outBytes)}）");
                return one;
            }

            // 上游断了：不能写终止块！否则客户端会把「截断」当成「正常回答结束」——
            // 表现就是答案说到一半突然没了，而且不报错。不写终止块 = 客户端读到传输中断，
            // 会按错误处理（重试 / 提示），比静默截断好一万倍。
            if (upstreamBroke)
            {
                one.Status = 499;
                one.Error = brokeMsg;
                one.Bytes = outBytes;
                Write($"[上游] {cfg.Name} 流中断：{brokeMsg}（已转 {FmtBytes(outBytes)}；主动断开客户端，避免把截断当正常结束）");
                return one;
            }

            await client.WriteAsync(Encoding.ASCII.GetBytes("0\r\n\r\n"), ct);
            await client.FlushAsync(ct);

            one.Bytes = outBytes;
            var (pt, ctok) = TokenScan.Parse(tail.Text());
            one.PromptTok = pt;
            one.ComplTok = ctok;
            return one;
        }
        catch (OperationCanceledException) { one.Status = 499; return one; }
        catch (Exception ex)
        {
            one.Retryable = one.Status == 0;    // 一个字节都没发出去 = 可以换条路重来
            one.Error = Brief(ex.Message);
            if (!one.Retryable) Write($"[上游] {cfg.Name} 转发中断：{ex.Message}");
            return one;
        }
        finally { try { req?.Dispose(); } catch { } }
    }

    private static string FmtBytes(long n) => n >= 1024 ? (n / 1024) + " KB" : n + " B";
    private static async Task<(int, long)> FailAsync(NetworkStream client, string msg, CancellationToken ct)
    {
        try
        {
            var json = new JsonObject { ["error"] = new JsonObject { ["message"] = msg, ["type"] = "cnm_upstream_error" } }.ToJsonString();
            var bytes = Encoding.UTF8.GetBytes(json);
            var head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 502 Bad Gateway\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n");
            await client.WriteAsync(head, ct);
            await client.WriteAsync(bytes, ct);
            await client.FlushAsync(ct);
        }
        catch { }
        return (502, 0);
    }

    /// <summary>把请求体里的 model 换成指定的名字（中转站模型名和 agent 里写的不一致时用）</summary>
    private static byte[] RewriteModel(byte[] body, string model)
    {
        if (body.Length == 0) return body;
        try
        {
            if (JsonNode.Parse(Encoding.UTF8.GetString(body)) is not JsonObject o) return body;
            if (o["model"] is JsonValue v && string.Equals(v.ToString(), model, StringComparison.Ordinal)) return body;
            o["model"] = model;
            return Encoding.UTF8.GetBytes(o.ToJsonString());
        }
        catch { return body; }
    }
}
