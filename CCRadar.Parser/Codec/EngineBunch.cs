namespace CCRadar.Parser.Codec;

/// <summary>
/// 引擎精确的 DFM bunch 头读取器（从 ReceivedPacket 反汇编恢复）。
/// 1:1 移植自 Python: codec/engine_bunch.py。
///
/// 本模块与历史/暂行布局的 codec/bunch.py 有意分离。解码输入是"纯 bunch 区"：
/// 连接级内层终止位与 9/17 位 PacketHeader 必须已被剥除（由 EnginePacket 负责，
/// 并提供 raw/ue/packet_body 坐标基）。
///
/// 恢复出的头字段顺序（LSB-first）：
///   control_gate
///     若置位：open, close, [CloseReason = SerializeInt(15)]
///   replication_paused, reliable, pre_channel_flag
///   channel = DFM SerializeIntPacked（继续标志在 bit 0）
///   has_exports, has_must_be_mapped_guids, partial
///   若 reliable：channel_sequence_mod = SerializeInt(1024)
///   若 partial：partial_initial, partial_final
///   若 open 或 reliable：FName
///   payload_bits = SerializeInt(MaxPacket * 8)
///   payload
///
/// 引擎将 10 位可靠序列针对每通道活状态展开；离线解析因此记录序列化的
/// 模值，由有状态的调用方在排序后展开。
/// </summary>
public static class EngineBunchCodec
{
    public const int DfmMaxPackedBytes = 5;
    public const int HardCodedFNameMax = 0x25C;
    public const int ReliableSequenceMax = 0x400;
    public const int DefaultMaxPacketBytes = 1024;
    public const int DefaultMaxChannelIndex = 32767;

    /// <summary>
    /// 读取 DFM SerializeIntPacked 字节组（Python: read_dfm_packed_int()）。
    /// 每个序列化字节的 bit 0 是继续标志，bits 1..7 是下 7 个值位。
    /// 这是恢复出的通道索引读取处的精确内联路径。返回值与原始字节列表。
    /// </summary>
    public static (uint Value, List<uint> Raw) ReadDfmPackedInt(BitReader reader)
    {
        uint value = 0;
        var raw = new List<uint>();
        for (var group = 0; group < DfmMaxPackedBytes; group++)
        {
            var encoded = reader.ReadUInt8();
            raw.Add(encoded);
            value |= (uint)(encoded >> 1) << (group * 7);
            if ((encoded & 1) == 0) return (value, raw);
        }
        throw new BitError("DFM SerializeIntPacked exceeds 32 bits");
    }

    // 读 FString，限制最大单元数（Python: _read_fstring()）。
    private static string ReadFString(BitReader reader, int maxUnits)
    {
        var length = reader.ReadInt32();
        var units = Math.Abs(length);
        if (units > maxUnits)
            throw new BitError($"invalid FName FString length {length}");
        if (length >= 0)
        {
            var raw = new byte[units];
            for (var i = 0; i < units; i++) raw[i] = (byte)reader.ReadUInt8();
            if (raw.Length > 0 && raw[^1] == 0) raw = raw[..^1];
            return System.Text.Encoding.UTF8.GetString(raw);
        }
        var raw16 = new byte[units * 2];
        for (var i = 0; i < units; i++)
        {
            var unit = reader.ReadUInt16();
            raw16[i * 2] = (byte)(unit & 0xFF);
            raw16[i * 2 + 1] = (byte)(unit >> 8);
        }
        if (raw16.Length >= 2 && raw16[^1] == 0 && raw16[^2] == 0) raw16 = raw16[..^2];
        return System.Text.Encoding.Unicode.GetString(raw16);
    }

