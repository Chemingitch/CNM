using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CNM.Core;

/// <summary>拉起 / 停止 llama-server.exe（Vulkan 后端），并等它健康</summary>
public sealed class EngineManager
{
    private readonly Settings _s;
    private readonly ModelLibrary _lib;
    private Process _proc;
    private ModelEntry _pending;          // 正在加载、还没确认好：此时不能对界面说“已加载”
    private Process _stoppingProc;        // 这个是我们主动停的，不是崩了
    private double _vramModel, _vramKv, _vramCompute;
    private readonly Queue<string> _tail = new();
    private HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public event Action<string, string> Log;         // source, line
    public event Action StateChanged;
    public event Action<string> Crashed;             // 引擎自己退出了（不是用户点停止）

    public int BackendPort { get; private set; }
    public ModelEntry Loaded { get; private set; }
    public bool IsRunning => _proc != null && !_proc.HasExited;
    public string LastError { get; private set; } = "";
    public bool IsLoading { get; private set; }

    /// <summary>引擎自己报的显存占用（MiB）：模型 + KV + 计算缓冲，加载完才有值</summary>
    public double VramMiB => LiveVramMiB >= 0 ? LiveVramMiB : (_vramModel + _vramKv + _vramCompute);

    /// <summary>实测显存（MiB，走性能计数器），-1 = 没量到</summary>
    public double LiveVramMiB { get; private set; } = -1;

    private DateTime _lastUsedUtc = DateTime.UtcNow;
    private int _busy;                                  // 正在服务中的请求数

    /// <summary>多久没人用过（秒）</summary>
    public int IdleSeconds => (int)Math.Max(0, (DateTime.UtcNow - _lastUsedUtc).TotalSeconds);

    /// <summary>有人用了：重置空闲计时</summary>
    public void Touch() => _lastUsedUtc = DateTime.UtcNow;

    public void BeginRequest() => Interlocked.Increment(ref _busy);
    public void EndRequest() => Interlocked.Decrement(ref _busy);

    /// <summary>引擎日志落盘位置（崩了以后还能查）</summary>
    public string LogPath => Path.Combine(Settings.LogsDir, "engine.log");

    public EngineManager(Settings s, ModelLibrary lib) { _s = s; _lib = lib; }

