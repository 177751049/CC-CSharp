using CCRadar.Parser.Codec;

namespace CCRadar.Parser.Core;

/// <summary>握手状态（Python: handshake.Handshake dataclass）。</summary>
public sealed class Handshake
{
    public bool Hello { get; set; }
    /// <summary>挑战串（hex）。</summary>
    public string? Challenge { get; set; }
    /// <summary>收到 Login（C→S）。</summary>
    public bool Login { get; set; }
    /// <summary>地图路径（S→C），null=未进入对局。</summary>
    public string? Welcome { get; set; }
    /// <summary>客户端 Join。</summary>
    public bool Join { get; set; }
    /// <summary>被拒原因（如有）。</summary>
    public string? Failure { get; set; }
    /// <summary>Welcome 所在帧号（双向合并计数）。</summary>
    public int WelcomeFrame { get; set; } = -1;
    /// <summary>完整握手轮数（重连/换图会重跑）。</summary>
    public int Rounds { get; set; }

    /// <summary>引擎门：客户端视角"这是对局会话"。</summary>
    public bool Ok => Welcome is not null;

    public bool Full => Ok && Hello && Login && Join && Challenge is not null;

    /// <summary>转 JSON 字典（Python: as_dict）。</summary>
    public Dictionary<string, object?> AsDict() => new()
    {
        ["ok"] = Ok,
        ["full"] = Full,
        ["welcome"] = Welcome,
        ["challenge"] = Challenge,
        ["failure"] = Failure,
        ["rounds"] = Rounds,
        ["welcome_frame"] = WelcomeFrame,
    };
}

/// <summary>
/// handshake.py 的 C# 等价实现 —— UE4 控制通道握手扫描器（战斗流识别的引擎级判据）。
///
/// 游戏逻辑（与客户端完全一致，非启发式）：
///   UE4 客户端把一条 UDP 连接当作对局会话，当且仅当控制通道(ch0)上完成了
///   握手状态机：
///       C→S  NMT_HELLO      (open bunch, 引擎版本)
///       S→C  NMT_CHALLENGE  (hex 挑战串)
///       C→S  NMT_LOGIN      (FString URL "/Game/...")
///       S→C  NMT_WELCOME    (FString 地图路径 "/Game/...")   ← 引擎门
///       C→S  NMT_JOIN       (单字节)
///   客户端只有在收到 NMT_WELCOME 后才开始复制 Actor（PlayerState 等）；
///   被拒连接收到 NMT_FAILURE 后直接断开。因此：
///       战斗流 ⇔ S→C ch0 出现 NMT_WELCOME 且载荷含 "/Game/" 字符串
///
/// DFM 的 NMT 枚举值由全部历史抓包逐字节钉死（与标准 UE4 编号不同）：
///   HELLO=0, WELCOME=1, CHALLENGE=3, LOGIN=5, FAILURE=6, JOIN=9
///
/// 存储形态说明：Python 版从 flow 目录（frames.jsonl + request.bin/response.bin）
/// 读帧；C# 版面向主程序的实时内存流，输入改为 (方向, seq, 原始载荷) 枚举——
/// 判定语义逐帧一致，只是取数形态适配宿主进程。
/// </summary>
public static class HandshakeScanner
{
    public const int NmtHello = 0;
    public const int NmtWelcome = 1;
    public const int NmtChallenge = 3;
    public const int NmtLogin = 5;
    public const int NmtFailure = 6;
    public const int NmtJoin = 9;

    /// <summary>握手必在连接最初几帧内完成（实测全部战斗流 ≤8 帧）；96 帧是宽裕上限。</summary>
    public const int HsMaxFrames = 96;

