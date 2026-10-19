using System.Text.Json;
using System.Text.Json.Serialization;

namespace CNM.Core;

/// <summary>
/// 序列化策略：磁盘上统一 camelCase（与前端/App.cs 手工映射的键名完全一致）。
/// 反序列化开大小写不敏感 —— 旧版本的 PascalCase 配置照样能读进来，无痛升级。
/// 曾经 C# 默认存 PascalCase、前端读 camelCase，靠 App.cs 手工映射硬对齐；
/// 每加一个设置要手改 5 处，漏一处就静默失效（MmprojOnGpu 就踩过这个坑）。
/// </summary>
public static class SettingsJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static readonly JsonSerializerOptions LoadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}

/// <summary>应用设置，落在 %APPDATA%\CNM\settings.json</summary>
public sealed class Settings
{
    /// <summary>
    /// 模型根目录（常见布局：&lt;作者&gt;\&lt;仓库&gt;\xxx.gguf）。
    /// 空 = 自动定位（Load 时填 DefaultModelsDir）—— 不再硬编码某台机器的绝对路径，
    /// 否则新分发用户第一次打开会扫描不存在的目录、看不到模型。
    /// 老用户 settings.json 里已存路径，不受影响。
    /// </summary>
    public string ModelsDir { get; set; } = "";

    /// <summary>自动定位模型目录：优先「程序目录下 models」，其次「上一级目录下 models」，
    /// 都没有就返回程序目录下的 models（Scan 会创建它）。</summary>
    public static string DefaultModelsDir()
    {
        var exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetDirectoryName(exeDir) ?? exeDir;
        foreach (var cand in new[] { Path.Combine(exeDir, "models"), Path.Combine(parent, "models") })
        {
            try { if (Directory.Exists(cand)) return cand; } catch { }
        }
        return Path.Combine(exeDir, "models");
    }

    /// <summary>额外扫描目录</summary>
    public List<string> ExtraModelDirs { get; set; } = new();

    /// <summary>对外 API Key，空 = 不校验</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>对外服务端口</summary>
    public int Port { get; set; } = 1234;

    /// <summary>true = 监听 0.0.0.0（局域网可用），false = 只 127.0.0.1</summary>
    public bool LanExposed { get; set; } = true;

    /// <summary>程序启动时自动开始对外服务</summary>
    public bool AutoStartServer { get; set; } = true;

    /// <summary>模型列表（/v1/models）也要带 key（关掉就是旧的免 key 行为）</summary>
    public bool ModelsNeedKey { get; set; } = true;

    /// <summary>收到请求时自动加载/切换模型</summary>
    public bool AutoLoadOnRequest { get; set; } = true;

    /// <summary>启动时自动加载上次用的模型</summary>
    public bool AutoLoadLastModel { get; set; } = false;

    /// <summary>空闲多少分钟自动卸载模型、把显存还回去（0 = 不自动卸载）</summary>
    public int IdleUnloadMinutes { get; set; } = 10;

    /// <summary>上下文长度</summary>
    public int ContextSize { get; set; } = 8192;

    /// <summary>卸载到 GPU 的层数（99 = 全部）</summary>
    public int GpuLayers { get; set; } = 99;

    /// <summary>CPU 线程数，0 = 自动</summary>
    public int Threads { get; set; } = 0;

    /// <summary>并行请求槽位</summary>
    public int Parallel { get; set; } = 2;

    /// <summary>Flash Attention</summary>
    public bool FlashAttn { get; set; } = true;

    /// <summary>
    /// 视觉编码器（mmproj）卸载到 GPU。默认 false（CNM 历史行为：
    /// mmproj 留在 CPU/内存跑，省显存，代价是每次发图 CPU 全核编码、慢）。
    /// 大显存卡（≥16GB）建议开 —— 编码快几倍，CPU 不再发烫。
    /// </summary>
    public bool MmprojOnGpu { get; set; } = false;

    /// <summary>llama-server.exe 路径，空 = 自动查找 engine\llama\</summary>
    public string EnginePath { get; set; } = "";

    /// <summary>HF 端点（镜像）</summary>
    public string HfEndpoint { get; set; } = "https://hf-mirror.com";

    /// <summary>云端上游：网上的 OpenAI 兼容服务（API 中转站）</summary>
    public List<UpstreamCfg> Upstreams { get; set; } = new();

    /// <summary>默认上游的名字（空 = 不兜底）。请求的模型本地没有时，转给它</summary>
    public string UpstreamDefault { get; set; } = "";

    /// <summary>转发给上游前，把请求里的 model 换成这个（空 = 原样转发）</summary>
    public string UpstreamModel { get; set; } = "";

    /// <summary>GitHub 加速前缀（下引擎包用），空 = 直连。
    /// 写法是「前缀 + 原始完整 URL」，例如 https://ghproxy.net/https://github.com/…</summary>
    public string EngineMirror { get; set; } = "";

