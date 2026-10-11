using System.Diagnostics;
using CCRadar.Parser.Codec;

namespace CCRadar.Parser.Core;

/// <summary>
/// c2s_self.py 的 C# 等价实现 —— 从上行（C2S）明文解自机位姿
/// （位置/速度/朝向/俯仰）。数据来源：客户端→服务器的 ch3 消息
/// parse_fgpmove_params（起点 bit 53），移植自 udp_actors.h::ParseFgp。
///
/// 实测校验（run_20260912_192140 flow 3，3030 个可配对样本）：yaw 命中 84%；
/// loc X/Y 中位差 0.0cm，Z 常量 +86cm（脚底/眼高口径差）。
///
/// 尾部两种形态（2026-09-15 团队模式一局实测）：full（acc 量化向量 + 8 位
/// flags）与 lite（直接 8 位收尾）。lite 才是 ch3 里出现最多的类；收下它，
/// 自机位姿 7.4Hz → 15.0Hz。判据仍是"正好读尽"：先试 full，读不尽就回溯
/// 试 lite。
/// </summary>
public static class C2sSelf
{
    /// <summary>parse_fgpmove_params 在 ch3 载荷中的起始位（Python: FGP_START_BIT）。</summary>
    public const int FgpStartBit = 53;

    private const double LocSane = 2.0e6;      // |坐标| 上限（厘米）
    private const double LocMinXy = 5000.0;    // 世界坐标下界：小于 50m 视为没解对
    private const double TsMin = 1.0, TsMax = 1.0e6;

    /// <summary>位流读取（Python: _BR——LSB-first，越界置 error 而非抛异常）。</summary>
    private struct Br
    {
        private readonly byte[] _d;
        public int Pos;
        private readonly int _n;
        public bool Error;

        public Br(byte[] d, int pos, int nbits)
        {
            _d = d; Pos = pos; _n = nbits; Error = false;
        }

        public readonly int Remaining() => _n - Pos;

        public ulong Read(int width)
        {
            if (width < 0 || Pos + width > _n)
            {
                Error = true;
                return 0;
            }
            ulong v = 0;
            for (var i = 0; i < width; i++)
            {
                var b = Pos + i;
                if ((_d[b >> 3] & (1 << (b & 7))) != 0) v |= 1ul << i;
            }
            Pos += width;
            return v;
        }

        /// <summary>UE 的变长 SerializeInt（值 + 掩码 ≥ 上限就停）。</summary>
        public long ReadInt(long maxValue)
        {
            if (maxValue <= 1) return 0;
            long value = 0, mask = 1;
            while (value + mask < maxValue)
            {
                if (Read(1) != 0) value |= mask;
                if (Error) return 0;
                mask <<= 1;
            }
            return value;
        }
    }

    private static float F32(ulong bits)
        => BitConverter.UInt32BitsToSingle((uint)(bits & 0xFFFFFFFF));

    /// <summary>UE 的量化向量（Python: _packed_vec）：位宽码（+2 位）+ 3 分量。</summary>
    private static double[]? PackedVec(ref Br r, int maxBits, double scale)
    {
        var code = r.ReadInt(maxBits);
        if (r.Error) return null;
        var width = (int)code + 2;
        if (width <= 0 || width >= 32) return null;
        var span = 1L << width;
        var output = new double[3];
        for (var i = 0; i < 3; i++)
        {
            var raw = r.ReadInt(span);
            if (r.Error) return null;
            output[i] = (raw - span * 0.5) / scale;
        }
        return output;
    }

    /// <summary>16 位压缩角 → ±180 度（Python: _angle）。</summary>
    private static double Angle(long short16)
    {
        var deg = short16 * (360.0 / 65536.0);
        return deg > 180.0 ? deg - 360.0 : deg;
    }