    /// <summary>UE4 FString（i32 长度含结尾 \0，ASCII）（Python: _fstring_at）。</summary>
    private static string? FStringAt(byte[] payload, int off)
    {
        if (payload.Length < off + 4) return null;
        var n = BitConverter.ToInt32(payload, off);
        if (n <= 1 || n > 512) return null;
        if (payload.Length < off + 4 + n - 1) return null;
        try
        {
            return System.Text.Encoding.ASCII.GetString(payload, off + 4, n - 1);
        }
        catch (System.Text.DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// 单 bunch 扫描（Python: _scan_bunch 的公开等价）——flowlive 的增量握手
    /// 跟踪按帧逐 bunch 调用。
    /// 返回 (nmt, detail)。detail：challenge 串 / welcome 地图 / failure 文本。
    /// </summary>
    public static (int? Nmt, string? Detail) ScanSingleBunch(byte[] payload, int pbits)
    {
        if (pbits < 8 || payload.Length == 0) return (null, null);
        var nmt = payload[0];
        switch (nmt)
        {
            case NmtChallenge:
                var challenge = FStringAt(payload, 1);
                return challenge is not null ? ((int?)nmt, challenge) : (null, null);
            case NmtWelcome:
            {
                var map = FStringAt(payload, 1);
                if (map is not null && map.StartsWith("/Game/", StringComparison.Ordinal))
                {
                    return ((int?)nmt, map);
                }
                var j = Array.IndexOf(payload, System.Text.Encoding.ASCII.GetBytes("/Game/"));
                if (j >= 0 && j < 128)
                {
                    var end = Array.IndexOf(payload, (byte)0, j);
                    var raw = end < 0 ? payload[j..] : payload[j..end];
                    return ((int?)nmt, System.Text.Encoding.ASCII.GetString(raw));
                }
                return (null, null);
            }
            case NmtFailure:
                return ((int?)nmt, FStringAt(payload, 1));
            default:
                return ((int?)nmt, null);
        }
    }

    /// <summary>一帧输入：方向（request/response）、包序号、原始载荷。</summary>
    public readonly record struct Frame(string Dir, uint Seq, byte[] Raw);

    /// <summary>
    /// 扫描双向 ch0 控制 bunch，重建握手状态机（Python: scan_control）。
    /// 确定性、只看前 HsMaxFrames 帧。s2cTable 非空时 response 帧先过 S2C 去混淆。
    /// </summary>
    public static Handshake ScanControl(
        IEnumerable<Frame> frames,
        byte[]? s2cTable = null,
        int maxFrames = HsMaxFrames)
    {
        var hs = new Handshake();
        var idx = new Dictionary<string, int> { ["request"] = 0, ["response"] = 0 };
        var seen = new HashSet<string>();
        foreach (var frame in frames)
        {
            var d = frame.Dir;
            if (d is not ("request" or "response")) continue;
            if (idx[d] >= maxFrames) continue;
            if (!seen.Add(d + ":" + frame.Seq)) continue;
            idx[d]++;
            var raw = frame.Raw;
            if (d == "response" && s2cTable is not null)
            {
                raw = S2cDec.DecodeS2c(raw, s2cTable);
            }
            EnginePacketTrace tr;
            try
            {
                tr = EnginePacketCodec.DecodeEnginePacket((int)frame.Seq, d, raw);
            }
            catch
            {
                continue;
            }
            var bp = tr.BunchParse;
            if (tr.Error is not null || bp is null || bp.Error is not null) continue;
            foreach (var bn in bp.Bunches)
            {
                if (bn.Error is not null || bn.ChannelIndex != 0 || bn.Partial != 0) continue;
                var (nmt, detail) = ScanSingleBunch(bn.Payload, bn.PayloadBitCount);
                if (nmt is null) continue;
                switch (nmt.Value)
                {
                    case NmtHello:
                        hs.Hello = true;
                        break;
                    case NmtChallenge:
                        hs.Challenge = detail;
                        break;
                    case NmtLogin:
                        hs.Login = true;
                        break;
                    case NmtWelcome:
                        if (hs.Welcome is null)
                        {
                            hs.Welcome = detail;
                            hs.WelcomeFrame = idx[d] - 1;
                        }
                        hs.Rounds++;
                        break;
                    case NmtJoin:
                        hs.Join = true;
                        break;
                    case NmtFailure when hs.Failure is null:
                        hs.Failure = detail;
                        break;
                }
            }
        }
        return hs;
    }
}
