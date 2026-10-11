namespace CCRadar.Parser.Codec;

/// <summary>待解码的原始包输入（与流标识绑定，用于并行分区）。</summary>
public sealed record PacketInput(
    string FlowKey,
    int CaptureSequence,
    string Direction,
    byte[] Raw,
    bool RequestPrefix = true);

/// <summary>一个包的并行解码结果（保留流标识以便按流聚合）。</summary>
public sealed record PacketDecodeResult(
    string FlowKey,
    int CaptureSequence,
    EnginePacketTrace Trace);

/// <summary>
/// 分区保序并行的包解码器。
///
/// 并行模型（由协议状态机约束决定，详见可行性分析）：
///   • 不同流（FlowKey = 会话/五元组/epoch 组合）之间完全独立 → 可并行；
///   • 同一流内的包必须按序处理 —— 可靠序列 unwrap、分片重组、序号完整性
///     都是顺序状态机 → 分区内严格串行；
///   • 实现：按 FlowKey 的哈希把输入包分区（同一流恒落入同一分区），
///     用 Parallel.For 对分区并行，每个分区内部按输入顺序解码；
///   • 输出按「分区序 + 分区内序」排列 —— 同一输入永远得到同一输出顺序，
///     满足黄金对拍对确定性的要求。
///
/// 每个分区的解码互不共享任何可变状态（DecodeEnginePacket 为无状态纯函数，
/// 逐包新建 BitReader），因此并行无需加锁。分区结果写入预分配数组的专属槽位，
/// 无数据竞争。
/// </summary>
public static class ParallelPacketDecoder
{
    /// <summary>
    /// 并行解码一批原始包。
    /// </summary>
    /// <param name="packets">输入包（可来自任意流，顺序任意）。</param>
    /// <param name="maxDegreeOfParallelism">最大并行度；-1 = 使用全部核心。</param>
    /// <param name="maxPacketBytes">单 bunch 载荷上限（传递给解码器）。</param>
    /// <param name="maxChannelIndex">通道索引上限（传递给解码器）。</param>
    /// <param name="maxBunches">单包 bunch 数上限（传递给解码器）。</param>
    /// <returns>按「分区序 + 分区内序」确定性排列的解码结果。</returns>
    public static List<PacketDecodeResult> Decode(
        IReadOnlyList<PacketInput> packets,
        int maxDegreeOfParallelism = -1,
        int maxPacketBytes = EngineBunchCodec.DefaultMaxPacketBytes,
        int? maxChannelIndex = EngineBunchCodec.DefaultMaxChannelIndex,
        int maxBunches = 256)
    {
        if (packets.Count == 0) return [];

        // 1) 分区：同 FlowKey 恒定落入同一分区，保证流内保序。
        //    保持首次出现的顺序作为分区顺序 → 输出确定性。
        var flowKeys = new List<string>();
        var partitions = new Dictionary<string, List<PacketInput>>();
        foreach (var packet in packets)
        {
            if (!partitions.TryGetValue(packet.FlowKey, out var list))
            {
                list = [];
                partitions[packet.FlowKey] = list;
                flowKeys.Add(packet.FlowKey);
            }
            list.Add(packet);
        }

        // 2) 分区并行：每个分区串行解码，结果写入数组专属槽位（无竞争）。
        var results = new List<PacketDecodeResult>?[flowKeys.Count];
        var options = new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism };
        Parallel.For(0, flowKeys.Count, options, partitionIndex =>
        {
            var list = new List<PacketDecodeResult>(partitions[flowKeys[partitionIndex]].Count);
            foreach (var packet in partitions[flowKeys[partitionIndex]])
            {
                var trace = EnginePacketCodec.DecodeEnginePacket(
                    packet.CaptureSequence,
                    packet.Direction,
                    packet.Raw,
                    requestPrefix: packet.RequestPrefix,
                    maxPacketBytes: maxPacketBytes,
                    maxChannelIndex: maxChannelIndex,
                    maxBunches: maxBunches);
                list.Add(new PacketDecodeResult(packet.FlowKey, packet.CaptureSequence, trace));
            }
            results[partitionIndex] = list;
        });

        // 3) 汇总：按分区序拼接 → 输出顺序确定（对拍可比）。
        var output = new List<PacketDecodeResult>(packets.Count);
        foreach (var partitionResult in results)
        {
            if (partitionResult is not null) output.AddRange(partitionResult);
        }
        return output;
    }
}
