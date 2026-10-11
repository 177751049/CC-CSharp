namespace CCRadar.Parser.Codec;

/// <summary>
/// 连接级帧封装：剥除嵌套的两层 UE 终止位，读取 9/17 位 PacketHeader，
/// 并对剩余区间调用引擎精确 bunch 解码。
/// 1:1 移植自 Python: codec/engine_packet.py。
///
/// 正常捕获的数据报里有两层嵌套的 UE 终止位：
///   PacketHandler 外层帧：handler + FNetPacketNotify + history + ReceivedPacket body + 外层终止位
///   UNetConnection::ReceivedPacket body：9/17 位 legacy PacketHeader + bunches + 内层终止位
/// </summary>
public static class EnginePacketCodec
{
    /// <summary>
    /// 读取在 ReceivedPacket 处恢复的 9/17 位 PacketHeader（Python: decode_legacy_packet_header()）。
    /// 首位请求/上报 server-frame-time 信息。server→client 包在该位置位且连接支持时
    /// 额外携带一个字节；client→server 不携带。两个方向都始终携带远端带宽字节。
    /// </summary>
    public static LegacyPacketHeader DecodeLegacyPacketHeader(
        byte[] packetBody,
        int dataBits,
        string direction,
        bool responseFrameTimeCapable = true)
    {
        var result = new LegacyPacketHeader { Direction = direction };
        var reader = new BitReader(packetBody, dataBits);
        try
        {
            result.FrameTimeFlag = reader.ReadBit();
            if (direction == PacketLayer.Response && result.FrameTimeFlag != 0 && responseFrameTimeCapable)
                result.ServerFrameTimeByte = (int)reader.ReadUInt8();
            result.RemoteKbytesPerSecondByte = (int)reader.ReadUInt8();
            result.EndBit = reader.Position;
        }
        catch (BitError exc)
        {
            result.EndBit = reader.Position;
            result.Error = exc.Message;
            result.ErrorBit = reader.Position;
        }
        return result;
    }

    /// <summary>
    /// 解码两层终止位、PacketHeader 与全部引擎 bunches（Python: decode_engine_packet()）。
    /// </summary>
    public static EnginePacketTrace DecodeEnginePacket(
        int captureSequence,
        string direction,
        ReadOnlySpan<byte> raw,
        bool requestPrefix = true,
        bool responseFrameTimeCapable = true,
        int maxPacketBytes = EngineBunchCodec.DefaultMaxPacketBytes,
        int? maxChannelIndex = EngineBunchCodec.DefaultMaxChannelIndex,
        int maxBunches = 256)
    {
        var packet = PacketLayer.DecodePacketParts(captureSequence, direction, raw, requestPrefix);
        var rawUeStart = direction == PacketLayer.Request && requestPrefix && raw.Length >= 9 ? 64 : 0;
        var trace = new EnginePacketTrace
        {
            Packet = packet,
            RawLength = raw.Length,
            RawUeStartBit = rawUeStart,
        };

        if (packet.PacketKind != PacketLayer.PacketKindNormal)
        {
            trace.Error = packet.Error;
            return trace;
        }

        // 外层：Packet.ue 的数据位数与终止位。
        var outerDataBits = BitPrimitives.TerminationDataBits(packet.Ue);
        trace.OuterTerminationUeBit = outerDataBits;
        trace.PacketBodyUeStartBit = outerDataBits - packet.BodyBits;
        if (trace.PacketBodyUeStartBit < 0)
        {
            trace.Error = "packet body starts before UE bit zero";
            return trace;
        }

        // 内层：body 内必须存在终止位（body 至少含一个非零字节）。
        if (packet.Body.Length == 0 || packet.Body.All(b => b == 0))
        {
            trace.Error = "missing inner termination bit";
            return trace;
        }
        var innerDataBits = BitPrimitives.TerminationDataBits(packet.Body);
        if (BitPrimitives.GetBit(packet.Body, innerDataBits) != 1)
        {
            trace.Error = "missing inner termination bit";
            return trace;
        }
        trace.InnerTerminationBodyBit = innerDataBits;
        trace.InnerTrailingZeroBits = packet.BodyBits - innerDataBits - 1;
        if (trace.InnerTrailingZeroBits < 0)
        {
            trace.Error = "inner termination lies outside Packet.body";
            return trace;
        }

        // 内层 PacketHeader（9/17 位）。
        var header = DecodeLegacyPacketHeader(
            packet.Body, innerDataBits, direction,
            responseFrameTimeCapable: responseFrameTimeCapable);
        trace.PacketHeader = header;
        if (header.Error is not null)
        {
            trace.Error = $"PacketHeader: {header.Error}";
            return trace;
        }

        // 纯 bunch 区切片。
        var bunchBits = innerDataBits - header.EndBit;
        if (bunchBits < 0)
        {
            trace.Error = "PacketHeader exceeds inner data interval";
            return trace;
        }
        var bunchData = BitPrimitives.SliceBits(packet.Body, header.EndBit, bunchBits);
        var bodyBase = header.EndBit;
        var ueBase = trace.PacketBodyUeStartBit!.Value + bodyBase;
        var coordinateBases = new Dictionary<string, int>
        {
            ["packet_body"] = bodyBase,
            ["ue"] = ueBase,
            ["raw"] = rawUeStart + ueBase,
        };
        trace.BunchParse = EngineBunchCodec.DecodeEngineBunches(
            bunchData, bunchBits,
            maxPacketBytes: maxPacketBytes,
            maxChannelIndex: maxChannelIndex,
            maxBunches: maxBunches,
            coordinateBases: coordinateBases);
        return trace;
    }
}

/// <summary>
/// 紧挨第一个 bunch 之前的连接级头（Python: LegacyPacketHeader）。
/// 9/17 位：帧时间标志 [+ server frame time 字节（仅响应方向且支持时）] + 远端带宽字节。
/// </summary>
public sealed class LegacyPacketHeader
{
    public string Direction { get; set; } = string.Empty;
    public int FrameTimeFlag { get; set; }
    public int? ServerFrameTimeByte { get; set; }
    public int? RemoteKbytesPerSecondByte { get; set; }
    public int StartBit { get; set; }
    public int EndBit { get; set; }
    public string? Error { get; set; }
    public int? ErrorBit { get; set; }

    public int BitCount => EndBit - StartBit;
}

/// <summary>正常包 + 全部恢复的嵌套框架坐标（Python: EnginePacketTrace）。</summary>
public sealed class EnginePacketTrace
{
    public Packet Packet { get; set; } = new();
    public int RawLength { get; set; }
    public int RawUeStartBit { get; set; }
    public int? OuterTerminationUeBit { get; set; }
    public int? PacketBodyUeStartBit { get; set; }
    public int? InnerTerminationBodyBit { get; set; }
    public int InnerTrailingZeroBits { get; set; }
    public LegacyPacketHeader? PacketHeader { get; set; }
    public EngineBunchParse? BunchParse { get; set; }
    public string? Error { get; set; }
}
