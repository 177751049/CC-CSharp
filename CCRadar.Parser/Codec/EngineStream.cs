namespace CCRadar.Parser.Codec;

/// <summary>
/// 有状态可靠序列跟踪与位紧凑的分片重组。
/// 1:1 移植自 Python: codec/engine_stream.py。
/// </summary>
public static class EngineStream
{
    /// <summary>
    /// 把每通道 10 位可靠序列展开为邻近整数（Python: ReliableSequenceUnwrapper）。
    /// </summary>
    public sealed class ReliableSequenceUnwrapper
    {
        private readonly int _modulus;
        // key = (streamKey, channelIndex) → 上一次绝对序列。
        private readonly Dictionary<(object? StreamKey, int Channel), int?> _last = new();

        public ReliableSequenceUnwrapper(int modulus = EngineBunchCodec.ReliableSequenceMax)
        {
            if (modulus <= 1) throw new ArgumentException("modulus must be greater than one");
            _modulus = modulus;
        }

        /// <summary>
        /// 展开通道内的模序列为绝对序列（Python: unwrap()）。
        /// 首见直接取模值；之后以"上次值的模基座 + 模值"为候选，向最近方向解绕。
        /// 仅当首见或绝对值前进时才更新记忆（回绕/乱序不回退基线）。
        /// </summary>
        public int Unwrap(int channelIndex, int sequenceMod, object? streamKey = null)
        {
            if (sequenceMod < 0 || sequenceMod >= _modulus)
                throw new ArgumentException($"sequence {sequenceMod} outside [0, {_modulus})");
            var key = (streamKey, channelIndex);
            _last.TryGetValue(key, out var last);
            int absolute;
            if (last is null)
            {
                absolute = sequenceMod;
            }
            else
            {
                var baseValue = last.Value - (last.Value % _modulus);
                absolute = baseValue + sequenceMod;
                var half = _modulus / 2;
                if (absolute - last.Value > half)
                    absolute -= _modulus;
                else if (last.Value - absolute >= half)
                    absolute += _modulus;
            }
            if (last is null || absolute > last.Value) _last[key] = absolute;
            return absolute;
        }

        /// <summary>
        /// 就地展开 bunch 的可靠序列（Python: apply()）。
        /// 非 reliable 或无模值 → ReliableSequence 置 null。
        /// </summary>
        public int? Apply(EngineBunch bunch, object? streamKey = null)
        {
            if (bunch.Reliable == 0 || bunch.ChannelSequenceMod is null)
            {
                bunch.ReliableSequence = null;
                return null;
            }
            bunch.ReliableSequence = Unwrap(bunch.ChannelIndex, bunch.ChannelSequenceMod.Value, streamKey);
            return bunch.ReliableSequence;
        }
    }

    /// <summary>重组完成的逻辑 bunch（Python: ReassembledBunch）。</summary>
    public sealed class ReassembledBunch
    {
        public int ChannelIndex { get; set; }
        public byte[] Payload { get; set; } = Array.Empty<byte>();
        public int PayloadBitCount { get; set; }
        public bool Reliable { get; set; }
        public bool WasPartial { get; set; }
        public List<Dictionary<string, object?>> Fragments { get; set; } = new();
        public int? FirstReliableSequence { get; set; }
        public int? LastReliableSequence { get; set; }
        public int Open { get; set; }
        public int Close { get; set; }
        public int? CloseReason { get; set; }
        public int Dormant { get; set; }

        /// <summary>
        /// 把合并载荷上的区间 [start, end) 映射回全部源分片（Python: map_payload_interval()）。
        /// </summary>
        public List<Dictionary<string, object?>> MapPayloadInterval(int start, int end)
        {
            if (start < 0 || end < start || end > PayloadBitCount)
                throw new ArgumentException($"invalid combined interval [{start}, {end})");
            var mapped = new List<Dictionary<string, object?>>();
            foreach (var fragment in Fragments)
            {
                // 区间统一存 List<object?>（int 装箱），与 Append 写入侧一致。
                var interval = (List<object?>?)fragment["combined_payload_interval"]
                    ?? throw new InvalidOperationException("fragment missing combined_payload_interval");
                var fstart = Convert.ToInt32(interval[0]);
                var fend = Convert.ToInt32(interval[1]);
                var left = Math.Max(start, fstart);
                var right = Math.Min(end, fend);
                if (left >= right) continue;
                var item = new Dictionary<string, object?>(fragment);
                item["overlap_combined_interval"] = new List<int> { left, right };
                item["fragment_local_interval"] = new List<int> { left - fstart, right - fstart };
                mapped.Add(item);
            }
            return mapped;
        }
    }

