using System.Text;

namespace CNM.Core;

/// <summary>
/// 极简 GGUF 头解析：只读元数据 KV，不碰张量。
/// 目的：拿到架构 / 上下文长度 / 量化 / 是否带视觉编码器 / 是否有 chat 模板。
/// </summary>
public static class Gguf
{
    private const int Magic = 0x46554747; // "GGUF" little-endian

    // 值类型
    private const uint T_UINT8 = 0, T_INT8 = 1, T_UINT16 = 2, T_INT16 = 3, T_UINT32 = 4, T_INT32 = 5,
                       T_FLOAT32 = 6, T_BOOL = 7, T_STRING = 8, T_ARRAY = 9, T_UINT64 = 10, T_INT64 = 11, T_FLOAT64 = 12;

    public sealed class Meta
    {
        public uint Version;
        public ulong TensorCount;
        public ulong KvCount;
        public Dictionary<string, object> Values = new(StringComparer.Ordinal);
        public bool HasChatTemplate;
        public bool HasVision;
        public bool IsMmproj;
        public string Arch = "";
        public int ContextLength;
        public string Name = "";
        public string ReadError = "";

        public string Str(string key) => Values.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

        /// <summary>找 "&lt;arch&gt;.context_length"，退化为任意 *.context_length</summary>
        public int Ctx()
        {
            if (ContextLength > 0) return ContextLength;
            if (Arch.Length > 0 && Values.TryGetValue(Arch + ".context_length", out var c) && c is ulong u)
            {
                ContextLength = (int)Math.Min(u, int.MaxValue);
                return ContextLength;
            }
            foreach (var kv in Values)
            {
                if (kv.Key.EndsWith(".context_length") && kv.Value is ulong uu)
                {
                    ContextLength = (int)Math.Min(uu, int.MaxValue);
                    return ContextLength;
                }
            }
            return 0;
        }
    }

    private sealed class Reader : IDisposable
    {
        private readonly FileStream _fs;
        private readonly byte[] _buf = new byte[64 * 1024];
        private int _pos, _len;
        public long BytesRead;

        public Reader(string path) { _fs = File.OpenRead(path); }
        public bool Eof => _pos >= _len && _fs.Position >= _fs.Length;

        private void Fill()
        {
            _len = _fs.Read(_buf, 0, _buf.Length);
            _pos = 0;
            BytesRead += _len;
        }

        public byte[] Read(int n)
        {
            var outb = new byte[n];
            int got = 0;
            while (got < n)
            {
                if (_pos >= _len) { Fill(); if (_len == 0) throw new EndOfStreamException(); }
                int take = Math.Min(n - got, _len - _pos);
                Array.Copy(_buf, _pos, outb, got, take);
                _pos += take; got += take;
            }
            return outb;
        }

        public uint U32() { var b = Read(4); return BitConverter.ToUInt32(b, 0); }
        public ulong U64() { var b = Read(8); return BitConverter.ToUInt64(b, 0); }

        /// <summary>跳过 n 字节（尽量用 seek）</summary>
        public void Skip(long n)
        {
            int inBuf = _len - _pos;
            if (n <= inBuf) { _pos += (int)n; return; }
            _fs.Seek(n - inBuf, SeekOrigin.Current);
            _pos = _len = 0;
            BytesRead += n - inBuf;
        }

        public void Dispose() => _fs.Dispose();
    }

