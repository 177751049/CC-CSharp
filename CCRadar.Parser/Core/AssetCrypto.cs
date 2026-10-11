using System.Security.Cryptography;

namespace CCRadar.Parser.Core;

/// <summary>
/// asset_crypto.py 的 C# 等价实现 —— 数据文件静态保护层。
///
/// Python 原版（ErCore/src/asset_crypto.py）：
///   * 格式：MAGIC("NRA1", 4 字节) + (原文件字节 XOR keystream)
///   * keystream = hashlib.shake_256(KEY + purpose).digest(len(data))
///   * 有魔数就解密，没有就原样返回（源码仓库形态是明文，打包后才加密），
///     因此源码跑与 exe 跑走同一份读取代码。
///
/// .NET 8 没有 SHAKE-256，这里自带一份 Keccak-f[1600] 海绵实现（仅 XOF 模式，
/// rate=136 字节，域分隔字节 0x1F —— 与 FIPS-202 的 SHAKE256 完全一致），
/// 并用固定测试向量在自测程序里与 Python hashlib 交叉验证。
/// </summary>
public static class AssetCrypto
{
    /// <summary>加密资产魔数（Python: MAGIC = b"NRA1"）。</summary>
    public static readonly byte[] Magic = [(byte)'N', (byte)'R', (byte)'A', (byte)'1'];

    /// <summary>
    /// 打包密钥（32 字节，Python: _KEY）。与 Python 版保持一致，使两版可读同一批加密资产。
    /// </summary>
    private static readonly byte[] Key =
    [
        0x1f, 0x71, 0x46, 0x14, 0xc0, 0xf2, 0x1a, 0x3f, 0x7d, 0x58, 0xd0, 0x69, 0x58, 0x11, 0x98, 0xda,
        0x9a, 0x56, 0xf0, 0x7a, 0x04, 0xa6, 0xbd, 0x6a, 0xd4, 0x30, 0x7f, 0xd1, 0x74, 0xee, 0x0b, 0x2b,
    ];

    // ---- 运行期数据用途标识（Python: PURPOSE_*）----

    /// <summary>恢复出的 S2C 表缓存（= 该连接的解密钥）。</summary>
    public static readonly byte[] PurposeFlowcache = "flowcache"u8.ToArray();

    /// <summary>flow 解析缓存（含解密后的原始载荷）。</summary>
    public static readonly byte[] PurposeFlowparse = "flowparse"u8.ToArray();

    /// <summary>其它运行期状态（战斗流检测缓存等）。</summary>
    public static readonly byte[] PurposeRuntime = "runtime"u8.ToArray();

    /// <summary>
    /// 明文 → 带魔数的密文（Python: encrypt）。随包资产用空 purpose。
    /// </summary>
    public static byte[] Encrypt(byte[] data, byte[]? purpose = null)
    {
        var body = Xor(data, purpose);
        var blob = new byte[Magic.Length + body.Length];
        Magic.CopyTo(blob, 0);
        body.CopyTo(blob, Magic.Length);
        return blob;
    }

    /// <summary>
    /// 带魔数密文 → 明文（Python: decrypt）。缺魔数抛异常，与 Python 一致。
    /// </summary>
    public static byte[] Decrypt(byte[] blob, byte[]? purpose = null)
    {
        if (!blob.AsSpan().StartsWith(Magic))
            throw new ArgumentException("不是加密资产（缺魔数）");
        var body = new byte[blob.Length - Magic.Length];
        Array.Copy(blob, Magic.Length, body, 0, body.Length);
        return Xor(body, purpose);
    }

    /// <summary>有魔数就解密，没有就原样返回；不缓存（Python: decrypt_blob）。</summary>
    public static byte[] DecryptBlob(byte[] blob, byte[]? purpose = null)
        => blob.AsSpan().StartsWith(Magic) ? Decrypt(blob, purpose) : blob;

    // 按路径缓存明文结果（Python: _read_cached 的 lru_cache 等价）。
    private static readonly Dictionary<string, byte[]> ReadCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object ReadGate = new();

