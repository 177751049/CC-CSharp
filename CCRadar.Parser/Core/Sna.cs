using CCRadar.Parser.Codec;

namespace CCRadar.Parser.Core;

/// <summary>引擎非法读异常（Python: sna.Bad）。</summary>
public sealed class SnaBad : Exception
{
    public SnaBad(string message) : base(message) { }
}

/// <summary>
/// sna.py 的 C# 等价实现 —— 确定性 UE4 open-bunch 前缀解码（engine-exact）。
///
/// 恢复自引擎（注释保留 Python 原版调用链）：
///   UNetConnection::ReceivedRawBunch
///     if (Bunch.bHasPackageMapExports) → UPackageMapClient::ReceiveNetGUIDBunch(Bunch)
///   UActorChannel::ReceivedBunch
///     if (Bunch.bHasMustBeMappedGUIDs) → u16 N ; N × packed FNetworkGUID
///   UActorChannel::ProcessBunch → UPackageMapClient::SerializeNewActor
///     SerializeObject(Actor); 动态 GUID 再序列化 Archetype/Level/位置/旋转/缩放/速度
///   → 内容块起点
///
/// InternalLoadObject:
///     packed FNetworkGUID ; if 0 → return
///     if (guid == 1 (default) or exporting) uint8 ExportFlags
///     if ExportFlags.bHasPath: InternalLoadObject(outer) ; FString PathName
///         if ExportFlags.bHasNetworkChecksum: uint32
///
/// 实现说明：Python 版把载荷展开成 0/1 位数组（np.unpackbits little）后逐位索引；
/// C# 版直接在字节载荷上按绝对位读（BitPrimitives.ReadBitsAt），LSB-first 位序与
/// numpy bitorder="little" 完全一致，语义逐位等价。
/// </summary>
public static class Sna
{
    /// <summary>读绝对位 pos 起的 n 位（LSB-first）。越界抛 Bad("eof")。</summary>
    public static ulong ReadBits(ReadOnlySpan<byte> data, int bitLength, ref int pos, int n)
    {
        if (pos < 0 || pos + n > bitLength) throw new SnaBad("eof");
        var v = BitPrimitives.ReadBitsAt(data, pos, n);
        pos += n;
        return v;
    }

    /// <summary>
    /// packed FNetworkGUID（Python: packed）：每 8 位一组，低 1 位是"继续"标志，
    /// 高 7 位是数据段，按 7 位组拼接（LSB 组在低有效端），最多 5 组。
    /// </summary>
    public static (ulong Value, int Pos) Packed(ReadOnlySpan<byte> data, int bitLength, int pos)
    {
        ulong v = 0;
        var cnt = 0;
        while (true)
        {
            var b = ReadBits(data, bitLength, ref pos, 8);
            v += (b >> 1) << (7 * cnt);
            cnt++;
            if ((b & 1) == 0) return (v, pos);
            if (cnt > 5) throw new SnaBad("packed>5");
        }
    }

    /// <summary>
    /// UE 序列化整数（Python: ser_int）：在 [0, vmax) 内逐位吸收（LSB-first），
    /// 位宽自适应 vmax。
    /// </summary>
    public static (ulong Value, int Pos) SerInt(ReadOnlySpan<byte> data, int bitLength, int pos, int vmax)
    {
        ulong v = 0;
        ulong mask = 1;
        while ((long)(v + mask) < vmax)
        {
            if (pos >= bitLength) throw new SnaBad("eof serint");
            if (BitPrimitives.ReadBitsAt(data, pos, 1) != 0) v |= mask;
            pos++;
            mask <<= 1;
        }
        return (v, pos);
    }

