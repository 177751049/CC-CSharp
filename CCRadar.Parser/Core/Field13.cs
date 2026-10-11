using CCRadar.Parser.Codec;

namespace CCRadar.Parser.Core;

/// <summary>
/// field13.py 的 C# 等价实现 —— NetMulticast_CharacterLocationNew（小队位置广播）
/// 确定性解码。
///
/// 语义（round75 定版）：一次广播 3 条 GPS 轨迹 = 小队 3 人（我 + 2 队友），
/// 与游戏内小地图小队标记一致；服务器直发，不依赖 MRA 槽位认领。
/// vals64 为各轨迹的 wire id；"我"的 wire id 跨局稳定（0xBE3732C5A970451E/1F，
/// flag 0/1），既作帧定位锚也作自机轨迹识别。
///
/// 位序说明：Python 侧把载荷展开成 LSB-first 位数组（np.unpackbits little）后
/// 逐位组值；C# 直接用 BitPrimitives.ReadBitsAt（同一 LSB-first 语义）按绝对位读，
/// 逐位等价。
/// </summary>
public static class Field13
{
    /// <summary>"我"的 wire id（Python: SELF_WIRE）。</summary>
    public static readonly ulong[] SelfWire = [0xBE3732C5A970451EUL, 0xBE3732C5A970451FUL];

    private static readonly byte[][] Patterns =
        SelfWire.Select(BitConverter.GetBytes).ToArray();

    /// <summary>
    /// 廉价预过滤：只命中「全量帧」（含"我"的 wire id）。
    /// field13 另有增量帧（仅坐标、无 vals64），预过滤抓不到 —— 需全量解码。
    /// </summary>
    public static bool PayloadHit(ReadOnlySpan<byte> payload)
    {
        foreach (var pattern in Patterns)
        {
            if (payload.IndexOf(pattern) >= 0) return true;
        }
        return false;
    }

    /// <summary>vals64 里"我"的下标；找不到返回 0（对局惯例 locs[0]=自机）。</summary>
    public static int SelfIndex(IEnumerable<ulong>? vals64)
    {
        if (vals64 is not null)
        {
            var i = 0;
            foreach (var v in vals64)
            {
                if (Array.IndexOf(SelfWire, v) >= 0) return i;
                i++;
            }
        }
        return 0;
    }

    /// <summary>
    /// DFM packed int（Python: _dfm_packed）：每字节 bit0=继续标志、bits1..7=数据段，
    /// 最多 max_bytes=6 字节。返回 (值, 结束位·绝对位)。越界抛 ArgumentException。
    /// </summary>
    private static (ulong Value, int Pos) DfmPacked(byte[] data, int bitLength, int pos, int maxBytes = 6)
    {
        ulong val = 0;
        var shift = 0;
        var nb = 0;
        while (true)
        {
            if (pos + 8 > bitLength || nb >= maxBytes) throw new ArgumentException("overflow");
            var enc = BitPrimitives.ReadBitsAt(data, pos, 8);
            pos += 8;
            nb++;
            val |= (enc >> 1) << shift;
            if ((enc & 1) == 0) return (val, pos);
            shift += 7;
        }
    }

    /// <summary>
    /// 解码一条 field13 广播（Python: decode）。
    /// 返回 {"vals64":[...], "vals32":[...], "locs":[[x,y,z](cm), ...]} 或 null。
    ///
    /// 帧结构（495 局 478 帧逐帧实测，全部一致）：
    ///     [field_index 4bit][content_len packed][payload_len packed]
    ///     [n64 packed][vals64 = n64×64bit][n32 packed][vals32 = n32×32bit]
    ///     [2bit gap][坐标 codec: 3×axes(5bit 头+prefix)][num_locs][locs]
    /// 坐标 codec 起始按帧头动态计算（消除对局差异风险）。
    /// vals32 = 运动相关字段（实测移动时变化、静止时恒定，非朝向角度），备用。
    /// </summary>
    public static Dictionary<string, object?>? Decode(byte[] payload, int nbits)
    {
        try
        {
            // 帧头：跳过 2 位起始位，content_len packed（位位置为绝对位）。
            var (contentBits, contentStart) = DfmPacked(payload, nbits, 2);
            if (contentStart + (int)contentBits > nbits) return null;
            var contentBitsLen = (int)contentBits;
            if (contentBitsLen < 4 || BitPrimitives.ReadBitsAt(payload, contentStart, 4) != 13) return null;

            // payload_len packed（content 内相对位 4 → 绝对位 contentStart+4）。
            var (payloadBits, fpAbs) = DfmPacked(payload, nbits, contentStart + 4);
            if (fpAbs + (int)payloadBits > contentStart + contentBitsLen) return null;
            var fpBits = (int)payloadBits;   // fp 区间 = [fpAbs, fpAbs+fpBits)

            // n64 + vals64（各 64 位，LSB-first，等价 _read_bits）。
            var (n64, p) = DfmPacked(payload, fpAbs + fpBits, fpAbs);
            var vals64 = new List<ulong>();
            for (var i = 0; i < (int)n64; i++)
            {
                vals64.Add(BitPrimitives.ReadBitsAt(payload, p + i * 64, 64));
            }
            p += (int)n64 * 64;

            // n32 + vals32。
            var (n32, pAbs) = DfmPacked(payload, fpAbs + fpBits, p);
            p = pAbs;
            var vals32 = new List<ulong>();
            for (var i = 0; i < (int)n32; i++)
            {
                vals32.Add(BitPrimitives.ReadBitsAt(payload, p + i * 32, 32));
            }
            p += (int)n32 * 32;

            p += 2;   // 2bit gap（478 帧实测恒定；引擎保留位）

            // 坐标 codec：3 组轴头（5 位位宽 pb + pb 位 prefix）。
            var axes = new List<(int Pb, ulong Prefix)>();
            for (var i = 0; i < 3; i++)
            {
                var pb = (int)BitPrimitives.ReadBitsAt(payload, p, 5);
                p += 5;
                var prefix = pb > 0 ? BitPrimitives.ReadBitsAt(payload, p, pb) : 0;
                p += pb;
                axes.Add((pb, prefix));
            }
            var (numLocs, pLocs) = DfmPacked(payload, fpAbs + fpBits, p);
            p = pLocs;
            var locs = new List<object?>();
            for (var i = 0; i < (int)numLocs; i++)
            {
                var loc = new List<object?>(3);
                foreach (var (pb, prefix) in axes)
                {
                    var rw = 32 - pb;
                    var raw = rw > 0 ? BitPrimitives.ReadBitsAt(payload, p, rw) : 0;
                    p += rw;
                    var combined = (prefix << rw) | raw;
                    // zigzag 解码（Python: (combined >> 1) ^ -(combined & 1)）。
                    var sv = (long)((combined >> 1) ^ (ulong)-(long)(combined & 1));
                    loc.Add(sv * 100.0);
                }
                locs.Add(loc);
            }
            return new Dictionary<string, object?>
            {
                ["vals64"] = vals64,
                ["vals32"] = vals32,
                ["locs"] = locs,
            };
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