    /// <summary>
    /// 解一条 ch3 自机移动消息（Python: parse_fgp）。成功返回 dict，否则 null。
    /// 返回 ts(秒)/loc(世界厘米)/vel/accel/yaw/pitch/flags/tail。
    /// </summary>
    public static Dictionary<string, object?>? ParseFgp(byte[] payload, int pbits, int start = FgpStartBit)
    {
        if (payload is null || pbits is < 190 or > 800) return null;
        var r = new Br(payload, start, pbits);
        if (r.Read(1) != 1 || r.Error) return null;
        var ts = F32(r.Read(32));
        if (r.Error || ts < TsMin || ts > TsMax) return null;
        var vel = PackedVec(ref r, 24, 10.0);
        if (vel is null) return null;
        r.Read(32);
        var yawPack = r.Read(32);
        if (r.Error) return null;
        var loc = PackedVec(ref r, 30, 100.0);
        if (loc is null || loc.Any(c => Math.Abs(c) >= LocSane)) return null;
        if (Math.Max(Math.Abs(loc[0]), Math.Abs(loc[1])) < LocMinXy) return null;
        // loc 之后分岔：回溯到同一位点分别试两种尾型，谁正好读尽谁算数。
        var tailPos = r.Pos;
        foreach (var kind in new[] { "full", "lite" })
        {
            r.Pos = tailPos;
            r.Error = false;
            r.Read(8);
            r.Read(32);
            r.Read(8);
            if (r.Error) continue;
            double[]? accel = null;
            long? flags = null;
            if (kind == "full")
            {
                accel = PackedVec(ref r, 30, 100.0);
                if (accel is null) continue;
                flags = (long)r.Read(8);
                if (r.Error || (flags.Value & 0xFB) != 0) continue;
                var left = r.Remaining();
                if ((flags.Value & 4) != 0)
                {
                    if (left != 696) continue;
                    r.Read(696);
                    if (r.Error || r.Remaining() != 0) continue;
                }
                else if (left != 0) continue;
            }
            else
            {
                r.Read(8);
                if (r.Error || r.Remaining() != 0) continue;
            }
            return new Dictionary<string, object?>
            {
                ["ts"] = ts,
                ["loc"] = loc,
                ["vel"] = vel,
                ["accel"] = accel,
                ["yaw"] = Angle((long)((yawPack >> 16) & 0xFFFF)),
                ["pitch"] = Angle((long)(yawPack & 0xFFFF)),
                ["flags"] = flags,
                ["tail"] = kind,
            };
        }
        return null;
    }
}

/// <summary>
/// flowlive.py 的 C# 等价实现 —— 增量 flow 加载器核心。
///
/// 与 Python IncrementalFlowLoader 完全同构的部分：握手跟踪（引擎级战斗流
/// 判据）、S2C 去混淆、可靠序列 unwrap、partial 分片重组、LB 产出、上行
/// 自机位姿（独立重组器与 head，绝不与下行共用）。
///
/// 形态适配（注释说明）：Python 从 frames.jsonl/response.bin 增量读文件并做
/// 按需增长/空闲压实——那是文件缓存形态的 IO 优化；C# 面向主程序实时流，
/// 调用方直接逐包调 ConsumePacket，无需文件缓冲层。
/// </summary>
public sealed class FlowLiveLoader
{
    /// <summary>逻辑 bunch（Python: flowload.LB —— 与 BunchView 字段一致）。</summary>
    public sealed class LB
    {
        public long Seq { get; init; }
        public int Ch { get; init; }
        public byte[] Payload { get; init; } = Array.Empty<byte>();
        public int PBits { get; init; }
        public int Open { get; init; }
        public int Close { get; init; }
        public int Hx { get; init; }
        public int Hm { get; init; }
        public int Partial { get; init; }
        public int Rel { get; init; }
        public int? CloseReason { get; init; }
        public int Dormant { get; init; }
        /// <summary>每 fragment 的 (start_bit, hx, hm)。</summary>
        public List<(int Start, bool Hx, bool Hm)>? Frags { get; init; }

        /// <summary>转 BunchView（业务层消费形态）。</summary>
        public BunchView ToView() => new()
        {
            Open = Open != 0, Close = Close != 0, CloseReason = CloseReason,
            Ch = Ch, Payload = Payload, PBits = PBits,
            Hx = Hx != 0, Hm = Hm != 0, Frags = Frags, Seq = Seq,
        };
    }

    public const int RespKeepTail = 262144;
    public const int RespCompactMin = 8 << 20;
    public const int RespGrowChunk = 4 << 20;

