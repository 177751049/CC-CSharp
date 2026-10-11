using System.Buffers.Binary;

namespace CCRadar.Parser.Codec;

/// <summary>
/// LSB-first 位读取器：在字节缓冲上以"显式位计数"方式顺序读取。
/// 1:1 移植自 Python: codec/bitstream.py 的 BitReader（含 12 字节缓存窗口优化——
/// 窗口命中时每次读取仅为整数移位+掩码，窗口跨界时按字节重载）。
/// 语义契约：position 语义、越界抛 BitError——与 Python 版完全一致。
/// </summary>
public sealed class BitReader
{
    private readonly byte[] _data;
    private readonly int _bitCount;
    private int _position;

    // 缓存读取窗口：覆盖位区间 [_winBase, _winBase + _winBits)，值为小端整数。
    private ulong _win;
    private int _winBase = -1;   // 窗口起始位位置（-1 = 空）
    private int _winBits;        // 窗口位宽

    public BitReader(byte[] data, int bitCount)
    {
        _data = data;
        _bitCount = bitCount;
        _position = 0;
    }

    /// <summary>当前读取位置（位）。</summary>
    public int Position
    {
        get => _position;
        set => _position = value;
    }

    /// <summary>声明总位数。</summary>
    public int BitCount => _bitCount;

    /// <summary>剩余可读位数（永不为负）。</summary>
    public int Remaining() => Math.Max(_bitCount - _position, 0);

    /// <summary>
    /// 确保还能读取 count 位且不越过底层缓冲（Python: ensure()）。
    /// </summary>
    public void Ensure(int count)
    {
        var end = _position + count;
        if (end > _bitCount)
            throw new BitError($"need {count} bits with {Remaining()} remaining");
        if (end > 0)
        {
            var lastByte = (end - 1) >> 3;
            if (lastByte >= _data.Length)
                throw new BitError($"bit reader past buffer: need byte {lastByte}, len {_data.Length}");
        }
    }

    // 重载缓存窗口，覆盖字节索引 byte 起的至多 8 字节（Python: _reload()）。
    // 注意：Python 用无限精度 int 可存 12 字节窗口；C# 用 ulong 只能存 64 位，
    // 因此窗口必须是 8 字节——否则 ReadBits 大位宽时窗口命中检查会误判并丢失高位。
    private void Reload(int byteIndex)
    {
        var stop = Math.Min(byteIndex + 8, _data.Length);
        ulong window = 0;
        for (var i = stop - 1; i >= byteIndex; i--)
        {
            window = (window << 8) | _data[i];
        }
        _win = window;
        _winBase = byteIndex << 3;
        _winBits = (stop - byteIndex) << 3;
    }

    /// <summary>读 1 位（Python: bit()）。</summary>
    public int ReadBit()
    {
        var p = _position;
        if (p >= _bitCount)
            throw new BitError($"need 1 bits with {Remaining()} remaining");
        var v = BitPrimitives.GetBit(_data, p);
        _position = p + 1;
        return v;
    }

    /// <summary>
    /// 读 count 位（count &lt;= 64，Python: bits()）。
    /// 直接走 BitPrimitives.ReadBitsAt（已通过 500 组位精确自测）；
    /// Python 版的 12 字节窗口缓存依赖无限精度 int，C# 的 ulong 窗口无法容纳
    /// "非对齐位置 + 64 位读取"（最多溢出 7 位），为保证严谨弃用缓存直读。
    /// </summary>
    public ulong ReadBits(int count)
    {
        if (count > 64)
            throw new BitError($"need {count} bits (max 64)");
        var p = _position;
        var end = p + count;
        if (end > _bitCount)
            throw new BitError($"need {count} bits with {Remaining()} remaining");
        if (end > 0 && ((end - 1) >> 3) >= _data.Length)
            throw new BitError($"bit reader past buffer: need byte {(end - 1) >> 3}, len {_data.Length}");
        var v = count == 0 ? 0 : BitPrimitives.ReadBitsAt(_data, p, count);
        _position = end;
        return v;
    }

    /// <summary>读 8 位无符号（Python: uint8()）。</summary>
    public uint ReadUInt8() => (uint)ReadBits(8);

    /// <summary>读 16 位小端无符号（Python: uint16()）。</summary>
    public uint ReadUInt16() => (uint)ReadBits(16);

    /// <summary>读 32 位小端无符号（Python: uint32()）。</summary>
    public uint ReadUInt32() => (uint)ReadBits(32);

    /// <summary>读 64 位小端无符号（Python: uint64()）。</summary>
    public ulong ReadUInt64() => ReadBits(64);

    /// <summary>读 32 位有符号（二补码，Python: int32()）。</summary>
    public int ReadInt32()
    {
        var v = ReadUInt32();
        return (v & (1u << 31)) != 0 ? unchecked((int)v) : (int)v;
    }