    /// <summary>
    /// 读资产：有魔数就解密，没有就原样返回；结果按路径缓存（Python: read_bytes）。
    /// </summary>
    public static byte[] ReadBytes(string path)
    {
        lock (ReadGate)
        {
            if (ReadCache.TryGetValue(path, out var cached)) return cached;
            var blob = File.ReadAllBytes(path);
            var plain = blob.AsSpan().StartsWith(Magic) ? Decrypt(blob) : blob;
            ReadCache[path] = plain;
            return plain;
        }
    }

    /// <summary>读文本资产（Python: read_text，UTF-8）。</summary>
    public static string ReadText(string path) => System.Text.Encoding.UTF8.GetString(ReadBytes(path));

    /// <summary>
    /// keystream XOR（Python: _xor）。keystream = SHAKE256(KEY + purpose)，
    /// 逐字节与数据等长 XOR。大整数异或的 Python 实现只是性能优化，语义相同。
    /// </summary>
    private static byte[] Xor(byte[] data, byte[]? purpose)
    {
        if (data.Length == 0) return data;
        var keyMaterial = new byte[Key.Length + (purpose?.Length ?? 0)];
        Key.CopyTo(keyMaterial, 0);
        purpose?.CopyTo(keyMaterial, Key.Length);
        var ks = Shake256Xof.HashData(keyMaterial, data.Length);
        var outBuf = new byte[data.Length];
        for (var i = 0; i < data.Length; i++) outBuf[i] = (byte)(data[i] ^ ks[i]);
        return outBuf;
    }
}

/// <summary>
/// SHAKE-256 XOF（FIPS-202）：Keccak-f[1600] 海绵，rate=136 字节（1088 bit），
/// 域分隔字节 0x1F。纯托管实现，供 AssetCrypto 的 keystream 派生使用。
/// 正确性由自测程序用 Python hashlib.shake_256 的固定向量交叉验证。
/// </summary>
public static class Shake256Xof
{
    // Keccak 轮常数（24 轮）。
    private static readonly ulong[] RoundConstants =
    [
        0x0000000000000001UL, 0x0000000000008082UL, 0x800000000000808AUL, 0x8000000080008000UL,
        0x000000000000808BUL, 0x0000000080000001UL, 0x8000000080008081UL, 0x8000000000008009UL,
        0x000000000000008AUL, 0x0000000000000088UL, 0x0000000080008009UL, 0x000000008000000AUL,
        0x000000008000808BUL, 0x800000000000008BUL, 0x8000000000008089UL, 0x8000000000008003UL,
        0x8000000000008002UL, 0x8000000000000080UL, 0x000000000000800AUL, 0x800000008000000AUL,
        0x8000000080008081UL, 0x8000000000008080UL, 0x0000000080000001UL, 0x8000000080008008UL,
    ];

    /// <summary>
    /// 计算 SHAKE-256 摘要（任意输出长度）。
    /// padByte 参数默认 0x1F（SHAKE 域分隔）；传 0x06 即 SHA3-256，
    /// 供自测用 .NET 内置 Sha3/已知向量隔离验证 Keccak-f 置换正确性。
    /// </summary>
    public static byte[] HashData(ReadOnlySpan<byte> input, int outputLength, byte padByte = 0x1F)
    {
        const int rate = 136;   // SHAKE256 的海绵速率（字节）
        var state = new ulong[25];

        // ---- 吸收段 ----
        var offset = 0;
        while (input.Length - offset >= rate)
        {
            AbsorbBlock(state, input.Slice(offset, rate));
            KeccakF1600(state);
            offset += rate;
        }
        // 末块 + 填充：SHAKE 域分隔 0x1F，块尾字节再 | 0x80。
        Span<byte> last = stackalloc byte[rate];
        var tail = input[offset..];
        tail.CopyTo(last);
        for (var i = tail.Length; i < rate; i++) last[i] = 0;
        last[tail.Length] ^= padByte;
        last[rate - 1] ^= 0x80;
        AbsorbBlock(state, last);
        KeccakF1600(state);

        // ---- 挤出段 ----
        var output = new byte[outputLength];
        var produced = 0;
        while (produced < outputLength)
        {
            var chunk = Math.Min(rate, outputLength - produced);
            SqueezeBlock(state, output.AsSpan(produced, chunk));
            produced += chunk;
            if (produced < outputLength) KeccakF1600(state);
        }
        return output;
    }