    private readonly bool _requestPrefix;
    public int PacketErrors { get; private set; }
    /// <summary>诊断：帧解码返回空 BunchParse 的次数。</summary>
    public int NBunchParseNull { get; private set; }
    /// <summary>诊断：看到的 bunch 总数（含错误）。</summary>
    public long NBunchesSeen { get; private set; }
    /// <summary>诊断：重组器产出的逻辑 bunch 数。</summary>
    public long NFeedEmitted { get; private set; }
    /// <summary>诊断：重组器/解包器抛异常次数。</summary>
    public int DupFeedCount { get; private set; }
    private readonly HashSet<long> _emittedHashes = [];
    public int NReasmErrors { get; private set; }
    public int PoseSamples { get; private set; }

    private byte[]? _s2cTable;
    private bool _autoS2c;

    /// <summary>S2C 模式（pending/s2c/bare；Python: s2c_mode）。</summary>
    public string S2cMode { get; private set; }

    private readonly EngineStream.PartialBunchReassembler _reasm = new();
    private readonly EngineStream.ReliableSequenceUnwrapper _unw = new();
    private readonly Dictionary<int, int[]> _head = new();

    /// <summary>通道 → 逻辑 bunch 列表（Python: self.b）。</summary>
    public Dictionary<int, List<LB>> B { get; } = new();

    // C2S 自机位姿：独立重组器与 head，绝不与下行共用。
    private bool _c2sSelf;
    private int _c2sEvery = 4;
    private long _reqSeen;
    private readonly EngineStream.PartialBunchReassembler _reasmReq = new();
    private readonly Dictionary<int, int[]> _headReq = new();

    /// <summary>最新自机位姿（Python: self_pose；null = 无）。</summary>
    public Dictionary<string, object?>? SelfPose { get; private set; }
    /// <summary>该位姿的墙钟时刻（判陈旧用；Python: self_pose_t）。</summary>
    public double SelfPoseT { get; private set; }

    /// <summary>(seq, ts) response 帧时间戳（Python: seq_ts）。</summary>
    public List<(long Seq, double Ts)> SeqTs { get; } = [];

    public int NFrames { get; private set; }
    public int NLb { get; private set; }

    // ---- 控制通道握手跟踪（引擎级战斗流判据）----
    /// <summary>握手状态（Python: self.hs）。</summary>
    public Handshake Hs { get; } = new();
    public int NReqDecoded { get; private set; }
    /// <summary>握手已收敛（welcome+join 齐或帧数超限）。</summary>
    public bool CtrlDone { get; private set; }

    public FlowLiveLoader(byte[]? s2cTable = null, bool autoS2c = false,
        bool c2sSelf = false, bool requestPrefix = true)
    {
        if (s2cTable is not null && s2cTable.Length != 512)
        {
            throw new ArgumentException("s2c_table must be 512 bytes");
        }
        _s2cTable = s2cTable is not null ? (byte[])s2cTable.Clone() : null;
        _autoS2c = autoS2c && s2cTable is null;
        S2cMode = _autoS2c ? "pending" : (_s2cTable is not null ? "s2c" : "bare");
        _requestPrefix = requestPrefix;
        _c2sSelf = c2sSelf;
    }

    /// <summary>注入 S2C 表（主程序链路：TGCP 握手挖出的密钥不经过这里；外部源时由宿主注入）。</summary>
    public void SetS2cTable(byte[] table)
    {
        if (table.Length != 512) throw new ArgumentException("s2c_table must be 512 bytes");
        _s2cTable = (byte[])table.Clone();
        _autoS2c = false;
        S2cMode = "s2c";
    }

    /// <summary>
    /// 开始解上行自机位姿（Python: enable_c2s_self）。默认不抽稀（every=1）：
    /// ch3 里只有约 20% 帧是自机位姿消息，抽稀会把位姿率打掉一半——第一视角
    /// 俯仰"顿+延迟"的根源。
    /// </summary>
    public void EnableC2sSelf(int every = 1)
    {
        _c2sSelf = true;
        _c2sEvery = Math.Max(1, every);
    }