    /// <summary>
    /// 位流上的 UE FString（Python: fstring）：i32 长度（含结尾 NUL，可负表示 UTF-16），
    /// ASCII(8bit)/UTF-16LE(16bit)，去尾部 NUL。maxu 默认 4096。
    /// </summary>
    public static (string Value, int Pos) FString(ReadOnlySpan<byte> data, int bitLength, int pos, int maxu = 4096)
    {
        // 32 位长度按有符号解释：≥ 0x80000000 表示 UTF-16（负数）。
        var nRaw = ReadBits(data, bitLength, ref pos, 32);
        var n = nRaw >= 0x80000000UL ? (long)nRaw - (1L << 32) : (long)nRaw;
        var u = Math.Abs((long)n);
        if (u > maxu) throw new SnaBad($"fstring len {n}");
        if (u == 0) return ("", pos);
        var width = n > 0 ? 8 : 16;
        var nb = (int)(u * width);
        if (pos + nb > bitLength) throw new SnaBad("eof fstring");
        // 批量组字节：LSB-first 位序 → 字节序（等价 np.packbits(bitorder="little")）。
        var raw = new byte[nb / 8];
        for (var i = 0; i < raw.Length; i++)
        {
            raw[i] = (byte)BitPrimitives.ReadBitsAt(data, pos + i * 8, 8);
        }
        pos += nb;
        // Python: raw.decode("utf-8" if w == 8 else "utf-16-le")——负长度（w=16 位/字符）
        // 为 UTF-16-LE。此前漏掉 UTF-16 分支导致少读一半字节 → 位漂移 → 后续
        // export 的 guid/路径全部错位（黄金对拍实证 54 条不一致 + guid 82→680 配错）。
        var text = width == 8
            ? System.Text.Encoding.UTF8.GetString(raw)
            : System.Text.Encoding.Unicode.GetString(raw);
        return (text.TrimEnd('\0'), pos);
    }

    /// <summary>
    /// UPackageMapClient::InternalLoadObject（Python: load_object）。
    /// exporting=true 时每个 GUID 都带 ExportFlags；sink 非空则收集条目。
    /// </summary>
    public static (ulong Guid, int Pos) LoadObject(
        ReadOnlySpan<byte> data, int bitLength, int pos, bool exporting,
        int depth = 0, List<Dictionary<string, object?>>? sink = null)
    {
        if (depth > 16) throw new SnaBad("recursion");
        ulong g;
        (g, pos) = Packed(data, bitLength, pos);
        if (g == 0) return (g, pos);
        var flags = 0;
        if (g == 1 || exporting)
        {
            flags = (int)ReadBits(data, bitLength, ref pos, 8);
            if ((flags & 0xF8) != 0) throw new SnaBad($"exportflags {flags:X2}");
        }
        if ((flags & 1) != 0)
        {
            ulong og;
            (og, pos) = LoadObject(data, bitLength, pos, exporting, depth + 1, sink);
            string path;
            (path, pos) = FString(data, bitLength, pos);
            if ((flags & 4) != 0) pos += 32;   // bHasNetworkChecksum: u32
            sink?.Add(new Dictionary<string, object?>
            {
                ["guid"] = g, ["outer"] = og, ["path"] = path, ["flags"] = flags,
            });
        }
        else
        {
            sink?.Add(new Dictionary<string, object?>
            {
                ["guid"] = g, ["outer"] = null, ["path"] = null, ["flags"] = flags,
            });
        }
        return (g, pos);
    }

    /// <summary>UPackageMapClient::ReceiveNetGUIDBunch（Python: read_exports2）。</summary>
    public static (int NumGuids, int Pos) ReadExports2(
        ReadOnlySpan<byte> data, int bitLength, int pos, List<Dictionary<string, object?>> sink)
    {
        var hasReplayout = ReadBits(data, bitLength, ref pos, 1);
        if (hasReplayout != 0) throw new SnaBad("bHasRepLayoutExport");
        var n = (int)ReadBits(data, bitLength, ref pos, 32);
        if (n < 0 || n > 2048) throw new SnaBad($"numguids {n}");
        for (var i = 0; i < n; i++)
        {
            (_, pos) = LoadObject(data, bitLength, pos, exporting: true, sink: sink);
        }
        return (n, pos);
    }

    /// <summary>ConditionallySerializeQuantizedVector（Python: cond_vector）。</summary>
    public static (double[]? Value, int Pos) CondVector(
        ReadOnlySpan<byte> data, int bitLength, int pos, bool quantizeBit, double scale, int maxBits)
    {
        var f = ReadBits(data, bitLength, ref pos, 1);
        if (f == 0) return (null, pos);
        var q = 1;
        if (quantizeBit)
        {
            q = (int)ReadBits(data, bitLength, ref pos, 1);
        }
        if (q != 0)
        {
            var (nbRaw, newPos) = SerInt(data, bitLength, pos, maxBits);
            var nb = (int)nbRaw;
            pos = newPos;
            var bias = 1 << (nb + 1);
            var w = nb + 2;
            var v = new double[3];
            for (var i = 0; i < 3; i++)
            {
                v[i] = ((long)ReadBits(data, bitLength, ref pos, w) - bias) / scale;
            }
            return (v, pos);
        }
        var full = new double[3];
        for (var i = 0; i < 3; i++)
        {
            full[i] = BitConverter.UInt32BitsToSingle((uint)ReadBits(data, bitLength, ref pos, 32));
        }
        return (full, pos);
    }

