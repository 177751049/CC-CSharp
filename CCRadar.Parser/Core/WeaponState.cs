using CCRadar.Parser.Codec;

namespace CCRadar.Parser.Core;

/// <summary>
/// weapon_state.py 的 C# 等价实现 —— 当前武器状态：RepLayout 数组 + 帧化
/// FastArray 字段 5/17。
/// </summary>
public static class WeaponState
{
    /// <summary>武器值读取器（Python: weapon_value）：EWeaponSwitchType max=9 → 4 位。</summary>
    public static object? WeaponValue(BitReader r, int t, Dictionary<string, object?>? meta)
    {
        if (meta is not null && meta.TryGetValue("weapon_switch", out var ws) && ws is not null
            && (!(ws is bool b) || b))
        {
            return (long)r.ReadBits(4);
        }
        return RepDec4.ReadVal(r, t, meta);
    }

    /// <summary>武器槽位信息项（Python: _info）。</summary>
    internal static Dictionary<string, object?> Info(BitReader r) => new()
    {
        ["reserve"] = r.ReadBit() != 0,
        ["switch"] = (long)r.ReadBits(4),
        ["identity"] = r.ReadUInt32(),
        ["class_path"] = RepDec4.FastWeaponBp(r),
        ["frame"] = r.ReadUInt32(),
        ["slot"] = r.ReadUInt8(),
    };

    /// <summary>
    /// UE FastArraySerializer 帧化批量（Python: fast_array）。
    /// 输出 {key, base, mode, deleted:[rid], items:[(rid, item)]}。
    /// </summary>
    public static Dictionary<string, object?> FastArray(
        byte[] data, int start, int end, Func<BitReader, Dictionary<string, object?>> itemReader)
    {
        var r = new BitReader(data, end) { Position = start };
        var mode = r.ReadBit();
        var key = r.ReadInt32();
        var bas = r.ReadInt32();
        var nd = r.ReadInt32();
        var nc = r.ReadInt32();
        if (nd is < 0 or > 4096 || nc is < 0 or > 4096) throw new BitError("FastArray counts");
        var deleted = new List<long>();
        for (var i = 0; i < nd; i++) deleted.Add(r.ReadInt32());
        var items = new List<object?>();
        for (var i = 0; i < nc; i++)
        {
            var rid = r.ReadInt32();
            items.Add(new List<object?> { rid, itemReader(r) });
        }
        if (r.Position != end) throw new BitError("FastArray trailing bits");
        return new Dictionary<string, object?>
        {
            ["key"] = key, ["base"] = bas, ["mode"] = (long)mode,
            ["deleted"] = deleted, ["items"] = items,
        };
    }

    /// <summary>当前武器快照（Python: current 的返回 dict）。</summary>
    public static Dictionary<string, object?> Current(WeaponTracker tracker, ulong actor, long? atSeq = null)
        => tracker.Current(actor, atSeq);

    /// <summary>命名字段投影（Python: weapon_fields）。</summary>
    public static Dictionary<string, object?> WeaponFields(
        WeaponTracker tracker, ulong actor, IReadOnlyDictionary<long, string> names, long? atSeq = null)
    {
        var d = tracker.Current(actor, atSeq);
        string? name = null;
        if (d.TryGetValue("known", out var knownObj) && knownObj is true)
        {
            var wid = d.TryGetValue("weapon_id", out var widObj) ? widObj : null;
            var status = d.TryGetValue("status", out var stObj) ? stObj?.ToString() : null;
            name = status == "empty"
                ? "空手"
                : (wid is long l && names.TryGetValue(l, out var n)
                    ? n
                    : wid is long wl ? $"武器 {wl}" : "未知武器");
        }
        return new Dictionary<string, object?>
        {
            ["curr_weapon"] = name,
            ["curr_weapon_known"] = d.TryGetValue("known", out var k2) && k2 is true,
            ["curr_weapon_id"] = d.TryGetValue("weapon_id", out var w2) ? w2 : null,
            ["curr_weapon_status"] = d.TryGetValue("status", out var s2) ? s2 : null,
            ["curr_weapon_seq"] = d.TryGetValue("seq", out var sq2) ? sq2 : null,
        };
    }
}

