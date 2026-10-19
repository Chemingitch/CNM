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
    /// <summary>本地引擎返回过错误的次数（失败里属于"走本地"的那部分）</summary>
    public int FailLocal { get; set; }
    /// <summary>云端上游返回过错误的次数（失败里属于"走外地"的那部分）</summary>
    public int FailUp { get; set; }
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

    /// <summary>
    /// 第一次用上 CNM 的时间（也就是"装上那天"）。
    /// 图表的起点用它 —— 以前写死"往前 5 年 / 12 个月"，刚装的用户会看到
    /// 一排 2022、2023 的空柱子，除了占地方没有任何信息。
    /// </summary>
    public DateTime FirstSeen { get; private set; } = DateTime.Now;

    public static string FilePath => Path.Combine(Settings.DataDir, "usage.json");

    public UsageStats() { Load(); }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var o = JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject;
            if (o == null) return;

            // 起点优先读存盘的值（首次运行时写进去的那天）
            if (o["firstSeen"]?.GetValue<string>() is string fs && DateTime.TryParse(fs, out var fd))
                FirstSeen = fd;

            if (o["days"] is not JsonObject days) return;
            lock (_lock)
            {
                foreach (var kv in days)
                {
                    if (kv.Value is not JsonObject d) continue;
                    var day = new UsageDay
                    {
                        Req = (int?)d["Req"] ?? 0,
                        Local = (int?)d["Local"] ?? 0,
                        Up = (int?)d["Up"] ?? 0,
                        Fail = (int?)d["Fail"] ?? 0,
                        FailLocal = (int?)d["FailLocal"] ?? 0,
                        FailUp = (int?)d["FailUp"] ?? 0,
                        Bytes = (long?)d["Bytes"] ?? 0,
                        Ms = (long?)d["Ms"] ?? 0,
                        PromptTok = (long?)d["PromptTok"] ?? 0,
                        ComplTok = (long?)d["ComplTok"] ?? 0
                    };
                    // 旧数据没存过「失败按本地/外地拆」这一对字段 —— 那时候还没有条形图。
                    // ⚠️ 判据不能看「键在不在」：新代码 Save 时会把两个键都写成 0，
                    //    于是"键存在但两项相加 ≠ 总数"，用 ContainsKey 判断会永远跳过修复。
                    //    正确判据 = 两项相加和总失败数对不对得上。
                    if (day.Fail > 0 && day.FailLocal + day.FailUp != day.Fail)
                    {
                        int tot = day.Local + day.Up;
                        if (tot > 0)
                        {
                            day.FailLocal = (int)Math.Round((double)day.Fail * day.Local / tot);
                            day.FailUp = day.Fail - day.FailLocal;
                        }
                        else day.FailLocal = day.Fail;      // 分不出来就都算本地（罕见）
                    }
                    _days[kv.Key] = day;
                }
                // 老数据里没有 firstSeen：拿最早那天当起点（比"今天"更符合事实）
                if (_days.Count > 0)
                {
                    var earliest = _days.Keys.Min(StringComparer.Ordinal);
                    if (DateTime.TryParse(earliest, out var ed) && ed < FirstSeen) FirstSeen = ed;
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
                        ["FailLocal"] = kv.Value.FailLocal,
                        ["FailUp"] = kv.Value.FailUp,
                        ["Bytes"] = kv.Value.Bytes,
                        ["Ms"] = kv.Value.Ms,
                        ["PromptTok"] = kv.Value.PromptTok,
                        ["ComplTok"] = kv.Value.ComplTok
                    };
                }
                _dirty = false;
                _lastSave = DateTime.Now;
            }
            var root = new JsonObject
            {
                ["firstSeen"] = FirstSeen.ToString("yyyy-MM-dd"),
                ["days"] = days
            };
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
            if (status >= 400)
            {
                d.Fail++;
                // 失败再按"走本地/走外地"拆开：条形图要画「本地/本地失败/外地/外地失败」四段
                if (upstream) d.FailUp++; else d.FailLocal++;
            }
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
        to.FailLocal += from.FailLocal; to.FailUp += from.FailUp;
        to.Bytes += from.Bytes; to.Ms += from.Ms;
        to.PromptTok += from.PromptTok; to.ComplTok += from.ComplTok;
    }

    private static JsonObject Day2Json(UsageDay d) => new()
    {
        ["req"] = d.Req,
        ["local"] = d.Local,
        ["up"] = d.Up,
        ["fail"] = d.Fail,
        ["failLocal"] = d.FailLocal,
        ["failUp"] = d.FailUp,
        ["bytes"] = d.Bytes,
        ["ms"] = d.Ms,
        ["pt"] = d.PromptTok,
        ["ct"] = d.ComplTok
    };

    /// <summary>
    /// 给界面用的聚合：今天 / 本月 / 今年 / 累计 + 日 / 月 / 年 三组柱子。
    ///
    /// 柱子只画「装上那天 → 今天」这一段，**装了几格就画几格**：
    ///   10/18 装的，日=2 根（10/18、10/19）、月=1 根（10月）、年=1 根（2026）。
    /// 以前写死往前 7 天 / 12 月 / 5 年，刚装的人会看到 10/13…10/17、2022…2025
    /// 一大片**永远不会有数据**的空格子，纯占地方。
    ///
    /// 上限（7 / 12 / 5）只在"用得久"时才起作用 —— 那时候确实该只看最近一段。
    /// </summary>
    public JsonObject Json()
    {
        var now = DateTime.Now;
        var first = FirstSeen.Date > now.Date ? now.Date : FirstSeen.Date;

        // 「装上到现在」各粒度跨几格（含首尾 +1）
        int daySpan = (int)(now.Date - first.Date).TotalDays + 1;
        int monthSpan = (now.Year - first.Year) * 12 + now.Month - first.Month + 1;
        int yearSpan = now.Year - first.Year + 1;

        // 只截上限，不抬下限 —— 装机头几天就该只有一两根柱子
        int dayCount = Math.Clamp(daySpan, 1, 7);
        int monthCount = Math.Clamp(monthSpan, 1, 12);
        int yearCount = Math.Clamp(yearSpan, 1, 5);

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
                    // ⚠️ 不能精确查 _days：那里的键是完整日期（2026-10-18），
                    //    而月 / 年序列的键是 2026-10 / 2026 —— TryGetValue 必然落空，
                    //    于是月、年柱子全是 0（而汇总数字又是对的，很难看出问题）。
                    //    日序列恰好键格式一致，所以一直没暴露这个 bug。
                    //    正确做法：按前缀把该区间里的每一天合起来。
                    var day = new UsageDay();
                    foreach (var kv in _days)
                    {
                        if (!kv.Key.StartsWith(k, StringComparison.Ordinal)) continue;
                        var x = kv.Value;
                        day.Req += x.Req; day.Local += x.Local; day.Up += x.Up;
                        day.Fail += x.Fail; day.FailLocal += x.FailLocal; day.FailUp += x.FailUp;
                        day.Bytes += x.Bytes; day.Ms += x.Ms;
                        day.PromptTok += x.PromptTok; day.ComplTok += x.ComplTok;
                    }
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
                ["firstSeen"] = first.ToString("yyyy-MM-dd"),
                ["today"] = Day2Json(Sum(k => k == now.ToString("yyyy-MM-dd"))),
                ["month"] = Day2Json(Sum(k => k.StartsWith(now.ToString("yyyy-MM"), StringComparison.Ordinal))),
                ["year"] = Day2Json(Sum(k => k.StartsWith(now.ToString("yyyy"), StringComparison.Ordinal))),
                ["total"] = Day2Json(Sum(_ => true)),
                ["days"] = Series(dayCount, i => now.AddDays(i - (dayCount - 1)).Date,
                                  d => d.ToString("yyyy-MM-dd"), d => d.ToString("M/d")),
                ["months"] = Series(monthCount, i => monthStart.AddMonths(i - (monthCount - 1)),
                                    d => d.ToString("yyyy-MM"), d => d.ToString("M月")),
                ["years"] = Series(yearCount, i => new DateTime(now.Year - (yearCount - 1 - i), 1, 1),
                                   d => d.ToString("yyyy"), d => d.ToString("yyyy"))
            };
        }
    }
}
