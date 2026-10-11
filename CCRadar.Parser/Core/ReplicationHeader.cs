using CCRadar.Parser.Codec;

namespace CCRadar.Parser.Core;

/// <summary>
/// replication_header.py 的 C# 等价实现 —— package-map 前缀专用；
/// 普通 delta bunch 没有 NewActor 头（Python 原注释）。
/// </summary>
public static class ReplicationHeader
{
    /// <summary>
    /// 读 exports/must-map 前缀（Python: exports_prefix）。
    /// bunch 无 hx 且无 hm 时返回 ([], 0)。
    /// </summary>
    /// <param name="payload">bunch 载荷字节。</param>
    /// <param name="pbits">载荷总位数。</param>
    /// <param name="hx">bHasPackageMapExports。</param>
    /// <param name="hm">bHasMustBeMappedGUIDs。</param>
    /// <param name="frags">partial bunch 的 fragment 边界 [(start_bit, hx, hm)]。</param>
    /// <returns>(exports 条目列表, 前缀结束位)。</returns>
    public static (List<Dictionary<string, object?>> Exports, int Pos) ExportsPrefix(
        ReadOnlySpan<byte> payload, int pbits, bool hx, bool hm,
        List<(int Start, bool Hx, bool Hm)>? frags = null)
    {
        var exports = new List<Dictionary<string, object?>>();
        if (!hx && !hm) return (exports, 0);
        var pos = 0;
        if (hx)
        {
            if (frags != null)
            {
                foreach (var (start, fragHx, _) in frags)
                {
                    if (!fragHx) continue;
                    (_, pos) = Sna.ReadExports2(payload, pbits, start, exports);
                }
            }
            else
            {
                (_, pos) = Sna.ReadExports2(payload, pbits, pos, exports);
            }
        }
        if (hm)
        {
            // must-map 段起点：fragment 中第一个带 hm 的；否则接 exports 之后。
            var start = pos;
            if (frags != null)
            {
                foreach (var (fstart, _, fragHm) in frags)
                {
                    if (!fragHm) continue;
                    start = fstart;
                    break;
                }
            }
            // 直接读 16 位数量（Python: sna.rbits(bits, start, 16)）。
            var n = (int)BitPrimitives.ReadBitsAt(payload, start, 16);
            if (n > 256) throw new SnaBad("too many must-map GUIDs");
            pos = start + 16;
            for (var i = 0; i < n; i++)
            {
                (_, pos) = Sna.Packed(payload, pbits, pos);
            }
        }
        return (exports, pos);
    }
}
