using CCRadar.Parser.Codec;

namespace CCRadar.Parser.Core;

/// <summary>
/// repmove3.py 的 C# 等价实现 —— FRepMovementInfo (h21) 帧 + 物品读取器。
///
/// Item 字段顺序（libUE4 偏移 = 执行顺序，Python 原注释保留）：
///   sel=2bits → mask {0:0x41,1:0x20,2:0x61} else 12 bits
///   0x0040 : scalar  (32-ctx.bits[3])  zigzag/10000  → item+164
///   0x001a: 3 comps (32-ctx.bits[k])  zigzag/10      → item+24..32  (POSITION)
///   0x001b: PackedVector h=SerializeInt(24), Max=1&lt;&lt;(h+2), 3×SerializeInt(Max)-1&lt;&lt;(h+1)
///   0x0080 : 1 byte → +168 | 0x100: 1 bit → +169 | 0x200: 1 bit → +170
///   0x0800 : 1 bit  → +172 | 0x400: 1 bit → +171
///   0x001c: 2 bits (+52) + FRotator::SerializeCompressed(byte) → +40,44,48
///   0x0002: SerializeIntPacked NetGUID → +56 ; Ar&lt;&lt;FName → +64 ; 1b ; sub_F1FF2D8 ; 2b
///   0x0004: 1b(+132) 1b(+134) [1 byte(+133)] PackedVector(+116..124) [PackedVector(+104..112)] 1 byte(+136)
///   0x0008: u32 → +144 ; 3 bits → +142,+140,+141
///   0x0020: 32 bits → +160
///   0x0010: sub_EAFCF10 object read → +152
///
/// Frame:
///   u32 Count ; u32 mask[ceil(Count/32)]
///   present items (header re-read whenever (i&gt;&gt;4) changes)
///   1 bit ; 1 bit hasBitArray [Count bits] ; 1 bit hasEmbedded [header + item]
///   u32 Count2 ; u32 mask2[ceil(Count2/32)]
/// </summary>
public static class RepMove3
{
    /// <summary>sel → mask 映射（Python: SEL_MASK）。</summary>
    public static readonly Dictionary<int, int> SelMask = new() { [0] = 0x41, [1] = 0x20, [2] = 0x61 };

    /// <summary>v3 未建模的 mask 位（Python: UNMODELLED = 0x002 | 0x004 | 0x010）。</summary>
    public const int Unmodelled = 0x002 | 0x004 | 0x010;

    /// <summary>zigzag 解码（Python: zz）：u32 → 带符号 i32（溢出到负时减 2^32）。</summary>
    public static long ZigZag(ulong v)
    {
        var u = (uint)(v & 0xFFFFFFFF);
        var s = ((u >> 1) ^ (uint)-(long)(u & 1)) & 0xFFFFFFFF;
        return (s & 0x80000000) != 0 ? s - (1L << 32) : s;
    }

    /// <summary>16 项一组的量化上下文（Python: Ctx）。</summary>
    public sealed class Ctx
    {
        public long[] Val { get; } = [0, 0, 0, 0];
        public int[] Bits { get; } = [-1, -1, -1, -1];
        public int Flags { get; set; }
    }

    /// <summary>
    /// 组头读取（Python: read_header）：flags 2 位；flags&amp;1 → 3 个量化分量，
    /// flags&amp;2 → 第 4 个（scalar）。每个分量：5 位位宽 + 有值时原始位。
    /// 原始值左移 (32-nb) 对齐到高位（&amp;0xFFFFFFFF 截断）。
    /// </summary>
    public static void ReadHeader(BitReader r, Ctx ctx)
    {
        ctx.Flags = (int)r.ReadBits(2);
        if ((ctx.Flags & 1) != 0)
        {
            for (var k = 0; k <= 2; k++)
            {
                var nb = (int)r.ReadBits(5);
                ctx.Bits[k] = nb;
                var raw = nb != 0 ? r.ReadBits(nb) : 0;
                ctx.Val[k] = nb != 0 ? (long)((raw << (32 - nb)) & 0xFFFFFFFF) : 0;
            }
        }
        if ((ctx.Flags & 2) != 0)
        {
            var nb = (int)r.ReadBits(5);
            ctx.Bits[3] = nb;
            var raw = nb != 0 ? r.ReadBits(nb) : 0;
            ctx.Val[3] = nb != 0 ? (long)((raw << (32 - nb)) & 0xFFFFFFFF) : 0;
        }
    }

    /// <summary>PackedVector（Python: _pv）：h=SerializeInt(24)，Max=1&lt;&lt;(h+2)，3×SerializeInt(Max)-bias。</summary>
    public static long[] Pv(BitReader r)
    {
        var h = r.ReadSerializeInt(24);
        var mx = 1 << (h + 2);
        var bias = 1 << (h + 1);
        var result = new long[3];
        for (var i = 0; i < 3; i++)
        {
            result[i] = r.ReadSerializeInt(mx) - bias;
        }
        return result;
    }

    /// <summary>压缩旋转（Python: _rot）：每轴 1 非零位 + u8 × 360/256。</summary>
    public static double[] Rot(BitReader r)
    {
        var result = new double[3];
        for (var i = 0; i < 3; i++)
        {
            result[i] = r.ReadBit() != 0 ? r.ReadUInt8() * 360.0 / 256.0 : 0.0;
        }
        return result;
    }