/// <summary>武器状态跟踪器（Python: WeaponTracker）。</summary>
public sealed class WeaponTracker
{
    private readonly RepCatalog _catalog;
    private readonly Dictionary<int, Dictionary<string, object?>?> _meta;
    private readonly Dictionary<ulong, Dictionary<string, object?>> _actors = new();
    private readonly bool _keepHistory;
    private readonly Dictionary<ulong, List<(long Seq, Dictionary<string, object?> Snapshot)>> _history = new();
    private readonly Dictionary<ulong, string> _paths = new();
    private readonly Dictionary<int, ulong> _channels = new();

    /// <summary>计数器（Python: Counter）。</summary>
    public Dictionary<string, int> Stats { get; } = new();

    public WeaponTracker(bool keepHistory = false)
    {
        _catalog = RepDec4.LoadCatalogs()["WeaponReplicateComponent"];
        _meta = new Dictionary<int, Dictionary<string, object?>?>(RepDec6.ClsMeta(_catalog.ClassName));
        foreach (var index in new[] { 26, 34 })
        {
            _meta[index] = new Dictionary<string, object?>(
                _meta.TryGetValue(index, out var m) ? m ?? [] : [])
            {
                ["weapon_switch"] = true,
            };
        }
        _keepHistory = keepHistory;
    }

    private void Bump(string key) => Stats[key] = Stats.TryGetValue(key, out var c) ? c + 1 : 1;

    /// <summary>消费一个 bunch（Python: consume）。</summary>
    public void Consume(BunchView x)
    {
        try
        {
            var (exports, start) = ReplicationHeader.ExportsPrefix(
                x.Payload, x.PBits, x.Hx, x.Hm, x.Frags);
            foreach (var e in exports)
            {
                if (e.TryGetValue("path", out var pathObj) && pathObj is string path
                    && e.TryGetValue("guid", out var guidObj) && guidObj is ulong guid)
                {
                    _paths[guid] = path;
                }
            }
            if (x.Open)
            {
                _channels.Remove(x.Ch);
                if (x.Close) return;
                var (info, newStart) = Sna.ParsePrefix(
                    x.Payload, x.PBits, x.Hx, x.Hm,
                    quantizeBit: true, frags: x.Frags);
                var arch = info.TryGetValue("arch", out var archObj) ? archObj : null;
                var name = arch is ulong g && _paths.TryGetValue(g, out var p) ? p : "";
                if (!name.Contains("Character", StringComparison.Ordinal)
                    || name.Contains("Weapon", StringComparison.Ordinal))
                {
                    return;
                }
                newStart += 8;   // Character custom spawn byte after NewActor
                start = newStart;
                _channels[x.Ch] = (ulong)info["actor"]!;
            }
            var actor = _channels.TryGetValue(x.Ch, out var ch) ? ch : (ulong?)null;
            if (actor is null) return;
            var blocks = Blocks.Tile(x.Payload, x.PBits, start);
            if (blocks is null) return;
            foreach (var block in blocks)
            {
                if (block.Deleted || block.HasRep == 0) continue;
                var guid = block.Stably != 0 ? block.Sub : block.Cls;
                var gp = guid is ulong g2 && _paths.TryGetValue(g2, out var p2) ? p2 : "";
                if (gp.Contains("WeaponReplicate", StringComparison.Ordinal))
                {
                    ApplyBlock(actor.Value, x.Seq, x.Payload, block);
                }
            }
        }
        catch (BitError)
        {
            Bump("framing_error");
        }
        catch (SnaBad)
        {
            Bump("framing_error");
        }
        finally
        {
            if (x.Close) _channels.Remove(x.Ch);
        }
    }