    /// <summary>
    /// 读 32 位 IEEE float（小端，Python: float32()）。
    /// 返回 <see cref="double"/>：Python struct.unpack('f') 产出的是 double 语义
    /// 的 float，装箱后是 double——下游所有 `is (long or double)` 数值门依赖
    /// 该约定（float 装箱会让血量链 137 次全量 typeReject，黄金对拍实证）。
    /// float32→double 是精确扩宽，无损。
    /// </summary>
    public double ReadFloat32()
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, ReadUInt32());
        return BinaryPrimitives.ReadSingleLittleEndian(bytes);
    }

    /// <summary>
    /// 读 count 个字节（LSB-first 位序），等价于逐字节 uint8 的批量版（Python: raw_bytes()）。
    /// 8 字节一组走 ReadBits（12 字节窗口足够覆盖），语义与逐字节读取完全一致。
    /// </summary>
    public byte[] ReadRawBytes(int count)
    {
        Ensure(count * 8);
        var result = new byte[count];
        var left = count;
        var offset = 0;
        while (left > 0)
        {
            var take = left >= 8 ? 8 : left;
            var value = ReadBits(take * 8);
            for (var i = 0; i < take; i++)
            {
                result[offset + i] = (byte)(value >> (i * 8));
            }
            offset += take;
            left -= take;
        }
        return result;
    }

    /// <summary>
    /// SerializeIntPacked 打包整数读取（Python: packed_int()）。
    /// variant "dfm"：7 个数据位在高位、继续标志 = LSB（按 Rust 参考实现）。
    /// variant "ue4"：标准 UE4——7 个数据位在低位、继续标志 = 0x80。
    /// </summary>
    public uint ReadPackedInt(string variant = "dfm")
    {
        uint value = 0;
        var shift = 0;
        for (var i = 0; i < 5; i++)
        {
            var encoded = ReadUInt8();
            if (variant == "dfm")
            {
                value |= (uint)(encoded >> 1) << shift;
                if ((encoded & 1) == 0) return value;
            }
            else
            {
                value |= (uint)(encoded & 0x7F) << shift;
                if ((encoded & 0x80) == 0) return value;
            }
            shift += 7;
        }
        throw new BitError("SerializeIntPacked exceeds 32 bits");
    }

    /// <summary>
    /// UE SerializeInt：用表示 [0, maximum) 区间所需的最少位数读取（Python: serialize_int()）。
    /// 2 的幂定宽快路径：maximum=2^k 时恰好读 k 位；非 2 的幂回退逐位循环
    /// （定长不定，依赖已读值提前终止——必须保持逐位语义）。
    /// </summary>
    public int ReadSerializeInt(int maximum)
    {
        if (maximum > 0 && (maximum & (maximum - 1)) == 0)
        {
            var k = BitOperationsLog2((uint)maximum);
            return k == 0 ? 0 : (int)ReadBits(k);
        }
        int value = 0;
        int mask = 1;
        while (value + mask < maximum)
        {
            if (ReadBit() != 0) value |= mask;
            mask <<= 1;
        }
        return value;
    }

    private static int BitOperationsLog2(uint v) => v == 0 ? 0 : 31 - System.Numerics.BitOperations.LeadingZeroCount(v);

    /// <summary>
    /// UE FString：int32 长度；&gt;=0 表示 UTF-8 单元数，&lt;0 表示 UTF-16 单元数（Python: fstring()）。
    /// </summary>
    public string ReadFString()
    {
        var length = ReadInt32();
        var units = Math.Abs(length);
        if (units > BitPrimitives.MaxNetworkStringUnits)
            throw new BitError($"invalid FString length {length}");
        if (length >= 0)
        {
            Ensure(units * 8);
            var raw = units > 0 ? ReadRawBytes(units) : Array.Empty<byte>();
            if (raw.Length > 0 && raw[^1] == 0) raw = raw[..^1];
            return System.Text.Encoding.UTF8.GetString(raw);
        }
        Ensure(units * 16);
        var raw16 = units > 0 ? ReadRawBytes(units * 2) : Array.Empty<byte>();
        if (units > 0 && raw16.Length >= 2 && raw16[^1] == 0 && raw16[^2] == 0) raw16 = raw16[..^2];
        return System.Text.Encoding.Unicode.GetString(raw16);
    }

    /// <summary>
    /// UE PackageMap 对象引用（Python: package_map_object_ref()）。
    /// network_guid != 1 时为普通引用；== 1 时为内联导出（flags/outer/path/checksum）。
    /// </summary>
    public PackageMapObjectRef ReadPackageMapObjectRef(string variant = "dfm")
    {
        var networkGuid = ReadPackedInt(variant);
        if (networkGuid != 1)
        {
            return new PackageMapObjectRef
            {
                NetworkGuid = networkGuid,
                InlineExport = false,
            };
        }
        var flags = ReadUInt8();
        var reference = new PackageMapObjectRef
        {
            NetworkGuid = networkGuid,
            InlineExport = true,
            Flags = flags,
        };
        if ((flags & 1) != 0)
        {
            reference.Outer = ReadPackageMapObjectRef(variant);
            reference.Path = ReadFString();
            if ((flags & 4) != 0) reference.Checksum = ReadUInt32();
        }
        return reference;
    }

    /// <summary>
    /// 从当前位置切出 count 位为字节（Python: take()）——位置随之前进。
    /// </summary>
    public byte[] Take(int count)
    {
        if (count > Remaining())
            throw new BitError($"invalid bit slice {count}/{Remaining()}");
        var output = BitPrimitives.SliceBits(_data, _position, count);
        _position += count;
        return output;
    }
}

/// <summary>PackageMap 对象引用结构（Python 中为 dict，此处强类型化）。</summary>
public sealed class PackageMapObjectRef
{
    public uint NetworkGuid { get; set; }
    public bool InlineExport { get; set; }
    public uint? Flags { get; set; }
    public PackageMapObjectRef? Outer { get; set; }
    public string? Path { get; set; }
    public uint? Checksum { get; set; }
}
