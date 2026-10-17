using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;

namespace CNM.Core;

/// <summary>
/// 「谁家的」真 logo —— 能拿到真商标就用真的，拿不到才退回首字母色块。
///
/// 两个来源（都走国内真能通的线路，不需要用户会翻墙）：
///   1) <b>simple-icons</b>（jsdelivr 直连）：各家的官方品牌 SVG —— Qwen / DeepSeek / OpenAI / Claude /
///      Google / Meta / Microsoft / Kimi / MiniMax / NVIDIA … 拉回来就是真 logo（单色路径，界面里染色）。
///   2) <b>HF 头像</b>：给没有现成图标的作者用（unsloth / bartowski / TheBloke…）。先用 hf-mirror 的
///      <c>/api/{users|organizations}/{name}/avatar</c> 问出图地址，再取字节：直连不通就借本机代理
///      （和上游那套「自动检测」一个思路：直连 → 系统代理 → 本机常见端口，谁先用谁）。
///
/// 取回来的图做磁盘缓存（%APPDATA%\CNM\logos），第二次开程序不再联网；取不到的本次会话不再重试。
/// </summary>
public static class BrandLogos
{
    private static readonly string Dir = Path.Combine(Settings.DataDir, "logos");
    private static readonly HttpClient Direct = Make("");
    private static readonly ConcurrentDictionary<string, HttpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Task<string>> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, bool> _miss = new(StringComparer.OrdinalIgnoreCase);
    private static string _proxyKnown;          // null = 还没探过；"" = 直连就行；其它 = 上次成功的代理

    /// <summary>
    /// items: [{ key: "icon:qwen" | "avatar:unsloth", kind: "icon"|"avatar", name: "qwen" | "unsloth" }]
    /// 返回 { ok, logos: { key: "data:image/...;base64,…" } }（拿不到的就不在里面，界面自己退回首字母）
    /// </summary>
    public static async Task<JsonObject> FetchAsync(JsonArray items, string hfEndpoint)
    {
        var outp = new JsonObject();
        var jobs = new List<Task<string>>();
        var keys = new List<string>();
        int n = 0;
        foreach (var it in items ?? new JsonArray())
        {
            if (n++ >= 40) break;                       // 一次最多问 40 个，别让界面为了一张图等太久
            var key = it?["key"]?.GetValue<string>() ?? "";
            var kind = it?["kind"]?.GetValue<string>() ?? "icon";
            var name = it?["name"]?.GetValue<string>() ?? "";
            if (key.Length == 0 || name.Length == 0) continue;

            var cached = ReadCache(key);
            if (cached != null) { outp[key] = cached; continue; }
            if (_miss.ContainsKey(key)) continue;
            keys.Add(key);
            jobs.Add(GetAsync(key, kind, name, hfEndpoint));
        }

        if (jobs.Count > 0)
        {
            var done = await Task.WhenAll(jobs);
            for (int i = 0; i < done.Length; i++)
            {
                if (!string.IsNullOrEmpty(done[i])) outp[keys[i]] = done[i];
                else _miss[keys[i]] = true;
            }
        }
        return new JsonObject { ["ok"] = true, ["logos"] = outp };
    }

    private static Task<string> GetAsync(string key, string kind, string name, string hf)
        => _inflight.GetOrAdd(key, _k => Task.Run(async () =>
        {
            try
            {
                var data = kind == "avatar" ? await AvatarAsync(name, hf) : await IconAsync(name);
                if (!string.IsNullOrEmpty(data)) { try { WriteCache(key, data); } catch { } }
                return data;
            }
            catch { return null; }
            finally { _inflight.TryRemove(key, out _); }
        }));