    // 进行中的分片组装状态（Python: _PartialAssembly）。
    private sealed class PartialAssembly
    {
        public int ChannelIndex { get; init; }
        public bool Reliable { get; init; }
        public BitWriter Writer { get; } = new();
        public List<Dictionary<string, object?>> Fragments { get; } = new();
        public int? FirstReliableSequence { get; set; }
        public int? LastReliableSequence { get; set; }
        public int? LastSequenceMod { get; set; }
        public int Open { get; set; }
        public int Close { get; set; }
        public int? CloseReason { get; set; }
        public int Dormant { get; set; }
    }

    /// <summary>
    /// 重组 UE 分片 bunch 链，无需字节对齐分片（Python: PartialBunchReassembler）。
    /// 规则：完整 bunch 直接完成；分片链以 partial_initial 开建，可靠序列必须连续，
    /// 可靠性变化/序列断裂 → 丢弃链并记录诊断；partial_final 完成。
    /// </summary>
    public sealed class PartialBunchReassembler
    {
        private readonly int _sequenceModulus;
        private readonly Dictionary<(object? StreamKey, int Channel), PartialAssembly> _pending = new();
        public List<Dictionary<string, object?>> Diagnostics { get; } = new();

        public PartialBunchReassembler(int sequenceModulus = EngineBunchCodec.ReliableSequenceMax)
        {
            _sequenceModulus = sequenceModulus;
        }

        private static Dictionary<string, object?> SourceDict(EngineBunch bunch, Dictionary<string, object?>? source)
        {
            var item = source is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?>(source);
            item["bunch_index"] = bunch.Index;
            item["channel_index"] = bunch.ChannelIndex;
            item["payload_bits"] = bunch.PayloadBitCount;
            item["channel_sequence_mod"] = bunch.ChannelSequenceMod;
            item["reliable_sequence"] = bunch.ReliableSequence;
            item["has_package_map_exports"] = bunch.HasPackageMapExports;
            item["has_must_be_mapped_guids"] = bunch.HasMustBeMappedGuids;
            item["coordinates"] = bunch.CoordinateIntervals();
            return item;
        }

        private static void Append(PartialAssembly assembly, EngineBunch bunch, Dictionary<string, object?>? source)
        {
            var start = assembly.Writer.BitCount;
            assembly.Writer.Append(bunch.Payload, bunch.PayloadBitCount);
            var end = assembly.Writer.BitCount;
            var item = SourceDict(bunch, source);
            item["combined_payload_interval"] = new List<object?> { start, end };
            assembly.Fragments.Add(item);
            assembly.FirstReliableSequence ??= bunch.ReliableSequence;
            assembly.LastReliableSequence = bunch.ReliableSequence;
            assembly.LastSequenceMod = bunch.ChannelSequenceMod;
            assembly.Close = bunch.Close;
            assembly.CloseReason = bunch.CloseReason;
            assembly.Dormant = bunch.Dormant;
        }

        private static ReassembledBunch Finish(PartialAssembly assembly, bool wasPartial) => new()
        {
            ChannelIndex = assembly.ChannelIndex,
            Payload = assembly.Writer.ToBytes(),
            PayloadBitCount = assembly.Writer.BitCount,
            Reliable = assembly.Reliable,
            WasPartial = wasPartial,
            Fragments = assembly.Fragments,
            FirstReliableSequence = assembly.FirstReliableSequence,
            LastReliableSequence = assembly.LastReliableSequence,
            Open = assembly.Open,
            Close = assembly.Close,
            CloseReason = assembly.CloseReason,
            Dormant = assembly.Dormant,
        };

