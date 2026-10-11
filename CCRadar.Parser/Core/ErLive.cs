using CCRadar.Parser.Codec;

namespace CCRadar.Parser.Core;

/// <summary>
/// er_live.py 的 C# 等价实现 —— 确定性网络层 name → (x,y,z) 函数库
/// （实时链路必需部分；离线 CLI（main/detect_battle_server/extract_players/
/// extract_char_links/extract_slots 的文件批量形态）在回放适配层提供）。
/// </summary>
public static class ErLive
{
    /// <summary>type 6 枚举上限（Python: ENUM_MAX）。</summary>
    private static readonly Dictionary<string, int> EnumMax = new()
    {
        ["RemoteRole"] = 4, ["Role"] = 4,
        ["ReplicatedMovementMode"] = 8, ["PlayerGender"] = 3,
    };

    /// <summary>er_live 的 UE 变长 SerializeInt（Python: ser_int）。</summary>
    public static long SerInt(BitReader r, int vmax)
    {
        long v = 0;
        long m = 1;
        while ((v + m) < vmax)
        {
            if (r.ReadBit() != 0) v |= m;
            m <<= 1;
        }
        return v;
    }

    /// <summary>er_live 的扁平值读取（Python: read_val；枚举感知）。</summary>
    public static object? ReadVal(BitReader r, int t, string? nm)
    {
        switch (t)
        {
            case 3:
            case 21:
                return r.ReadBit() != 0;
            case 4: return r.ReadFloat32();
            case 5: return (long)r.ReadInt32();
            case 6:
                return nm is not null && EnumMax.TryGetValue(nm, out var em)
                    ? SerInt(r, em)
                    : (object?)(long)r.ReadUInt8();
            case 7: return EngineBunchCodec.ReadSerializedFName(r).AsDictionary();
            case 8: return EngineBunchCodec.ReadDfmPackedInt(r).Value;
            case 9: return (long)r.ReadUInt32();
            case 19: return r.ReadFString();
            case 20: return r.ReadUInt64();
            case 14:
            {
                var (n, _) = EngineBunchCodec.ReadDfmPackedInt(r);
                var bytes = new byte[n];
                for (var i = 0; i < n; i++) bytes[i] = (byte)r.ReadUInt8();
                return Convert.ToHexString(bytes).ToLowerInvariant();
            }
            case 0:
            case 2:
                throw new BitError("complex");
            default:
                throw new BitError($"t{t}");
        }
    }

    /// <summary>
    /// 顺序 RepLayout 解码（Python: _decode_flat）：
    /// bDoChecksum(1) + handle(packed) + 值…，提取 want 中的 handle。
    /// </summary>
    public static Dictionary<long, object?> DecodeFlat(
        byte[] payload, int pstart, int pend,
        IReadOnlyList<RepCmd> cmds, IReadOnlyList<Dictionary<string, object?>> parents,
        IReadOnlySet<long> want, long? stopAfter = null)
    {
        var r = new BitReader(payload, pend) { Position = pstart };
        var output = new Dictionary<long, object?>();
        try
        {
            r.ReadBit();
        }
        catch (BitError)
        {
            return output;
        }
        var cur = 0;
        long h;
        try
        {
            h = EngineBunchCodec.ReadDfmPackedInt(r).Value;
        }
        catch (BitError)
        {
            return output;
        }
        while (true)
        {
            RepCmd? c = null;
            while (cur < cmds.Count)
            {
                cur++;
                var cc = cmds[cur - 1];
                if (cc.Type == 0)
                {
                    cur = (cc.EndCmd ?? (cur + 1)) - 1;
                    continue;
                }
                if (cc.Index == h) { c = cc; break; }
            }
            if (c is null) return output;
            var nm = c.ParentIndex < parents.Count
                ? parents[c.ParentIndex].TryGetValue("name", out var n) ? n?.ToString() : "?"
                : "?";
            object? v;
            try
            {
                v = ReadVal(r, c.Type, nm);
            }
            catch (BitError)
            {
                return output;
            }
            if (want.Contains(h)) output[h] = v;
            if (stopAfter is not null && h > stopAfter.Value) break;
            try
            {
                h = EngineBunchCodec.ReadDfmPackedInt(r).Value;
            }
            catch (BitError)
            {
                return output;
            }
            if (h == 0) break;
        }
        return output;
    }