    /// <summary>
    /// 跳过恢复出的读取辅助所使用的 FName 格式（Python: read_serialized_fname()）。
    /// hardcoded=1：DFM packed int 硬编码索引；=0：FString 文本 + int32 编号。
    /// </summary>
    public static SerializedFName ReadSerializedFName(
        BitReader reader,
        int hardcodedMax = HardCodedFNameMax,
        int maxStringUnits = 4096)
    {
        var start = reader.Position;
        var hardcoded = reader.ReadBit() != 0;
        if (hardcoded)
        {
            var (index, _) = ReadDfmPackedInt(reader);
            if (index > hardcodedMax)
                throw new BitError($"hardcoded FName index {index} exceeds {hardcodedMax}");
            return new SerializedFName
            {
                Hardcoded = true,
                HardcodedIndex = (int)index,
                StartBit = start,
                EndBit = reader.Position,
            };
        }

        var text = ReadFString(reader, maxStringUnits);
        var number = reader.ReadInt32();
        return new SerializedFName
        {
            Hardcoded = false,
            Text = text,
            Number = number,
            StartBit = start,
            EndBit = reader.Position,
        };
    }

    // 读取单个 bunch（Python: _read_one_bunch()）。
    // 失败时抛出携带"已解码部分字段"的 BunchReadFailure —— 保留证据而非丢壳。
    private static EngineBunch ReadOneBunch(
        BitReader reader,
        int index,
        int maxPacketBytes,
        int? maxChannelIndex)
    {
        var bunch = new EngineBunch { Index = index, HeaderStartBit = reader.Position };
        try
        {
            bunch.ControlGate = reader.ReadBit();
            if (bunch.ControlGate != 0)
            {
                bunch.Open = reader.ReadBit();
                bunch.Close = reader.ReadBit();
                if (bunch.Close != 0)
                {
                    bunch.CloseReason = reader.ReadSerializeInt(15);
                    // 引擎从 EChannelCloseReason == 1 推导 bDormant。
                    bunch.Dormant = bunch.CloseReason == 1 ? 1 : 0;
                }
            }

            bunch.ReplicationPaused = reader.ReadBit();
            bunch.Reliable = reader.ReadBit();
            bunch.PreChannelFlag = reader.ReadBit();

            var (channelIndex, packedBytes) = ReadDfmPackedInt(reader);
            bunch.ChannelIndex = (int)channelIndex;
            bunch.ChannelPackedBytes = packedBytes;
            if (maxChannelIndex is not null && bunch.ChannelIndex >= maxChannelIndex)
                throw new BitError($"channel index {bunch.ChannelIndex} >= limit {maxChannelIndex}");

            bunch.HasPackageMapExports = reader.ReadBit();
            bunch.HasMustBeMappedGuids = reader.ReadBit();
            bunch.Partial = reader.ReadBit();

            if (bunch.Reliable != 0)
                bunch.ChannelSequenceMod = reader.ReadSerializeInt(ReliableSequenceMax);
            if (bunch.Partial != 0)
            {
                bunch.PartialInitial = reader.ReadBit();
                bunch.PartialFinal = reader.ReadBit();
            }
            if (bunch.Open != 0 || bunch.Reliable != 0)
                bunch.ChannelName = ReadSerializedFName(reader);

            bunch.PayloadBitCount = reader.ReadSerializeInt(maxPacketBytes << 3);
            bunch.HeaderEndBit = reader.Position;
            bunch.PayloadStartBit = reader.Position;
            if (bunch.PayloadBitCount > reader.Remaining())
            {
                // 分片 bunch：声明长度超出本包剩余位，余下部分在后续包。收下本包
                // 能给的全部位并标 partial，交给 PartialBunchReassembler 跨包缝合。
                // 不置 Error：这是合法分片，不是错误。
                var available = reader.Remaining();
                bunch.Partial = 1;
                bunch.Payload = available > 0 ? reader.Take(available) : Array.Empty<byte>();
                bunch.PayloadBitCount = available;
            }
            else
            {
                bunch.Payload = reader.Take(bunch.PayloadBitCount);
            }
            bunch.PayloadEndBit = reader.Position;
            return bunch;
        }
        catch (BitError exc)
        {
            // 保留失败前已解码的所有字段。旧代码用空壳替换对象，会掩盖
            // 畸形/截断捕获中最有用的证据。
            bunch.HeaderEndBit = Math.Max(bunch.HeaderEndBit, reader.Position);
            if (bunch.PayloadStartBit == 0) bunch.PayloadStartBit = reader.Position;
            bunch.PayloadEndBit = reader.Position;
            bunch.Error = exc.Message;
            bunch.ErrorBit = reader.Position;
            throw new BunchReadFailure(bunch, exc);
        }
    }

