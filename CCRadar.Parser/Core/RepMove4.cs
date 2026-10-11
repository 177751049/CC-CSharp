using CCRadar.Parser.Codec;

namespace CCRadar.Parser.Core;

/// <summary>
/// repmove4.py 的 C# 等价实现 —— FRepMovementInfo item reader with FULL mask
/// modelling (0x02, 0x04)。
///
/// Mask 0x02（来自 ida_sub_F1FD0A4.c @0xf1fdd38）：
///     NetGUID (SerializeIntPacked) → +56
///     FName  (Ar&lt;&lt;FName, vtable+16) → +64
///     1 bit → +97
///     sub_F1FF2D8 : 6× u32 → +84..92（v3 误标 3×u32，实测 6×——见 v4 注释）
///     1 bit → +96
///     1 bit → +98
/// Mask 0x04（@0xf1fdd3c）：
///     1 bit → +132 ; 1 bit → +134
///     if (+132 or +134):
///         if +132: 1 byte → +133 ; PackedVector/10 → +116..124
///         if +134: PackedVector → +104..112 ; 1 bit → +135 ; 1 byte → +136
///     注意 +104 向量在 +135/+136 之前读（0xf1fe4d8 → 0xf1fe4f4 → 0xf1fe520），
///     先读 135/136 会错位帧内所有后续项。
/// Mask 0x08 : u32 +144 ; 1b +142 ; 1b +140 ; 1b +141
/// Mask 0x20 : 32 bits → +160
/// Mask 0x10 : sub_EAFCF10 object read（仍然停住）
/// </summary>
public static class RepMove4
{
    /// <summary>
    /// 读取一个移动项（Python: read_item）。FULL mask：0x02/0x04 完整建模不再停住。
    /// </summary>
    public static (Dictionary<string, object?> Item, string? Stop) ReadItem(BitReader r, RepMove3.Ctx ctx)
    {
        var sel = (int)r.ReadBits(2);
        var m = RepMove3.SelMask.TryGetValue(sel, out var known) ? known : (int)r.ReadBits(12);
        var o = new Dictionary<string, object?> { ["sel"] = sel, ["mask"] = m };

        if ((m & 0x40) != 0)
        {
            var n = ctx.Bits[3];
            if (n is < 0 or > 32) throw new BitError($"bits3={n}");
            var w = 32 - n;
            o["scalar"] = RepMove3.ZigZag((w != 0 ? r.ReadBits(w) : 0) | (ulong)ctx.Val[3]) / 10000.0;
        }
        if ((m & 0x01) != 0)
        {
            var pos = new List<object?>(3);
            for (var k = 0; k <= 2; k++)
            {
                var n = ctx.Bits[k];
                if (n is < 0 or > 32) throw new BitError($"bits{k}={n}");
                var w = 32 - n;
                pos.Add(RepMove3.ZigZag((w != 0 ? r.ReadBits(w) : 0) | (ulong)ctx.Val[k]) / 10.0);
            }
            o["pos"] = pos;
            o["pv"] = RepMove3.Pv(r);
        }
        if ((m & 0x080) != 0) o["b168"] = (long)r.ReadUInt8();
        if ((m & 0x100) != 0) o["b169"] = (long)r.ReadBit();
        if ((m & 0x200) != 0) o["b170"] = (long)r.ReadBit();
        if ((m & 0x800) != 0) o["b172"] = (long)r.ReadBit();
        if ((m & 0x400) != 0) o["b171"] = (long)r.ReadBit();
        if ((m & 0x001) != 0)
        {
            o["f52"] = (long)r.ReadBits(2);
            o["rot"] = RepMove3.Rot(r);
        }
        if ((m & 0x002) != 0)
        {
            // f1fd0a4_pseudo.c @0xf1fdd38: sub_F1FCF50(NetGUID packed) ; vtable+16 FName ;
            //   bit(+97) ; sub_F1FF2D8 ; bit(+96) ; bit(+98).
            // sub_F1FF2D8 消耗 6×u32（192 位），不是 3：flow495 seq1291 item6（mask 0xc3）
            // 实测——item7 真起点 = bit 814 = 619+1+192+1+1。3×u32 会留 96 位残差错位
            // 帧内所有后续项。
            var (guid, _) = EngineBunchCodec.ReadDfmPackedInt(r);
            o["guid"] = guid;
            o["fname64"] = EngineBunchCodec.ReadSerializedFName(r).AsDictionary();
            o["b97"] = (long)r.ReadBit();
            var u32s = new List<object?>();
            for (var i = 0; i < 6; i++) u32s.Add(r.ReadUInt32());
            o["u32_84"] = u32s;
            o["b96"] = (long)r.ReadBit();
            o["b98"] = (long)r.ReadBit();
        }
        if ((m & 0x004) != 0)
        {
            // f1fd0a4_pseudo.c @0xf1fe2c0..0xf1fe528 (mask&4 段，engine-exact)。
            // 注意 +104 向量先于 +135/+136 读，否则错位所有后续项。
            var b132 = r.ReadBit();
            var b134 = r.ReadBit();
            o["b132"] = (long)b132;
            o["b134"] = (long)b134;
            if (b132 != 0 || b134 != 0)
            {
                if (b132 != 0)
                {
                    o["b133"] = (long)r.ReadUInt8();
                    o["vec116"] = RepMove3.Pv(r);
                }
                if (b134 != 0)
                {
                    o["vec104"] = RepMove3.Pv(r);
                    o["b135"] = (long)r.ReadBit();
                    o["b136"] = (long)r.ReadUInt8();
                }
            }
        }
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

    /// <summary>
    /// 读取一帧（Python: read_frame4）——与 v3 相同的帧骨架，FULL mask 物品读取器。
    /// </summary>
    public static (Dictionary<string, object?> Frame, BitReader Reader) ReadFrame4(
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
        var ctx = new RepMove3.Ctx();
        var grp = -1;
        foreach (var i in present)
        {
            if ((i >> 4) != grp)
            {
                grp = i >> 4;
                RepMove3.ReadHeader(r, ctx);
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
            var ectx = new RepMove3.Ctx();
            RepMove3.ReadHeader(r, ectx);
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
