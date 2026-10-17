using System.Text.Json;
using System.Text.RegularExpressions;

namespace CNM.Core;

public sealed class ModelEntry
{
    public string Id { get; set; } = "";          // 对外模型名（agent 里填这个）
    public string Name { get; set; } = "";        // 显示名
    public string FilePath { get; set; } = "";
    public string Dir { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Repo { get; set; } = "";
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Quant { get; set; } = "";
    public string Arch { get; set; } = "";
    public long ContextLength { get; set; }
    public bool Vision { get; set; }
    public string MmprojPath { get; set; } = "";
    public string MmprojName { get; set; } = "";
    public bool HasChatTemplate { get; set; }
    public string Error { get; set; } = "";
    public bool IsMmproj { get; set; }
    public long Mtime { get; set; }

    public string SizeText => SizeBytes >= 1073741824L
        ? (SizeBytes / 1073741824.0).ToString("0.00") + " GB"
        : (SizeBytes / 1048576.0).ToString("0") + " MB";
}

/// <summary>扫描模型目录（LM Studio 布局），带元数据缓存</summary>
public sealed class ModelLibrary
{
    private readonly Settings _settings;
    private Dictionary<string, CacheItem> _cache = new();
    private string CachePath => Path.Combine(Settings.DataDir, "gguf-cache.json");

    public List<ModelEntry> Models { get; private set; } = new();

    private static readonly Regex QuantRe = new(
        @"(?:^|[-_.])(IQ[1-4]_[A-Z0-9_]+|Q[2-8]_K_[A-Z]+|Q[2-8]_K|Q[2-8]_[01]|Q[2-8]_[01]_[A-Z]+|MXFP4|BF16|F16|F32|FP16|FP32|Q8_0|Q6_K|Q5_K|Q4_K|Q3_K|Q2_K|Q1_0)(?:$|[-_.])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed class CacheItem
    {
        public long Size { get; set; }
        public long Mtime { get; set; }
        public string Arch { get; set; } = "";
        public string Name { get; set; } = "";
        public long Ctx { get; set; }
        public bool Vision { get; set; }
        public bool Mmproj { get; set; }
        public bool Chat { get; set; }
        public int FileType { get; set; } = -1;
    }

    public ModelLibrary(Settings settings)
    {
        _settings = settings;
        try
        {
            if (File.Exists(CachePath))
                _cache = JsonSerializer.Deserialize<Dictionary<string, CacheItem>>(File.ReadAllText(CachePath)) ?? new();
        }
        catch { _cache = new(); }
    }

    public List<string> Roots()
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(_settings.ModelsDir)) roots.Add(_settings.ModelsDir);
        foreach (var d in _settings.ExtraModelDirs ?? new List<string>())
            if (!string.IsNullOrWhiteSpace(d)) roots.Add(d);
        return roots;
    }

