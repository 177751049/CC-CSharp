using System.Text;
using CCRadar.Parser.Codec;

namespace CCRadar.Parser.SelfTest;

/// <summary>
/// 解析内核自测程序：所有用例为自验证式（期望值由逻辑推导/往返不变式给出），
/// 任意一项失败即以非零码退出。
/// </summary>
public static class Program
{
    private static int _failures;
    private static int _passed;

    private static void Check(bool condition, string name)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"  [通过] {name}");
        }
        else
        {
            _failures++;
            Console.WriteLine($"  [失败] {name}");
        }
    }

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        // 子命令模式：probe-bunches <probe_out.json> —— Python 并排 exports 对拍。
        if (args.Length >= 2 && args[0] == "probe-bunches")
        {
            return BunchProbe.Run(args[1]);
        }
        // 子命令模式：serve-smoke <journal> <webRoot> <modelsRoot> <pak> —— 进程内引擎 HTTP 冒烟。
        if (args.Length >= 5 && args[0] == "serve-smoke")
        {
            return ServeSmoke.Run(args[1], args[2], args[3], args[4]);
        }
        // 子命令模式：asset-race <pak> —— AssetStore 并发读取一致性探针。
        if (args.Length >= 2 && args[0] == "asset-race")
        {
            return AssetRace.Run(args[1]);
        }
        // 子命令模式：tile-http <pak> <webRoot> <modelsRoot> —— HTTP 层全量瓦片校验。
        if (args.Length >= 4 && args[0] == "tile-http")
        {
            return AssetRace.RunHttp(args[1], args[2], args[3]);
        }
        // 子命令模式：state-bench <journal> [iters] —— State() 单次构建耗时基准。
        if (args.Length >= 2 && args[0] == "state-bench")
        {
            return StateBench.Run(args[1], args.Length >= 3 && int.TryParse(args[2], out var it) ? it : 300);
        }
        Console.WriteLine("=== CC雷达 解析内核自测 ===\n");

        TestBitWriterReaderRoundTrip();
        TestSliceBitsBitExactness();
        TestTerminationDataBits();
        TestPackedIntRoundTrip();
        TestSerializeIntBoundaries();
        TestFStringRoundTrip();
        TestSyntheticPacketEndToEnd();
        TestPartialReassembly();
        TestParallelConsistency();

        // 阶段 2：核心层 + 黄金对拍。
        CrossTests.RunAll();
        _passed += CrossTests.Passed;
        _failures += CrossTests.Failures;

        // 阶段 4：端到端黄金对拍（实测会话重放）。
        GoldenReplay.RunAll();
        _passed += GoldenReplay.Passed;
        _failures += GoldenReplay.Failures;

        Console.WriteLine($"\n=== 结果：通过 {_passed}，失败 {_failures} ===");
        return _failures == 0 ? 0 : 1;
    }

    // 用例 1：BitWriter 写 → BitReader 读，任意位宽往返不变。
    private static void TestBitWriterReaderRoundTrip()
    {
        Console.WriteLine("[1] BitWriter/BitReader 往返");
        var random = new Random(12345);
        var ok = true;
        for (var round = 0; round < 200; round++)
        {
            var writer = new BitWriter();
            var expected = new List<ulong>();
            var widths = new List<int>();
            for (var i = 0; i < 20; i++)
            {
                var width = random.Next(1, 65);
                // 统一用字节填充生成随机值（NextInt64 在 width=63/64 时因范围差值溢出抛错），
                // 再按位宽掩码。
                var bytes = new byte[8];
                random.NextBytes(bytes);
                var value = width == 64
                    ? BitConverter.ToUInt64(bytes)
                    : BitConverter.ToUInt64(bytes) & ((1ul << width) - 1);
                writer.WriteBits(value, width);
                expected.Add(value);
                widths.Add(width);
            }
            var reader = new BitReader(writer.ToBytes(), writer.BitCount);
            for (var i = 0; i < expected.Count; i++)
            {
                var actual = reader.ReadBits(widths[i]);
                if (actual != expected[i])
                {
                    ok = false;
                    Console.WriteLine($"    差异: 轮={round} 段={i} 宽度={widths[i]} 期望={expected[i]} 实际={actual} pos={reader.Position}");
                    break;
                }
            }
            if (reader.Position != writer.BitCount)
            {
                ok = false;
                Console.WriteLine($"    差异: 轮={round} 终止位 pos={reader.Position} 期望={writer.BitCount}");
            }
        }
        Check(ok, "200 轮随机位宽（1-64 位）写读往返");
    }

    // 用例 2：SliceBits 位精确性——结果位 i 必须等于源位 position+i（LSB-first）。
    private static void TestSliceBitsBitExactness()
    {
        Console.WriteLine("[2] SliceBits 位精确性（对齐/非对齐/越界）");
        var random = new Random(777);
        var ok = true;
        var data = new byte[64];
        random.NextBytes(data);
        for (var round = 0; round < 500; round++)
        {
            var position = random.Next(0, 400);
            var count = random.Next(1, 100);
            var sliced = BitPrimitives.SliceBits(data, position, count);
            // 逐位核对：sliced 位 i == data 位 position+i（越界源位为 0）。
            for (var i = 0; i < count; i++)
            {
                var sourceBit = position + i < data.Length * 8 ? BitPrimitives.GetBit(data, position + i) : 0;
                var slicedBit = BitPrimitives.GetBit(sliced, i);
                if (sourceBit != slicedBit) { ok = false; break; }
            }
            // 输出字节数必须是 ceil(count/8)。
            if (sliced.Length != (count + 7) >> 3) ok = false;
        }
        Check(ok, "500 组随机 (position, count) 逐位核对");
    }

    // 用例 3：termination_data_bits = 最后置位位索引。
    private static void TestTerminationDataBits()
    {
        Console.WriteLine("[3] TerminationDataBits");
        Check(BitPrimitives.TerminationDataBits(new byte[] { 0x00 }) == 0, "全零 → 0");
        Check(BitPrimitives.TerminationDataBits(new byte[] { 0x01 }) == 0, "0x01 → 位 0");
        Check(BitPrimitives.TerminationDataBits(new byte[] { 0x80 }) == 7, "0x80 → 位 7");
        Check(BitPrimitives.TerminationDataBits(new byte[] { 0x00, 0x02 }) == 9, "第二字节 0x02 → 位 9");
    }

    // 用例 4：PackedInt dfm/ue4 两种变体写读往返 + 跨度边界。
    private static void TestPackedIntRoundTrip()
    {
        Console.WriteLine("[4] PackedInt 往返（dfm/ue4）");
        var ok = true;
        foreach (var variant in new[] { "dfm", "ue4" })
        {
            foreach (var value in new uint[] { 0, 1, 63, 64, 127, 128, 16383, 16384, 0x1FFFFFFF, 0x20000000, uint.MaxValue / 2 })
            {
                var writer = new BitWriter();
                writer.WritePackedInt(value, variant);
                var reader = new BitReader(writer.ToBytes(), writer.BitCount);
                var read = reader.ReadPackedInt(variant);
                if (read != value) { ok = false; Console.WriteLine($"    差异: variant={variant} value={value} read={read}"); }
            }
        }
        Check(ok, "dfm/ue4 变体 × 11 个边界值往返");
    }

    // 用例 5：SerializeInt——2 的幂（定宽快路径）与非 2 的幂（逐位循环）都必须一致。
    private static void TestSerializeIntBoundaries()
    {
        Console.WriteLine("[5] SerializeInt 边界（2 的幂/非 2 的幂）");
        var ok = true;
        foreach (var maximum in new[] { 1, 2, 3, 4, 15, 16, 17, 1024, 1025, 32767 })
        {
            foreach (var value in Enumerable.Range(0, maximum))
            {
                var writer = new BitWriter();
                writer.WriteSerializeInt(value, maximum);
                var reader = new BitReader(writer.ToBytes(), writer.BitCount);
                var read = reader.ReadSerializeInt(maximum);
                // 写读消耗的位数必须一致（往返自洽）。
                if (read != value || reader.Position != writer.BitCount)
                {
                    ok = false;
                    Console.WriteLine($"    差异: max={maximum} value={value} read={read}");
                }
            }
        }
        Check(ok, "11 个 maximum × 全值域往返");
    }

    // 用例 6：FString UTF-8 / UTF-16 回退往返。
    private static void TestFStringRoundTrip()
    {
        Console.WriteLine("[6] FString 往返（UTF-8 / UTF-16 回退）");
        var ok = true;
        foreach (var text in new[] { "hello", "", "/Game/Maps/Delta", "中文测试" })
        {
            var writer = new BitWriter();
            writer.WriteFString(text);
            var reader = new BitReader(writer.ToBytes(), writer.BitCount);
            var read = reader.ReadFString();
            if (read != text)
            {
                ok = false;
                Console.WriteLine($"    差异: 期望=[{text}] 实际=[{read}] 位数={writer.BitCount}");
            }
        }
        Check(ok, "4 个字符串（含中文/空串）往返");
    }

    // 用例 7：端到端——手工构造"bunch 流 → PacketHeader → 外层 UE 帧"合成包，
    // 经 DecodeEnginePacket 还原，字段与构造值一致（双终止位嵌套全覆盖）。
    private static void TestSyntheticPacketEndToEnd()
    {
        Console.WriteLine("[7] 合成包端到端解码（双终止位嵌套）");

        // 第一层：构造纯 bunch 流——一个 reliable、channel=5、payload=64 字节的 bunch。
        var bunchStream = new BitWriter();
        bunchStream.WriteBit(0);                                    // control_gate = 0
        bunchStream.WriteBit(0);                                    // replication_paused
        bunchStream.WriteBit(1);                                    // reliable = 1
        bunchStream.WriteBit(0);                                    // pre_channel_flag
        bunchStream.WritePackedInt(5, "dfm");                       // channel = 5
        bunchStream.WriteBit(0);                                    // has_package_map_exports
        bunchStream.WriteBit(0);                                    // has_must_be_mapped_guids
        bunchStream.WriteBit(0);                                    // partial = 0
        bunchStream.WriteSerializeInt(123, EngineBunchCodec.ReliableSequenceMax);   // channel_sequence_mod
        // 引擎规则：open 或 reliable 的 bunch 必须带 FName（Python: if b.open or b.reliable）。
        bunchStream.WriteBit(1);                                    // FName hardcoded = 1
        bunchStream.WritePackedInt(10, "dfm");                      // 硬编码索引 10
        bunchStream.WriteSerializeInt(64 * 8, 1024 * 8);            // payload_bits
        var payload = new byte[64];
        new Random(42).NextBytes(payload);
        bunchStream.Append(payload, payload.Length * 8);
        var bunchStreamBytes = bunchStream.ToBytes();
        var bunchStreamBits = bunchStream.BitCount;

        // 第二层：PacketHeader（下行响应方向）+ bunch 流 + 内层终止位。
        var header = new BitWriter();
        header.WriteBit(1);                                         // frame_time_flag = 1（响应方向）
        header.WriteUInt8(30);                                      // server_frame_time_byte
        header.WriteUInt8(120);                                     // remote_kbytes_per_second_byte
        var innerBody = new BitWriter();
        innerBody.Append(header.ToBytes(), header.BitCount);
        innerBody.Append(bunchStreamBytes, bunchStreamBits);
        var innerTerminated = innerBody.TerminatedBytes();          // 内层终止位

        // 第三层：外层 FNetPacketNotify + 内层 body + 外层终止位。
        var outer = new BitWriter();
        outer.WriteBit(0);                                          // handler = 0（数据）
        outer.WriteBits(0x0A, 4);                                   // history_count-1 = 10 → 11 个历史字
        outer.WriteBits(777, 14);                                   // acked_seq
        outer.WriteBits(888, 14);                                   // packet_seq
        for (var i = 0; i < 11; i++) outer.WriteUInt32((uint)(0x1000 + i));   // ack 历史字
        outer.Append(innerTerminated, innerTerminated.Length * 8);
        var raw = outer.TerminatedBytes();                          // 外层终止位

        // 解码并核对。
        var trace = EnginePacketCodec.DecodeEnginePacket(1, PacketLayer.Response, raw);
        Check(trace.Error is null, $"无错误（实际: {trace.Error ?? "null"}）");
        Check(trace.Packet.PacketSequence == 888, $"packet_seq=888（实际 {trace.Packet.PacketSequence}）");
        Check(trace.Packet.AckedSequence == 777, $"acked_seq=777（实际 {trace.Packet.AckedSequence}）");
        Check(trace.Packet.HistoryWords.Count == 11, $"历史字 11 个（实际 {trace.Packet.HistoryWords.Count}）");
        Check(trace.PacketHeader?.ServerFrameTimeByte == 30, $"server_frame_time=30（实际 {trace.PacketHeader?.ServerFrameTimeByte}）");
        Check(trace.PacketHeader?.RemoteKbytesPerSecondByte == 120, $"remote_kbytes=120（实际 {trace.PacketHeader?.RemoteKbytesPerSecondByte}）");
        var parse = trace.BunchParse;
        Check(parse is { Tiled: true, Bunches.Count: 1 }, $"恰好 1 个 bunch 且 tiled（实际 {parse?.Bunches.Count}, tiled={parse?.Tiled}）");
        var bunch = parse?.Bunches.FirstOrDefault();
        Check(bunch is { Reliable: 1, ChannelIndex: 5 }, $"channel=5 reliable（实际 {bunch?.ChannelIndex}/{bunch?.Reliable}）");
        Check(bunch?.ChannelSequenceMod == 123, $"channel_sequence_mod=123（实际 {bunch?.ChannelSequenceMod}）");
        Check(bunch is { PayloadBitCount: 512 } && bunch.Payload.AsSpan().SequenceEqual(payload), "payload 512 位逐字节一致");
    }

    // 用例 8：分片 bunch 跨包重组——initial+middle+final 三片位级拼接。
    private static void TestPartialReassembly()
    {
        Console.WriteLine("[8] 分片 bunch 跨包重组（3 片位级连续）");

        // 构造一个 100 字节的目标载荷，切成 3 片（非字节对齐的位切分）。
        var payload = new byte[100];
        new Random(7).NextBytes(payload);
        // 切点故意取非 8 倍数：24 bits / 418 bits / 剩余。
        var cut1 = 24;
        var cut2 = 442;

        var reassembler = new EngineStream.PartialBunchReassembler();
        var unwrapper = new EngineStream.ReliableSequenceUnwrapper();
        var allBits = new List<byte[]>();
        // 可靠链要求通道序列模值连续（+1 mod 1024）——三片分别用 100/101/102。
        int[] sequenceMods = [100, 101, 102];
        int[] cuts = [0, cut1, cut2, payload.Length * 8];
        for (var i = 0; i < 3; i++)
        {
            var start = cuts[i];
            var count = cuts[i + 1] - cuts[i];
            var piece = BitPrimitives.SliceBits(payload, start, count);

            var stream = new BitWriter();
            stream.WriteBit(0);                                     // control_gate
            stream.WriteBit(0);                                     // replication_paused
            stream.WriteBit(1);                                     // reliable = 1
            stream.WriteBit(0);                                     // pre_channel_flag
            stream.WritePackedInt(9, "dfm");                        // channel = 9
            stream.WriteBit(0);                                     // has_package_map_exports
            stream.WriteBit(0);                                     // has_must_be_mapped_guids
            stream.WriteBit(1);                                     // partial = 1
            // 引擎头顺序（LSB-first）：if reliable → seq_mod；if partial → initial/final；
            // if open|reliable → FName；payload_bits；payload。
            stream.WriteSerializeInt(sequenceMods[i], EngineBunchCodec.ReliableSequenceMax);  // channel_sequence_mod
            stream.WriteBit(i == 0 ? 1 : 0);                        // partial_initial
            stream.WriteBit(i == 2 ? 1 : 0);                        // partial_final
            stream.WriteBit(1);                                     // FName hardcoded = 1
            stream.WritePackedInt(10, "dfm");                       // 硬编码索引 10
            stream.WriteSerializeInt(count, 1024 * 8);              // payload_bits（真实位长，非字节对齐）
            stream.Append(piece, count);

            // 解码该片（作为独立包的 bunch 流）。
            var parse = EngineBunchCodec.DecodeEngineBunches(stream.ToBytes(), stream.BitCount);
            var bunch = parse.Bunches.Single();
            unwrapper.Apply(bunch, "test-stream");
            if (bunch.Error is not null) Console.WriteLine($"    片 {i} 解析错误: {bunch.Error} @ bit {bunch.ErrorBit}");
            // 喂给重组器。
            var done = reassembler.Feed(bunch, source: null, streamKey: "test-stream");
            if (done.Count == 0)
            {
                var diag = reassembler.Diagnostics.LastOrDefault();
                Console.WriteLine($"    片 {i} 未完成 bunch，诊断: {(diag is null ? "无" : string.Join(",", diag.Select(kv => $"{kv.Key}={kv.Value}")))}");
            }
            if (i < 2) Check(done.Count == 0, $"第 {i + 1} 片后不产生完成 bunch");
            if (i == 2)
            {
                Check(done.Count == 1, $"第 3 片（final）后完成 1 个逻辑 bunch（实际 {done.Count}）");
                var assembled = done.Single();
                Check(assembled.ChannelIndex == 9, "通道 = 9");
                Check(assembled.WasPartial, "标记为分片重组");
                Check(assembled.PayloadBitCount == payload.Length * 8,
                    $"合并位长 = {payload.Length * 8}（实际 {assembled.PayloadBitCount}）");
                var firstDiff = -1;
                for (var d = 0; d < payload.Length; d++)
                {
                    if (assembled.Payload[d] != payload[d]) { firstDiff = d; break; }
                }
                Check(firstDiff < 0,
                    firstDiff < 0
                        ? "合并载荷逐字节一致"
                        : $"合并载荷差异 @ 字节 {firstDiff}: 期望 0x{payload[firstDiff]:X2} 实际 0x{assembled.Payload[firstDiff]:X2}");
            }
            allBits.Add(piece);
        }
        Check(reassembler.Pending().Count == 0, "无残留未完成链");
    }

    // 用例 9：并行解码与串行解码结果完全一致（多线程正确性）。
    private static void TestParallelConsistency()
    {
        Console.WriteLine("[9] 并行 vs 串行一致性（8 流 × 250 包 = 2000 包）");

        // 构造 8 条流，每条 250 个合成包（复用用例 7 的构造逻辑，payload 随机）。
        var packets = new List<PacketInput>();
        var random = new Random(2024);
        for (var flow = 0; flow < 8; flow++)
        {
            for (var seq = 0; seq < 250; seq++)
            {
                packets.Add(new PacketInput(
                    $"flow-{flow}",
                    seq,
                    PacketLayer.Response,
                    BuildSyntheticResponse((uint)seq, (byte)(seq & 0xFF), random)));
            }
        }

        // 串行：逐包顺序解码。
        var serial = packets.Select(p => new PacketDecodeResult(
            p.FlowKey, p.CaptureSequence,
            EnginePacketCodec.DecodeEnginePacket(p.CaptureSequence, p.Direction, p.Raw))).ToList();

        // 并行：分区保序并行解码（限定 4 线程放大调度交错概率）。
        var parallel = ParallelPacketDecoder.Decode(packets, maxDegreeOfParallelism: 4);

        // 一致性：数量、顺序（分区序+序内序——串行按输入顺序，并行按分区序，
        // 因此对拍时比较"同流同序列号"的键集合与内容，而不是原始列表顺序）。
        Check(serial.Count == parallel.Count, $"结果数量一致（{serial.Count} vs {parallel.Count}）");

        var serialMap = serial.ToDictionary(r => (r.FlowKey, r.CaptureSequence), r => Summarize(r.Trace));
        var parallelMap = parallel.ToDictionary(r => (r.FlowKey, r.CaptureSequence), r => Summarize(r.Trace));
        var same = serialMap.Count == parallelMap.Count &&
                   serialMap.All(kv => parallelMap.TryGetValue(kv.Key, out var value) && value == kv.Value);
        Check(same, "全部 2000 个包的解码摘要逐字段一致");

        // 分区内保序验证：任一流内的 CaptureSequence 必须按输入顺序出现。
        var ordered = true;
        foreach (var group in parallel.GroupBy(r => r.FlowKey))
        {
            var sequences = group.Select(r => r.CaptureSequence).ToList();
            if (!sequences.SequenceEqual(sequences.OrderBy(s => s).ToList())) ordered = false;
        }
        Check(ordered, "流内保序（分区并行不乱序）");
    }

    // 构造一帧合成响应包（用例 7 的参数化版本）。
    private static byte[] BuildSyntheticResponse(uint packetSeq, byte frameTime, Random random)
    {
        var payload = new byte[random.Next(16, 96)];
        random.NextBytes(payload);

        var bunchStream = new BitWriter();
        bunchStream.WriteBit(0);
        bunchStream.WriteBit(0);
        bunchStream.WriteBit(1);
        bunchStream.WriteBit(0);
        bunchStream.WritePackedInt((uint)random.Next(0, 30), "dfm");
        bunchStream.WriteBit(0);
        bunchStream.WriteBit(0);
        bunchStream.WriteBit(0);
        bunchStream.WriteSerializeInt(random.Next(0, EngineBunchCodec.ReliableSequenceMax), EngineBunchCodec.ReliableSequenceMax);
        bunchStream.WriteSerializeInt(payload.Length * 8, 1024 * 8);
        bunchStream.Append(payload, payload.Length * 8);

        var header = new BitWriter();
        header.WriteBit(1);
        header.WriteUInt8(frameTime);
        header.WriteUInt8(120);
        var inner = new BitWriter();
        inner.Append(header.ToBytes(), header.BitCount);
        inner.Append(bunchStream.ToBytes(), bunchStream.BitCount);
        var innerTerminated = inner.TerminatedBytes();

        var outer = new BitWriter();
        outer.WriteBit(0);
        outer.WriteBits(0, 4);              // history_count-1 = 0 → 1 个历史字
        outer.WriteBits(packetSeq % 16384, 14);
        outer.WriteBits(packetSeq % 16384, 14);
        outer.WriteUInt32(0xDEADBEEF);
        outer.Append(innerTerminated, innerTerminated.Length * 8);
        return outer.TerminatedBytes();
    }

    // 解码结果的确定性摘要（并行一致性比较用）。
    private static string Summarize(EnginePacketTrace trace) =>
        $"{trace.Error}|{trace.Packet.PacketKind}|{trace.Packet.PacketSequence}|{trace.Packet.AckedSequence}|" +
        $"{trace.PacketHeader?.FrameTimeFlag}|{trace.PacketHeader?.ServerFrameTimeByte}|" +
        $"{trace.BunchParse?.ConsumedBits}|{trace.BunchParse?.Bunches.Count}|" +
        string.Join(";", (trace.BunchParse?.Bunches ?? []).Select(b =>
            $"{b.ChannelIndex}:{b.PayloadBitCount}:{b.Reliable}:{b.ChannelSequenceMod}:" +
            Convert.ToHexString(b.Payload)));
}