    /// <summary>
    /// 用恢复出的引擎顺序解码顺序排列的 bunches（Python: decode_engine_bunches()）。
    /// 在第一个畸形/截断头处停止，并保留精确的错误位与未消费区间——这对
    /// "packet-handler 组件在到达 ReceivedPacket 的位区间之外留了数据"的捕获很重要。
    /// </summary>
    public static EngineBunchParse DecodeEngineBunches(
        byte[] packetBody,
        int bodyBits,
        int maxPacketBytes = DefaultMaxPacketBytes,
        int? maxChannelIndex = DefaultMaxChannelIndex,
        int maxBunches = 256,
        IReadOnlyDictionary<string, int>? coordinateBases = null)
    {
        if (maxPacketBytes <= 0)
            throw new ArgumentException("max_packet_bytes must be positive");
        if (bodyBits < 0)
            throw new ArgumentException("body_bits must be non-negative");
        if (maxChannelIndex is not null && maxChannelIndex <= 0)
            throw new ArgumentException("max_channel_index must be positive or None");

        var reader = new BitReader(packetBody, bodyBits);
        var bunches = new List<EngineBunch>();
        string? error = null;
        int? errorBit = null;

        while (reader.Remaining() > 0 && bunches.Count < maxBunches)
        {
            var start = reader.Position;
            EngineBunch bunch;
            try
            {
                bunch = ReadOneBunch(reader, bunches.Count, maxPacketBytes, maxChannelIndex);
            }
            catch (BunchReadFailure exc)
            {
                error = exc.Message;
                var failed = exc.Bunch;
                failed.CoordinateBases = coordinateBases is null
                    ? new Dictionary<string, int>()
                    : new Dictionary<string, int>(coordinateBases);
                errorBit = failed.ErrorBit;
                bunches.Add(failed);
                reader.Position = start;
                break;
            }
            bunch.CoordinateBases = coordinateBases is null
                ? new Dictionary<string, int>()
                : new Dictionary<string, int>(coordinateBases);
            bunches.Add(bunch);
        }

        if (reader.Remaining() > 0 && bunches.Count >= maxBunches && error is null)
        {
            error = $"bunch limit {maxBunches} reached";
            errorBit = reader.Position;
        }

        var consumed = reader.Position;
        return new EngineBunchParse
        {
            Bunches = bunches,
            BodyBits = bodyBits,
            ConsumedBits = consumed,
            TrailingStartBit = consumed,
            Error = error,
            ErrorBit = errorBit,
        };
    }

    /// <summary>半开 body 位区间 [start, end) 的便捷切片（Python: body_slice()）。</summary>
    public static byte[] BodySlice(byte[] packetBody, int start, int end)
    {
        if (start < 0 || end < start)
            throw new ArgumentException($"invalid interval [{start}, {end})");
        return BitPrimitives.SliceBits(packetBody, start, end - start);
    }
}

/// <summary>FName 的线格式（推进 bunch 头游标所需，Python: SerializedFName）。</summary>
public sealed class SerializedFName
{
    public bool Hardcoded { get; set; }
    public int? HardcodedIndex { get; set; }
    public string? Text { get; set; }
    public int Number { get; set; }
    public int StartBit { get; set; }
    public int EndBit { get; set; }

