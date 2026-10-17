using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CNM.Core;

/// <summary>一天的用量（本地日期归类）</summary>
public sealed class UsageDay
{
    /// <summary>请求数</summary>
    public int Req { get; set; }
    /// <summary>走本机引擎的次数</summary>
    public int Local { get; set; }
    /// <summary>走云端上游的次数</summary>
    public int Up { get; set; }
    /// <summary>返回过错误的次数（4xx/5xx）</summary>
    public int Fail { get; set; }
    /// <summary>回给 agent 的字节数</summary>
    public long Bytes { get; set; }
    /// <summary>累计耗时（毫秒）</summary>
    public long Ms { get; set; }
    /// <summary>输入 token</summary>
    public long PromptTok { get; set; }
    /// <summary>输出 token</summary>
    public long ComplTok { get; set; }
}

/// <summary>
/// 只留最后 64 KB 的响应（流式也照样），从里面把 usage 抠出来 —— 前面几十 KB 的正文不用留。
/// </summary>
public sealed class TailCheck
{
    private readonly byte[] _buf = new byte[64 * 1024];
    private int _len;

    public void Add(byte[] b, int n)
    {
        if (n <= 0 || b == null) return;
        if (n >= _buf.Length) { Array.Copy(b, n - _buf.Length, _buf, 0, _buf.Length); _len = _buf.Length; return; }
        if (_len + n > _buf.Length)
        {
            int keep = _buf.Length - n;
            Array.Copy(_buf, _len - keep, _buf, 0, keep);
            _len = keep;
        }
        Array.Copy(b, 0, _buf, _len, n);
        _len += n;
    }

    public string Text()
    {
        try { return Encoding.UTF8.GetString(_buf, 0, _len); } catch { return ""; }
    }
}

/// <summary>从响应里认 token 用量（OpenAI 的 prompt/completion、Anthropic 的 input/output 都认）</summary>
public static class TokenScan
{
    public static (long prompt, long completion) Parse(string s)
    {
        long pt = 0, ct = 0;
        if (string.IsNullOrEmpty(s)) return (0, 0);
        try
        {
            // 不抠括号配对（usage 里可能嵌 prompt_tokens_details 之类），
            // 直接找到 "usage" 往后看一段，把字段捞出来 —— 简单且够用。
            int i = s.IndexOf("\"usage\"", StringComparison.Ordinal);
            while (i >= 0)
            {
                var seg = s.Substring(i, Math.Min(900, s.Length - i));
                pt = Math.Max(pt, Field(seg, "prompt_tokens", "input_tokens"));
                ct = Math.Max(ct, Field(seg, "completion_tokens", "output_tokens"));
                i = s.IndexOf("\"usage\"", i + 7, StringComparison.Ordinal);
            }
        }
        catch { }
        return (pt, ct);
    }

    private static long Field(string seg, string a, string b)
    {
        foreach (var k in new[] { a, b })
        {
            var m = System.Text.RegularExpressions.Regex.Match(seg, "\"" + k + "\"\\s*:\\s*(\\d+)");
            if (m.Success && long.TryParse(m.Groups[1].Value, out var v)) return v;
        }
        return 0;
    }
}

/// <summary>
/// 用量统计：这台机器上的 agent 一共调了多少次、走本地还是云端、多少 token、多少字节。
/// 只记数字（不记对话内容），一天一条，落盘 %APPDATA%\CNM\usage.json。
/// </summary>
public sealed class UsageStats
{
    private readonly Dictionary<string, UsageDay> _days = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private bool _dirty;
    private DateTime _lastSave = DateTime.MinValue;

    public static string FilePath => Path.Combine(Settings.DataDir, "usage.json");