    /// <summary>simple-icons：官方品牌 SVG（先 jsdelivr，再 fastly 的镜像域名）</summary>
    private static async Task<string> IconAsync(string slug)
    {
        slug = (slug ?? "").Trim().ToLowerInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(slug, "^[a-z0-9._-]{1,60}$")) return null;
        foreach (var host in new[]
                 {
                     "https://cdn.jsdelivr.net/npm/simple-icons@latest/icons/",
                     "https://fastly.jsdelivr.net/npm/simple-icons@latest/icons/"
                 })
        {
            try
            {
                using var r = await Direct.GetAsync(host + slug + ".svg");
                if (!r.IsSuccessStatusCode) continue;
                var bytes = await r.Content.ReadAsByteArrayAsync();
                if (bytes.Length < 80 || bytes.Length > 200_000) continue;
                return "data:image/svg+xml;base64," + Convert.ToBase64String(bytes);
            }
            catch { }
        }
        return null;
    }

    /// <summary>HF 头像：hf-mirror 问地址（直连就行），图本身直连不通时借本机代理</summary>
    private static async Task<string> AvatarAsync(string name, string hf)
    {
        name = (name ?? "").Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9._-]{1,60}$")) return null;
        var root = string.IsNullOrWhiteSpace(hf) ? "https://hf-mirror.com" : hf.Trim().TrimEnd('/');

        string url = null;
        foreach (var kind in new[] { "users", "organizations" })
        {
            try
            {
                var txt = await Direct.GetStringAsync($"{root}/api/{kind}/{Uri.EscapeDataString(name)}/avatar");
                var u = JsonNode.Parse(txt)?["avatarUrl"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(u)) { url = u; break; }
            }
            catch { }
        }
        if (string.IsNullOrEmpty(url)) return null;

        // hf-mirror 会中转 /avatars/xxx.svg 这种路径；CDN 上的图直连不通，就借本机代理拿
        var viaMirror = url.Replace("https://huggingface.co/avatars/", root + "/avatars/");
        var img = await FetchImageAsync(viaMirror);
        if (img == null && viaMirror != url) img = await FetchImageAsync(url);
        return img == null ? null : img;
    }

    private static async Task<string> FetchImageAsync(string url)
    {
        // 直连 → 系统代理 → 本机常见端口，并发试，谁先拿回图就用谁（探到一次就记住）
        var cands = new List<string>();
        if (_proxyKnown != null) cands.Add(_proxyKnown);
        else cands.AddRange(new[]
        {
            "", "system",
            "http://127.0.0.1:7890", "http://127.0.0.1:7897", "http://127.0.0.1:10809",
            "socks5://127.0.0.1:7890", "socks5://127.0.0.1:1080"
        });

        var tasks = cands.Distinct().Select(p => TryImageAsync(url, p)).ToList();
        while (tasks.Count > 0)
        {
            var done = await Task.WhenAny(tasks);
            tasks.Remove(done);
            var (proxy, data) = await done;
            if (!string.IsNullOrEmpty(data)) { _proxyKnown = proxy; return data; }
        }
        return null;
    }

    private static async Task<(string proxy, string data)> TryImageAsync(string url, string proxy)
    {
        try
        {
            using var r = await Client(proxy).GetAsync(url);
            if (!r.IsSuccessStatusCode) return (proxy, null);
            var ct = r.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
            var bytes = await r.Content.ReadAsByteArrayAsync();
            if (bytes.Length < 64 || bytes.Length > 1_500_000) return (proxy, null);
            var mime = ct.StartsWith("image/") ? ct : Sniff(bytes);
            if (mime == null) return (proxy, null);
            return (proxy, "data:" + mime + ";base64," + Convert.ToBase64String(bytes));
        }
        catch { return (proxy, null); }
    }

    private static string Sniff(byte[] b)
    {
        if (b.Length > 5 && b[0] == '<') return "image/svg+xml";                        // <?xml / <svg
        if (b.Length > 4 && b[0] == 0x89 && b[1] == 0x50) return "image/png";
        if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8) return "image/jpeg";
        if (b.Length > 4 && b[0] == 0x47 && b[1] == 0x49) return "image/gif";
        if (b.Length > 12 && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "image/webp";
        return null;
    }

    private static HttpClient Client(string proxy)
    {
        if (string.IsNullOrEmpty(proxy)) return Direct;
        var p = UpstreamHub.NormalizeProxy(proxy);
        if (p.Length == 0 || p.Equals("auto", StringComparison.OrdinalIgnoreCase)) return Direct;
        return _clients.GetOrAdd(p, Make);
    }

    private static HttpClient Make(string proxy)
    {
        var p = UpstreamHub.NormalizeProxy(proxy);
        var h = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(4),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        if (p.Length == 0 || p.Equals("auto", StringComparison.OrdinalIgnoreCase)) { h.UseProxy = false; h.Proxy = null; }
        else if (p.Equals("system", StringComparison.OrdinalIgnoreCase)) h.UseProxy = true;
        else { h.UseProxy = true; h.Proxy = new WebProxy(p); }
        return new HttpClient(h) { Timeout = TimeSpan.FromSeconds(15) };
    }

    // ---- 磁盘缓存：一个键一个 .txt，里面就存整条 data URL ----

    private static string FileOf(string key)
    {
        var safe = new string(key.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '.' ? c : '-').ToArray()).ToLowerInvariant();
        if (safe.Length == 0) safe = "x";
        if (safe.Length > 80) safe = safe.Substring(0, 80);
        return Path.Combine(Dir, safe + ".txt");
    }

    private static string ReadCache(string key)
    {
        try
        {
            var f = FileOf(key);
            if (!File.Exists(f)) return null;
            var t = File.ReadAllText(f).Trim();
            return t.StartsWith("data:image/") ? t : null;
        }
        catch { return null; }
    }

    private static void WriteCache(string key, string data)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FileOf(key), data);
    }
}
