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
    /// 自动检测：并发把「直连 / 系统代理 / 本机常见端口」都试一遍，谁先通就用谁。
    /// 这就是回答“这个站是不是必须走代理”——不用人猜，机器试。
    /// </summary>
    public async Task<(bool ok, string proxy, string root, List<string> models, string error, string detail)> DetectProxyAsync(
        UpstreamCfg cfg, CancellationToken ct)
    {
        var root = Clean(cfg.BaseUrl);
        if (root.Length == 0) return (false, "", "", new List<string>(), "地址是空的", "");

        var proxies = AutoProxies();
        var notes = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var win = new TaskCompletionSource<(bool ok, string proxy, string root, List<string> models)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(40));      // 全部候选的总上限

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

        var detail = string.Join(" ｜ ", proxies.Select(p => notes.TryGetValue(Label(p), out var n) ? n : null)
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
            else info.Error = err + (IsDirect(cfg.Proxy)
                ? "。如果这台机器直连不了这个站，把代理那格填「自动」，让 CNM 自己找条能通的路"
                : "");
            Write(info.Ok
                ? $"[上游] {cfg.Name} 连上了：{info.Models.Count} 个模型（{info.Root}）"
                : $"[上游] {cfg.Name} 连不上：{info.Error}");
        }

        lock (_cache) _cache[cfg.Name] = info;
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
        if (use.Length == 0)
            use = Peek(cfg.Name)?.Models.FirstOrDefault() ?? "gpt-4o-mini";

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

        // 自动模式：这条路没通（连头都没发出去）→ 现场探一遍再发，用户什么都不用配
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
                                                 NetworkStream client, CancellationToken ct, string proxy)
    {
        var one = new OneShot();
        HttpRequestMessage req = null;
        try
        {
            var content = new ByteArrayContent(body);
            content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
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
            while (true)
            {
                int n;
                try { n = await src.ReadAsync(buf, 0, buf.Length, ct); } catch { break; }
                if (n <= 0) break;
                tail.Add(buf, n);
                await client.WriteAsync(Encoding.ASCII.GetBytes(n.ToString("x") + "\r\n"), ct);
                await client.WriteAsync(buf.AsMemory(0, n), ct);
                await client.WriteAsync(crlf, ct);
                await client.FlushAsync(ct);
                outBytes += n;
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