        private void Drop((object? StreamKey, int Channel) key, string reason)
        {
            _pending.Remove(key, out var old);
            Diagnostics.Add(new Dictionary<string, object?>
            {
                ["kind"] = "partial_drop",
                ["channel_index"] = key.Channel,
                ["reason"] = reason,
                ["fragments"] = old?.Fragments.Count ?? 0,
                ["payload_bits"] = old?.Writer.BitCount ?? 0,
            });
        }

        /// <summary>
        /// 消费一个 bunch，返回零或一个完成的逻辑 bunch（Python: feed()）。
        /// </summary>
        public List<ReassembledBunch> Feed(EngineBunch bunch, Dictionary<string, object?>? source = null, object? streamKey = null)
        {
            var key = (streamKey, bunch.ChannelIndex);
            if (bunch.Partial == 0)
            {
                // 完整 bunch：若该通道有进行中的分片链，链被打断 → 丢弃。
                if (_pending.ContainsKey(key))
                    Drop(key, "complete bunch interrupted pending partial chain");
                var assembly = new PartialAssembly
                {
                    ChannelIndex = bunch.ChannelIndex,
                    Reliable = bunch.Reliable != 0,
                    Open = bunch.Open,
                };
                Append(assembly, bunch, source);
                return [Finish(assembly, wasPartial: false)];
            }

            PartialAssembly pending;
            if (bunch.PartialInitial != 0)
            {
                // 新的初始分片替换进行中的链。
                if (_pending.ContainsKey(key))
                    Drop(key, "new initial fragment replaced pending chain");
                pending = new PartialAssembly
                {
                    ChannelIndex = bunch.ChannelIndex,
                    Reliable = bunch.Reliable != 0,
                    Open = bunch.Open,
                };
                _pending[key] = pending;
            }
            else
            {
                // 非初始分片：必须有进行中的链，否则是孤儿分片 → 丢弃并记录。
                if (!_pending.TryGetValue(key, out var orphan))
                {
                    Diagnostics.Add(new Dictionary<string, object?>
                    {
                        ["kind"] = "orphan_partial",
                        ["channel_index"] = bunch.ChannelIndex,
                        ["partial_final"] = bunch.PartialFinal,
                        ["channel_sequence_mod"] = bunch.ChannelSequenceMod,
                    });
                    return [];
                }
                pending = orphan;
            }

            // 链内可靠性必须一致。
            if (bunch.Reliable != 0 != pending.Reliable)
            {
                Drop(key, "reliability changed within partial chain");
                return [];
            }
            // 可靠链要求序列连续（先比绝对序列，退化比模序列）。
            if (pending.Reliable && pending.Fragments.Count > 0)
            {
                if (bunch.ReliableSequence is not null && pending.LastReliableSequence is not null)
                {
                    var expected = pending.LastReliableSequence.Value + 1;
                    if (bunch.ReliableSequence != expected)
                    {
                        Drop(key, $"reliable sequence gap: expected {expected}, got {bunch.ReliableSequence}");
                        return [];
                    }
                }
                else if (bunch.ChannelSequenceMod is not null && pending.LastSequenceMod is not null)
                {
                    var expectedMod = (pending.LastSequenceMod.Value + 1) % _sequenceModulus;
                    if (bunch.ChannelSequenceMod != expectedMod)
                    {
                        Drop(key, $"reliable sequence gap: expected mod {expectedMod}, got {bunch.ChannelSequenceMod}");
                        return [];
                    }
                }
            }

            Append(pending, bunch, source);
            if (bunch.PartialFinal != 0)
            {
                _pending.Remove(key);
                return [Finish(pending, wasPartial: true)];
            }
            return [];
        }

        /// <summary>列出当前未完成的分片链（Python: pending()）。</summary>
        public List<Dictionary<string, object?>> Pending()
        {
            return _pending.Select(kv => new Dictionary<string, object?>
            {
                ["stream_key"] = kv.Key.StreamKey?.ToString(),
                ["channel_index"] = kv.Key.Channel,
                ["fragments"] = kv.Value.Fragments.Count,
                ["payload_bits"] = kv.Value.Writer.BitCount,
                ["last_reliable_sequence"] = kv.Value.LastReliableSequence,
            }).ToList();
        }
    }
}