    public UsageStats() { Load(); }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var o = JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject;
            if (o?["days"] is not JsonObject days) return;
            lock (_lock)
            {
                foreach (var kv in days)
                {
                    if (kv.Value is not JsonObject d) continue;
                    _days[kv.Key] = new UsageDay
                    {
                        Req = (int?)d["Req"] ?? 0,
                        Local = (int?)d["Local"] ?? 0,
                        Up = (int?)d["Up"] ?? 0,
                        Fail = (int?)d["Fail"] ?? 0,
                        Bytes = (long?)d["Bytes"] ?? 0,
                        Ms = (long?)d["Ms"] ?? 0,
                        PromptTok = (long?)d["PromptTok"] ?? 0,
                        ComplTok = (long?)d["ComplTok"] ?? 0
                    };
                }
            }
        }
        catch { /* 读不出来就当空的，不能因为统计挂了服务 */ }
    }

    public void Save()
    {
        try
        {
            JsonObject days;
            lock (_lock)
            {
                days = new JsonObject();
                foreach (var kv in _days.OrderBy(x => x.Key))
                {
                    days[kv.Key] = new JsonObject
                    {
                        ["Req"] = kv.Value.Req,
                        ["Local"] = kv.Value.Local,
                        ["Up"] = kv.Value.Up,
                        ["Fail"] = kv.Value.Fail,
                        ["Bytes"] = kv.Value.Bytes,
                        ["Ms"] = kv.Value.Ms,
                        ["PromptTok"] = kv.Value.PromptTok,
                        ["ComplTok"] = kv.Value.ComplTok
                    };
                }
                _dirty = false;
                _lastSave = DateTime.Now;
            }
            var root = new JsonObject { ["days"] = days };
            Directory.CreateDirectory(Settings.DataDir);
            File.WriteAllText(FilePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        }
        catch { }
    }

    /// <summary>后台小管家定期叫一下：有变化、且距上次写盘超过 15 秒才真写</summary>
    public void SaveIfDirty()
    {
        bool due;
        lock (_lock) due = _dirty && (DateTime.Now - _lastSave).TotalSeconds > 15;
        if (due) Save();
    }

    /// <summary>记一次真实调用（chat/completions 这类干活儿的请求，模型列表/探活不算）</summary>
    public void Add(DateTime when, bool upstream, int status, long bytes, long ms, long promptTok, long complTok)
    {
        var key = when.ToString("yyyy-MM-dd");
        lock (_lock)
        {
            if (!_days.TryGetValue(key, out var d)) { d = new UsageDay(); _days[key] = d; }
            d.Req++;
            if (upstream) d.Up++; else d.Local++;
            if (status >= 400) d.Fail++;
            if (bytes > 0) d.Bytes += bytes;
            if (ms > 0) d.Ms += ms;
            if (promptTok > 0) d.PromptTok += promptTok;
            if (complTok > 0) d.ComplTok += complTok;
            _dirty = true;

            // 三年以前的天数没必要留着
            if (_days.Count > 1600)
            {
                var cut = when.AddYears(-3).ToString("yyyy-MM-dd");
                foreach (var k in _days.Keys.Where(k => string.CompareOrdinal(k, cut) < 0).ToList()) _days.Remove(k);
            }
        }
    }

    private static void Merge(UsageDay to, UsageDay from)
    {
        to.Req += from.Req; to.Local += from.Local; to.Up += from.Up; to.Fail += from.Fail;
        to.Bytes += from.Bytes; to.Ms += from.Ms;
        to.PromptTok += from.PromptTok; to.ComplTok += from.ComplTok;
    }

    private static JsonObject Day2Json(UsageDay d) => new()
    {
        ["req"] = d.Req,
        ["local"] = d.Local,
        ["up"] = d.Up,
        ["fail"] = d.Fail,
        ["bytes"] = d.Bytes,
        ["ms"] = d.Ms,
        ["pt"] = d.PromptTok,
        ["ct"] = d.ComplTok
    };

    /// <summary>给界面用的聚合：今天 / 本月 / 今年 / 累计 + 最近 7 天 / 12 个月 / 5 年</summary>
    public JsonObject Json()
    {
        var now = DateTime.Now;
        lock (_lock)
        {
            UsageDay Sum(Func<string, bool> pick)
            {
                var total = new UsageDay();
                foreach (var kv in _days) if (pick(kv.Key)) Merge(total, kv.Value);
                return total;
            }

            JsonArray Series(int n, Func<int, DateTime> at, Func<DateTime, string> key, Func<DateTime, string> label)
            {
                var arr = new JsonArray();
                for (int i = 0; i < n; i++)
                {
                    var d = at(i);
                    var k = key(d);
                    var day = _days.TryGetValue(k, out var v) ? v : new UsageDay();
                    var o = Day2Json(day);
                    o["k"] = k;
                    o["l"] = label(d);
                    arr.Add(o);
                }
                return arr;
            }

            var monthStart = new DateTime(now.Year, now.Month, 1);
            return new JsonObject
            {
                ["today"] = Day2Json(Sum(k => k == now.ToString("yyyy-MM-dd"))),
                ["month"] = Day2Json(Sum(k => k.StartsWith(now.ToString("yyyy-MM"), StringComparison.Ordinal))),
                ["year"] = Day2Json(Sum(k => k.StartsWith(now.ToString("yyyy"), StringComparison.Ordinal))),
                ["total"] = Day2Json(Sum(_ => true)),
                ["days"] = Series(7, i => now.AddDays(i - 6).Date,
                                  d => d.ToString("yyyy-MM-dd"), d => d.ToString("M/d")),
                ["months"] = Series(12, i => monthStart.AddMonths(i - 11),
                                    d => d.ToString("yyyy-MM"), d => d.ToString("M月")),
                ["years"] = Series(5, i => new DateTime(now.Year - (4 - i), 1, 1),
                                   d => d.ToString("yyyy"), d => d.ToString("yyyy"))
            };
        }
    }
}