    // ---- PlayerState 完整 RepLayout 解码 ----

    private static Dictionary<int, Dictionary<string, object?>?>? _psPropIndex;

    /// <summary>BP_DFMPlayerState_C 的 prop_meta {cmd_index: meta}，懒加载一次。</summary>
    private static Dictionary<int, Dictionary<string, object?>?> PsPropIndex()
    {
        if (_psPropIndex is null)
        {
            try
            {
                var entries = RepDec4.LoadPropmeta().TryGetValue("BP_DFMPlayerState_C", out var m)
                    ? m : new List<Dictionary<string, object?>>();
                _psPropIndex = entries
                    .Where(it => it.TryGetValue("index", out var i) && i is long)
                    .ToDictionary(it => (int)Convert.ToInt64(it["index"]), it => it);
            }
            catch
            {
                _psPropIndex = [];
            }
        }
        return _psPropIndex;
    }

    /// <summary>
    /// PlayerState 完整 RepLayout 解码（Python: decode_playerstate_full）——
    /// 枚举感知 + 动态数组 walk。只写顶层（depth==0）的 want 字段：数组内部
    /// handle 编号与顶层撞号（数组元素 handle 21 == 顶层 bIsABot 21）。
    /// 解到 stop_after 即停（之后是 type 2 复杂字段，无需消费）。
    /// </summary>
    public static Dictionary<long, object?> DecodePlayerstateFull(
        byte[] payload, int pstart, int pend,
        IReadOnlyList<RepCmd> cmds, IReadOnlyList<Dictionary<string, object?>> parents,
        IReadOnlySet<long> want, long stopAfter = 63)
    {
        var r = new BitReader(payload, pend) { Position = pstart };
        var pname = parents.Select(p => p.TryGetValue("name", out var n) ? n?.ToString() ?? "?" : "?").ToList();
        var output = new Dictionary<long, object?>();
        var pmi = PsPropIndex();
        RepDec4.St st;
        try
        {
            r.ReadBit();   // bDoChecksum
            st = new RepDec4.St(EngineBunchCodec.ReadDfmPackedInt(r).Value);
        }
        catch (BitError)
        {
            return output;
        }

        // 局部递归（Python 的嵌套 _props）：cur 递增 + 数组 handle 跨元素展平。
        void Props(int i0, int i1, long[] cur, int depth)
        {
            if (depth > 8) throw new BitError("depth");
            var i = i0;
            while (true)
            {
                if (cur[0] > stopAfter) return;
                cur[0]++;
                var c = cmds[i];
                if (st.H != cur[0])
                {
                    if (c.Type == 0) i = (c.EndCmd ?? (i + 2)) - 1;
                }
                else
                {
                    if (c.Type != 0)
                    {
                        var nm = c.ParentIndex < pname.Count ? pname[c.ParentIndex] : "?";
                        object? v;
                        if (c.Type == 2)
                        {
                            // 通用 Property（prop_meta 驱动）：中途加入者的初始 PS 含
                            // h48 WeaponAmmoCount(UInt16)，旧码 BitError('complex')
                            // 炸掉 → 排在其后的 TeamID/Camp 永远解不到。meta 缺失时
                            // Opaque 走优雅降级。
                            v = RepDec4.ReadVal(r, 2, pmi.TryGetValue(c.Index, out var m) ? m : null);
                        }
                        else
                        {
                            v = ReadVal(r, c.Type, nm);
                        }
                        if (depth == 0 && want.Contains(cur[0])) output[cur[0]] = v;
                        st.H = EngineBunchCodec.ReadDfmPackedInt(r).Value;
                    }
                    else
                    {
                        var n = r.ReadUInt16();
                        st.H = EngineBunchCodec.ReadDfmPackedInt(r).Value;
                        if (st.H != 0)
                        {
                            if (n > 4096) throw new BitError($"arraynum {n}");
                            var end = (c.EndCmd ?? (i + 2)) - 1;
                            var icur = new long[1];   // 共享：数组 handle 跨元素展平
                            for (var e = 0; e < n; e++)
                            {
                                Props(i + 1, end, icur, depth + 1);
                            }
                            if (st.H != 0) throw new BitError($"array unterminated h{st.H}");
                        }
                        i = (c.EndCmd ?? (i + 2)) - 1;
                        st.H = EngineBunchCodec.ReadDfmPackedInt(r).Value;
                    }
                }
                i++;
                if (i >= i1) return;
            }
        }

        try
        {
            Props(0, cmds.Count, [0], 0);
        }
        catch (BitError)
        {
            // 优雅降级（Python: except BitError: pass）。
        }
        return output;
    }