    /// <summary>应用一个属性块（Python: apply_block）。返回是否有变化。</summary>
    public bool ApplyBlock(ulong actor, long seq, byte[] payload, ContentBlock block)
    {
        var (ev, pos, status) = RepDec6.DecodeBlock(
            payload, block.PayloadStart, block.PayloadEnd, _catalog, _meta,
            includeArrays: true, valueReader: WeaponState.WeaponValue);
        Bump("layout_" + status);
        if (!_actors.TryGetValue(actor, out var state))
        {
            state = new Dictionary<string, object?>
            {
                ["current"] = new List<object?>(),
                ["extra"] = new Dictionary<long, Dictionary<string, object?>>(),
                ["info"] = new Dictionary<long, Dictionary<string, object?>>(),
            };
            _actors[actor] = state;
        }
        if (state.TryGetValue("seq", out var seqObj) && seqObj is long oldSeq && seq < oldSeq)
        {
            Bump("stale_block");
            return false;
        }
        // 完整数组作用域可能先于一个不支持的原生属性出现。永不应用不完整
        // 数组或截断的 resize（Python 原注释）。
        var fields = new Dictionary<int, string>
        {
            [25] = "reserve", [26] = "switch", [27] = "identity",
            [28] = "class_path", [29] = "frame", [30] = "slot",
        };
        List<object?>? pending = null;
        var changed = false;
        foreach (var e in ev)
        {
            if (e.CmdIndex == 24 && e.Path.Count == 0)
            {
                if (e.Kind == "array_begin")
                {
                    var n = Convert.ToInt32(e.Value);
                    var current = (List<object?>)state["current"]!;
                    pending = current.Take(n)
                        .Select(item => (object?)CloneEntry((Dictionary<string, object?>)item!))
                        .ToList();
                    while (pending.Count < n)
                    {
                        pending.Add(new Dictionary<string, object?>
                        {
                            ["identity"] = 0L, ["slot"] = 0L, ["frame"] = 0L,
                        });
                    }
                }
                else if (e.Kind == "array_end" && pending is not null)
                {
                    state["current"] = pending;
                    state["current_seq"] = seq;
                    changed = true;
                    pending = null;
                }
            }
            else if (pending is not null && fields.TryGetValue(e.CmdIndex, out var fieldName))
            {
                var ei = Convert.ToInt32(e.Path[0].Element);
                ((Dictionary<string, object?>)pending[ei]!)[fieldName] = e.Value;
            }
        }
        if (status is "short" or "exact")
        {
            var r = new BitReader(payload, block.PayloadEnd) { Position = pos };
            while (r.Position < r.BitCount)
            {
                try
                {
                    // 运行时 FClassNetCache：24 字段，WeapInfoArray=5，
                    // WeaponReplicatedExtraDataList=17。长度定界每段。
                    var fi = r.ReadSerializeInt(24);
                    var n = EngineBunchCodec.ReadDfmPackedInt(r).Value;
                    var end = r.Position + (int)n;
                    if (end > r.BitCount) throw new BitError("weapon field length");
                    if (fi is 5 or 17)
                    {
                        var batch = WeaponState.FastArray(
                            payload, r.Position, end,
                            fi == 5 ? WeaponState.Info : WeaponNative.WeaponExtra);
                        var target = (Dictionary<long, Dictionary<string, object?>>)state[
                            fi == 5 ? "info" : "extra"]!;
                        // 初始快照 base=0；后续变更以 ReplicationID（区别于 identity）为键。
                        if (Convert.ToInt32(batch["base"]) == 0) target.Clear();
                        // 解码层产出的集合元素类型动态（long/object 混合），非泛型枚举 + Convert。
                        foreach (var ridObj in (System.Collections.IEnumerable)batch["deleted"]!)
                        {
                            target.Remove(Convert.ToInt64(ridObj));
                        }
                        foreach (var itemObj in (System.Collections.IEnumerable)batch["items"]!)
                        {
                            // item = (ReplicationID, entry) 二元组（集合元素类型动态）。
                            var pairEnum = ((System.Collections.IEnumerable)itemObj!).Cast<object?>().ToList();
                            var rid = Convert.ToInt64(pairEnum[0]);
                            var item = (Dictionary<string, object?>)pairEnum[1]!;
                            var copy = CloneEntry(item);
                            copy["seq"] = seq;
                            target[rid] = copy;
                        }
                        Bump("fast_array_" + fi);
                        changed = true;
                    }
                    r.Position = end;
                }
                catch (BitError)
                {
                    Bump("fast_array_error");
                    break;
                }
            }
        }
        if (changed)
        {
            state["seq"] = seq;
            if (_keepHistory)
            {
                if (!_history.TryGetValue(actor, out var rows))
                {
                    rows = [];
                    _history[actor] = rows;
                }
                rows.Add((seq, Current(actor)));
            }
        }
        return changed;
    }