    public static Meta Read(string path, int softCapBytes = 8 * 1024 * 1024)
    {
        var m = new Meta();
        try
        {
            using var r = new Reader(path);
            if (r.U32() != Magic) { m.ReadError = "不是 GGUF 文件"; return m; }
            m.Version = r.U32();
            m.TensorCount = r.U64();
            m.KvCount = r.U64();

            for (ulong i = 0; i < m.KvCount; i++)
            {
                // key
                var klen = (int)r.U64();
                if (klen <= 0 || klen > 1 << 20) break;
                string key = Encoding.UTF8.GetString(r.Read(klen));

                uint type = r.U32();

                if (type == T_ARRAY)
                {
                    uint etype = r.U32();
                    ulong count = r.U64();
                    if (key == "tokenizer.chat_template") m.HasChatTemplate = true;
                    if (key.StartsWith("clip.") || key.StartsWith("mmproj.")) m.HasVision = true;

                    if (etype == T_STRING)
                    {
                        // 分词表：一旦遇到就说明我们要的 KV 基本读完了，直接收工
                        if (count > 2000) break;
                        for (ulong j = 0; j < count; j++)
                        {
                            int sl = (int)r.U64();
                            if (sl > 0) r.Skip(sl);
                        }
                    }
                    else if (etype == T_ARRAY) { break; } // 嵌套数组，放弃
                    else
                    {
                        long esz = ElemSize(etype);
                        if (esz < 0) break;
                        // count 是文件里写着的，可能被改坏或压根不是 GGUF：
                        // 乘出来一溢出就变成负数，Skip 会往回跳。超软上限的直接不伺候。
                        if (count > (ulong)(softCapBytes * 4L / Math.Max(1, esz))) break;
                        r.Skip((long)count * esz);
                    }
                }
                else
                {
                    object val = ReadValue(r, type, key, m);
                    if (val != null) m.Values[key] = val;
                    if (key == "tokenizer.chat_template") m.HasChatTemplate = true;
                    if (key.StartsWith("clip.") || key.StartsWith("mmproj.")) m.HasVision = true;
                }

                if (r.BytesRead > softCapBytes) break;
            }

            m.Arch = m.Str("general.architecture");
            m.Name = m.Str("general.name");
            m.IsMmproj = m.Arch.Equals("clip", StringComparison.OrdinalIgnoreCase)
                      || Path.GetFileName(path).StartsWith("mmproj", StringComparison.OrdinalIgnoreCase)
                      || m.HasVision && m.TensorCount < 1200 && m.Str("general.architecture").Length == 0;
            m.Ctx();
        }
        catch (Exception ex)
        {
            m.ReadError = ex.GetType().Name + ": " + ex.Message;
        }
        return m;
    }

    private static long ElemSize(uint t) => t switch
    {
        T_UINT8 or T_INT8 or T_BOOL => 1,
        T_UINT16 or T_INT16 => 2,
        T_UINT32 or T_INT32 or T_FLOAT32 => 4,
        T_UINT64 or T_INT64 or T_FLOAT64 => 8,
        _ => -1
    };

    private static object ReadValue(Reader r, uint type, string key, Meta m)
    {
        switch (type)
        {
            case T_UINT8: return (ulong)r.Read(1)[0];
            case T_INT8: return (long)(sbyte)r.Read(1)[0];
            case T_UINT16: return (ulong)BitConverter.ToUInt16(r.Read(2), 0);
            case T_INT16: return (long)BitConverter.ToInt16(r.Read(2), 0);
            case T_UINT32: return (ulong)r.U32();
            case T_INT32: return (long)BitConverter.ToInt32(r.Read(4), 0);
            case T_UINT64: return r.U64();
            case T_INT64: return BitConverter.ToInt64(r.Read(8), 0);
            case T_FLOAT32: return BitConverter.ToSingle(r.Read(4), 0);
            case T_FLOAT64: return BitConverter.ToDouble(r.Read(8), 0);
            case T_BOOL: return r.Read(1)[0] != 0;
            case T_STRING:
                int len = (int)r.U64();
                if (len < 0 || len > 1 << 22) throw new InvalidDataException("字符串过长");
                var s = Encoding.UTF8.GetString(r.Read(len));
                // 分词表之类的大字符串不进内存
                return key.StartsWith("tokenizer.ggml.tokens") || key.StartsWith("tokenizer.ggml.merges") ? null : s;
            default: throw new InvalidDataException("未知类型 " + type);
        }
    }
}
