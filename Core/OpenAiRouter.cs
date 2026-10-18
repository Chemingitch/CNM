using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CNM.UI;

namespace CNM.Core;

public sealed class RequestLog
{
    public string Time { get; set; } = "";
    public string Remote { get; set; } = "";
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public string Model { get; set; } = "";
    public string Upstream { get; set; } = "";
    /// <summary>转给上游时实际用的模型名（配了「这个上游用哪个模型」才和 Model 不同）</summary>
    public string UpModel { get; set; } = "";
    public int Status { get; set; }
    public long Ms { get; set; }
    public long Bytes { get; set; }
    /// <summary>输入 token（能从响应里认出 usage 时才有）</summary>
    public long PromptTok { get; set; }
    /// <summary>输出 token</summary>
    public long ComplTok { get; set; }
}

/// <summary>
/// 对外 OpenAI 兼容服务：自己实现 HTTP/1.1（不依赖 HttpListener，免管理员 / urlacl），
/// 把 /v1/* 原样转发给内部 llama-server，并支持按请求自动加载模型。
/// </summary>
public sealed class OpenAiRouter
{
    private readonly Settings _s;
    private readonly EngineManager _eng;
    private readonly ModelLibrary _lib;
    private readonly Func<string, CancellationToken, Task<EngineManager.LoadResult>> _ensure;

    /// <summary>云端上游（API 中转站）：本地没有的模型转给它</summary>
    public UpstreamHub Upstreams { get; }

    /// <summary>用量统计（日/月/年）—— 只记数字</summary>
    public UsageStats Usage { get; } = new();

    /// <summary>哪些请求算「干活儿的」（模型列表、探活不算用量）</summary>
    private static bool IsWorkPath(string path) =>
        path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("/completions", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase);

    private TcpListener _listener;
    private CancellationTokenSource _cts;

    public event Action<RequestLog> RequestLogged;
    public event Action<string> Log;
    public event Action StateChanged;

    public bool Running => _listener != null;
    public int Port { get; private set; }

    public long RequestCount { get; private set; }
    public string LastError { get; private set; } = "";

    public OpenAiRouter(Settings s, EngineManager eng, ModelLibrary lib,
                        Func<string, CancellationToken, Task<EngineManager.LoadResult>> ensure)
    {
        _s = s; _eng = eng; _lib = lib; _ensure = ensure;
        Upstreams = new UpstreamHub(s);
    }

    public void Start()
    {
        if (Running) return;
        Port = _s.Port;
        var addr = _s.LanExposed ? IPAddress.Any : IPAddress.Loopback;
        try
        {
            _listener = new TcpListener(addr, Port);
            _listener.Start();
        }
        catch (Exception ex)
        {
            _listener = null;
            LastError = $"端口 {Port} 监听失败：{ex.Message}";
            Emit("[错误] " + LastError);
            StateChanged?.Invoke();
            return;
        }
        _cts = new CancellationTokenSource();
        LastError = "";
        Emit($"[服务启动] http://{( _s.LanExposed ? "0.0.0.0" : "127.0.0.1")}:{Port}/v1  （局域网 {( _s.LanExposed ? "可访问" : "不可访问")}）");
        StateChanged?.Invoke();
        _ = AcceptLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        Emit("[服务停止]");
        StateChanged?.Invoke();
    }

