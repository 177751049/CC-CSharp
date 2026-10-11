namespace CCRadar.Parser.Codec;

/// <summary>
/// LSB-first 位写入器（UE FBitWriter 约定）。
/// 1:1 移植自 Python: codec/bitstream.py 的 BitWriter（含高性能 append 整段位拼接）。
/// </summary>
public sealed class BitWriter
{
    private byte[] _data = Array.Empty<byte>();
    private int _bitCount;

    /// <summary>已写入的总位数。</summary>
    public int BitCount => _bitCount;

    /// <summary>导出当前写入的字节（Python: bytes()）。</summary>
    public byte[] ToBytes() => (byte[])_data.Clone();

    /// <summary>写 1 位（Python: bit()）。</summary>
    public void WriteBit(int value)
    {
        if ((_bitCount & 7) == 0)
        {
            EnsureCapacity(_bitCount + 8);
            _data[_bitCount >> 3] = 0;
        }
        if ((value & 1) != 0)
        {
            _data[_bitCount >> 3] |= (byte)(1 << (_bitCount & 7));
        }
        _bitCount += 1;
    }

    /// <summary>按 LSB-first 顺序写 count 位（Python: bits()）。</summary>
    public void WriteBits(ulong value, int count)
    {
        for (var i = 0; i < count; i++)
        {
            WriteBit((int)((value >> i) & 1));
        }
    }

    /// <summary>写 8 位（Python: uint8()）。</summary>
    public void WriteUInt8(uint value) => WriteBits(value, 8);

    /// <summary>写 16 位（Python: uint16()）。</summary>
    public void WriteUInt16(uint value) => WriteBits(value, 16);

    /// <summary>写 32 位（Python: uint32()）。</summary>
    public void WriteUInt32(uint value) => WriteBits(value, 32);

    /// <summary>写 64 位（Python: uint64()）。</summary>
    public void WriteUInt64(ulong value) => WriteBits(value, 64);

    /// <summary>写 32 位有符号（Python: int32()）。</summary>
    public void WriteInt32(int value) => WriteUInt32((uint)value);

    /// <summary>
    /// SerializeIntPacked 打包整数写入（Python: packed_int()）。
    /// variant "dfm"：数据位左移 1 位、继续标志 = LSB；"ue4"：标准 LEB128 风格。
    /// </summary>
    public void WritePackedInt(uint value, string variant = "dfm")
    {
        if (variant == "dfm")
        {
            while (true)
            {
                var encoded = (uint)(((value & 0x7F) << 1) | (value >= 0x80 ? 1 : 0));
                WriteUInt8(encoded);
                value >>= 7;
                if (value == 0) break;
            }
        }
        else
        {
            while (true)
            {
                var encoded = (uint)((value & 0x7F) | (value >= 0x80 ? 0x80 : 0));
                WriteUInt8(encoded);
                value >>= 7;
                if (value == 0) break;
            }
        }
    }

    /// <summary>
    /// UE SerializeInt 写入（Python: serialize_int()）。
    /// 注意跟踪的是"已写位数 current"（Rust 参考实现用 current 而非 value），移植时保持一致。
    /// </summary>
    public void WriteSerializeInt(int value, int maximum)
    {
        if (value >= maximum)
            throw new BitError($"SerializeInt value {value} >= maximum {maximum}");
        int current = 0;
        int mask = 1;
        while (current + mask < maximum)
        {
            var setBit = (value & mask) != 0;
            WriteBit(setBit ? 1 : 0);
            if (setBit) current |= mask;
            mask <<= 1;
        }
    }

    /// <summary>
    /// UE FString 写入：优先 UTF-8（含 \0 终止），编码失败回退 UTF-16-LE（Python: fstring()）。
    /// </summary>
    public void WriteFString(string value)
    {
        var raw = System.Text.Encoding.UTF8.GetBytes(value);
        WriteInt32(raw.Length + 1);
        foreach (var b in raw) WriteUInt8(b);
        WriteUInt8(0);
    }

    /// <summary>
    /// 从 data 追加 count 位（LSB-first）——位精确等价于历史逐位循环
    /// （data 之外的位补 0），但整字节搬移。曾是实时补包最热路径（约 60% CPU）。
    /// （Python: append()）
    /// </summary>
    public void Append(ReadOnlySpan<byte> data, int count)
    {
        if (count <= 0) return;
        var available = data.Length << 3;
        var effective = count <= available ? count : available;   // 实际可拷贝的位数
        var nbytes = (effective + 7) >> 3;
        // 用大整数完成"源段提取 → 移位对齐 → 合并"，与 Python 的 int 语义逐位一致。
        var src = System.Numerics.BigInteger.Zero;
        for (var i = nbytes - 1; i >= 0; i--)
        {
            src = (src << 8) | (i < data.Length ? data[i] : 0);
        }
        if (effective < (nbytes << 3))
        {
            src &= (System.Numerics.BigInteger.One << effective) - 1;   // 掩掉末字节冗余位
        }
        var p = _bitCount;
        var byteIndex = p >> 3;
        var shift = p & 7;
        var outLength = (p + count + 7) >> 3;
        EnsureCapacity(outLength << 3);
        if (shift == 0)
        {
            for (var i = 0; i < nbytes; i++)
            {
                var byteValue = (byte)((src >> (i * 8)) & 0xFF);
                _data[byteIndex + i] = effective > i * 8 ? byteValue : (byte)0;
            }
            // effective 之后的整字节补 0：EnsureCapacity 预清零 + 上面的条件赋值。
            for (var i = nbytes; byteIndex + i < outLength; i++)
            {
                _data[byteIndex + i] = 0;
            }
        }
        else
        {
            // 目标字节不对齐：读旧窗口 → 合并 → 整段写回。
            var win = outLength - byteIndex;
            System.Numerics.BigInteger old = 0;
            for (var i = win - 1; i >= 0; i--)
            {
                old = (old << 8) | _data[byteIndex + i];
            }
            var merged = old | (src << shift);
            for (var i = 0; i < win; i++)
            {
                _data[byteIndex + i] = (byte)((merged >> (i * 8)) & 0xFF);
            }
        }
        _bitCount = p + count;
    }

    /// <summary>
    /// 返回带终止位的字节序列：复制当前数据后追加单个 1 位（Python: terminated_bytes()）。
    /// UE FNetBitWriter 语义：终止位的位索引 = 数据位数。
    /// </summary>
    public byte[] TerminatedBytes()
    {
        var writer = new BitWriter();
        var usedBytes = (_bitCount + 7) >> 3;          // 已使用的字节数
        var totalBytes = (_bitCount + 1 + 7) >> 3;     // 追加 1 位后所需字节数
        writer._data = new byte[Math.Max(totalBytes, usedBytes)];
        Array.Copy(_data, writer._data, usedBytes);
        writer._bitCount = _bitCount;
        writer.WriteBit(1);
        return writer.ToBytes();
    }

    // 确保底层缓冲能容纳 bitCapacity 位。
    private void EnsureCapacity(int bitCapacity)
    {
        var required = (bitCapacity + 7) >> 3;
        if (_data.Length >= required) return;
        var newSize = _data.Length == 0 ? 64 : _data.Length * 2;
        while (newSize < required) newSize *= 2;
        Array.Resize(ref _data, newSize);
    }
}
