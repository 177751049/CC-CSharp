using System.Text;
using System.Text.RegularExpressions;

namespace CCRadar.Parser.Core;

/// <summary>
/// ai_name_table.py 的 C# 等价实现 —— LOC_AICharacterNameTable 每局名字表解析
/// + AI 角色 key 关联。
///
/// 逆向结论（round75 坐实，Python 原注释保留）：
///   1. AI 角色（BP_DFMCharacter_AI_*_C）spawn 无 PlayerState 引用（h14/h18/h19 全
///      None），名字字段承载本地化引用：
///          LOCTABLE(".../LOC_AICharacterNameTable.LOC_AICharacterNameTable", "&lt;N&gt;_Name")
///   2. 名字表在 wire 明文传输，每局一张（同 key 跨局不同名），结构：
///          表名(49B) + \0 + 4B padding + int32 count
///          + count × [ "&lt;N&gt;_Name\0" + (int32 A + 变宽常量 + FString(len+UTF-16)) × k ]
///   3. key 与 AI 子类绑定实测（flow 1368）：Firebat → 167_Name，ShielderLight → 162_Name。
/// </summary>
public static partial class AiNameTable
{
    private static readonly byte[] FullName = Encoding.ASCII.GetBytes(
        "LOC_AICharacterNameTable.LOC_AICharacterNameTable");
    private static readonly byte[] TableHead = Encoding.ASCII.GetBytes(
        "LOC_AICharacterNameTable.LOC_AICharacterNameTable\0");

    [GeneratedRegex(@"LOCTABLE\(""([^""]*LOC_AICharacterNameTable[^""]*)"",\s*""(\d+)_Name""\)")]
    private static partial Regex RefRegex();

    [GeneratedRegex(@"(\d+)_Name\x00")]
    private static partial Regex KeyRegex();

    /// <summary>名字合理性检查（Python: plausible）。</summary>
    public static bool Plausible(string s)
    {
        if (s.Length is < 1 or > 64) return false;
        foreach (var c in s)
        {
            if (c < 0x20 || c == 0x7F) return false;
        }
        if (s.Contains('\ufffd')) return false;
        var good = s.Count(c => char.IsLetterOrDigit(c) || " _-·.`~!@#".Contains(c));
        return good >= Math.Min(2, s.Length);
    }

    /// <summary>
    /// key 后位置 p 读一个条目（Python: _read_entry_name）：
    /// int32 A + 变宽常量 + FString(len&lt;=0 取负, UTF-16)。
    /// 长度字段间距不固定，在 p+4..p+12 窗口扫描定位（界限 [-128,-1]）。
    /// </summary>
    private static (string? Name, int Pos) ReadEntryName(byte[] tail, int p)
    {
        for (var delta = 4; delta < 13; delta++)
        {
            var q = p + delta;
            if (q + 4 > tail.Length) break;
            var n = BitConverter.ToInt32(tail, q);
            if (n is < -128 or > -1) continue;
            var units = new List<char>();
            var r = q + 4;
            var ok = true;
            for (var i = 0; i < -n; i++)
            {
                if (r + 2 > tail.Length) { ok = false; break; }
                var u = tail[r] | (tail[r + 1] << 8);
                r += 2;
                if (u == 0) break;
                if (!(u is >= 0x20 and <= 0xD7FF || u is >= 0xE000 and <= 0xFFFF)) { ok = false; break; }
                units.Add((char)u);
            }
            if (!ok || units.Count == 0) continue;
            var s = new string(units.ToArray());
            if (Plausible(s)) return (s, r);
        }
        return (null, p);
    }

    /// <summary>
    /// 单个报文 payload → {key:int → name}（无表头返回空）。
    /// 同实例内后写覆盖先写；调用方负责跨实例合并策略。
    /// </summary>
    public static Dictionary<int, string> ParseTableInstance(byte[] payload)
    {
        var output = new Dictionary<int, string>();
        var m = IndexOf(payload, TableHead);
        if (m < 0) return output;
        var tail = payload[m..];
        var p = FullName.Length + 1 + 4;   // 表名 + \0 + 4B padding
        if (p + 4 > tail.Length) return output;
        var count = BitConverter.ToInt32(tail, p);
        p += 4;
        if (count is < 1 or > 64) return output;
        for (var i = 0; i < count; i++)
        {
            var km = KeyRegex().Match(Encoding.ASCII.GetString(tail, Math.Min(p, tail.Length), Math.Max(0, tail.Length - p)));
            if (!km.Success || km.Index != 0) break;
            var key = int.Parse(km.Groups[1].Value);
            p = p + km.Length;
            while (p + 16 <= tail.Length)
            {
                var a = BitConverter.ToInt32(tail, p);
                if (a > 0x10000) break;
                var (name, p2) = ReadEntryName(tail, p);
                if (name is null) break;
                output[key] = name;   // 后条目覆盖（同 key 多条目取最新）
                p = p2;
            }
        }
        return output;
    }

    /// <summary>AI 角色 spawn payload → LOCTABLE key（int）或 null（Python: extract_ref_key）。</summary>
    public static int? ExtractRefKey(byte[] payload)
    {
        var text = Encoding.ASCII.GetString(payload);
        var m = RefRegex().Match(text);
        return m.Success ? int.Parse(m.Groups[2].Value) : null;
    }

    /// <summary>
    /// 扫全部逻辑 bunch（Python: collect_table，输入 (seq, payload) 序列）
    /// → 当局 {key → name}。同 key 多实例取 seq 更大者（对局内状态更新以最新为准）。
    /// </summary>
    public static Dictionary<int, string> CollectTable(IEnumerable<(long Seq, byte[] Payload)> bunches)
    {
        var bestSeq = new Dictionary<int, (long Seq, string Name)>();
        foreach (var (seq, payload) in bunches)
        {
            if (IndexOf(payload, TableHead) < 0) continue;
            foreach (var (k, name) in ParseTableInstance(payload))
            {
                if (!bestSeq.TryGetValue(k, out var cur) || seq >= cur.Seq)
                {
                    bestSeq[k] = (seq, name);
                }
            }
        }
        return bestSeq.ToDictionary(kv => kv.Key, kv => kv.Value.Name);
    }

    /// <summary>字节序列搜索（Python: bytes.find 的等价）。</summary>
    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
        => haystack.IndexOf(needle);
}
