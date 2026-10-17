using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CNM.Core;

public sealed class HfFile
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Quant { get; set; } = "";
    public bool IsMmproj { get; set; }
    public string SizeText => Size <= 0 ? "?"
        : Size >= 1073741824L ? (Size / 1073741824.0).ToString("0.00") + " GB"
        : (Size / 1048576.0).ToString("0") + " MB";
}

/// <summary>HuggingFace（镜像）客户端：搜索 / 看文件 / 下载（断点续传）</summary>
public sealed class HfClient
{
    private readonly HttpClient _api = new() { Timeout = TimeSpan.FromSeconds(45) };
    private readonly HttpClient _dl = new() { Timeout = Timeout.InfiniteTimeSpan };

    public string Endpoint { get; set; }

    public HfClient(string endpoint)
    {
        Endpoint = endpoint.TrimEnd('/');
        _api.DefaultRequestHeaders.UserAgent.ParseAdd("CNM/0.1");
        _dl.DefaultRequestHeaders.UserAgent.ParseAdd("CNM/0.1");
    }

    /// <summary>取文本：失败重试一次（镜像偶尔抽风），别把一次网络抖动当成"没有结果"</summary>
    private async Task<string> GetTextAsync(string url, string what, CancellationToken ct)
    {
        Exception last = null;
        for (int i = 0; i < 2; i++)
        {
            try { return await _api.GetStringAsync(url, ct); }
            catch (Exception ex)
            {
                last = ex;
                if (i == 0) { try { await Task.Delay(400, ct); } catch { } }
            }
        }
        throw new InvalidOperationException($"{what}失败：{last?.Message}", last);
    }

    /// <summary>解析 JSON；不是 JSON 或带 error 字段就明确抛出来，不要静默变成空列表</summary>
    private static JsonNode ParseJson(string txt, string what)
    {
        JsonNode node = null;
        try { node = JsonNode.Parse(txt); } catch { }
        if (node == null) throw new InvalidOperationException($"{what}失败：服务器返回的不是 JSON（{Snippet(txt)}）");
        if (node is JsonObject o && o["error"] != null)
            throw new InvalidOperationException($"{what}失败：{o["error"]}");
        return node;
    }

    private static string Snippet(string s) =>
        string.IsNullOrEmpty(s) ? "空响应" : (s.Length <= 140 ? s.Trim() : s.Substring(0, 140).Trim() + "…");

    public async Task<JsonArray> SearchAsync(string query, string sort, int limit, CancellationToken ct)
    {
        var url = $"{Endpoint}/api/models?filter=gguf&limit={limit}&direction=-1&sort={Uri.EscapeDataString(sort)}";
        if (!string.IsNullOrWhiteSpace(query)) url += "&search=" + Uri.EscapeDataString(query.Trim());
        var txt = await GetTextAsync(url, "搜索", ct);
        if (ParseJson(txt, "搜索") is not JsonArray arr)
            throw new InvalidOperationException($"搜索失败：服务器返回的不是列表（{Snippet(txt)}）");
        return arr;
    }

    public async Task<JsonObject> RepoAsync(string repoId, CancellationToken ct)
    {
        var txt = await GetTextAsync($"{Endpoint}/api/models/{repoId}?blobs=true", "读取仓库", ct);
        if (ParseJson(txt, "读取仓库") is not JsonObject o)
            throw new InvalidOperationException($"读取仓库失败：返回的不是仓库信息（{Snippet(txt)}）");
        return o;
    }

    public static List<HfFile> FilesOf(JsonObject repo)
    {
        var list = new List<HfFile>();
        if (repo?["siblings"] is not JsonArray sib) return list;
        foreach (var s in sib)
        {
            var name = s?["rfilename"]?.GetValue<string>();
            if (string.IsNullOrEmpty(name) || !name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) continue;
            long size = 0;
            if (s["lfs"] is JsonObject lfs && lfs["size"] != null) size = (long)lfs["size"];
            else if (s["size"] != null) size = (long)s["size"];
            var fn = name.Split('/')[^1];
            list.Add(new HfFile
            {
                Path = name,
                Size = size,
                Quant = ModelLibrary.GuessQuant(fn),
                IsMmproj = fn.StartsWith("mmproj", StringComparison.OrdinalIgnoreCase)
            });
        }
        list.Sort((a, b) =>
        {
            if (a.IsMmproj != b.IsMmproj) return a.IsMmproj ? 1 : -1;
            return string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        });
        return list;
    }

    public string ResolveUrl(string repoId, string file) =>
        $"{Endpoint}/{repoId}/resolve/main/{Uri.EscapeDataString(file).Replace("%2F", "/")}?download=true";

    /// <summary>下载到目标路径，支持 .part 断点续传</summary>
    public async Task DownloadAsync(string url, string dest, Action<long, long, double> onProgress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var part = dest + ".part";
        long existing = File.Exists(part) ? new FileInfo(part).Length : 0;

        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0) req.Headers.Range = new RangeHeaderValue(existing, null);
        using var resp = await _dl.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (existing > 0 && resp.StatusCode != HttpStatusCode.PartialContent) existing = 0;
        resp.EnsureSuccessStatusCode();