    /// <summary>
    /// Decode the unchanged Character prefix through h60 MoveHandle.Index
    /// （Python: decode_character_identity）。RemoteRole/Role 是 SerializeInt(4)；
    /// ReplicatedMovementMode 是 raw byte。未知原生结构停止 walk，只保留已证前缀。
    /// </summary>
    public static Dictionary<long, object?> DecodeCharacterIdentity(
        byte[] payload, int pstart, int pend,
        IReadOnlyList<RepCmd> cmds)
    {
        var r = new BitReader(payload, pend) { Position = pstart };
        var fields = new Dictionary<long, object?>();
        var last = 0L;
        try
        {
            if (r.ReadBit() != 0)
            {
                return fields;   // checked properties have not been verified
            }
            while (true)
            {
                var h = EngineBunchCodec.ReadDfmPackedInt(r).Value;
                if (h == 0 || h > 60) return fields;
                if (h <= last || h > cmds.Count)
                {
                    return [];   // malformed prefix cannot establish identity
                }
                var cmd = cmds[(int)h - 1];
                if (h == 60)
                {
                    // MoveHandle 校验：cmd 的 parent 名必须是 MoveHandle。
                    // （C# RepCmd 只有 ParentIndex——用 Handle==Index 语义，
                    // Python cmds[h-1]["parent_index"] 查 parents 名。此处 cmds
                    // 列表已按 index 排序，cmd.ParentIndex 交给调用方 parents。）
                    if (!IsMoveHandle(cmd)) return fields;
                    var value = (long)EngineBunchCodec.ReadDfmPackedInt(r).Value;
                    if (value > 0xFFFFFFFFL) return [];
                    if ((value & 0x80000000L) != 0) value -= 1L << 32;
                    fields[h] = value;
                }
                else if (h is 6 or 15)
                {
                    fields[h] = SerInt(r, 4);
                }
                else if (cmd.Type == 8)
                {
                    fields[h] = EngineBunchCodec.ReadDfmPackedInt(r).Value;
                }
                else
                {
                    fields[h] = RepDec4.ReadVal(r, cmd.Type);
                }
                if (h is 14 or 18 or 60) { /* fields 已按 h 存 */ }
                last = h;
            }
        }
        catch (RepOpaque)
        {
            return fields;
        }
        catch (BitError)
        {
            return [];   // truncated values never create a partial identity update
        }
    }

    /// <summary>MoveHandle 判定回调（由 LiveView 注入 parents 名表，避免本层依赖 catalog JSON 形态）。</summary>
    public static Func<RepCmd, bool>? MoveHandlePredicate;

    private static bool IsMoveHandle(RepCmd cmd) => MoveHandlePredicate?.Invoke(cmd) ?? false;

    /// <summary>合并角色身份字段（Python: merge_character_identity）。</summary>
    public static void MergeCharacterIdentity(
        Dictionary<string, object?> link, IReadOnlyDictionary<long, object?> fields, long seq)
    {
        foreach (var (h, key) in new[] { (14L, "owner"), (18L, "ps_guid"), (60L, "move_handle") })
        {
            if (!fields.TryGetValue(h, out var value)) continue;
            var seqKey = key + "_seq";
            if (seq >= Convert.ToInt64(link.TryGetValue(seqKey, out var oldSeq) ? oldSeq : -1))
            {
                link[key] = value;
                link[seqKey] = seq;
                if (h == 60)
                {
                    if (!link.TryGetValue("move_handle_history", out var histObj)
                        || histObj is not List<object?> history)
                    {
                        history = [];
                        link["move_handle_history"] = history;
                    }
                    var lastPair = history.Count > 0 ? history[^1] : null;
                    if (lastPair is not List<object?> lp
                        || lp.Count != 2 || Convert.ToInt64(lp[0]) != seq
                        || !Equals(lp[1], value))
                    {
                        history.Add((List<object?>)[seq, value]);
                    }
                }
            }
        }
    }