    /// <summary>
    /// 转 JSON 友好字典，键名与 Python SerializedFName.as_dict() 完全一致：
    /// hardcoded / hardcoded_index / text / number / interval。
    /// </summary>
    public Dictionary<string, object?> AsDictionary() => new()
    {
        ["hardcoded"] = Hardcoded,
        ["hardcoded_index"] = HardcodedIndex,
        ["text"] = Text,
        ["number"] = Number,
        ["interval"] = new List<object?> { StartBit, EndBit },
    };
}

/// <summary>单个引擎 bunch 的完整解码结果（Python: EngineBunch）。</summary>
public sealed class EngineBunch
{
    public int Index { get; set; }
    public int HeaderStartBit { get; set; }
    public int HeaderEndBit { get; set; }
    public int PayloadStartBit { get; set; }
    public int PayloadEndBit { get; set; }
    public int ControlGate { get; set; }
    public int Open { get; set; }
    public int Close { get; set; }
    public int? CloseReason { get; set; }
    public int Dormant { get; set; }
    public int ReplicationPaused { get; set; }
    public int Reliable { get; set; }
    public int PreChannelFlag { get; set; }
    public int ChannelIndex { get; set; }
    public List<uint> ChannelPackedBytes { get; set; } = new();
    public int HasPackageMapExports { get; set; }
    public int HasMustBeMappedGuids { get; set; }
    public int Partial { get; set; }
    public int PartialInitial { get; set; } = 1;
    public int PartialFinal { get; set; } = 1;
    public int? ChannelSequenceMod { get; set; }
    public int? ReliableSequence { get; set; }
    public SerializedFName? ChannelName { get; set; }
    public int PayloadBitCount { get; set; }
    public byte[] Payload { get; set; } = Array.Empty<byte>();
    public string? Error { get; set; }
    public int? ErrorBit { get; set; }
    public Dictionary<string, int> CoordinateBases { get; set; } = new();

    /// <summary>
    /// 返回在每个已提供坐标系下的半开区间。
    /// header_*/payload_* 相对纯 bunch 流；调用方可附加 packet_body/raw 基。
    /// （Python: coordinate_intervals()）
    /// </summary>
    public Dictionary<string, Dictionary<string, List<int>>> CoordinateIntervals()
    {
        var bases = new Dictionary<string, int> { ["bunch_stream"] = 0 };
        foreach (var kv in CoordinateBases) bases[kv.Key] = kv.Value;
        var result = new Dictionary<string, Dictionary<string, List<int>>>();
        foreach (var (name, baseValue) in bases)
        {
            result[name] = new Dictionary<string, List<int>>
            {
                ["bunch_interval"] = [baseValue + HeaderStartBit, baseValue + PayloadEndBit],
                ["header_interval"] = [baseValue + HeaderStartBit, baseValue + HeaderEndBit],
                ["payload_interval"] = [baseValue + PayloadStartBit, baseValue + PayloadEndBit],
            };
        }
        return result;
    }
}

/// <summary>一包内所有 bunch 的解析结果（Python: EngineBunchParse）。</summary>
public sealed class EngineBunchParse
{
    public List<EngineBunch> Bunches { get; set; } = new();
    public int BodyBits { get; set; }
    public int ConsumedBits { get; set; }
    public int TrailingStartBit { get; set; }
    public string? Error { get; set; }
    public int? ErrorBit { get; set; }

    /// <summary>无错误且恰好消费完整个 body（Python: tiled 属性）。</summary>
    public bool Tiled => Error is null && ConsumedBits == BodyBits;
}

/// <summary>内部异常：携带部分解码的 bunch（Python: _BunchReadFailure）。</summary>
public sealed class BunchReadFailure : BitError
{
    public EngineBunch Bunch { get; }

    public BunchReadFailure(EngineBunch bunch, Exception inner)
        : base(bunch.Error ?? "malformed bunch", inner)
    {
        Bunch = bunch;
    }
}