        long total = (resp.Content.Headers.ContentLength ?? 0) + existing;
        using var fs = new FileStream(part, existing > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var src = await resp.Content.ReadAsStreamAsync(ct);

        var buf = new byte[1 << 20];
        long done = existing, lastB = existing;
        double lastT = 0;
        var sw = Stopwatch.StartNew();
        long lastByteTick = Environment.TickCount64;
        using var stall = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stall.Token);
        var wt = linked.Token;
        // 卡住检测：连着 120 秒一个字节都没来（上游把连接吊着不放）就认输，
        // 否则两条死连接会把整个下载队列（只有 2 个槽位）永久占死。
        var watchdog = Task.Run(async () =>
        {
            while (!linked.IsCancellationRequested)
            {
                try { await Task.Delay(5000, linked.Token); } catch { return; }
                if (Environment.TickCount64 - Interlocked.Read(ref lastByteTick) > 120_000)
                {
                    try { stall.Cancel(); } catch { }
                    return;
                }
            }
        });
        onProgress?.Invoke(done, total, 0);

        try
        {
            while (true)
            {
                int n = await src.ReadAsync(buf, 0, buf.Length, wt);
                if (n <= 0) break;
                await fs.WriteAsync(buf, 0, n, wt);
                done += n;
                Interlocked.Exchange(ref lastByteTick, Environment.TickCount64);
                double el = sw.Elapsed.TotalSeconds;
                if (el - lastT > 0.4)
                {
                    double speed = (done - lastB) / Math.Max(0.001, el - lastT);
                    lastT = el; lastB = done;
                    onProgress?.Invoke(done, total, speed);
                }
            }
        }
        catch (OperationCanceledException) when (stall.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new IOException("下载卡住了（120 秒没有新数据）。断点已经留着，再点一次会接着下。");
        }
        fs.Flush(); fs.Close();

        if (File.Exists(dest)) File.Delete(dest);
        File.Move(part, dest);
        onProgress?.Invoke(done, total, 0);
    }
}

public sealed class DownloadJob
{
    public string Key { get; set; } = "";
    public string Repo { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Url { get; set; } = "";
    public string Dest { get; set; } = "";
    public long Done { get; set; }
    public long Total { get; set; }
    public double Speed { get; set; }
    public string Status { get; set; } = "queued"; // queued running done error canceled
    public string Error { get; set; } = "";
    public CancellationTokenSource Cts { get; set; }

    public int Percent => Total > 0 ? (int)Math.Min(100, Done * 100 / Total) : 0;
}

public sealed class DownloadManager
{
    private readonly HfClient _hf;
    private readonly SemaphoreSlim _gate = new(2);
    private readonly List<DownloadJob> _jobs = new();
    private readonly object _lock = new();

    public event Action StateChanged;

    public DownloadManager(HfClient hf) { _hf = hf; }

    public List<DownloadJob> Jobs
    {
        get { lock (_lock) return _jobs.ToList(); }
    }

    public DownloadJob Enqueue(string url, string dest, string repo, string fileName)
    {
        DownloadJob j;
        lock (_lock)
        {
            var exist = _jobs.FirstOrDefault(x => x.Dest.Equals(dest, StringComparison.OrdinalIgnoreCase));
            if (exist != null && (exist.Status == "running" || exist.Status == "queued")) return exist;
            j = new DownloadJob
            {
                Key = dest,
                Url = url,
                Dest = dest,
                Repo = repo,
                FileName = fileName,
                Cts = new CancellationTokenSource()
            };
            _jobs.Add(j);
        }
        StateChanged?.Invoke();
        _ = Task.Run(() => RunAsync(j));
        return j;
    }

    public void Cancel(string key)
    {
        DownloadJob j;
        lock (_lock) j = _jobs.FirstOrDefault(x => x.Key == key);
        try { j?.Cts?.Cancel(); } catch { }
    }

    public void ClearFinished()
    {
        lock (_lock) _jobs.RemoveAll(x => x.Status is "done" or "error" or "canceled");
        StateChanged?.Invoke();
    }

    private async Task RunAsync(DownloadJob j)
    {
        await _gate.WaitAsync();
        try
        {
            j.Status = "running";
            StateChanged?.Invoke();
            await _hf.DownloadAsync(j.Url, j.Dest, (done, total, speed) =>
            {
                j.Done = done; j.Total = total; j.Speed = speed;
                StateChanged?.Invoke();
            }, j.Cts.Token);
            j.Status = "done";
            j.Speed = 0;
        }
        catch (OperationCanceledException) { j.Status = "canceled"; j.Error = "已取消"; }
        catch (Exception ex) { j.Status = "error"; j.Error = ex.Message; }
        finally
        {
            _gate.Release();
            StateChanged?.Invoke();
        }
    }

    public static bool LooksLikeRepoUrl(string url, out string repoId)
    {
        repoId = null;
        var m = Regex.Match(url, @"^https?://[^/]+/([^/\s]+/[^/\s?#]+?)(?:/tree/[^/]+)?/?$");
        if (!m.Success) return false;
        var id = m.Groups[1].Value;
        if (id.Contains(".")) return false; // 像文件名，不是仓库
        repoId = id;
        return true;
    }
}