    /// <summary>增量跟踪 ch0 控制 bunch（Python: _track_ctrl）。</summary>
    private void TrackCtrl(EngineBunchParse bp)
    {
        foreach (var bn in bp.Bunches)
        {
            if (bn.Error is not null || bn.ChannelIndex != 0 || bn.Partial != 0
                || bn.PayloadBitCount < 8 || bn.Payload.Length == 0)
            {
                continue;
            }
            var (nmt, det) = ScanBunch(bn.Payload, bn.PayloadBitCount);
            if (nmt is null) continue;
            switch (nmt.Value)
            {
                case HandshakeScanner.NmtHello:
                    Hs.Hello = true;
                    break;
                case HandshakeScanner.NmtChallenge when det is not null:
                    Hs.Challenge = det;
                    break;
                case HandshakeScanner.NmtLogin:
                    Hs.Login = true;
                    break;
                case HandshakeScanner.NmtWelcome when det is not null:
                    if (Hs.Welcome is null)
                    {
                        Hs.Welcome = det;
                        Hs.WelcomeFrame = NFrames;
                    }
                    Hs.Rounds++;
                    break;
                case HandshakeScanner.NmtJoin:
                    Hs.Join = true;
                    break;
                case HandshakeScanner.NmtFailure when Hs.Failure is null:
                    Hs.Failure = det;
                    break;
            }
        }
    }

    /// <summary>单 bunch 扫描（与 handshake._scan_bunch 相同；复用 HandshakeScanner 的判定）。</summary>
    private static (int? Nmt, string? Detail) ScanBunch(byte[] payload, int pbits)
    {
        // 走 HandshakeScanner 的公开扫描：单帧扫描（scan_control 是多帧状态机，
        // 这里只需要单 bunch 判定——复制其内部逻辑的最小子集）。
        return HandshakeScanner.ScanSingleBunch(payload, pbits);
    }

    /// <summary>握手是否已收敛（Python: _ctrl_converged）。</summary>
    private bool CtrlConverged()
        => (Hs.Welcome is not null && Hs.Join)
           || NFrames >= HandshakeScanner.HsMaxFrames
           || NReqDecoded >= HandshakeScanner.HsMaxFrames;

    /// <summary>
    /// 消费一个包（Python: consume_packet）——文件或 V5 明文适配器皆可。
    /// 返回新增 LB 数（request 方向恒 0；response < 0 表示字节未就绪留待重试——
    /// 实时形态恒 ≥ 0）。
    /// </summary>
    public int ConsumePacket(long seq, string d, byte[] raw, double ts)
    {
        if (d == "request")
        {
            // 握手跟踪需要 C→S 方向（Hello/Login/Join）；收敛后就不再解码。
            // 开了 c2s_self 要一直解：自机位姿只有上行有。抽稀放在解码之前。
            if (CtrlDone && !_c2sSelf) return 0;
            if (CtrlDone && _c2sSelf)
            {
                _reqSeen++;
                if (_reqSeen % _c2sEvery != 0) return 0;
            }
            NReqDecoded++;
            EnginePacketTrace tr;
            try
            {
                tr = EnginePacketCodec.DecodeEnginePacket((int)seq, "request", raw,
                    requestPrefix: _requestPrefix);
            }
            catch
            {
                PacketErrors++;
                return 0;
            }
            var bp = tr.BunchParse;
            if (tr.Error is not null || (bp is not null && bp.Error is not null))
            {
                PacketErrors++;
                return 0;
            }
            if (bp is null) { NBunchParseNull++; return 0; }
            if (!CtrlDone)
            {
                TrackCtrl(bp);
                CtrlDone = CtrlConverged();
            }
            if (_c2sSelf) C2sSelfFromBunches(seq, bp);
            return 0;
        }
        // response
        NFrames++;
        SeqTs.Add((seq, ts));
        if (SeqTs.Count > 100000)
        {
            SeqTs.RemoveRange(0, 50000);
        }
        try
        {
            if (_s2cTable is not null)
            {
                raw = S2cDec.DecodeS2c(raw, _s2cTable);
            }
            var tr2 = EnginePacketCodec.DecodeEnginePacket((int)seq, "response", raw);
            var bp2 = tr2.BunchParse;
            if (tr2.Error is not null || bp2 is null || bp2.Error is not null)
            {
                if (tr2.Error is not null || (bp2 is not null && bp2.Error is not null))
                {
                    PacketErrors++;
                }
                return 0;
            }
            if (!CtrlDone)
            {
                TrackCtrl(bp2);
                CtrlDone = CtrlConverged();
            }
            var added = 0;
            foreach (var bn in bp2.Bunches)
            {
                NBunchesSeen++;
                if (bn.Error is not null) continue;
                try { _unw.Apply(bn); }
                catch { NReasmErrors++; continue; }
                var ci = bn.ChannelIndex;
                var starts = bn.Partial == 0 || bn.PartialInitial != 0;
                if (starts || !_head.ContainsKey(ci))
                {
                    _head[ci] = [0, 0, (int)seq];
                }
                _head[ci][0] |= bn.HasPackageMapExports != 0 ? 1 : 0;
                _head[ci][1] |= bn.HasMustBeMappedGuids != 0 ? 1 : 0;
                List<EngineStream.ReassembledBunch> emitted;
                try { emitted = _reasm.Feed(bn, null, $"ch{ci}"); }
                catch { NReasmErrors++; continue; }
                NFeedEmitted += emitted.Count;
                foreach (var log in emitted)
                {
                    // 重复产出检测：同一 (ch, payload 前缀哈希) 的逻辑 bunch 只应出现一次。
                    var h = -1L;
                    var pl = log.Payload;
                    for (var i = 0; i < Math.Min(16, pl.Length); i++) h = h * 31 + pl[i];
                    h = h * 31 + log.PayloadBitCount + log.ChannelIndex * 1000003L;
                    if (!_emittedHashes.Add(h)) DupFeedCount++;

                    var has = _head.TryGetValue(ci, out var hv);
                    var head = has ? hv : new int[] { 0, 0, (int)seq };
                    if (has) _head.Remove(ci);
                    var frags = log.Fragments.Select(f =>
                    {
                        var interval = f.TryGetValue("combined_payload_interval", out var iv) && iv is List<object?> il && il.Count == 2
                            ? Convert.ToInt32(il[0]) : 0;
                        var fhx = f.TryGetValue("has_package_map_exports", out var hx2) && hx2 is not null && Convert.ToInt64(hx2) != 0;
                        var fhm = f.TryGetValue("has_must_be_mapped_guids", out var hm2) && hm2 is not null && Convert.ToInt64(hm2) != 0;
                        return (interval, fhx, fhm);
                    }).ToList();
                    if (!B.TryGetValue(ci, out var list))
                    {
                        list = [];
                        B[ci] = list;
                    }
                    list.Add(new LB
                    {
                        Seq = head[2], Ch = ci, Payload = log.Payload,
                        PBits = log.PayloadBitCount, Open = log.Open,
                        Close = log.Close, Hx = head[0], Hm = head[1],
                        Partial = log.WasPartial ? 1 : 0, Rel = log.Reliable ? 1 : 0,
                        CloseReason = log.CloseReason, Dormant = log.Dormant,
                        Frags = frags,
                    });
                    NLb++;
                    added++;
                }
            }
            return added;
        }
        catch
        {
            PacketErrors++;
            return 0;
        }
    }

