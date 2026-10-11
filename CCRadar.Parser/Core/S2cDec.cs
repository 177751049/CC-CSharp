namespace CCRadar.Parser.Core;

/// <summary>
/// s2c_dec.py 的 C# 等价实现 —— 战斗 S2C UDP 去混淆（512 字节循环表 + 密文反馈 XOR）。
///
/// 模型（DOWNLINK_ENCRYPTION_0904.md §3.19/3.20）：
///     P[i] = C[i] ^ C[i-1] ^ K[i % 512],  C[-1] = 0, 每条 UDP 记录独立重置。
/// 还原后即标准引擎包，直接进 codec/repdec4 原生管线。
///
/// 该层用于 light_relay / nic_capture 等不经过 TGCP 中间人的密文链路；
/// C# 主程序的 TGCP 中间人链路拿到的是明文，不走这里（两条路互不相干，与 Python 一致）。
/// </summary>
public static class S2cDec
{
    /// <summary>从 JSON 文件加载 512 字节表（Python: load_table——JSON 数字数组）。</summary>
    public static byte[] LoadTable(string path)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var table = new byte[512];
        var i = 0;
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            table[i++] = (byte)v.GetInt32();
        }
        if (i != 512) throw new ArgumentException($"S2C 表长度 {i} != 512");
        return table;
    }

    /// <summary>
    /// 去混淆一条 UDP 记录（Python: decode_s2c）。每条记录独立重置 prev=0。
    /// </summary>
    public static byte[] DecodeS2c(ReadOnlySpan<byte> ciphertext, byte[] table)
    {
        if (table.Length != 512) throw new ArgumentException("table must be 512 entries");
        var output = new byte[ciphertext.Length];
        var prev = 0;
        for (var i = 0; i < ciphertext.Length; i++)
        {
            output[i] = (byte)(ciphertext[i] ^ prev ^ table[i & 511]);
            prev = ciphertext[i];
        }
        return output;
    }

    /// <summary>
    /// D 位置众数快速恢复（Python: recover_table）。头部正确，bunch 区带 mode(P) 残差；
    /// 无参照分布时用。
    /// </summary>
    public static byte[] RecoverTable(IReadOnlyList<byte[]> datagrams, int minCount = 8)
    {
        // 每个表位维护 (值 -> 计数) 直方图。
        var counts = new Dictionary<int, int>[512];
        foreach (var datagram in datagrams)
        {
            var prev = 0;
            for (var i = 0; i < datagram.Length; i++)
            {
                var j = i & 511;
                var v = datagram[i] ^ prev;
                (counts[j] ??= new Dictionary<int, int>())[v] = (counts[j].TryGetValue(v, out var c) ? c : 0) + 1;
                prev = datagram[i];
            }
        }
        var table = new byte[512];
        for (var j = 0; j < 512; j++)
        {
            var hist = counts[j];
            if (hist == null || hist.Count == 0) continue;
            var bestV = 0;
            var bestC = -1;
            foreach (var (v, c) in hist)
            {
                // Python: max(c, key=c.get) —— 并列时保留首个遇到的项（dict 插入序）。
                if (c > bestC) { bestC = c; bestV = v; }
            }
            // min_count 门槛不达标置 0（与 Python 一致）。
            table[j] = bestC >= minCount ? (byte)bestV : (byte)0;
        }
        return table;
    }

    /// <summary>
    /// 裸流参照分布 → 对数概率表 [512][256]（Python: load_ref_dist）。
    /// </summary>
    public static double[][] LoadRefDist(string path)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var dist = doc.RootElement.GetProperty("dist");
        var reference = new double[512][];
        var j = 0;
        foreach (var pairs in dist.EnumerateArray())
        {
            double total = 76.8;
            foreach (var pair in pairs.EnumerateArray()) total += pair[1].GetDouble();
            var tbl = new double[256];
            Array.Fill(tbl, -20.0);
            foreach (var pair in pairs.EnumerateArray())
            {
                var v = pair[0].GetInt32();
                var c = pair[1].GetDouble();
                tbl[v] = Math.Log((c + 0.3) / total);
            }
            reference[j++] = tbl;
        }
        return reference;
    }

    /// <summary>
    /// 文档 §7 评分恢复（Python: recover_table_scored）：
    /// K[j] = argmax_k Σ_c Dcnt_j(c) · log(P_ref_j(c ^ k))。
    /// </summary>
    public static byte[] RecoverTableScored(IReadOnlyList<byte[]> datagrams, double[][] reference)
    {
        var counts = new Dictionary<int, int>[512];
        foreach (var datagram in datagrams)
        {
            var prev = 0;
            for (var i = 0; i < datagram.Length; i++)
            {
                var j = i & 511;
                var v = datagram[i] ^ prev;
                (counts[j] ??= new Dictionary<int, int>())[v] = (counts[j].TryGetValue(v, out var c) ? c : 0) + 1;
                prev = datagram[i];
            }
        }
        var keys = new byte[512];
        for (var j = 0; j < 512; j++)
        {
            var pl = reference[j];
            var hist = counts[j] ?? new Dictionary<int, int>();
            var bestK = 0;
            var bestScore = double.NegativeInfinity;
            for (var k = 0; k < 256; k++)
            {
                double score = 0;
                foreach (var (c, cnt) in hist)
                {
                    score += cnt * pl[c ^ k];
                }
                if (score > bestScore) { bestScore = score; bestK = k; }
            }
            keys[j] = (byte)bestK;
        }
        return keys;
    }
}
