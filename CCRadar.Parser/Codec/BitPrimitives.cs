using System.Buffers.Binary;
using System.Numerics;

namespace CCRadar.Parser.Codec;

/// <summary>
/// 位级序列化异常：读取超出位流声明长度时抛出。
/// 对应 Python: codec/bitstream.py 的 BitError。
/// </summary>
public class BitError : Exception
{
    public BitError(string message) : base(message) { }

    public BitError(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// DFM 风格 UE 网络位流的位级原语（静态工具方法）。
/// 位序为每字节内 LSB-first（UE FBitWriter 约定）：第一个序列化的位是第一个字节的 bit 0。
/// 1:1 移植自 Python: codec/bitstream.py 的模块级函数（其本身是 Rust 参考实现的移植）。
/// </summary>
public static class BitPrimitives
{
    /// <summary>UE 网络字符串的最大单元数（Python: MAX_NETWORK_STRING_UNITS = 4096）。</summary>
    public const int MaxNetworkStringUnits = 4096;

    /// <summary>
    /// 读取绝对位 <paramref name="position"/> 处的单个位（LSB-first）。
    /// 越界返回 0（Python: bit()）。
    /// </summary>
    public static int GetBit(ReadOnlySpan<byte> data, int position)
    {
        var idx = position >> 3;
        if (idx >= data.Length) return 0;
        return (data[idx] >> (position & 7)) & 1;
    }

    /// <summary>
    /// 从绝对位 <paramref name="position"/> 起读取 <paramref name="count"/> 位（LSB-first）。
    /// 越界的源位读 0（Python: read_bits_at()）。
    /// 注意：非对齐 shift + count 组合的跨度可达 71 位，超出 ulong——
    /// 必须用 BigInteger 完成"小端组装 → 移位 → 掩码"（与 Python int 语义逐位一致）。
    /// </summary>
    public static ulong ReadBitsAt(ReadOnlySpan<byte> data, int position, int count)
    {
        if (count == 0) return 0;
        var byteIndex = position >> 3;
        if (byteIndex >= data.Length) return 0;
        var shift = position & 7;
        var end = Math.Min(byteIndex + (count + shift + 7) / 8, data.Length);
        BigInteger value = 0;
        for (var i = end - 1; i >= byteIndex; i--)
        {
            value = (value << 8) | data[i];
        }
        value >>= shift;
        value &= (BigInteger.One << count) - 1;
        // count <= 64，结果必在 ulong 值域内；ToByteArray 为小端。
        var bytes = value.ToByteArray();
        ulong result = 0;
        for (var i = Math.Min(bytes.Length, 8) - 1; i >= 0; i--)
        {
            result = (result << 8) | bytes[i];
        }
        return result;
    }

    /// <summary>
    /// 把从绝对位 <paramref name="position"/> 起的 <paramref name="count"/> 位拷贝为字节。
    /// 结果保持 LSB-first 布局：结果位 i == 源位 position+i；末字节掩到有效低位；缺失源位读 0。
    /// （Python: slice_bits() —— Python 用大整数做单次移位，此处等价实现。）
    /// </summary>
    public static byte[] SliceBits(ReadOnlySpan<byte> data, int position, int count)
    {
        if (count == 0) return Array.Empty<byte>();
        var endBit = position + count;
        var endByte = (endBit + 7) >> 3;
        var outLength = (count + 7) >> 3;
        if ((position >> 3) >= data.Length) return new byte[outLength];

        var start = position >> 3;
        var stop = Math.Min(endByte, data.Length);
        // 用大整数完成整段移位+掩码（与 Python 的 int 语义逐位一致）。
        var value = new BigInteger(0);
        for (var i = stop - 1; i >= start; i--)
        {
            value = (value << 8) | data[i];
        }
        value >>= position & 7;
        value &= (BigInteger.One << count) - 1;
        var bytes = value.ToByteArray();        // BigInteger.ToByteArray() 为小端
        var result = new byte[outLength];
        var copy = Math.Min(bytes.Length, outLength);
        Array.Copy(bytes, result, copy);
        // BigInteger 对正数的最高有效位可能带符号扩展位，但我们在掩码后值为正，
        // ToByteArray() 保证无冗余符号字节；高位缺失字节保持 0（与 Python 语义一致）。
        return result;
    }

    /// <summary>
    /// 最后一个置位位的索引（= UE termination bit 位置）。
    /// UE FNetBitWriter 会在载荷后追加单个 1 位，因此该索引等于数据位数。
    /// （Python: termination_data_bits() —— 返回索引而非 index+1。）
    /// </summary>
    public static int TerminationDataBits(ReadOnlySpan<byte> data)
    {
        for (var idx = data.Length - 1; idx >= 0; idx--)
        {
            var b = data[idx];
            if (b != 0) return idx * 8 + BitOperations.Log2(b);
        }
        return 0;
    }
}