    /// <summary>
    /// 显式槽位认领（Python: explicit_slot_claims）——最新显式分配胜出槽位复用；
    /// -1 永不回退几何。
    /// </summary>
    public static Dictionary<int, ulong> ExplicitSlotClaims(
        IReadOnlyDictionary<ulong, Dictionary<string, object?>> links, long? atSeq = null)
    {
        var candidates = new Dictionary<int, ((long Stamp, long Spawn) Order, ulong? Actor)>();
        var current = new Dictionary<ulong, object?>();
        foreach (var (actor, link) in links)
        {
            var slot = link.TryGetValue("move_handle", out var mh) ? mh : null;
            var assignmentSeq = link.TryGetValue("move_handle_seq", out var mhs)
                ? Convert.ToInt64(mhs)
                : link.TryGetValue("spawn_seq", out var ss) ? Convert.ToInt64(ss) : -1;
            var history = link.TryGetValue("move_handle_history", out var hObj)
                && hObj is List<object?> hList
                ? hList.Select(p => p is List<object?> pair && pair.Count == 2
                    ? (Convert.ToInt64(pair[0]), pair[1])
                    : (assignmentSeq, slot)).ToList()
                : [(assignmentSeq, slot)];
            var points = history
                .Where(p => atSeq is null || p.Item1 <= atSeq.Value)
                .ToList();
            if (points.Count == 0) continue;
            current[actor] = points[^1].Item2;
            foreach (var (stamp, sl) in points)
            {
                if (sl is not long slotL || slotL < 0) continue;
                var slotVal = (int)slotL;
                var order = (stamp, link.TryGetValue("spawn_seq", out var s2) ? Convert.ToInt64(s2) : -1);
                if (!candidates.TryGetValue(slotVal, out var prev))
                {
                    candidates[slotVal] = (order, actor);
                }
                else if (order.CompareTo(prev.Order) > 0)
                {
                    candidates[slotVal] = (order, actor);
                }
                else if (order.CompareTo(prev.Order) == 0 && prev.Actor != actor)
                {
                    candidates[slotVal] = (order, null);
                }
            }
        }
        var result = new Dictionary<int, ulong>();
        foreach (var (slot, (_, actor)) in candidates)
        {
            if (actor is not null && current.TryGetValue(actor.Value, out var cur2)
                && cur2 is long cl && cl == slot)
            {
                result[slot] = actor.Value;
            }
        }
        return result;
    }

    /// <summary>观察过的移动槽集合（Python: observed_move_slots）。</summary>
    public static HashSet<int> ObservedMoveSlots(
        IReadOnlyDictionary<ulong, Dictionary<string, object?>> links)
    {
        var slots = new HashSet<int>();
        foreach (var link in links.Values)
        {
            var history = link.TryGetValue("move_handle_history", out var hObj)
                && hObj is List<object?> hList
                ? hList.Select(p => p is List<object?> pair && pair.Count == 2
                    ? (Convert.ToInt64(pair[0]), pair[1])
                    : (0L, link.TryGetValue("move_handle", out var mh) ? mh : null)).ToList()
                : [(0L, link.TryGetValue("move_handle", out var mh) ? mh : null)];
            foreach (var (_, slot) in history)
            {
                if (slot is long sl && sl >= 0) slots.Add((int)sl);
            }
        }
        return slots;
    }