    private static Dictionary<string, object?> CloneEntry(Dictionary<string, object?> src)
        => new(src);

    /// <summary>当前武器快照（Python: current）。atSeq 提供历史时点查询。</summary>
    public Dictionary<string, object?> Current(ulong actor, long? atSeq = null)
    {
        if (atSeq is not null)
        {
            var rows = _history.TryGetValue(actor, out var h) ? h : new List<(long Seq, Dictionary<string, object?> Snapshot)>();
            // bisect_right(rows, at_seq, key=row[0]) - 1：最后一个 Seq <= atSeq。
            var lo = 0;
            var hi = rows.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (rows[mid].Seq <= atSeq.Value) lo = mid + 1;
                else hi = mid;
            }
            var idx = lo - 1;
            return idx >= 0 ? CloneEntry(rows[idx].Snapshot) : new Dictionary<string, object?> { ["known"] = false };
        }
        if (!_actors.TryGetValue(actor, out var state) || !state.ContainsKey("current_seq"))
        {
            return new Dictionary<string, object?> { ["known"] = false };
        }
        // 槽 0 是手持武器。副手/技能槽保持活跃；比较它们的 frame 号会在
        // 技能使用时选错手（Python 原注释）。
        var current = (List<object?>)state["current"]!;
        var entries = current
            .Select(e => (Dictionary<string, object?>)e!)
            .Where(e => Convert.ToInt64(e.TryGetValue("slot", out var s) ? s : 0) == 0)
            .ToList();
        var currentSeq = state.TryGetValue("current_seq", out var csObj) ? csObj : null;
        if (entries.Count == 0)
        {
            return new Dictionary<string, object?>
            {
                ["known"] = true, ["status"] = "empty", ["weapon_id"] = null,
                ["seq"] = currentSeq,
            };
        }
        var entry = entries[^1];
        var identity = Convert.ToInt64(entry.TryGetValue("identity", out var idObj) ? idObj : 0);
        // 类路径 ID 标识可复用武器类（type 2/3 亦然）。记录实例：RPG class
        // 18080000001 → AI item 18080000007；bow class 18130000001 → item
        // 18130000002。它们不能在实例 FastArray 条目删除后/到达前替代实例映射。
        // identity 作用域限本 actor，冲突即拒绝（Python 原注释）。
        var extra = (Dictionary<long, Dictionary<string, object?>>)state["extra"]!;
        var mapped = extra.Values
            .Where(x2 => identity != 0
                && Convert.ToInt64(x2.TryGetValue("identity", out var xi) ? xi : 0) == identity
                && x2.TryGetValue("weapon_id", out var wi2) && wi2 is long widL
                && widL > 0)
            .Select(x2 => (long)x2["weapon_id"]!)
            .ToHashSet();
        long? wid = mapped.Count == 1 ? mapped.First() : null;
        return new Dictionary<string, object?>
        {
            ["known"] = true,
            ["status"] = identity == 0 ? "empty" : wid is not null ? "resolved" : "unresolved",
            ["weapon_id"] = wid,
            ["identity"] = identity,
            ["slot"] = 0L,
            ["frame"] = entry.TryGetValue("frame", out var fr) ? fr : null,
            ["seq"] = currentSeq,
            ["resolved_seq"] = state.TryGetValue("seq", out var sq) ? sq : null,
            ["source"] = wid is not null ? "weapon_extra"
                : mapped.Count > 1 ? "conflicting_extra" : "missing_extra",
        };
    }
}