    /// <summary>把一个 rate 块按小端 lane 序异或进海绵状态。</summary>
    private static void AbsorbBlock(ulong[] state, ReadOnlySpan<byte> block)
    {
        for (var i = 0; i < block.Length / 8; i++)
        {
            state[i] ^= System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(i * 8, 8));
        }
    }

    /// <summary>按小端 lane 序从海绵状态挤出一个 rate 块（尾部可不足 8 字节）。</summary>
    private static void SqueezeBlock(ulong[] state, Span<byte> output)
    {
        var fullLanes = output.Length / 8;
        for (var i = 0; i < fullLanes; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(output.Slice(i * 8, 8), state[i]);
        }
        var rem = output.Length - fullLanes * 8;
        if (rem > 0)
        {
            Span<byte> tmp = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(tmp, state[fullLanes]);
            tmp[..rem].CopyTo(output[(fullLanes * 8)..]);
        }
    }

    /// <summary>
    /// 调试入口：对给定 25-lane 状态原位执行 Keccak-f[1600] 并返回，
    /// 供自测与官方测试向量（零状态 → f1258f7940e1dde7…）对照。
    /// </summary>
    internal static ulong[] DebugPermute(ulong[] state)
    {
        KeccakF1600(state);
        return state;
    }

    /// <summary>Keccak-f[1600] 置换（24 轮 theta/rho+pi/chi/iota）。</summary>
    private static void KeccakF1600(ulong[] a)
    {
        Span<ulong> b = stackalloc ulong[25];
        Span<ulong> c = stackalloc ulong[5];
        Span<ulong> d = stackalloc ulong[5];

        for (var round = 0; round < 24; round++)
        {
            // theta：列校验 + 邻列异或
            for (var x = 0; x < 5; x++)
            {
                c[x] = a[x] ^ a[x + 5] ^ a[x + 10] ^ a[x + 15] ^ a[x + 20];
            }
            for (var x = 0; x < 5; x++)
            {
                d[x] = c[(x + 4) % 5] ^ RotateLeft(c[(x + 1) % 5], 1);
            }
            for (var i = 0; i < 25; i++)
            {
                a[i] ^= d[i % 5];
            }

            // rho + pi：旋转并重排（lane 移位表内联展开）
            RhoPi(a, b);

            // chi：逐行非线性
            for (var y = 0; y < 5; y++)
            {
                var row = 5 * y;
                for (var x = 0; x < 5; x++)
                {
                    a[x + row] = b[x + row] ^ (~b[(x + 1) % 5 + row] & b[(x + 2) % 5 + row]);
                }
            }

            // iota：轮常数
            a[0] ^= RoundConstants[round];
        }
    }

    /// <summary>rho 旋转 + pi 重排（lane 移位量按 FIPS-202 表内联）。</summary>
    private static void RhoPi(ReadOnlySpan<ulong> a, Span<ulong> b)
    {
        // b[y + 5*((2x+3y)%5)] = ROTL(a[x + 5*y], r[x][y])
        // flat 表按 lane 索引 i = x + 5y 布局（列优先：flat[x + 5y] = ROT[x][y]），
        // 即 flat[0..4] 是 y=0 行（x=0..4）、flat[5..9] 是 y=1 行……
        Span<int> r = stackalloc int[25]
        {
            // y=0: ROT[0..4][0]
             0,  1, 62, 28, 27,
            // y=1: ROT[0..4][1]
            36, 44,  6, 55, 20,
            // y=2: ROT[0..4][2]
             3, 10, 43, 25, 39,
            // y=3: ROT[0..4][3]
            41, 45, 15, 21,  8,
            // y=4: ROT[0..4][4]
            18,  2, 61, 56, 14,
        };
        for (var x = 0; x < 5; x++)
        {
            for (var y = 0; y < 5; y++)
            {
                b[y + 5 * ((2 * x + 3 * y) % 5)] = RotateLeft(a[x + 5 * y], r[x + 5 * y]);
            }
        }
    }

    private static ulong RotateLeft(ulong v, int bits) => (v << bits) | (v >> (64 - bits));
}