    /// <summary>
    /// MRA 通道探测（Python: find_mra_channel）——handle=21 块数最多的通道。
    /// </summary>
    public static (int? Channel, int Count) FindMraChannel(IEnumerable<BunchView> bunches)
    {
        int? best = null;
        var bestN = 0;
        foreach (var group in bunches.GroupBy(b => b.Ch).OrderBy(g => g.Key))
        {
            var n = 0;
            foreach (var x in group)
            {
                var blocks = Blocks.Tile(x.Payload, x.PBits, 0);
                if (blocks is null) continue;
                foreach (var k in blocks)
                {
                    if (k.HasRep == 0 || k.Deleted || k.IsActor == 0) continue;
                    var r0 = new BitReader(x.Payload, k.PayloadEnd) { Position = k.PayloadStart };
                    try
                    {
                        if (r0.ReadBit() != 0) continue;
                        var h = EngineBunchCodec.ReadDfmPackedInt(r0).Value;
                        if (h == 21) n++;
                    }
                    catch (BitError)
                    {
                        // 块内位流异常跳过（Python: except BitError: pass）。
                    }
                }
            }
            if (n > bestN) { bestN = n; best = group.Key; }
        }
        return (best, bestN);
    }

    /// <summary>
    /// DFMCharacterFashionComponent 里 currentCharacterHeroID 的顶层 wire handle
    /// （Python: _fashion_hero_handle）。
    /// </summary>
    public static int? FashionHeroHandle(IReadOnlyList<RepCmd> cmds)
    {
        var inner = new HashSet<int>();
        foreach (var c in cmds)
        {
            if (c.Type == 0 && c.EndCmd is not null)
            {
                for (var i = c.Index + 1; i < c.EndCmd; i++) inner.Add(i);
            }
        }
        var h = 0;
        foreach (var c in cmds)
        {
            if (inner.Contains(c.Index)) continue;
            h++;
            if (FashionNamePredicate is not null && FashionNamePredicate(c))
            {
                return h;
            }
        }
        return null;
    }

    /// <summary>currentCharacterHeroID parent 名判定回调（由 LiveView 注入）。</summary>
    public static Func<RepCmd, bool>? FashionNamePredicate;

    /// <summary>
    /// fashion 组件块 payload → currentCharacterHeroID（Python: _walk_fashion_hero）。
    /// 88000000001..99 之外的值视为错位丢弃。
    /// </summary>
    public static long? WalkFashionHero(
        byte[] payload, int pstart, int pend,
        IReadOnlyList<RepCmd> cmds, Func<RepCmd, bool> isHeroCmd, long wantHandle)
    {
        var r = new BitReader(payload, pend) { Position = pstart };
        RepDec4.St st;
        try
        {
            r.ReadBit();
            st = new RepDec4.St(EngineBunchCodec.ReadDfmPackedInt(r).Value);
        }
        catch (BitError)
        {
            return null;
        }
        long? hid = null;
        var cur = new long[1];

        void Props(int i0, int i1, long[] cu, int d)
        {
            if (d > 4) throw new BitError("depth");
            var i = i0;
            while (true)
            {
                if (cu[0] > wantHandle) return;
                cu[0]++;
                var c = cmds[i];
                if (st.H != cu[0])
                {
                    if (c.Type == 0) i = (c.EndCmd ?? (i + 2)) - 1;
                }
                else
                {
                    if (c.Type != 0)
                    {
                        var v = ReadVal(r, c.Type, null);
                        var isHero = cu[0] == wantHandle && v is long vl
                                     && vl is >= 88000000001L and <= 88000000099L;
                        if (isHero) hid = (long)v!;
                        try
                        {
                            st.H = EngineBunchCodec.ReadDfmPackedInt(r).Value;
                        }
                        catch (BitError)
                        {
                            if (isHero) return;
                            throw;
                        }
                    }
                    else
                    {
                        var n = r.ReadUInt16();
                        st.H = EngineBunchCodec.ReadDfmPackedInt(r).Value;
                        if (st.H != 0)
                        {
                            var end = (c.EndCmd ?? (i + 2)) - 1;
                            var icu = new long[1];
                            for (var e = 0; e < n; e++)
                            {
                                Props(i + 1, end, icu, d + 1);
                            }
                            if (st.H != 0) throw new BitError("arr");
                        }
                        i = (c.EndCmd ?? (i + 2)) - 1;
                        st.H = EngineBunchCodec.ReadDfmPackedInt(r).Value;
                    }
                }
                i++;
                if (i >= i1) return;
            }
        }

        try
        {
            Props(0, cmds.Count, cur, 0);
        }
        catch (BitError)
        {
            return hid;
        }
        catch (Exception)
        {
            return hid;
        }
        return hid;
    }
}