    /// <summary>上行 ch3 自机位姿诊断：ch3 bunch 数 / 重组抛错数 / 重组产出数 / 成功解出位姿数。</summary>
    public long NCh3Bunches { get; private set; }
    public long NReasmReqThrow { get; private set; }
    public long NReasmReqOut { get; private set; }

    /// <summary>上行 bunch → 独立重组 → ch3 自机位姿（Python: _c2s_self_from_bunches）。</summary>
    private void C2sSelfFromBunches(long seq, EngineBunchParse bp)
    {
        foreach (var bn in bp.Bunches)
        {
            if (bn.ChannelIndex != 3) continue;
            NCh3Bunches++;
            if (bn.Error is not null && bn.Payload.Length == 0) continue;
            var starts = bn.Partial == 0 || bn.PartialInitial != 0;
            if (starts || !_headReq.ContainsKey(3))
            {
                _headReq[3] = [0, 0, (int)seq];
            }
            _headReq[3][0] |= bn.HasPackageMapExports != 0 ? 1 : 0;
            _headReq[3][1] |= bn.HasMustBeMappedGuids != 0 ? 1 : 0;
            List<EngineStream.ReassembledBunch> logs;
            try
            {
                logs = _reasmReq.Feed(bn, null, "req3");
            }
            catch
            {
                NReasmReqThrow++;
                _headReq.Remove(3);
                continue;
            }
            NReasmReqOut += logs.Count;
            foreach (var log in logs)
            {
                _headReq.Remove(3);
                var pose = C2sSelf.ParseFgp(log.Payload, log.PayloadBitCount);
                if (pose is not null)
                {
                    PoseSamples++;
                    pose["seq"] = seq;
                    SelfPose = pose;
                    SelfPoseT = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
                }
            }
        }
    }
}