    /// <summary>关窗收进托盘（服务继续跑）</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>主题：dark / light</summary>
    public string Theme { get; set; } = "dark";

    // ---------- 全局快捷键 ----------
    // 写法："Ctrl+Alt+C"；空 = 不启用这个键。主键可以是 A-Z / 0-9 / F1-F24 / 方向键等。

    /// <summary>呼出 / 退下主窗口（同一个键来回切）</summary>
    public string HotkeyToggle { get; set; } = "Ctrl+Alt+C";

    /// <summary>切换到下一个已装模型（在模型库里轮换，切到哪个就加载哪个）</summary>
    public string HotkeyNextModel { get; set; } = "Ctrl+Alt+N";

    /// <summary>卸载当前模型、把显存还回去</summary>
    public string HotkeyUnload { get; set; } = "Ctrl+Alt+U";

    /// <summary>上次使用的模型 Id</summary>
    public string LastModelId { get; set; } = "";

    /// <summary>窗口尺寸</summary>
    public int WinWidth { get; set; } = 1060;
    public int WinHeight { get; set; } = 700;

    [JsonIgnore]
    public static string DataDir
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CNM");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    private static string FilePath => Path.Combine(DataDir, "settings.json");

    // ---------- 自定义图标 ----------

    [JsonIgnore] public static string LogsDir => Path.Combine(DataDir, "logs");
    [JsonIgnore] public static string BrandDir => Path.Combine(DataDir, "brand");
    [JsonIgnore] public static string UserIcoPath => Path.Combine(BrandDir, "icon.ico");
    [JsonIgnore] public static string UserPngPath => Path.Combine(BrandDir, "icon.png");

    /// <summary>用户是否换过图标</summary>
    [JsonIgnore]
    public static bool HasUserIcon
    {
        get { try { return File.Exists(UserIcoPath); } catch { return false; } }
    }

    /// <summary>图标版本号（界面拿来刷新缓存）</summary>
    [JsonIgnore]
    public static long IconVersion
    {
        get { try { return HasUserIcon ? File.GetLastWriteTimeUtc(UserIcoPath).Ticks : 0L; } catch { return 0L; } }
    }

    /// <summary>从旧的 ChemLLM 目录搬一次配置（原来那串 API Key 还能接着用）</summary>
    private static void MigrateOldData()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var oldDir = Path.Combine(appData, "ChemLLM");
            var oldSettings = Path.Combine(oldDir, "settings.json");
            if (!File.Exists(oldSettings) || File.Exists(FilePath)) return;
            Directory.CreateDirectory(DataDir);
            File.Copy(oldSettings, FilePath, false);

            foreach (var sub in new[] { "brand", "webview2" })
            {
                var from = Path.Combine(oldDir, sub);
                var to = Path.Combine(DataDir, sub);
                if (Directory.Exists(from) && !Directory.Exists(to))
                    Directory.CreateDirectory(to); // 只建目录，缓存不必搬
            }
        }
        catch { }
    }

    public static Settings Load()
    {
        MigrateOldData();
        try
        {
            if (File.Exists(FilePath))
            {
                // 用 LoadOptions（camelCase + 大小写不敏感），老 PascalCase 配置无缝兼容
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), SettingsJson.LoadOptions);
                if (s != null)
                {
                    if (string.IsNullOrWhiteSpace(s.ApiKey)) s.ApiKey = NewKey();
                    if (string.IsNullOrWhiteSpace(s.ModelsDir)) s.ModelsDir = DefaultModelsDir();
                    // 老配置里没有快捷键字段 → 反序列化后是 null（不是""），会被当成"用户主动清空"。
                    // 用 ?? 补默认：只有真的没这个键（null）才填，用户手写的空串保持空。
                    s.HotkeyToggle ??= "Ctrl+Alt+C";
                    s.HotkeyNextModel ??= "Ctrl+Alt+N";
                    s.HotkeyUnload ??= "Ctrl+Alt+U";
                    return s;
                }
            }
        }
        catch
        {
            // 配置读坏了（手改出个错字符最典型）：先把现场留一份，别顺手把用户的上游地址和 Key 冲掉
            try { File.Copy(FilePath, FilePath + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true); } catch { }
        }

        var fresh = new Settings { ApiKey = NewKey(), ModelsDir = DefaultModelsDir() };
        fresh.Save();
        return fresh;
    }

    public void Save()
    {
        try
        {
            // camelCase 序列化：磁盘格式与前端键名完全对齐，App.cs 手工映射从此只是冗余备份
            var json = JsonSerializer.Serialize(this, SettingsJson.Options);
            File.WriteAllText(FilePath, json);
        }
        catch { }
    }

    public static string NewKey()
    {
        const string abc = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var rnd = Random.Shared;
        var sb = new System.Text.StringBuilder("sk-chem-");
        for (int i = 0; i < 32; i++) sb.Append(abc[rnd.Next(abc.Length)]);
        return sb.ToString();
    }
}