    public string ResolveEnginePath()
    {
        if (!string.IsNullOrWhiteSpace(_s.EnginePath) && File.Exists(_s.EnginePath)) return _s.EnginePath;

        var baseDir = AppContext.BaseDirectory;
        var direct = Path.Combine(baseDir, "engine", "llama", "llama-server.exe");
        if (File.Exists(direct)) return direct;

        // 往上找几层（开发时 exe 在 bin\Release\net8.0-windows 里）
        try
        {
            var dir = new DirectoryInfo(baseDir);
            for (int i = 0; i < 5 && dir != null; i++)
            {
                var p = Path.Combine(dir.FullName, "engine", "llama", "llama-server.exe");
                if (File.Exists(p)) return p;
                dir = dir.Parent;
            }
        }
        catch { }

        try
        {
            var root = Path.Combine(baseDir, "engine");
            if (Directory.Exists(root))
            {
                var hit = Directory.GetFiles(root, "llama-server.exe", SearchOption.AllDirectories);
                if (hit.Length > 0) return hit[0];
            }
        }
        catch { }

        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var f = Path.Combine(p.Trim(), "llama-server.exe");
                if (File.Exists(f)) return f;
            }
            catch { }
        }
        return null;
    }

    public string EngineVersionText { get; private set; } = "";

    public async Task<string> ProbeEngineAsync()
    {
        var exe = ResolveEnginePath();
        if (exe == null) { EngineVersionText = ""; return null; }
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            });
            var txt = await p.StandardOutput.ReadToEndAsync() + await p.StandardError.ReadToEndAsync();
            var line = txt.Split('\n').FirstOrDefault(l => l.Contains("version", StringComparison.OrdinalIgnoreCase))?.Trim();
            EngineVersionText = string.IsNullOrWhiteSpace(line) ? "已就绪" : line;
        }
        catch (Exception ex) { EngineVersionText = "探测失败: " + ex.Message; }
        return exe;
    }

    public sealed class LoadResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = "";
        public ModelEntry Entry { get; set; }
    }

    private static int FreePort(int start = 18080)
    {
        for (int p = start; p < start + 200; p++)
        {
            try
            {
                var l = new TcpListener(System.Net.IPAddress.Loopback, p);
                l.Start(); l.Stop();
                return p;
            }
            catch { }
        }
        return 0;
    }

    public async Task<LoadResult> EnsureAsync(string modelIdOrName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modelIdOrName))
        {
            if (IsRunning) return new LoadResult { Ok = true, Entry = Loaded };
            return new LoadResult { Ok = false, Message = "没有模型在跑，且请求里没带 model 名字。" };
        }

        var entry = _lib.Find(modelIdOrName);
        if (IsRunning && Loaded != null && entry != null && Loaded.FilePath == entry.FilePath)
            return new LoadResult { Ok = true, Entry = Loaded };

        if (entry == null)
        {
            if (IsRunning) return new LoadResult { Ok = true, Entry = Loaded }; // 名字不认识但引擎在跑：原样转发
            return new LoadResult { Ok = false, Message = $"本地模型库里没有 “{modelIdOrName}”，请先在 CNM 里下载或扫描。" };
        }
        return await StartAsync(entry, ct);
    }

    // 同一时刻只允许一次「换模型」—— 两次并发加载会互相踩 _proc / BackendPort，
    // 结果是 A 被标成「已就绪」而真正在跑的是 B，agent 就被喂给了错的模型。
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    public async Task<LoadResult> StartAsync(ModelEntry entry, CancellationToken ct = default)
    {
        await _loadGate.WaitAsync(ct);
        try { return await StartCoreAsync(entry, ct); }
        finally { _loadGate.Release(); }
    }

    private async Task<LoadResult> StartCoreAsync(ModelEntry entry, CancellationToken ct = default)
    {
        // 排队等锁的时候，别人可能已经把这个模型加载好了 —— 直接认，别白卸一遍
        if (IsRunning && Loaded != null && Loaded.FilePath == entry.FilePath)
            return new LoadResult { Ok = true, Entry = Loaded };

        var exe = ResolveEnginePath();
        if (exe == null)
            return new LoadResult { Ok = false, Message = "找不到 llama-server.exe，请把 llama.cpp 的 Vulkan 版解压到 engine\\llama\\ 下。", Entry = entry };

        Stop();
        await Task.Delay(400, ct);

        BackendPort = FreePort();
        LastError = "";
        _vramModel = _vramKv = _vramCompute = 0;
        _pending = entry;
        IsLoading = true;
        StateChanged?.Invoke();

        var args = new List<string>
        {
            "-m", entry.FilePath,
            "--host", "127.0.0.1",
            "--port", BackendPort.ToString(),
            "-c", _s.ContextSize.ToString(),
            "-ngl", _s.GpuLayers.ToString(),
            "--parallel", Math.Max(1, _s.Parallel).ToString(),
            "--alias", entry.Id,
            "--jinja",
            "--metrics",
            "--no-webui"
        };
        if (!_s.FlashAttn) args.AddRange(new[] { "-fa", "off" });
        if (_s.Threads > 0) args.AddRange(new[] { "-t", _s.Threads.ToString() });
        if (!string.IsNullOrWhiteSpace(entry.MmprojPath) && File.Exists(entry.MmprojPath))
            args.AddRange(new[] { "--mmproj", entry.MmprojPath, "--no-mmproj-offload" });

        Emit("engine", $"> {Path.GetFileName(exe)} {string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GGML_VK_VISIBLE_DEVICES"] = psi.Environment.TryGetValue("GGML_VK_VISIBLE_DEVICES", out var v) ? v : "0";

        try
        {
            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var proc = _proc;
            _proc.OutputDataReceived += (_, e) => { if (e.Data != null) Emit("engine", e.Data); };
            _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) Emit("engine", e.Data); };
            _proc.Exited += (_, _) => OnEngineExited(proc);
            _proc.Start();
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
            // 注意：这里不设 Loaded —— 进程起来 ≠ 模型加载好了。
            // 之前在这里就设 Loaded，引擎中途崩掉后界面会一直显示“已就绪”，显存却是空的。
        }
        catch (Exception ex)
        {
            IsLoading = false;
            StateChanged?.Invoke();
            return new LoadResult { Ok = false, Message = "启动引擎失败: " + ex.Message, Entry = entry };
        }

        // 等 /health 变绿。注意：这里盯的是「本次启动的那个进程」（局部变量），
        // 不是共享字段 _proc —— 否则别再动一下「停止模型」，_proc 变 null，这里就 NRE。
        var cur = _proc;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < 300)
        {
            if (ct.IsCancellationRequested) break;
            if (cur == null || cur.HasExited)
            {
                IsLoading = false;
                LastError = cur == null
                    ? "加载被中断了（引擎被停掉）。"
                    : $"引擎退出（代码 {cur.ExitCode}）。{Tail()}";
                Loaded = null;
                _pending = null;
                StateChanged?.Invoke();
                return new LoadResult { Ok = false, Message = LastError, Entry = entry };
            }
            try
            {
                var r = await _http.GetAsync($"http://127.0.0.1:{BackendPort}/health", ct);
                if ((int)r.StatusCode is >= 200 and < 300)
                {
                    IsLoading = false;
                    Loaded = _pending ?? entry;      // 到这里才敢说“加载好了”
                    _pending = null;
                    _lastUsedUtc = DateTime.UtcNow;
                    StateChanged?.Invoke();
                    Emit("engine", $"[就绪] {entry.Id} 已加载 · 后端端口 {BackendPort} · {sw.Elapsed.TotalSeconds:0.0}s" +
                                   (VramMiB > 0 ? $" · 显存 ≈ {VramMiB / 1024:0.0} GB" : ""));
                    return new LoadResult { Ok = true, Entry = entry, Message = "已加载" };
                }
            }
            catch { }
            await Task.Delay(600, ct);
        }

        // 300 秒还没好，说明卡住了：收掉，别留个半死不活的进程装成“已加载”
        IsLoading = false;
        _pending = null;
        LastError = "等引擎就绪超时（300 秒）。" + Tail();
        StateChanged?.Invoke();
        Stop();
        return new LoadResult { Ok = false, Message = LastError, Entry = entry };
    }

    public void Stop()
    {
        try
        {
            if (_proc != null && !_proc.HasExited)
            {
                _stoppingProc = _proc;          // 这是我们主动停的，别报“意外退出”
                _proc.Kill(entireProcessTree: true);
                _proc.WaitForExit(8000);
                Emit("engine", "[已停止] 引擎进程结束");
            }
        }
        catch { }
        _proc = null;
        Loaded = null;
        _pending = null;
        IsLoading = false;
        _vramModel = _vramKv = _vramCompute = 0;
        LiveVramMiB = -1;
        StateChanged?.Invoke();
    }

    /// <summary>引擎进程退出了：不管是崩的还是被停的，先把状态清干净（别再显示“已就绪”）</summary>
    private void OnEngineExited(Process proc)
    {
        if (!ReferenceEquals(_proc, proc)) return;      // 已经换成新进程了，这条是旧的，忽略

        int code = -1;
        try { code = proc.ExitCode; } catch { }

        bool expected = ReferenceEquals(_stoppingProc, proc);
        _proc = null;
        Loaded = null;
        _pending = null;
        IsLoading = false;
        _vramModel = _vramKv = _vramCompute = 0;
        LiveVramMiB = -1;

        if (!expected)
        {
            LastError = $"引擎意外退出（代码 {code}），日志：{LogPath}";
            Emit("engine", "[意外退出] 引擎没了，代码 " + code);
            try { Crashed?.Invoke(LastError); } catch { }
        }

        StateChanged?.Invoke();
    }

    private string Tail()
    {
        lock (_tail) return _tail.Count == 0 ? "" : " 日志尾部: " + string.Join(" | ", _tail);
    }

    private static readonly Regex VramLine = new(
        @"(model|KV|compute) buffer size\s*=\s*([\d.]+)\s*MiB",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private void Emit(string source, string line)
    {
        lock (_tail)
        {
            _tail.Enqueue(line.Length > 200 ? line[..200] : line);
            while (_tail.Count > 12) _tail.Dequeue();
        }

        // 引擎自己会报每块 buffer 吃多少显存，抓下来给界面看，省得去任务管理器猜
        var m = VramLine.Match(line ?? "");
        if (m.Success && double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mb))
        {
            var before = VramMiB;
            switch (m.Groups[1].Value)
            {
                case "model": _vramModel = mb; break;
                case "KV": _vramKv = mb; break;
                case "compute": _vramCompute = mb; break;
            }
            if (Math.Abs(VramMiB - before) > 0.5) StateChanged?.Invoke();
        }

        WriteLog(source, line);
        Log?.Invoke(source, line);
    }

    /// <summary>引擎日志落盘到 %APPDATA%\CNM\logs\engine.log（超过 4MB 轮转一次）</summary>
    private void WriteLog(string source, string line)
    {
        try
        {
            Directory.CreateDirectory(Settings.LogsDir);
            var fi = new FileInfo(LogPath);
            if (fi.Exists && fi.Length > 4 * 1024 * 1024)
            {
                var old = Path.Combine(Settings.LogsDir, "engine.1.log");
                try { File.Delete(old); } catch { }
                try { File.Move(LogPath, old); } catch { }
            }
            File.AppendAllText(LogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{source}] {line}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { }
    }

    /// <summary>量一下引擎进程实际占的显存（性能计数器，稍慢，交给后台定时器调）。返回是否值得推给界面。</summary>
    public bool PollVram()
    {
        var before = LiveVramMiB;
        int pid = 0;
        try { if (IsRunning) pid = _proc.Id; } catch { }
        LiveVramMiB = pid > 0 ? Vram.ProcessMiB(pid) : -1;
        return Math.Abs(LiveVramMiB - before) > 64;      // 变化超过 64MB 才推
    }

    /// <summary>空闲够久就把模型卸掉、显存还回去（下次请求会再自动加载）——“按需打开”的另一半</summary>
    public bool TryIdleUnload()
    {
        if (_s.IdleUnloadMinutes <= 0) return false;
        if (!IsRunning || IsLoading || _pending != null) return false;
        if (Volatile.Read(ref _busy) > 0) return false;                     // 正在服务请求，别动
        if ((DateTime.UtcNow - _lastUsedUtc).TotalMinutes < _s.IdleUnloadMinutes) return false;
        Emit("engine", $"[空闲卸载] {_s.IdleUnloadMinutes} 分钟没人调用，已释放显存（下次请求会自动加载）");
        Stop();
        return true;
    }

    public JsonObject Status()
    {
        return new JsonObject
        {
            ["running"] = IsRunning,
            ["loading"] = IsLoading,
            ["loadedId"] = Loaded?.Id,
            ["loadedPath"] = Loaded?.FilePath,
            ["pendingId"] = _pending?.Id,
            ["vramMiB"] = Math.Round(VramMiB),
            ["idleSeconds"] = IdleSeconds,
            ["idleUnloadMinutes"] = _s.IdleUnloadMinutes,
            ["busy"] = Volatile.Read(ref _busy),
            ["logPath"] = LogPath,
            ["backendPort"] = BackendPort,
            ["enginePath"] = ResolveEnginePath(),
            ["engineVersion"] = EngineVersionText,
            ["error"] = LastError
        };
    }
}