    /// <summary>
    /// 读取一个移动项（Python: read_item）。返回 (dict, 停止原因或 null)；
    /// 停止时调用方必须放弃整条流（引擎未建模分支出现）。
    /// </summary>
    public static (Dictionary<string, object?> Item, string? Stop) ReadItem(BitReader r, Ctx ctx)
    {
        var sel = (int)r.ReadBits(2);
        var m = SelMask.TryGetValue(sel, out var known) ? known : (int)r.ReadBits(12);
        var o = new Dictionary<string, object?> { ["sel"] = sel, ["mask"] = m };

        if ((m & 0x40) != 0)
        {
            var n = ctx.Bits[3];
            if (n is < 0 or > 32) throw new BitError($"bits3={n}");
            var w = 32 - n;
            o["scalar"] = ZigZag((w != 0 ? r.ReadBits(w) : 0) | (ulong)ctx.Val[3]) / 10000.0;
        }
        if ((m & 0x01) != 0)
        {
            var pos = new List<object?>(3);
            for (var k = 0; k <= 2; k++)
            {
                var n = ctx.Bits[k];
                if (n is < 0 or > 32) throw new BitError($"bits{k}={n}");
                var w = 32 - n;
                pos.Add(ZigZag((w != 0 ? r.ReadBits(w) : 0) | (ulong)ctx.Val[k]) / 10.0);
            }
            o["pos"] = pos;
            o["pv"] = Pv(r);
        }
        if ((m & 0x080) != 0) o["b168"] = (long)r.ReadUInt8();
        if ((m & 0x100) != 0) o["b169"] = (long)r.ReadBit();
        if ((m & 0x200) != 0) o["b170"] = (long)r.ReadBit();
        if ((m & 0x800) != 0) o["b172"] = (long)r.ReadBit();
        if ((m & 0x400) != 0) o["b171"] = (long)r.ReadBit();
        if ((m & 0x001) != 0)
        {
            o["f52"] = (long)r.ReadBits(2);
            o["rot"] = Rot(r);
        }
        if ((m & 0x002) != 0)
        {
            // v3 未建模 NetGUID/FName 段：读 NetGUID 后停住。
            var (guid, _) = EngineBunchCodec.ReadDfmPackedInt(r);
            o["guid"] = guid;
            return (o, "m&2");
        }
        if ((m & 0x004) != 0) return (o, "m&4");
        if ((m & 0x008) != 0)
        {
            o["u144"] = (long)r.ReadBits(32);
            o["b142"] = (long)r.ReadBit();
            o["b140"] = (long)r.ReadBit();
            o["b141"] = (long)r.ReadBit();
        }
        if ((m & 0x020) != 0) o["u160"] = (long)r.ReadBits(32);
        if ((m & 0x010) != 0) return (o, "m&0x10");
        return (o, null);
    }

    /// <summary>读取一帧（Python: read_frame）。输出 dict 结构与 Python 一致。</summary>
    public static (Dictionary<string, object?> Frame, BitReader Reader) ReadFrame(
        byte[] payload, int start, int end, int maxCount = 512)
    {
        var r = new BitReader(payload, end) { Position = start };
        var c = (long)r.ReadBits(32);
        if (c > maxCount) throw new BitError($"count {c}");
        var mask = new List<ulong>();
        for (var i = 0; i < (int)((c + 31) / 32); i++) mask.Add(r.ReadBits(32));
        var present = new List<int>();
        for (var i = 0; i < (int)c; i++)
        {
            if (((mask[i / 32] >> (i % 32)) & 1) != 0) present.Add(i);
        }
        var fr = new Dictionary<string, object?>
        {
            ["count"] = (long)c,
            ["present"] = present.Select(p => (object?)(long)p).ToList(),
            ["items"] = new Dictionary<int, Dictionary<string, object?>>(),
            ["stopped"] = null,
        };
        var ctx = new Ctx();
        var grp = -1;
        foreach (var i in present)
        {
            if ((i >> 4) != grp)
            {
                grp = i >> 4;
                ReadHeader(r, ctx);
            }
            var (it, stop) = ReadItem(r, ctx);
            ((Dictionary<int, Dictionary<string, object?>>)fr["items"]!)[i] = it;
            if (stop is not null)
            {
                fr["stopped"] = new List<object?> { $"item{i}", stop };
                fr["pos"] = r.Position;
                fr["left"] = end - r.Position;
                return (fr, r);
            }
        }
        fr["bit_a"] = (long)r.ReadBit();
        fr["has_bits"] = (long)r.ReadBit();
        if ((long)fr["has_bits"]! != 0 && c != 0)
        {
            fr["bitarray"] = (long)r.ReadBits((int)c);
        }
        fr["has_embedded"] = (long)r.ReadBit();
        if ((long)fr["has_embedded"]! != 0)
        {
            var ectx = new Ctx();
            ReadHeader(r, ectx);
            var (it, stop) = ReadItem(r, ectx);
            fr["embedded"] = it;
            if (stop is not null)
            {
                fr["stopped"] = new List<object?> { "embedded", stop };
                fr["pos"] = r.Position;
                fr["left"] = end - r.Position;
                return (fr, r);
            }
        }
        fr["tail32"] = (long)r.ReadBits(32);
        fr["pos"] = r.Position;
        fr["left"] = end - r.Position;
        return (fr, r);
    }
}
