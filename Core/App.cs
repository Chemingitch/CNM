using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using CNM.UI;

namespace CNM.Core;

/// <summary>把设置 / 模型库 / 引擎 / 转发 / 下载 串起来，并处理界面过来的命令</summary>
public sealed class App
{
    public Settings Settings { get; private set; }
    public ModelLibrary Library { get; private set; }
    public EngineManager Engine { get; private set; }
    public OpenAiRouter Router { get; private set; }
    public HfClient Hf { get; private set; }
    public DownloadManager Downloads { get; private set; }

    public event Action<string> Send;
    public event Action<string, string> Toast;   // kind, message
    public event Action<string> ThemeChanged;
    public event Action IconChanged;

    private readonly List<RequestLog> _reqLog = new();
    public bool Scanning { get; private set; }

    public App()
    {
        Settings = Settings.Load();
        Library = new ModelLibrary(Settings);
        Hf = new HfClient(Settings.HfEndpoint);
        Downloads = new DownloadManager(Hf);
        Engine = new EngineManager(Settings, Library);
        Router = new OpenAiRouter(Settings, Engine, Library, (m, ct) => Engine.EnsureAsync(m, ct));

        Engine.Log += (src, line) => Push(new JsonObject { ["type"] = "log", ["source"] = src, ["line"] = line });
        Engine.StateChanged += () => PushStateThrottled();      // 高频（加载各阶段/停止）→ 合并 300ms
        Engine.Crashed += msg => Toast?.Invoke("error", msg);
        Router.Log += line => Push(new JsonObject { ["type"] = "log", ["source"] = "router", ["line"] = line });
        Router.Upstreams.Log += line => Push(new JsonObject { ["type"] = "log", ["source"] = "upstream", ["line"] = line });
        Router.StateChanged += () => PushStateThrottled();      // 高频（每个请求开始/结束）→ 合并
        Router.RequestLogged += l =>
        {
            lock (_reqLog)
            {
                _reqLog.Insert(0, l);
                while (_reqLog.Count > 300) _reqLog.RemoveAt(_reqLog.Count - 1);
            }
            Push(new JsonObject { ["type"] = "req", ["entry"] = ReqJson(l) });   // 增量，很轻，保持即时
        };
        Downloads.StateChanged += () =>
        {
            Push(new JsonObject { ["type"] = "downloads", ["jobs"] = JobsJson() });
            // 刚下完的文件得扫一遍才会出现在模型库里 —— 不然用户以为白下了
            try
            {
                bool anyDone = Downloads.Jobs.Any(j => j.Status == "done");
                if (anyDone && !_scannedAfterDownload)
                {
                    _scannedAfterDownload = true;
                    Task.Run(() => { try { Scan(); Push(new JsonObject { ["type"] = "models", ["models"] = ModelsJson() }); } catch { } });
                }
                else if (!anyDone) _scannedAfterDownload = false;
            }
            catch { }
        };
    }

    private volatile bool _scannedAfterDownload;

    private void Push(JsonObject o)
    {
        try
        {
            if (o["type"]?.GetValue<string>() == "log")
                AppendLogFile(o["source"]?.GetValue<string>() ?? "", o["line"]?.GetValue<string>() ?? "");
            Send?.Invoke(o.ToJsonString());
        }
        catch { }
    }

    /// <summary>日志同时落一份文件（%APPDATA%\CNM\logs\cnm.log），出问题时能直接翻，不用截图</summary>
    private static readonly object _logFileLock = new();

    public static string AppLogPath => Path.Combine(Settings.LogsDir, "cnm.log");

    private static int _appLogLines;
    private static void AppendLogFile(string source, string line)
    {
        try
        {
            lock (_logFileLock)
            {
                var f = AppLogPath;
                // 每 256 行才查一次轮转：FileInfo 查询也是系统调用，高频日志时白做
                if ((++_appLogLines & 255) == 0)
                {
                    var fi = new FileInfo(f);
                    if (fi.Exists && fi.Length > 4L * 1024 * 1024) File.Delete(f);   // 简单滚动：超 4MB 换新的
                }
                File.AppendAllText(f, $"{DateTime.Now:HH:mm:ss} [{source}] {line}{Environment.NewLine}", System.Text.Encoding.UTF8);
            }
        }
        catch { }
    }

    private void PushState() => Push(new JsonObject { ["type"] = "state", ["state"] = StateJson() });

    // ---- PushState 合并节流：高频触发（每个请求完成/显存变化）时不全量序列化轰炸 UI ----
    private int _stateDirty;                    // Interlocked 标志：有待推状态
    private int _statePushing;                  // 防重入

