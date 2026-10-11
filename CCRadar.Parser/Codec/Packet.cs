namespace CCRadar.Parser.Codec;

using System.Buffers.Binary;

/// <summary>
/// DFM 包层：自定义 8 字节请求帧封装 + UE 风格位流头。
/// 1:1 移植自 Python: codec/packet.py。
///
/// 请求方向（client → server）帧封装为：
///   tag:u32 LE | serial:u32 LE | ue_data | 1 个填充字节
/// 应答帧为裸 UE 位流。
///
/// UE 位流布局（源自 battle_codec_capture_simplified.rs 的会话笔记）：
///   data_bits  = termination_data_bits(ue)          （最后一个置位位 + 1）
///   bit 0      = handler 位（0 = 数据，1 = 控制/握手）
///   bits 1..32 = packed u32：
///                  history_count (4 bits) → +1 得到数量
///                  acked_seq     (14 bits)
///                  packet_seq    (14 bits)
///   bits 33..  = history_count 个 32 位 ack 历史字
///   body       = 剩余位（bunch 流）
/// </summary>
public static class PacketLayer
{
    public const string Request = "request";
    public const string Response = "response";

    public const string PacketKindInvalid = "invalid";
    public const string PacketKindEmpty = "empty";
    public const string PacketKindControl = "control";
    public const string PacketKindNormal = "normal";
    public const string PacketKindHeaderOnly = "header_only";

    /// <summary>
    /// 解析一帧包的所有组成部分（Python: decode_packet_parts()）。
    /// 返回 Packet 记录；非 normal 时 packet.Error 说明原因。
    /// </summary>
    public static Packet DecodePacketParts(
        int captureSequence,
        string direction,
        ReadOnlySpan<byte> raw,
        bool requestPrefix = true)
    {
        var request = direction == Request;

        // 请求帧封装：tag(4) + serial(4) + ue + padding(1)
        uint? requestTag = null;
        uint? requestSerial = null;
        byte[] ue;
        if (request && requestPrefix && raw.Length >= 9)
        {
            requestTag = BinaryPrimitives.ReadUInt32LittleEndian(raw);
            requestSerial = BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(4, 4));
            ue = raw.Slice(8, raw.Length - 9).ToArray();   // 去掉 tag/serial 头与 1 字节填充
        }
        else
        {
            ue = raw.ToArray();
        }

        var packet = new Packet
        {
            CaptureSequence = captureSequence,
            Direction = direction,
            RequestTag = requestTag,
            RequestSerial = requestSerial,
            Ue = ue,
            PacketKind = PacketKindInvalid,
        };

        var dataBits = BitPrimitives.TerminationDataBits(ue);
        if (dataBits == 0)
        {
            packet.PacketKind = PacketKindEmpty;
            packet.Error = "no termination bit / zero payload";
            return packet;
        }

        packet.HandlerBit = BitPrimitives.GetBit(ue, 0);
        if (packet.HandlerBit == 1)
        {
            // 控制/握手包：结构未知，保留原始数据供分析。
            packet.PacketKind = PacketKindControl;
            return packet;
        }

        if (dataBits < 33)
        {
            packet.PacketKind = PacketKindInvalid;
            packet.Error = $"header shorter than 33 bits ({dataBits})";
            return packet;
        }

        var packed = BitPrimitives.ReadBitsAt(ue, 1, 32);
        var historyCount = (int)(packed & 0xF) + 1;
        var acked = (int)((packed >> 4) & 0x3FFF);
        var sequence = (int)((packed >> 18) & 0x3FFF);

        packet.AckedSequence = acked;
        packet.PacketSequence = sequence;

        var headerBits = 33 + historyCount * 32;
        if (headerBits > dataBits)
        {
            packet.PacketKind = PacketKindHeaderOnly;
            packet.Error = $"history words exceed packet ({headerBits}>{dataBits})";
            return packet;
        }

        packet.HistoryWords = new List<uint>(historyCount);
        for (var i = 0; i < historyCount; i++)
        {
            packet.HistoryWords.Add((uint)BitPrimitives.ReadBitsAt(ue, 33 + i * 32, 32));
        }

        packet.BodyBits = dataBits - headerBits;
        packet.Body = BitPrimitives.SliceBits(ue, headerBits, packet.BodyBits);
        packet.PacketKind = PacketKindNormal;
        return packet;
    }
}

/// <summary>一帧包的完整解析结果（Python: Packet dataclass）。</summary>
public sealed class Packet
{
    public int CaptureSequence { get; set; }
    public string Direction { get; set; } = string.Empty;
    public uint? RequestTag { get; set; }
    public uint? RequestSerial { get; set; }
    public byte[] Ue { get; set; } = Array.Empty<byte>();
    public string PacketKind { get; set; } = PacketLayer.PacketKindInvalid;
    public int HandlerBit { get; set; }
    public int? AckedSequence { get; set; }
    public int? PacketSequence { get; set; }
    public List<uint> HistoryWords { get; set; } = new();
    public byte[] Body { get; set; } = Array.Empty<byte>();
    public int BodyBits { get; set; }
    public string? Error { get; set; }

    /// <summary>导出为字典（Python: as_dict()），供日志/对拍使用。</summary>
    public Dictionary<string, object?> AsDict() => new()
    {
        ["capture_sequence"] = CaptureSequence,
        ["direction"] = Direction,
        ["request_tag"] = RequestTag,
        ["request_serial"] = RequestSerial,
        ["ue_len"] = Ue.Length,
        ["packet_kind"] = PacketKind,
        ["handler_bit"] = HandlerBit,
        ["acked_sequence"] = AckedSequence,
        ["packet_sequence"] = PacketSequence,
        ["history_words"] = HistoryWords,
        ["body_bits"] = BodyBits,
        ["error"] = Error,
    };
}