    public void Restart() { Stop(); Start(); }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception) { await Task.Delay(250, CancellationToken.None); continue; }

            _ = Task.Run(() => HandleAsync(client), CancellationToken.None);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var log = new RequestLog { Time = DateTime.Now.ToString("HH:mm:ss"), Remote = RemoteText(client) };
        bool began = false;      // 响应是不是已经开始往外写了（自己写了就不能再补一个 500 上去）
        System.Net.Sockets.NetworkStream ns = null;   // catch 里要是「一个字节都没回」还得补一个 500，所以提到外面来
        try
        {
            _eng.BeginRequest();          // 标记“正在服务”，空闲卸载不会在请求中途把引擎掐了
            client.NoDelay = true;
            using (client)
            using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30)))
            {
                var ct = cts.Token;
                ns = client.GetStream();
                var reader = new BufStream(ns);

                string reqLine = await reader.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(reqLine)) return;
                var parts = reqLine.Split(' ');
                if (parts.Length < 2) return;
                log.Method = parts[0].ToUpperInvariant();
                string target = parts[1];

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (true)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (string.IsNullOrEmpty(line)) break;
                    int i = line.IndexOf(':');
                    if (i <= 0) continue;
                    var k = line[..i].Trim();
                    var v = line[(i + 1)..].Trim();
                    if (!headers.ContainsKey(k)) headers[k] = v;
                }

                string path = target.Split('?')[0];
                log.Path = path;

                // 有些客户端把 base URL 配成 …/api/v1，或者按旧习惯来要 /api/v0/models。
                // 统一把开头这段 /api 折掉再走后面的路由 —— 它们就不会一直 404 了（日志里仍记原始路径）。
                if ((path.StartsWith("/api/v1", StringComparison.OrdinalIgnoreCase) ||
                     path.StartsWith("/api/v0", StringComparison.OrdinalIgnoreCase)) && path.Length > 4)
                {
                    path = path.Substring(4);
                    if (path.StartsWith("/v0", StringComparison.Ordinal)) path = "/v1" + path.Substring(3);
                }

                // 鉴权：真正干活的接口（chat/completions/embeddings…）要 key；
                // /、/health 这类“看一眼元信息”的接口不设门槛（监控/探活要能免 key）。
                // 模型列表默认也要 key（设置里能把「模型列表也要 Key」关掉，退回免 key）。
                bool listPath = path == "/v1/models" || path == "/v1/models/" || path.StartsWith("/v1/models/", StringComparison.Ordinal);
                bool openPath = path == "/" || path == "/health" || path == "/v1/health" || (listPath && !_s.ModelsNeedKey);
                if (!string.IsNullOrEmpty(_s.ApiKey) && !openPath && path.StartsWith("/v1", StringComparison.OrdinalIgnoreCase))
                {
                    string auth = headers.TryGetValue("Authorization", out var a) ? a : "";
                    string xk = headers.TryGetValue("x-api-key", out var x) ? x : "";
                    bool ok = auth.Equals("Bearer " + _s.ApiKey, StringComparison.Ordinal)
                           || xk.Equals(_s.ApiKey, StringComparison.Ordinal);
                    if (!ok)
                    {
                        await WriteJsonAsync(ns, 401, ErrorJson(
                            "API key 不对或者没带。请求头里加 Authorization: Bearer <key>（key 在 CNM 窗口 → 服务与日志 → 哨兵）。" +
                            "注意：这个端口是给程序调的 API，不是网页界面；浏览器里想看说明就打开根路径 /", "invalid_request_error"), ct);
                        log.Status = 401;
                        return;
                    }
                }

                if (log.Method == "OPTIONS")
                {
                    await WriteTextAsync(ns, 204, "text/plain", "", ct);
                    log.Status = 204;
                    return;
                }

                if (log.Method == "GET" && (path == "/v1/models" || path == "/v1/models/"))
                {
                    var json = BuildModelsJson();
                    await WriteJsonAsync(ns, 200, json, ct);
                    log.Status = 200;
                    return;
                }

                if (log.Method == "GET" && path.StartsWith("/v1/models/", StringComparison.Ordinal))
                {
                    string id = Uri.UnescapeDataString(path.Substring("/v1/models/".Length));
                    var e = _lib.Find(id);
                    if (e == null && (_eng.Loaded == null || !string.Equals(_eng.Loaded.Id, id, StringComparison.OrdinalIgnoreCase)))
                    {
                        var upName = Upstreams.UpstreamOf(id);   // 也可能是网上的模型
                        if (upName.Length == 0)
                        {
                            await WriteJsonAsync(ns, 404, "{\"error\":{\"message\":\"model not found\"}}", ct);
                            log.Status = 404;
                            return;
                        }
                        var upNode = new JsonObject
                        {
                            ["id"] = id,
                            ["object"] = "model",
                            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            ["owned_by"] = "cnm-upstream:" + upName,
                            ["cnm_upstream"] = upName
                        };
                        await WriteJsonAsync(ns, 200, upNode.ToJsonString(), ct);
                        log.Status = 200;
                        return;
                    }
                    var node = new JsonObject
                    {
                        ["id"] = id,
                        ["object"] = "model",
                        ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        ["owned_by"] = "cnm"
                    };
                    await WriteJsonAsync(ns, 200, node.ToJsonString(), ct);
                    log.Status = 200;
                    return;
                }

                if (log.Method == "GET" && path == "/favicon.ico")
                {
                    var ico = FaviconBytes();
                    if (ico.Length == 0) { await WriteTextAsync(ns, 204, "image/png", "", ct); log.Status = 204; }
                    else { await WriteBytesAsync(ns, 200, "image/png", ico, ct); log.Status = 200; }
                    return;
                }

                if (log.Method == "GET" && path == "/")
                {
                    var host = headers.TryGetValue("Host", out var hv) && hv.Length > 0 ? hv : "127.0.0.1:" + Port;
                    await WriteTextAsync(ns, 200, "text/html; charset=utf-8", LandingHtml(host), ct);
                    log.Status = 200;
                    return;
                }

                if (log.Method == "GET" && (path == "/health" || path == "/v1/health"))
                {
                    await WriteJsonAsync(ns, 200, StatusJson(), ct);
                    log.Status = 200;
                    return;
                }

                if (!path.StartsWith("/v1", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteJsonAsync(ns, 404, "{\"error\":{\"message\":\"只有 /v1/* 可用（OpenAI 兼容）\"}}", ct);
                    log.Status = 404;
                    return;
                }

                // ---- 读 body ----
                byte[] body = await ReadBodyAsync(reader, headers, ct);
                string model = ProbeModel(body, headers);
                log.Model = model ?? "";

                // ---- 云端上游：本地没有这个模型 → 转给中转站（不拉引擎、不占显存）----
                var up = Upstreams.Route(model, IsLocalModel);
                if (up != null)
                {
                    log.Upstream = up.Name;
                    // 名字本身就是某个上游的模型 → 原样转（像本地那样“点名哪个用哪个”，按需切换）；
                    // 名字谁都对不上（agent 里写 gpt-4o 之类）→ 用「模型库 → 外地模型」里接入的那个兜底
                    bool named = Upstreams.UpstreamOf(model).Length > 0;
                    log.UpModel = named || model.Length == 0 ? "" : (_s.UpstreamModel ?? "").Trim();
                    string q = target.Contains('?') ? target[target.IndexOf('?')..] : "";
                    // path 一定是 "/v1/..." —— 去掉 "/v1"（3 个字符）再拼到上游的 Base URL 后面，
                    // 少了这个斜杠会拼成 ".../v1chat/completions"，上游就回一个 404 Not Found。
                    began = true;      // 上游转发自己会把响应写出去，异常时别再抢着写 500
                    var fr = await Upstreams.ForwardAsync(up, path.Substring(3) + q, body, ns, ct, log.UpModel);
                    log.Status = fr.status;
                    log.Bytes = fr.bytes;
                    log.PromptTok = fr.pt;
                    log.ComplTok = fr.ct;
                    RequestCount++;
                    if (fr.status >= 400) Emit($"[上游] {up.Name} 返回 {fr.status}（模型 {model}）");
                    return;
                }

                // ---- 确保模型在跑 ----
                // 注意 IsLoading：模型正在加载时 Loaded 还是 null，光看 Loaded 会以为「不用管」，
                // 于是要 Y 的请求被塞给正在加载 X 的引擎（轻则 400，重则用 X 回答了 Y）。
                // 进了 EnsureAsync 会被 _loadGate 挡住，等 X 加载完再比对，就不会真的重复加载。
                bool needEnsure = !_eng.IsRunning || _eng.IsLoading
                    || (_eng.Loaded != null && !string.IsNullOrEmpty(model) && !ModelMatches(model, _eng.Loaded.Id));
                if (needEnsure)
                {
                    if (!_s.AutoLoadOnRequest && !_eng.IsRunning)
                    {
                        await WriteJsonAsync(ns, 503, ErrorJson($"服务未启动：先在 CNM 里加载模型，或打开“按请求自动加载”。"), ct);
                        log.Status = 503;
                        return;
                    }
                    if (_s.AutoLoadOnRequest)
                    {
                        // 加载可能要几分钟：一边等一边盯着客户端，断开就取消加载（ct 传进 EnsureAsync）
                        using var loadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        var loadTask = _ensure(model ?? "", loadCts.Token);
                        var watchTask = WatchClientAsync(client, loadCts);
                        await Task.WhenAny(loadTask, watchTask);
                        if (watchTask.IsCompleted)
                        {
                            log.Status = 499;      // 客户端先走了：取消加载，别白等
                            return;
                        }
                        var lr = loadTask.Result;
                        if (!lr.Ok)
                        {
                            await WriteJsonAsync(ns, 503, ErrorJson(lr.Message), ct);
                            log.Status = 503;
                            return;
                        }
                    }
                }

                if (!_eng.IsRunning)
                {
                    await WriteJsonAsync(ns, 503, ErrorJson("没有模型在跑。"), ct);
                    log.Status = 503;
                    return;
                }

                // ---- 转发 ----
                _eng.Touch();                 // 有人真的在推理：重置空闲计时
                began = true;                 // 本地引擎转发也是自己写响应，异常时别再抢着写 500
                var proxied = await ProxyAsync(ns, reader, headers, log.Method, target, body, ct);
                log.Status = proxied.status;
                log.Bytes = proxied.bytes;
                log.PromptTok = proxied.pt;
                log.ComplTok = proxied.ct;
                RequestCount++;
            }
        }
        catch (OperationCanceledException)
        {
            // 超时/断开：还没回包的话就没法回了，只能记个 499（客户端自己会超时）
            if (log.Status <= 0) log.Status = 499;
        }
        catch (System.IO.IOException)
        {
            // 客户端断开（读请求/写响应半途连接断了）：后面没意义了，掐掉正在等的加载/转发，
            // 不然客户端早跑了，模型还在那儿吭哧吭哧加载几分钟。
            if (log.Status <= 0) log.Status = 499;
        }
        catch (Exception ex)
        {
            Emit("[转发异常] " + ex.Message);
            // 到这儿才补 500 —— 前提是「一个字节都还没往外写」。
            // 以前这里只写日志不回包，客户端就一直干等到自己超时（表现为"卡住"）。
            if (log.Status == 0 && !began && ns != null)
            {
                log.Status = 500;
                try { await WriteJsonAsync(ns, 500, ErrorJson("服务器内部错误：" + ex.Message, "server_error"), CancellationToken.None); } catch { }
            }
            else if (log.Status == 0)
            {
                log.Status = 502;      // 响应已经写了一半，只能断开
            }
        }
        finally
        {
            _eng.EndRequest();
            log.Ms = sw.ElapsedMilliseconds;
            // 探测类请求（模型列表、探活、图标、首页说明）成功时不刷屏 —— 它们每几十秒就一次；
            // 但失败(4xx/5xx)照常显示，那才是需要人看的。
            bool probe = log.Path == "/" || log.Path == "/health" || log.Path == "/v1/health"
                      || log.Path == "/favicon.ico"
                      || log.Path.EndsWith("/models", StringComparison.OrdinalIgnoreCase)
                      || log.Path.EndsWith("/models/", StringComparison.OrdinalIgnoreCase);
            if (log.Path.Length > 0 && (!probe || log.Status >= 400))
                RequestLogged?.Invoke(log);

            // 用量：只有真正干活儿的请求才记账（模型列表 / 探活 / 首页不算），401 也不算
            if (IsWorkPath(log.Path) && log.Status != 401)
            {
                try { Usage.Add(DateTime.Now, log.Upstream.Length > 0, log.Status, log.Bytes, log.Ms, log.PromptTok, log.ComplTok); }
                catch { }
            }
            StateChanged?.Invoke();
        }
    }

    private static bool ModelMatches(string requested, string loaded)
    {
        if (string.IsNullOrEmpty(requested) || string.IsNullOrEmpty(loaded)) return true;
        if (string.Equals(requested, loaded, StringComparison.OrdinalIgnoreCase)) return true;
        if (requested.EndsWith("/" + loaded, StringComparison.OrdinalIgnoreCase)) return true;
        if (loaded.EndsWith("/" + requested, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>这个模型名说的是本机库里的模型吗（写全名或短名都算）；空名字按本地处理</summary>
    private bool IsLocalModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return true;
        if (_lib.Find(model) != null) return true;
        if (_eng.Loaded != null && ModelMatches(model, _eng.Loaded.Id)) return true;
        foreach (var m in _lib.Models)
            if (ModelMatches(model, m.Id)) return true;
        return false;
    }

    /// <summary>
    /// 盯着客户端还活着没：断开（Poll 返回 false）或出错就返回。
    /// 轮询 500ms —— 对「加载要几分钟」的场景足够快，开销可忽略。
    /// </summary>
    private static async Task WatchClientAsync(TcpClient client, CancellationTokenSource cancel)
    {
        try
        {
            while (true)
            {
                await Task.Delay(500, cancel.Token);
                if (cancel.Token.IsCancellationRequested) return;
                try
                {
                    if (client.Client == null || !client.Connected) { cancel.Cancel(); return; }
                    if (client.Client.Poll(0, SelectMode.SelectRead) && client.Client.Available == 0)
                    {
                        cancel.Cancel();     // 对端关闭
                        return;
                    }
                }
                catch (ObjectDisposedException) { return; }
                catch
                {
                    try { cancel.Cancel(); } catch { }
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private async Task<(int status, long bytes, long pt, long ct)> ProxyAsync(NetworkStream client, BufStream reader, Dictionary<string, string> headers,
                                       string method, string target, byte[] body, CancellationToken ct)
    {
        long bytesOut = 0;
        using var back = new TcpClient();
        try { await back.ConnectAsync(IPAddress.Loopback, _eng.BackendPort, ct); }
        catch (Exception ex)
        {
            await WriteJsonAsync(client, 502, ErrorJson("连不上本地引擎: " + ex.Message), ct);
            return (502, 0, 0, 0);
        }

        back.NoDelay = true;
        var bs = back.GetStream();

        var sb = new StringBuilder();
        sb.Append(method).Append(' ').Append(target).Append(" HTTP/1.1\r\n");
        sb.Append("Host: 127.0.0.1:").Append(_eng.BackendPort).Append("\r\n");
        foreach (var h in headers)
        {
            var k = h.Key.ToLowerInvariant();
            if (k is "host" or "connection" or "authorization" or "content-length" or "transfer-encoding" or "accept-encoding" or "x-api-key") continue;
            sb.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
        }
        sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        sb.Append("Accept-Encoding: identity\r\n");
        sb.Append("Connection: close\r\n\r\n");

        await bs.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);
        if (body.Length > 0) await bs.WriteAsync(body, ct);
        await bs.FlushAsync(ct);

        var buf = new byte[64 * 1024];
        int status = 0;
        var headProbe = new MemoryStream();
        bool headParsed = false;
        var tail = new TailCheck();

        while (true)
        {
            int n;
            try { n = await bs.ReadAsync(buf, 0, buf.Length, ct); }
            catch { break; }
            if (n <= 0) break;
            tail.Add(buf, n);

            if (!headParsed)
            {
                headProbe.Write(buf, 0, n);
                // 响应头第一行固定 "HTTP/x.x NNN"，状态码在字节 9-11（ASCII 数字）。
                // 手写读字节：比 Regex 快，省一次 ASCII 字符串分配 + 正则匹配（GC 降压）。
                if (headProbe.Length >= 12)
                {
                    var probe = headProbe.ToArray();
                    if (probe[9] >= (byte)'1' && probe[9] <= (byte)'9' &&
                        probe[10] >= (byte)'0' && probe[10] <= (byte)'9' &&
                        probe[11] >= (byte)'0' && probe[11] <= (byte)'9')
                    {
                        status = (probe[9] - '0') * 100 + (probe[10] - '0') * 10 + (probe[11] - '0');
                        headParsed = true;
                    }
                    else if (headProbe.Length > 4096) headParsed = true;   // 兜底：异常头防死循环
                }
            }

            try
            {
                await client.WriteAsync(buf, 0, n, ct);
                // 不再每包 FlushAsync：NoDelay=true 已保证低延迟，逐包 flush 反而多 N 次 syscall，
                // 高 token 速率下白给延迟。客户端读空后自然阻塞，不需要显式 flush。
                // 早期版本在这里显式 FlushAsync，实测对首 token 延迟有可测影响。
            }
            catch { break; }
            bytesOut += n;
        }

        if (status == 0) status = 200;
        using var _ = reader; // 客户端连接即将关闭
        var (pt, ctok) = TokenScan.Parse(tail.Text());
        return (status, bytesOut, pt, ctok);
    }

    // ---------- 辅助 ----------

    private static async Task<byte[]> ReadBodyAsync(BufStream reader, Dictionary<string, string> headers, CancellationToken ct)
    {
        if (headers.TryGetValue("Content-Length", out var cl) && long.TryParse(cl, out var len) && len > 0)
        {
            if (len > 64L * 1024 * 1024) throw new InvalidDataException("请求体过大");
            return await reader.ReadExactAsync((int)len, ct);
        }
        if (headers.TryGetValue("Transfer-Encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            using var ms = new MemoryStream();
            while (true)
            {
                var sizeLine = await reader.ReadLineAsync(ct);
                if (sizeLine == null) break;
                int semi = sizeLine.IndexOf(';');
                if (semi >= 0) sizeLine = sizeLine[..semi];
                if (!int.TryParse(sizeLine.Trim(), System.Globalization.NumberStyles.HexNumber, null, out int chunk) || chunk < 0) break;
                if (chunk == 0)
                {
                    while (true) { var t = await reader.ReadLineAsync(ct); if (string.IsNullOrEmpty(t)) break; }
                    break;
                }
                var data = await reader.ReadExactAsync(chunk, ct);
                ms.Write(data, 0, data.Length);
                await reader.ReadLineAsync(ct);
                if (ms.Length > 64L * 1024 * 1024) break;
            }
            return ms.ToArray();
        }
        return Array.Empty<byte>();
    }

    private static readonly Regex ModelRe = new(@"""model""\s*:\s*""([^""]{1,300})""", RegexOptions.Compiled);

    private static string ProbeModel(byte[] body, Dictionary<string, string> headers)
    {
        if (body.Length == 0) return null;
        try
        {
            var s = Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 65536));
            var m = ModelRe.Match(s);
            if (m.Success) return m.Groups[1].Value;
        }
        catch { }
        return null;
    }

    private string BuildModelsJson()
    {
        var arr = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (_eng.Loaded != null)
        {
            arr.Add(new JsonObject { ["id"] = _eng.Loaded.Id, ["object"] = "model", ["created"] = created, ["owned_by"] = "cnm", ["cnm_loaded"] = true });
            seen.Add(_eng.Loaded.Id);
        }
        foreach (var m in _lib.Models)
        {
            if (seen.Contains(m.Id)) continue;
            seen.Add(m.Id);
            arr.Add(new JsonObject { ["id"] = m.Id, ["object"] = "model", ["created"] = created, ["owned_by"] = "cnm", ["cnm_size"] = m.SizeBytes, ["cnm_quant"] = m.Quant });
        }

        // 云端上游的模型也列出来 —— agent 里挑模型时能直接看到"网上的那些"
        foreach (var up in Upstreams.Snapshot())
        {
            if (!up.Cfg.Enabled || !up.Ok) continue;
            foreach (var id in up.Models)
            {
                if (seen.Contains(id)) continue;
                seen.Add(id);
                arr.Add(new JsonObject
                {
                    ["id"] = id,
                    ["object"] = "model",
                    ["created"] = created,
                    ["owned_by"] = "cnm-upstream:" + up.Cfg.Name,
                    ["cnm_upstream"] = up.Cfg.Name
                });
            }
        }
        var root = new JsonObject { ["object"] = "list", ["data"] = arr };
        return root.ToJsonString();
    }

    private string StatusJson()
    {
        var o = new JsonObject
        {
            ["service"] = "CNM",
            ["running"] = Running,
            ["port"] = Port,
            ["lan"] = _s.LanExposed,
            ["requests"] = RequestCount,
            ["model"] = _eng.Loaded?.Id,
            ["engineRunning"] = _eng.IsRunning,
            ["loading"] = _eng.IsLoading,
            ["vramMiB"] = Math.Round(_eng.VramMiB),
            ["idleSeconds"] = _eng.IdleSeconds,
            ["idleUnloadMinutes"] = _s.IdleUnloadMinutes,
            ["engineLog"] = _eng.LogPath
        };
        return o.ToJsonString();
    }

    private static string ErrorJson(string msg, string type = "cnm_error")
    {
        var o = new JsonObject { ["error"] = new JsonObject { ["message"] = msg, ["type"] = type } };
        return o.ToJsonString();
    }

    /// <summary>浏览器打开服务地址时看到的一页说明。不含 key，局域网暴露也安全。</summary>
    private string LandingHtml(string host)
    {
        string model = _eng.Loaded?.Id ?? "（还没加载，来请求时按需加载）";
        string vram = _eng.VramMiB > 0 ? $"{_eng.VramMiB / 1024:0.0} GB" : "—";
        string state = _eng.IsLoading ? "正在加载模型…" : _eng.IsRunning ? "模型在显存里" : "空闲，没占显存";
        string auto = _s.IdleUnloadMinutes > 0
            ? $"空闲 {_s.IdleUnloadMinutes} 分钟没人调用会自动释放显存"
            : "不自动释放显存";

        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>CNM · 服务运行中</title><link rel=\"icon\" href=\"/favicon.ico\"><style>");
        sb.Append("body{margin:0;background:#0e1014;color:#e7ebf3;font:14px/1.75 'Microsoft YaHei UI',system-ui,sans-serif}");
        sb.Append(".w{max-width:760px;margin:7vh auto;padding:0 22px}");
        sb.Append("h1{font-size:21px;margin:0 0 6px;font-weight:600}");
        sb.Append(".ok{color:#35c98a}.mut{color:#8a93a6;font-size:12.5px}");
        sb.Append("table{border-collapse:collapse;width:100%}");
        sb.Append("td{border-bottom:1px solid #242b38;padding:8px 0;vertical-align:top}");
        sb.Append("tr:last-child td{border-bottom:0}");
        sb.Append("td:first-child{color:#8a93a6;width:124px}");
        sb.Append("code{background:#1b202a;border:1px solid #242b38;border-radius:7px;padding:2px 7px;font-size:12.5px;word-break:break-all}");
        sb.Append("pre{margin:6px 0 0;white-space:pre-wrap;word-break:break-all}");
        sb.Append(".card{background:#141821;border:1px solid #242b38;border-radius:14px;padding:16px 20px;margin-top:16px}");
        sb.Append("a{color:#5b8cff}");
        sb.Append("</style></head><body><div class=\"w\">");

        sb.Append("<h1><span class=\"ok\">●</span> CNM 正在运行</h1>");
        sb.Append("<div class=\"mut\">这是 OpenAI 兼容 API 的地址，给程序（agent、编辑器、脚本）调用的，不是网页界面 —— 界面在 CNM 窗口里。</div>");

        sb.Append("<div class=\"card\"><table>");
        Row(sb, "Base URL", "http://" + H(host) + "/v1");
        Row(sb, "可用接口", "GET /v1/models · POST /v1/chat/completions · POST /v1/completions · POST /v1/embeddings");
        Row(sb, "API Key", "在 CNM 窗口 → 服务与日志 → 哨兵 里看（故意不在这页显示，免得局域网里被人看到）");
        Row(sb, "鉴权", "请求头 <code>Authorization: Bearer &lt;key&gt;</code>，或者 <code>x-api-key: &lt;key&gt;</code>");
        Row(sb, "当前模型", H(model) + " · " + H(state));
        Row(sb, "显存", H(vram) + " · " + H(auto));
        Row(sb, "探活", "<a href=\"/health\">/health</a>（免 key，可直接打开）");

        var ups = Upstreams.Snapshot();
        if (ups.Count > 0 || _s.Upstreams.Count > 0)
        {
            var sb2 = new StringBuilder();
            foreach (var u in _s.Upstreams)
            {
                var info = ups.FirstOrDefault(x => string.Equals(x.Cfg.Name, u.Name, StringComparison.OrdinalIgnoreCase));
                if (sb2.Length > 0) sb2.Append(" · ");
                sb2.Append(H(u.Name)).Append(u.Enabled ? "" : "（停用）");
                if (info != null && info.Ok) sb2.Append("：").Append(info.Models.Count).Append(" 个模型");
                else if (info != null && info.Error.Length > 0) sb2.Append("：连不上");
            }
            var def = (_s.UpstreamDefault ?? "").Trim();
            Row(sb, "云端上游", sb2.ToString() + (def.Length > 0 ? " · 默认转 <b>" + H(def) + "</b>" : ""));
        }
        sb.Append("</table></div>");

        sb.Append("<div class=\"card\"><div class=\"mut\">本机试一下（把 key 换成界面里那串，模型名用 /v1/models 里的 Id）：</div><pre><code>curl http://");
        sb.Append(H(host));
        sb.Append("/v1/chat/completions \\\n  -H \"Authorization: Bearer sk-chem-…\" -H \"Content-Type: application/json\" \\\n  -d '{\"model\":\"&lt;模型Id&gt;\",\"messages\":[{\"role\":\"user\",\"content\":\"你好\"}]}'</code></pre></div>");

        sb.Append("<div class=\"mut\" style=\"margin-top:16px\">CNM — Chemingitch China Model · 本机 GGUF 转发 · 按需加载，空闲自动放显存</div>");
        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    private static void Row(StringBuilder sb, string k, string v)
        => sb.Append("<tr><td>").Append(k).Append("</td><td>").Append(v).Append("</td></tr>");

    private static string H(string s) => (s ?? "")
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static byte[] _favicon;

    /// <summary>浏览器打开服务地址时页签上的小图标：用户自定义了图标就用它，否则画一个 CNM 标记。</summary>
    private static byte[] FaviconBytes()
    {
        if (_favicon != null) return _favicon;
        try
        {
            if (Branding.HasUserIcon && File.Exists(Branding.UserPngPath))
                _favicon = File.ReadAllBytes(Branding.UserPngPath);
            else
            {
                using var bmp = Branding.Render(32);
                using var ms = new MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                _favicon = ms.ToArray();
            }
        }
        catch { _favicon = Array.Empty<byte>(); }
        return _favicon;
    }

    private static async Task WriteJsonAsync(NetworkStream ns, int status, string json, CancellationToken ct)
        => await WriteTextAsync(ns, status, "application/json; charset=utf-8", json, ct);

    private static async Task WriteBytesAsync(NetworkStream ns, int status, string ctype, byte[] body, CancellationToken ct)
    {
        var head = $"HTTP/1.1 {status} {Reason(status)}\r\n" +
                   $"Content-Type: {ctype}\r\n" +
                   $"Content-Length: {body.Length}\r\n" +
                   "Cache-Control: max-age=86400\r\n" +
                   "Access-Control-Allow-Origin: *\r\n" +
                   "Connection: close\r\n\r\n";
        await ns.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        if (body.Length > 0) await ns.WriteAsync(body, ct);
        await ns.FlushAsync(ct);
    }

    private static async Task WriteTextAsync(NetworkStream ns, int status, string ctype, string text, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(text);
        var head = $"HTTP/1.1 {status} {Reason(status)}\r\n" +
                   $"Content-Type: {ctype}\r\n" +
                   $"Content-Length: {body.Length}\r\n" +
                   "Access-Control-Allow-Origin: *\r\n" +
                   "Access-Control-Allow-Headers: *\r\n" +
                   "Access-Control-Allow-Methods: GET,POST,OPTIONS\r\n" +
                   "Connection: close\r\n\r\n";
        await ns.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        if (body.Length > 0) await ns.WriteAsync(body, ct);
        await ns.FlushAsync(ct);
    }

    private static string Reason(int s) => s switch
    {
        200 => "OK", 204 => "No Content", 400 => "Bad Request", 401 => "Unauthorized",
        404 => "Not Found", 429 => "Too Many Requests", 499 => "Client Closed Request",
        500 => "Internal Server Error", 502 => "Bad Gateway", 503 => "Service Unavailable",
        _ => "OK"
    };

    private static string RemoteText(TcpClient c)
    {
        try { return ((IPEndPoint)c.Client.RemoteEndPoint).Address.ToString(); }
        catch { return "?"; }
    }

    private void Emit(string s) => Log?.Invoke(s);

    /// <summary>带缓冲的 HTTP 读取器</summary>
    private sealed class BufStream : IDisposable
    {
        private readonly Stream _s;
        private readonly byte[] _buf = new byte[32 * 1024];
        private int _pos, _len;

        public BufStream(Stream s) { _s = s; }

        private async Task<int> AvailableAsync(CancellationToken ct)
        {
            if (_pos >= _len)
            {
                _pos = 0;
                _len = await _s.ReadAsync(_buf, 0, _buf.Length, ct);
                if (_len < 0) _len = 0;
            }
            return _len - _pos;
        }

        public async Task<byte[]> ReadExactAsync(int n, CancellationToken ct)
        {
            var outb = new byte[n];
            int got = 0;
            while (got < n)
            {
                int avail = await AvailableAsync(ct);
                if (avail <= 0) throw new EndOfStreamException();
                int take = Math.Min(n - got, avail);
                Array.Copy(_buf, _pos, outb, got, take);
                _pos += take; got += take;
            }
            return outb;
        }

        public async Task<string> ReadLineAsync(CancellationToken ct)
        {
            using var ms = new MemoryStream(256);
            while (true)
            {
                int avail = await AvailableAsync(ct);
                if (avail <= 0) return ms.Length == 0 ? null : Encoding.UTF8.GetString(ms.ToArray());
                while (_pos < _len)
                {
                    byte b = _buf[_pos++];
                    if (b == (byte)'\n')
                    {
                        var arr = ms.ToArray();
                        int len = arr.Length;
                        if (len > 0 && arr[len - 1] == (byte)'\r') len--;
                        return Encoding.UTF8.GetString(arr, 0, len);
                    }
                    ms.WriteByte(b);
                    if (ms.Length > 1 << 20) throw new InvalidDataException("请求行过长");
                }
            }
        }

        public void Dispose() { }
    }
}