    /// <summary>FRotator 3×16 short 序列化（Python: rot_short）：每轴 1 个 bIsZero 位 + 有值时 u16。</summary>
    public static (double[] Value, int Pos) RotShort(ReadOnlySpan<byte> data, int bitLength, int pos)
    {
        var v = new double[3];
        for (var i = 0; i < 3; i++)
        {
            var nz = ReadBits(data, bitLength, ref pos, 1);
            if (nz != 0)
            {
                v[i] = ReadBits(data, bitLength, ref pos, 16) * 360.0 / 65536.0;
            }
        }
        return (v, pos);
    }

    /// <summary>
    /// open bunch 前缀解析（Python: parse_prefix）。
    /// 返回 (info, 内容块起始位)。引擎非法读抛 SnaBad。
    ///
    /// frags：partial bunch 的 fragment 边界 [(start_bit, hx, hm), ...]——
    /// 每个 bHasPackageMapExports 的 fragment 各带独立 exports 段，必须逐段读。
    /// </summary>
    public static (Dictionary<string, object?> Info, int StreamStart) ParsePrefix(
        ReadOnlySpan<byte> data, int bitLength,
        bool hasExports, bool hasMust,
        bool quantizeBit = false, double nqScale = 10.0, int nqBits = 24,
        List<(int Start, bool Hx, bool Hm)>? frags = null)
    {
        var pos = 0;
        var info = new Dictionary<string, object?>
        {
            ["exports"] = new List<Dictionary<string, object?>>(),
            ["must"] = new List<ulong>(),
        };
        var numExports = 0;
        if (hasExports)
        {
            if (frags != null)
            {
                var lastEnd = pos;
                foreach (var (fstart, fhx, _) in frags)
                {
                    if (!fhx) continue;
                    int n;
                    (n, lastEnd) = ReadExports2(data, bitLength, fstart, (List<Dictionary<string, object?>>)info["exports"]!);
                    numExports += n;
                }
                pos = lastEnd;
            }
            else
            {
                int n;
                (n, pos) = ReadExports2(data, bitLength, pos, (List<Dictionary<string, object?>>)info["exports"]!);
                numExports = n;
            }
        }
        info["num_exports"] = numExports;
        if (hasMust)
        {
            var mustStart = pos;
            if (frags != null)
            {
                foreach (var (fstart, _, fhm) in frags)
                {
                    if (!fhm) continue;
                    mustStart = fstart;
                    break;
                }
            }
            var nm = (int)ReadBits(data, bitLength, ref mustStart, 16);
            pos = mustStart;
            if (nm > 256) throw new SnaBad($"nmust {nm}");
            for (var i = 0; i < nm; i++)
            {
                ulong g;
                (g, pos) = Packed(data, bitLength, pos);
                ((List<ulong>)info["must"]!).Add(g);
            }
        }
        ulong a;
        (a, pos) = LoadObject(data, bitLength, pos, exporting: false);
        info["actor"] = a;
        if (a == 0) throw new SnaBad("actor guid 0");
        if ((a & 1) != 0)
        {
            info["static_actor"] = true;
            info["stream_start"] = pos;
            return (info, pos);
        }
        ulong ar, lv;
        (ar, pos) = LoadObject(data, bitLength, pos, exporting: false);
        (lv, pos) = LoadObject(data, bitLength, pos, exporting: false);
        info["arch"] = ar;
        info["level"] = lv;
        double[]? loc;
        (loc, pos) = CondVector(data, bitLength, pos, quantizeBit, nqScale, nqBits);
        info["loc"] = loc;
        var rf = ReadBits(data, bitLength, ref pos, 1);
        info["rot"] = null;
        if (rf != 0)
        {
            double[]? rot;
            (rot, pos) = RotShort(data, bitLength, pos);
            info["rot"] = rot;
        }
        double[]? scale, vel;
        (scale, pos) = CondVector(data, bitLength, pos, quantizeBit, nqScale, nqBits);
        info["scale"] = scale;
        (vel, pos) = CondVector(data, bitLength, pos, quantizeBit, nqScale, nqBits);
        info["vel"] = vel;
        info["stream_start"] = pos;
        return (info, pos);
    }
}