    public List<ModelEntry> Scan(Action<string> progress = null)
    {
        var list = new List<ModelEntry>();
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        int scanned = 0, total = 0;

        foreach (var root in Roots())
        {
            if (!Directory.Exists(root)) continue;
            string[] files;
            try { files = Directory.GetFiles(root, "*.gguf", opts); } catch { continue; }
            total += files.Length;

            foreach (var f in files)
            {
                progress?.Invoke($"扫描 {Path.GetFileName(f)}");
                var e = Build(root, f, ++scanned);
                if (e != null) list.Add(e);
            }
        }

        // 配对 mmproj（同目录里的视觉编码器）
        var byDir = list.Where(x => !x.IsMmproj).GroupBy(x => x.Dir, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var mm in list.Where(x => x.IsMmproj))
        {
            if (byDir.TryGetValue(mm.Dir, out var sibs))
                foreach (var s in sibs) { s.MmprojPath = mm.FilePath; s.MmprojName = mm.FileName; s.Vision = true; }
        }

        var main = list.Where(x => !x.IsMmproj).ToList();

        // Id 去重：优先短名，重名再补仓库/作者
        foreach (var g in main.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (g.Count() == 1) continue;
            foreach (var e in g)
            {
                var withRepo = string.IsNullOrEmpty(e.Repo) ? e.Id : e.Repo + "/" + e.Id;
                e.Id = withRepo;
            }
        }
        foreach (var g in main.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (g.Count() == 1) continue;
            foreach (var e in g)
                e.Id = ((e.Publisher.Length > 0 ? e.Publisher + "/" : "") + e.Repo + "/" + Path.GetFileNameWithoutExtension(e.FileName)).TrimStart('/');
        }

        main.Sort((a, b) =>
        {
            int c = string.Compare(a.Repo, b.Repo, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
        });

        Models = main;
        SaveCache();
        return main;
    }

    private ModelEntry Build(string root, string file, int n)
    {
        var fi = new FileInfo(file);
        var rel = Path.GetRelativePath(root, file);
        var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var publisher = parts.Length >= 3 ? parts[0] : (parts.Length == 2 ? parts[0] : "");
        var repo = parts.Length >= 2 ? parts[parts.Length - 2] : new DirectoryInfo(root).Name;
        if (publisher == repo) repo = parts.Length >= 2 ? parts[^2] : repo;

        var e = new ModelEntry
        {
            FilePath = file,
            Dir = Path.GetDirectoryName(file) ?? root,
            FileName = fi.Name,
            Publisher = publisher,
            Repo = repo,
            SizeBytes = fi.Length,
            Mtime = fi.LastWriteTimeUtc.Ticks
        };

        // 元数据：命中缓存就不重复读盘
        var key = file.ToLowerInvariant();
        CacheItem ci = null;
        if (_cache.TryGetValue(key, out var c) && c.Size == fi.Length && c.Mtime == e.Mtime) ci = c;

        if (ci == null)
        {
            var meta = Gguf.Read(file);
            ci = new CacheItem
            {
                Size = fi.Length,
                Mtime = e.Mtime,
                Arch = meta.Arch,
                Name = meta.Name,
                Ctx = meta.Ctx(),
                Vision = meta.HasVision,
                Mmproj = meta.IsMmproj || fi.Name.StartsWith("mmproj", StringComparison.OrdinalIgnoreCase),
                Chat = meta.HasChatTemplate,
                FileType = meta.Values.TryGetValue("general.file_type", out var ft) && ft is ulong u ? (int)u : -1
            };
            if (!string.IsNullOrEmpty(meta.ReadError) && meta.Arch.Length == 0) ci.Name = "";
            _cache[key] = ci;
        }

        e.Arch = ci.Arch;
        e.ContextLength = ci.Ctx;
        e.Vision = ci.Vision;
        e.IsMmproj = ci.Mmproj;
        e.HasChatTemplate = ci.Chat;
        e.Quant = GuessQuant(fi.Name, ci.FileType);
        e.Name = string.IsNullOrWhiteSpace(ci.Name) ? Path.GetFileNameWithoutExtension(fi.Name) : ci.Name;
        e.Id = Path.GetFileNameWithoutExtension(fi.Name);
        return e;
    }

    private static readonly Dictionary<int, string> FileTypes = new()
    {
        [0] = "F32", [1] = "F16", [2] = "Q4_0", [3] = "Q4_1", [7] = "Q8_0", [8] = "Q5_0", [9] = "Q5_1",
        [10] = "Q2_K", [11] = "Q3_K_S", [12] = "Q3_K_M", [13] = "Q3_K_L",
        [14] = "Q4_K_S", [15] = "Q4_K_M", [16] = "Q5_K_S", [17] = "Q5_K_M", [18] = "Q6_K",
        [19] = "IQ2_XXS", [20] = "IQ2_XS", [21] = "Q2_K_S", [22] = "IQ3_XS", [23] = "IQ3_XXS",
        [24] = "IQ1_S", [25] = "IQ4_NL", [26] = "IQ3_S", [27] = "IQ3_M", [28] = "IQ2_S",
        [29] = "IQ2_M", [30] = "IQ4_XS", [31] = "IQ1_M", [32] = "BF16", [36] = "TQ1_0", [37] = "TQ2_0"
    };

    public static string GuessQuant(string fileName, int fileType = -1)
    {
        var m = QuantRe.Match(fileName);
        if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
        if (fileType >= 0 && FileTypes.TryGetValue(fileType, out var s)) return s;
        return "";
    }

    private void SaveCache()
    {
        try { File.WriteAllText(CachePath, JsonSerializer.Serialize(_cache)); } catch { }
    }

    public ModelEntry Find(string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName)) return null;
        var q = idOrName.Trim();
        var e = Models.FirstOrDefault(x => string.Equals(x.Id, q, StringComparison.OrdinalIgnoreCase));
        if (e != null) return e;
        e = Models.FirstOrDefault(x => string.Equals(Path.GetFileNameWithoutExtension(x.FileName), q, StringComparison.OrdinalIgnoreCase));
        if (e != null) return e;
        e = Models.FirstOrDefault(x => string.Equals(x.Repo + "/" + Path.GetFileNameWithoutExtension(x.FileName), q, StringComparison.OrdinalIgnoreCase));
        if (e != null) return e;
        e = Models.FirstOrDefault(x => x.Id.EndsWith("/" + q, StringComparison.OrdinalIgnoreCase));
        if (e != null) return e;
        return Models.FirstOrDefault(x => x.Id.Contains(q, StringComparison.OrdinalIgnoreCase));
    }
}