    /// <summary>延迟合并版 PushState：300ms 内多次触发只推一次（状态本来就是"最新快照"语义）。</summary>
    private void PushStateThrottled()
    {
        Interlocked.Exchange(ref _stateDirty, 1);
        if (Interlocked.CompareExchange(ref _statePushing, 1, 0) != 0) return;   // 已有人在推，走合并
        _ = Task.Run(async () =>
        {
            try
            {
                while (Interlocked.Exchange(ref _stateDirty, 1) == 1)
                {
                    Interlocked.Exchange(ref _stateDirty, 0);   // 取走标志
                    await Task.Delay(300);                       // 合并窗口
                    Push(new JsonObject { ["type"] = "state", ["state"] = StateJson() });
                }
            }
            catch { }
            finally { Interlocked.Exchange(ref _statePushing, 0); }
        });
    }

    public void StartUp()
    {
        _ = Task.Run(async () =>
        {
            Push(new JsonObject { ["type"] = "log", ["source"] = "cnm", ["line"] = "引擎日志文件：" + Engine.LogPath });
            await Engine.ProbeEngineAsync();
            PushState();
            Scan();

            // 云端上游：开机连一次，/v1/models 里就能直接看到网上的模型
            if (Settings.Upstreams.Count > 0) await Router.Upstreams.RefreshAllAsync(CancellationToken.None);

            if (Settings.AutoStartServer && !Router.Running) Router.Start();

            if (Settings.AutoLoadLastModel && !string.IsNullOrWhiteSpace(Settings.LastModelId))
            {
                var e = Library.Find(Settings.LastModelId);
                if (e != null) await Engine.StartAsync(e);
            }
            PushState();
        });

        // 后台小管家：每 3 秒量一次引擎真实显存占用；空闲够久就把模型卸掉、把显存还回去。
        // 这就是“按需打开”的另一半 —— 有人用才占显存，没人用自动放。
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try { await Task.Delay(3000); } catch { break; }
                if (Engine.PollVram()) PushStateThrottled();     // 显存变化每 3s 才量一次，但推还是合并的
                Router.Usage.SaveIfDirty();          // 用量统计：攒够 15 秒才真写盘
                if (Engine.TryIdleUnload())
                {
                    Toast?.Invoke("info", $"空闲 {Settings.IdleUnloadMinutes} 分钟，已卸载模型并释放显存（下次请求会自动加载）");
                    PushStateThrottled();
                }
            }
        });
    }

    public void Shutdown()
    {
        try { Router.Usage.Save(); } catch { }
        try { Router.Stop(); } catch { }
        try { Engine.Stop(); } catch { }
        try { Settings.Save(); } catch { }
    }

    // ---------- 状态 ----------

    public JsonObject StateJson()
    {
        var st = new JsonObject
        {
            ["engine"] = Engine.Status(),
            ["service"] = new JsonObject
            {
                ["running"] = Router.Running,
                ["port"] = Router.Port,
                ["lan"] = Settings.LanExposed,
                ["requests"] = Router.RequestCount,
                ["error"] = Router.LastError
            },
            ["settings"] = SettingsJson(),
            ["modelCount"] = Library.Models.Count,
            ["scanning"] = Scanning,
            ["ips"] = IpList(),
            ["usage"] = Router.Usage.Json(),
            // reqLog 有增量推送（type:"req"），全量列表只在前端主动调 state 时给：
            // 节流后的每次状态推送不用再拖着 300 条请求记录重建/序列化。
            ["reqLog"] = null
        };
        return st;
    }

    /// <summary>全量状态（带 reqLog）：只给前端的初始化调用和「state」命令用。</summary>
    public JsonObject FullStateJson()
    {
        var st = StateJson();
        st["reqLog"] = ReqLogJson();
        return st;
    }

    public JsonObject SettingsJson() => new()
    {
        ["modelsDir"] = Settings.ModelsDir,
        ["extraModelDirs"] = new JsonArray(Settings.ExtraModelDirs.Select(x => (JsonNode)JsonValue.Create(x)).ToArray()),
        ["apiKey"] = Settings.ApiKey,
        ["port"] = Settings.Port,
        ["lanExposed"] = Settings.LanExposed,
        ["autoStartServer"] = Settings.AutoStartServer,
        ["modelsNeedKey"] = Settings.ModelsNeedKey,
        ["autoLoadOnRequest"] = Settings.AutoLoadOnRequest,
        ["autoLoadLastModel"] = Settings.AutoLoadLastModel,
        ["idleUnloadMinutes"] = Settings.IdleUnloadMinutes,
        ["contextSize"] = Settings.ContextSize,
        ["gpuLayers"] = Settings.GpuLayers,
        ["threads"] = Settings.Threads,
        ["parallel"] = Settings.Parallel,
        ["flashAttn"] = Settings.FlashAttn,
        ["mmprojOnGpu"] = Settings.MmprojOnGpu,
        ["enginePath"] = Settings.EnginePath,
        ["engineMirror"] = Settings.EngineMirror,
        ["hfEndpoint"] = Settings.HfEndpoint,
        ["closeToTray"] = Settings.CloseToTray,
        ["theme"] = Settings.Theme,
        ["upstreams"] = new JsonArray(Settings.Upstreams.Select(u => (JsonNode)new JsonObject
        {
            ["name"] = u.Name,
            ["baseUrl"] = u.BaseUrl,
            ["apiKey"] = u.ApiKey,
            ["proxy"] = u.Proxy,
            ["proxyUsed"] = Router.Upstreams.Peek(u.Name)?.ProxyUsed ?? "",
            ["proxyDetail"] = Router.Upstreams.Peek(u.Name)?.ProxyDetail ?? "",
            ["enabled"] = u.Enabled,
            ["error"] = Router.Upstreams.Peek(u.Name)?.Error ?? "",
            ["models"] = Router.Upstreams.Peek(u.Name)?.Models.Count ?? 0,
            ["modelList"] = new JsonArray((Router.Upstreams.Peek(u.Name)?.Models ?? new List<string>())
                .Select(x => (JsonNode)JsonValue.Create(x)).ToArray())
        }).ToArray()),
        ["upstreamDefault"] = Settings.UpstreamDefault,
        ["upstreamModel"] = Settings.UpstreamModel,
        ["customIcon"] = Settings.HasUserIcon,
        ["iconVersion"] = Settings.IconVersion,
        ["lastModelId"] = Settings.LastModelId
    };

    private JsonArray ReqLogJson()
    {
        var arr = new JsonArray();
        lock (_reqLog) foreach (var l in _reqLog) arr.Add(ReqJson(l));
        return arr;
    }

    private static JsonObject ReqJson(RequestLog l) => new()
    {
        ["time"] = l.Time,
        ["remote"] = l.Remote,
        ["method"] = l.Method,
        ["path"] = l.Path,
        ["model"] = l.Model,
        ["upstream"] = l.Upstream,
        ["upModel"] = l.UpModel,
        ["status"] = l.Status,
        ["ms"] = l.Ms,
        ["bytes"] = l.Bytes
    };

    public JsonArray ModelsJson()
    {
        var arr = new JsonArray();
        foreach (var m in Library.Models)
        {
            arr.Add(new JsonObject
            {
                ["id"] = m.Id,
                ["name"] = m.Name,
                ["fileName"] = m.FileName,
                ["path"] = m.FilePath,
                ["dir"] = m.Dir,
                ["publisher"] = m.Publisher,
                ["repo"] = m.Repo,
                ["size"] = m.SizeBytes,
                ["sizeText"] = m.SizeText,
                ["quant"] = m.Quant,
                ["arch"] = m.Arch,
                ["ctx"] = m.ContextLength,
                ["vision"] = m.Vision,
                ["mmproj"] = m.MmprojPath,
                ["chat"] = m.HasChatTemplate,
                ["error"] = m.Error,
                ["loaded"] = Engine.Loaded != null && string.Equals(Engine.Loaded.Id, m.Id, StringComparison.OrdinalIgnoreCase)
            });
        }
        return arr;
    }

    private JsonArray JobsJson()
    {
        var arr = new JsonArray();
        foreach (var j in Downloads.Jobs)
        {
            arr.Add(new JsonObject
            {
                ["key"] = j.Key,
                ["repo"] = j.Repo,
                ["fileName"] = j.FileName,
                ["dest"] = j.Dest,
                ["done"] = j.Done,
                ["total"] = j.Total,
                ["percent"] = j.Percent,
                ["speed"] = Math.Round(j.Speed / 1048576.0, 2),
                ["status"] = j.Status,
                ["error"] = j.Error
            });
        }
        return arr;
    }

    private static JsonArray IpList()
    {
        var arr = new JsonArray();
        try
        {
            foreach (var a in Dns.GetHostAddresses(Dns.GetHostName()))
                if (a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                    arr.Add(a.ToString());
        }
        catch { }
        return arr;
    }

    // ---------- 命令 ----------

    public async Task<JsonNode> HandleAsync(JsonObject msg)
    {
        string cmd = msg["cmd"]?.GetValue<string>() ?? "";

        switch (cmd)
        {
            case "state":
                return FullStateJson();

            case "scan":
                return await Task.Run(() => { Scan(); return ModelsJson(); });

            case "models":
                return ModelsJson();

            case "load":
            {
                var id = msg["id"]?.GetValue<string>() ?? "";
                var e = Library.Find(id);
                if (e == null) return Err("模型库里找不到 " + id);
                var r = await Engine.StartAsync(e);
                if (r.Ok) { Settings.LastModelId = e.Id; Settings.Save(); }
                else Toast?.Invoke("error", r.Message);
                return new JsonObject { ["ok"] = r.Ok, ["message"] = r.Message };
            }

            case "unload":
                await Engine.StopAsync();       // 等退出放线程池，UI 不冻
                return new JsonObject { ["ok"] = true };

            case "openUrl":
            {
                var u = msg["url"]?.GetValue<string>() ?? "";
                if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); } catch { }
                }
                return new JsonObject { ["ok"] = true };
            }

            case "startServer":
                if (!Router.Running) Router.Start();
                return new JsonObject { ["ok"] = Router.Running, ["error"] = Router.LastError };

            case "stopServer":
                Router.Stop();
                return new JsonObject { ["ok"] = true };

            case "probeEngine":
                await Engine.ProbeEngineAsync();
                PushState();
                return Engine.Status();

            case "installEngine":
            {
                var variant = msg["variant"]?.GetValue<string>() ?? "vulkan";
                return await InstallEngineAsync(variant);
            }

            // 认显卡 + 建议后端（界面上一进设置就自动填好，省得用户猜）
            case "detectGpu":
            {
                var gpus = GpuInfo.Detect();
                var (rec, why) = GpuInfo.Recommend(gpus);
                var arr = new JsonArray();
                foreach (var g in gpus)
                {
                    arr.Add(new JsonObject
                    {
                        ["name"] = g.Name,
                        ["vendor"] = g.Vendor,
                        ["vramMiB"] = g.VramBytes > 0 ? (long)Math.Round(g.VramBytes / 1048576.0) : 0
                    });
                }
                return new JsonObject { ["ok"] = true, ["gpus"] = arr, ["recommend"] = rec, ["reason"] = why };
            }

            // 查最新引擎版本（只查不下）
            case "engineLatest":
            {
                var v = msg["variant"]?.GetValue<string>() ?? "vulkan";
                var l = await EngineInstaller.LatestAsync(v);
                if (l.error != null) return Err("查最新版本失败：" + l.error);
                return new JsonObject
                {
                    ["ok"] = true,
                    ["variant"] = v,
                    ["tag"] = l.tag,
                    ["name"] = l.name,
                    ["sizeMiB"] = Math.Round(l.size / 1048576.0),
                    ["current"] = Engine.EngineVersionText
                };
            }

            // ---- 云端上游（API 中转站）----
            case "testUpstream":
            {
                var cfg = new UpstreamCfg
                {
                    Name = (msg["name"]?.ToString() ?? "").Trim(),
                    BaseUrl = msg["baseUrl"]?.ToString() ?? "",
                    ApiKey = msg["apiKey"]?.ToString() ?? "",
                    Proxy = (msg["proxy"]?.ToString() ?? "").Trim(),
                    Enabled = true
                };
                if (cfg.Name.Length == 0) cfg.Name = "测试";
                var info = await Router.Upstreams.RefreshAsync(cfg, CancellationToken.None);
                PushState();
                return new JsonObject
                {
                    ["ok"] = info.Ok,
                    ["root"] = info.Root,
                    ["error"] = info.Error,
                    ["count"] = info.Models.Count,
                    ["models"] = new JsonArray(info.Models.Take(80).Select(x => (JsonNode)JsonValue.Create(x)).ToArray())
                };
            }

            case "useUpstreamModel":
            {
                // 「模型库 → 外地模型」里点接入：upstream/model 都填 = 所有请求都用这一个外地模型；
                // 传空串 = 取消接入（回到“按模型名走”）
                Settings.UpstreamDefault = (msg["upstream"]?.ToString() ?? "").Trim();
                Settings.UpstreamModel = (msg["model"]?.ToString() ?? "").Trim();
                Settings.Save();
                PushState();
                return SettingsJson();
            }

            case "refreshUpstreams":
                await Router.Upstreams.RefreshAllAsync(CancellationToken.None);
                PushState();
                return SettingsJson();

            case "detectUpstreamProxy":
            {
                // 自动检测：把「直连 / 系统代理 / 本机常见端口」并发试一遍，谁通用谁
                var cfg = new UpstreamCfg
                {
                    Name = (msg["name"]?.ToString() ?? "").Trim(),
                    BaseUrl = msg["baseUrl"]?.ToString() ?? "",
                    ApiKey = msg["apiKey"]?.ToString() ?? "",
                    Proxy = "auto",
                    Enabled = true
                };
                if (cfg.Name.Length == 0) cfg.Name = "检测";
                var d = await Router.Upstreams.DetectProxyAsync(cfg, CancellationToken.None);
                PushState();
                return new JsonObject
                {
                    ["ok"] = d.ok,
                    ["proxy"] = d.proxy,
                    ["count"] = d.models.Count,
                    ["detail"] = d.detail,
                    ["error"] = d.error
                };
            }

            case "saveUpstreams":
            {
                if (msg["upstreams"] is JsonArray ua) Settings.Upstreams = ParseUpstreams(ua);
                Settings.UpstreamDefault = (msg["upstreamDefault"]?.ToString() ?? Settings.UpstreamDefault).Trim();
                Settings.UpstreamModel = (msg["upstreamModel"]?.ToString() ?? Settings.UpstreamModel).Trim();
                Settings.Save();
                await Router.Upstreams.RefreshAllAsync(CancellationToken.None);
                PushState();
                return SettingsJson();
            }

            case "testUpstreamChat":
            {
                var (ok, detail) = await Router.Upstreams.ChatProbeAsync(new UpstreamCfg
                {
                    Name = (msg["name"]?.ToString() ?? "").Trim(),
                    BaseUrl = msg["baseUrl"]?.ToString() ?? "",
                    ApiKey = msg["apiKey"]?.ToString() ?? "",
                    Proxy = (msg["proxy"]?.ToString() ?? "").Trim(),
                    Enabled = true
                }, msg["model"]?.ToString() ?? "", CancellationToken.None);
                return new JsonObject { ["ok"] = ok, ["detail"] = detail };
            }

            case "installEngineZip":
            {
                string zip = msg["path"]?.GetValue<string>() ?? "";
                if (zip.Length == 0)
                {
                    using var dlg = new OpenFileDialog
                    {
                        Title = "选择 llama.cpp 的 Windows 构建 zip",
                        Filter = "llama.cpp 引擎包 (*.zip)|*.zip|所有文件 (*.*)|*.*"
                    };
                    if (dlg.ShowDialog() != DialogResult.OK)
                        return new JsonObject { ["ok"] = false, ["cancelled"] = true };
                    zip = dlg.FileName;
                }
                if (!File.Exists(zip)) return Err("找不到这个文件：" + zip);
                return await InstallEngineAsync("", zip);
            }

            // 设置页那个「选择目录…」按钮：弹系统的选择文件夹窗口，选中的路径回给界面填进输入框
            case "pickFolder":
            {
                var title = msg["title"]?.GetValue<string>() ?? "选择文件夹";
                var cur = msg["current"]?.GetValue<string>() ?? "";
                var picked = CNM.UI.FolderPicker.Pick(Form.ActiveForm, title, cur);
                return picked == null
                    ? new JsonObject { ["ok"] = false, ["cancelled"] = true }      // 用户取消，别报错
                    : new JsonObject { ["ok"] = true, ["path"] = picked };
            }

            case "saveSettings":
            {
                var s = msg["settings"] as JsonObject;
                if (s != null) ApplySettings(s);
                return new JsonObject { ["ok"] = true, ["settings"] = SettingsJson() };
            }

            case "setTheme":
            {
                var raw = msg["theme"]?.GetValue<string>() ?? "dark";
                Settings.Theme = string.Equals(raw, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
                Settings.Save();
                ThemeChanged?.Invoke(Settings.Theme);
                PushState();
                return new JsonObject { ["ok"] = true, ["theme"] = Settings.Theme };
            }

            case "setIcon":
            {
                var data = msg["data"]?.GetValue<string>() ?? "";
                int comma = data.IndexOf(',');
                if (comma < 0 || !data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    return Err("图片数据不完整，换一张试试");

                byte[] bytes;
                try { bytes = Convert.FromBase64String(data[(comma + 1)..]); }
                catch { return Err("图片解码失败，换一张试试"); }
                if (bytes.Length > 8 * 1024 * 1024) return Err("图片太大了（超过 8MB），先压一下");

                try
                {
                    using var bmp = Branding.Decode(bytes);
                    if (bmp.Width < 64 || bmp.Height < 64) return Err("图片太小了：至少 64×64，建议 256×256 以上");
                    Branding.SaveUserIcon(bmp);
                }
                catch (Exception ex) { return Err("这张图读不出来：" + ex.Message); }

                IconChanged?.Invoke();
                PushState();
                return new JsonObject { ["ok"] = true, ["iconVersion"] = Settings.IconVersion };
            }

            case "clearIcon":
                Branding.ClearUserIcon();
                IconChanged?.Invoke();
                PushState();
                return new JsonObject { ["ok"] = true };

            case "newKey":
                Settings.ApiKey = Settings.NewKey();
                Settings.Save();
                PushState();
                return new JsonObject { ["ok"] = true, ["apiKey"] = Settings.ApiKey };

            case "hfSearch":
            {
                var q = msg["q"]?.GetValue<string>() ?? "";
                var sort = msg["sort"]?.GetValue<string>() ?? "downloads";
                try
                {
                    var res = await Hf.SearchAsync(q, sort, 40, CancellationToken.None);
                    var arr = new JsonArray();
                    foreach (var r in res)
                    {
                        if (r is not JsonObject o) continue;
                        arr.Add(new JsonObject
                        {
                            ["id"] = o["id"]?.GetValue<string>(),
                            ["downloads"] = o["downloads"]?.GetValue<long>() ?? 0,
                            ["likes"] = o["likes"]?.GetValue<long>() ?? 0,
                            ["modified"] = o["lastModified"]?.GetValue<string>(),
                            ["pipeline"] = o["pipeline_tag"]?.GetValue<string>()
                        });
                    }
                    return new JsonObject { ["ok"] = true, ["results"] = arr };
                }
                catch (Exception ex) { return Err("搜索失败：" + ex.Message); }
            }

            case "hfFiles":
            {
                var repo = msg["repo"]?.GetValue<string>() ?? "";
                try
                {
                    var o = await Hf.RepoAsync(repo, CancellationToken.None);
                    var files = HfClient.FilesOf(o);
                    var arr = new JsonArray();
                    foreach (var f in files)
                        arr.Add(new JsonObject
                        {
                            ["path"] = f.Path,
                            ["size"] = f.Size,
                            ["sizeText"] = f.SizeText,
                            ["quant"] = f.Quant,
                            ["mmproj"] = f.IsMmproj
                        });
                    return new JsonObject { ["ok"] = true, ["repo"] = repo, ["files"] = arr };
                }
                catch (Exception ex) { return Err("读取仓库失败：" + ex.Message); }
            }

            case "logos":
            {
                var items = msg["items"] as JsonArray ?? new JsonArray();
                return await BrandLogos.FetchAsync(items, Settings.HfEndpoint);
            }

            case "download":
            {
                var repo = (msg["repo"]?.GetValue<string>() ?? "").Trim();
                // 仓库名来自网上（镜像 / 贴的链接）：顺手做个路径体检，
                // 别让 "..\..\Windows" 这种把 gguf 写到模型目录外面去。
                var flat = repo.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                var rootFull = Path.GetFullPath(Settings.ModelsDir);
                var upFull = repo.Length == 0 ? rootFull : Path.GetFullPath(Path.Combine(Settings.ModelsDir, flat));
                if (repo.Contains("..") || Path.IsPathRooted(flat) || !upFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                    return Err("仓库名不对劲，没敢往硬盘上写：" + repo);

                var files = msg["files"] as JsonArray ?? new JsonArray();
                int n = 0;
                foreach (var f in files)
                {
                    var name = f?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var dest = Path.Combine(upFull, Path.GetFileName(name));
                    Downloads.Enqueue(Hf.ResolveUrl(repo, name), dest, repo, Path.GetFileName(name));
                    n++;
                }
                return new JsonObject { ["ok"] = true, ["queued"] = n };
            }

            case "downloadUrl":
            {
                var url = msg["url"]?.GetValue<string>() ?? "";
                return await DownloadFromUrlAsync(url);
            }

            case "cancelDownload":
                Downloads.Cancel(msg["key"]?.GetValue<string>() ?? "");
                return new JsonObject { ["ok"] = true };

            case "clearDownloads":
                Downloads.ClearFinished();
                return new JsonObject { ["ok"] = true };

            case "deleteModel":
            {
                var id = msg["id"]?.GetValue<string>() ?? "";
                var e = Library.Find(id);
                if (e == null) return Err("找不到模型 " + id);
                if (Engine.Loaded != null && Engine.Loaded.FilePath == e.FilePath)
                    await Engine.StopAsync();                    // UI 不冻
                if (!IsUnderRoots(e.FilePath)) return Err("该文件不在模型目录内，拒绝删除。");
                try
                {
                    await Task.Run(() => File.Delete(e.FilePath));   // 几 GB 的文件删除也放线程池
                    Toast?.Invoke("ok", "已删除 " + e.FileName);
                    await Task.Run(() => Scan());
                    Push(new JsonObject { ["type"] = "models", ["models"] = ModelsJson() });
                    return new JsonObject { ["ok"] = true };
                }
                catch (Exception ex) { return Err("删除失败：" + ex.Message); }
            }

            case "openFolder":
            {
                var p = msg["path"]?.GetValue<string>() ?? Settings.ModelsDir;
                try
                {
                    if (File.Exists(p)) p = Path.GetDirectoryName(p);
                    if (!Directory.Exists(p)) return Err("目录不存在：" + p);
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + p + "\"") { UseShellExecute = true });
                    return new JsonObject { ["ok"] = true };
                }
                catch (Exception ex) { return Err(ex.Message); }
            }

            case "openLogFile":
            {
                try
                {
                    Directory.CreateDirectory(Settings.LogsDir);
                    var p = AppLogPath;
                    if (!File.Exists(p)) File.WriteAllText(p, "", System.Text.Encoding.UTF8);
                    Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
                    return new JsonObject { ["ok"] = true, ["path"] = p };
                }
                catch (Exception ex) { return Err(ex.Message); }
            }

            case "copy":
                return new JsonObject { ["ok"] = true };

            default:
                return Err("未知命令：" + cmd);
        }
    }

    private static JsonObject Err(string m) => new() { ["ok"] = false, ["message"] = m };

    private bool IsUnderRoots(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            foreach (var r in Library.Roots())
            {
                if (string.IsNullOrWhiteSpace(r)) continue;
                var rr = Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (full.StartsWith(rr, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { }
        return false;
    }

    public void Scan()
    {
        Scanning = true;
        PushState();
        try { Library.Scan(msg => Push(new JsonObject { ["type"] = "log", ["source"] = "scan", ["line"] = msg })); }
        catch { }
        Scanning = false;
        Push(new JsonObject { ["type"] = "models", ["models"] = ModelsJson() });
        PushState();
    }

    private void ApplySettings(JsonObject s)
    {
        string Str(string k, string cur) => s.TryGetPropertyValue(k, out var v) && v != null ? v.GetValue<string>() : cur;
        int Int(string k, int cur) => s.TryGetPropertyValue(k, out var v) && v != null && int.TryParse(v.ToString(), out var i) ? i : cur;
        bool Bool(string k, bool cur) => s.TryGetPropertyValue(k, out var v) && v != null && bool.TryParse(v.ToString(), out var b) ? b : cur;

        string oldDir = Settings.ModelsDir;
        int oldPort = Settings.Port;
        bool oldLan = Settings.LanExposed;

        Settings.ModelsDir = Str("modelsDir", Settings.ModelsDir).Trim();
        if (string.IsNullOrWhiteSpace(Settings.ModelsDir)) Settings.ModelsDir = Settings.DefaultModelsDir();
        Settings.ApiKey = Str("apiKey", Settings.ApiKey);
        Settings.Port = Int("port", Settings.Port);
        Settings.LanExposed = Bool("lanExposed", Settings.LanExposed);
        Settings.AutoStartServer = Bool("autoStartServer", Settings.AutoStartServer);
        Settings.ModelsNeedKey = Bool("modelsNeedKey", Settings.ModelsNeedKey);
        Settings.AutoLoadOnRequest = Bool("autoLoadOnRequest", Settings.AutoLoadOnRequest);
        Settings.AutoLoadLastModel = Bool("autoLoadLastModel", Settings.AutoLoadLastModel);
        Settings.IdleUnloadMinutes = Math.Max(0, Int("idleUnloadMinutes", Settings.IdleUnloadMinutes));
        Settings.ContextSize = Int("contextSize", Settings.ContextSize);
        Settings.GpuLayers = Int("gpuLayers", Settings.GpuLayers);
        Settings.Threads = Int("threads", Settings.Threads);
        Settings.Parallel = Int("parallel", Settings.Parallel);
        Settings.FlashAttn = Bool("flashAttn", Settings.FlashAttn);
        Settings.MmprojOnGpu = Bool("mmprojOnGpu", Settings.MmprojOnGpu);
        Settings.EnginePath = Str("enginePath", Settings.EnginePath);
        Settings.EngineMirror = Str("engineMirror", Settings.EngineMirror).Trim();
        Settings.HfEndpoint = Str("hfEndpoint", Settings.HfEndpoint);
        Settings.CloseToTray = Bool("closeToTray", Settings.CloseToTray);
        Settings.Theme = Str("theme", Settings.Theme);
        Settings.UpstreamDefault = Str("upstreamDefault", Settings.UpstreamDefault).Trim();
        Settings.UpstreamModel = Str("upstreamModel", Settings.UpstreamModel).Trim();

        // 云端上游：整份替换（界面那边就是这张表）
        if (s.TryGetPropertyValue("upstreams", out var ups) && ups is JsonArray ua)
            Settings.Upstreams = ParseUpstreams(ua);

        if (s.TryGetPropertyValue("extraModelDirs", out var ex) && ex is JsonArray ea)
        {
            Settings.ExtraModelDirs = ea.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList();
        }

        Settings.Save();
        Hf.Endpoint = Settings.HfEndpoint.TrimEnd('/');
        _ = Task.Run(() => Router.Upstreams.RefreshAllAsync(CancellationToken.None));   // 改了上游就重连一次

        if (!string.Equals(oldDir, Settings.ModelsDir, StringComparison.OrdinalIgnoreCase)) Scan();

        if (Router.Running && (oldPort != Settings.Port || oldLan != Settings.LanExposed))
        {
            Router.Stop();
            Router.Start();
        }
        ThemeChanged?.Invoke(Settings.Theme);
        PushState();
    }

    /// <summary>自定义链接：HF 仓库页 → 列文件；直链 → 直接下载</summary>
    private async Task<JsonNode> DownloadFromUrlAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return Err("链接是空的");
        url = url.Trim();

        if (DownloadManager.LooksLikeRepoUrl(url, out var repo) &&
            (url.Contains("huggingface.co") || url.Contains("hf-mirror.com") || !url.Contains(".")))
        {
            try
            {
                var o = await Hf.RepoAsync(repo, CancellationToken.None);
                var files = HfClient.FilesOf(o);
                var arr = new JsonArray();
                foreach (var f in files)
                    arr.Add(new JsonObject { ["path"] = f.Path, ["size"] = f.Size, ["sizeText"] = f.SizeText, ["quant"] = f.Quant, ["mmproj"] = f.IsMmproj });
                return new JsonObject { ["ok"] = true, ["repo"] = repo, ["files"] = arr };
            }
            catch (Exception ex) { return Err("当作 HF 仓库解析失败：" + ex.Message); }
        }

        if (!Regex.IsMatch(url, @"^https?://", RegexOptions.IgnoreCase)) return Err("只支持 http/https 链接");

        string name = Path.GetFileName(url.Split('?')[0].Split('#')[0]);
        if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            name = "model-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".gguf";

        var dest = Path.Combine(Settings.ModelsDir, "custom", name);
        Downloads.Enqueue(url, dest, "custom", name);
        return new JsonObject { ["ok"] = true, ["queued"] = 1, ["dest"] = dest };
    }

    /// <summary>解析界面送来的上游表（同名只留第一个）</summary>
    private static List<UpstreamCfg> ParseUpstreams(JsonArray a)
    {
        var list = new List<UpstreamCfg>();
        foreach (var it in a)
        {
            if (it is not JsonObject o) continue;
            var name = (o["name"]?.ToString() ?? "").Trim();
            if (name.Length == 0) continue;
            if (list.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(new UpstreamCfg
            {
                Name = name,
                BaseUrl = (o["baseUrl"]?.ToString() ?? "").Trim(),
                ApiKey = o["apiKey"]?.ToString() ?? "",
                Proxy = (o["proxy"]?.ToString() ?? "").Trim(),
                Enabled = bool.TryParse(o["enabled"]?.ToString(), out var en) ? en : true
            });
        }
        return list;
    }

    /// <summary>装引擎（逻辑都在 EngineInstaller 里，界面和命令行共用；这里只负责转成界面结果 + 重新检测）</summary>
    private async Task<JsonNode> InstallEngineAsync(string variant, string localZip = "")
    {
        var r = await EngineInstaller.InstallAsync(variant, localZip, Settings.EngineMirror, line => Log("engine", line));
        if (!r.ok) return Err(r.message);
        await Engine.ProbeEngineAsync();
        PushState();
        Toast?.Invoke("ok", "引擎安装完成");
        return new JsonObject { ["ok"] = true, ["path"] = r.enginePath, ["engine"] = Engine.ResolveEnginePath() };
    }

    private void Log(string source, string line) =>
        Push(new JsonObject { ["type"] = "log", ["source"] = source, ["line"] = line });
}
