using System.Buffers.Binary;
using CCRadar.Parser.Codec;

namespace CCRadar.Parser.Core;

/// <summary>
/// property_health.py 的 C# 等价实现 —— PropertyReplication 血量批量，
/// 0904 运行时确认。
///
/// 容器：dense 位 + packed 数量 + presence 集 + 长度定界条目。
/// 条目 handle：1..6 为 float 血量字段，7 为 Object GUID，0 结束。条目内无校验位。
/// Object 经 AttributeCenter 的导出 Outer 解析；容器索引不是移动槽
/// （109677 实测 self 91 vs MoveHandle 27，Python 原注释保留）。
/// </summary>
public static class PropertyHealth
{
    /// <summary>血量字段名表（Python: FIELDS）。</summary>
    public static readonly string[] Fields =
    [
        "Health", "ShieldHealth", "ArmorShieldHealth", "ArmorHealth",
        "HelmetArmorHealth", "IsAlive", "Object",
    ];

    /// <summary>
    /// 解码一个血量容器块（Python: decode_health_block）。
    /// 返回完整容器增量或抛异常；绝不提交部分块。
    /// </summary>
    public static List<Dictionary<string, object?>> DecodeHealthBlock(byte[] payload, int payloadStart, int payloadEnd)
    {
        var r = new BitReader(payload, payloadEnd) { Position = payloadStart };
        // 运行时/抓包使用 unchecked 复制。checked 属性序列化带额外的每属性标记，
        // 此处未验证（Python 原注释）。
        if (r.ReadBit() != 0)
        {
            throw new BitError("checked health block unsupported");
        }
        var output = new List<Dictionary<string, object?>>();
        var previous = 0;
        while (true)
        {
            var h = EngineBunchCodec.ReadDfmPackedInt(r).Value;
            if (h == 0)
            {
                if (r.Remaining() > 0)
                {
                    throw new BitError("health block has leftover bits");
                }
                return output;
            }
            if (!(previous < h && h <= 4))
            {
                throw new BitError("health component handle order/range");
            }
            previous = (int)h;
            if (h is 1 or 2)
            {
                r.ReadBit();   // bReplicates / bIsActive
                continue;
            }
            var dense = r.ReadBit();
            var count = (int)EngineBunchCodec.ReadDfmPackedInt(r).Value;
            if (count > 65535)
            {
                throw new BitError("health container count exceeds u16 index");
            }
            var indices = new List<int>();
            if (dense != 0)
            {
                r.Ensure(count);
                for (var i = 0; i < count; i++)
                {
                    if (r.ReadBit() != 0) indices.Add(i);
                }
            }
            else
            {
                while (true)
                {
                    var index = r.ReadUInt16();
                    if (index == 0) break;
                    if (index > count || (indices.Count > 0 && index - 1 <= indices[^1]))
                    {
                        throw new BitError("health presence index order/range");
                    }
                    indices.Add((int)(index - 1));
                }
            }
            var items = new List<object?>();
            foreach (var index in indices)
            {
                var size = (int)EngineBunchCodec.ReadDfmPackedInt(r).Value;
                r.Ensure(size);
                var end = r.Position + size;
                var inner = new BitReader(payload, end) { Position = r.Position };
                var values = new Dictionary<string, object?>();
                var last = 0;
                while (true)
                {
                    var field = (int)EngineBunchCodec.ReadDfmPackedInt(inner).Value;
                    if (field == 0) break;
                    if (!(last < field && field <= Fields.Length))
                    {
                        throw new BitError("health item handle order/range");
                    }
                    last = field;
                    object? value = field == 7
                        ? EngineBunchCodec.ReadDfmPackedInt(inner).Value
                        : inner.ReadFloat32();
                    if (field != 7)
                    {
                        var f = Convert.ToDouble(value);
                        if (!double.IsFinite(f) || f < 0)
                        {
                            throw new BitError("invalid health value");
                        }
                    }
                    values[Fields[field - 1]] = value;
                }
                if (inner.Position != end)
                {
                    throw new BitError("health item has leftover bits");
                }
                items.Add(new List<object?> { index, values });
                r.Position = end;
            }
            output.Add(new Dictionary<string, object?>
            {
                ["handle"] = (long)h, ["count"] = (long)count, ["items"] = items,
            });
        }
    }

    /// <summary>操作员 100 HP 上限规则（Python: operator_health）——不虚构当前血量。</summary>
    public static Dictionary<string, object?> OperatorHealth(
        IReadOnlyDictionary<string, object?> hp, bool isOperator = true)
    {
        if (!isOperator || hp.Count == 0 || !hp.TryGetValue("total", out var totalObj)
            || totalObj is not List<object?> total || total.Count < 2)
        {
            return new Dictionary<string, object?>(hp);
        }
        var current = total[0];
        var maximum = total[1];
        if (maximum is null && (current is long || current is double)
            && Convert.ToDouble(current) is >= 0 and <= 100)
        {
            var copy = new Dictionary<string, object?>(hp);
            copy["total"] = new List<object?> { current, 100.0 };
            copy["max_source"] = "operator_default";
            return copy;
        }
        return new Dictionary<string, object?>(hp);
    }

    /// <summary>只回放游标处观察到的数据，包括真实的零血（Python: health_at_seq）。</summary>
    public static Dictionary<string, object?>? HealthAtSeq(
        IReadOnlyDictionary<ulong, Dictionary<string, List<(long Seq, object? Value)>>> history,
        ulong actor, long seq)
    {
        if (!history.TryGetValue(actor, out var fields)
            || !fields.TryGetValue("Health", out var points)
            || points.Count == 0)
        {
            return null;
        }
        // bisect_right(points, seq, key=point[0]) - 1。
        var lo = 0;
        var hi = points.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (points[mid].Seq <= seq) lo = mid + 1;
            else hi = mid;
        }
        var index = lo - 1;
        if (index < 0) return null;
        var (stamp, value) = points[index];
        return new Dictionary<string, object?>
        {
            ["total"] = new List<object?> { Math.Round(Convert.ToDouble(value), 2), null },
            ["seq"] = stamp,
            ["source"] = "property_replication",
        };
    }
}

/// <summary>血量批量跟踪器（Python: HealthBatchTracker）。</summary>
public sealed class HealthBatchTracker
{
    private readonly Dictionary<ulong, Dictionary<string, object?>> _exports = new();
    private readonly Dictionary<int, ulong> _channels = new();
    private readonly Dictionary<(ulong Component, long Handle), Dictionary<string, object?>> _containers = new();
    private readonly Dictionary<ulong, Dictionary<string, (long Seq, object? Value)>> _objects = new();
    private readonly Dictionary<ulong, int> _objectOrder = new();
    private readonly HashSet<ulong> _dirty = new();
    private readonly Dictionary<(ulong Obj, string Name), long> _delivered = new();
    private readonly bool _keepHistory;

    /// <summary>血量历史（keep_history 时启用；Python: history）。</summary>
    public List<(ulong Obj, string Name, object? Value, long Seq)>? History { get; }

    /// <summary>计数器（Python: Counter）。</summary>
    public Dictionary<string, int> Stats { get; } = new();

    public HealthBatchTracker(bool keepHistory = false)
    {
        _keepHistory = keepHistory;
        History = keepHistory ? [] : null;
    }

    private void Bump(string key) => Stats[key] = Stats.TryGetValue(key, out var c) ? c + 1 : 1;

    /// <summary>观察 exports（Python: observe_exports）；迟到的导出触发待定值重解析。</summary>
    public void ObserveExports(IEnumerable<Dictionary<string, object?>> exports)
    {
        foreach (var entry in exports)
        {
            if (!entry.TryGetValue("path", out var pathObj) || pathObj is null) continue;
            if (!entry.TryGetValue("guid", out var guidObj) || guidObj is not ulong guid) continue;
            var has = _exports.TryGetValue(guid, out var previous);
            _exports[guid] = entry;
            if (_objects.ContainsKey(guid) && (!has || previous == null || previous.Count != entry.Count || previous.Any(kv => !entry.TryGetValue(kv.Key, out var v2) || !Equals(kv.Value, v2))))
            {
                // 字典引用比较近似 Python 的 entry 对象不等；语义：新导出即脏。
                _dirty.Add(guid);
            }
        }
    }

    private bool IsHealthActor(ulong guid)
        => _exports.TryGetValue(guid, out var e)
           && e.TryGetValue("path", out var p) && p?.ToString()?.Contains("BP_PropertyReplicationCharacterHealth_C") == true;

    private bool IsHealthComponent(ulong guid, ulong actor)
    {
        if (!_exports.TryGetValue(guid, out var entry)) return false;
        var path = entry.TryGetValue("path", out var p) ? p?.ToString() ?? "" : "";
        var name = path.Contains('.') ? path[(path.LastIndexOf('.') + 1)..] : path;
        var outerOk = entry.TryGetValue("outer", out var o) && o is ulong ou && ou == actor;
        return name is "PropertyReplication" or "PropertyReplicationComponent"
            && outerOk && IsHealthActor(actor);
    }

    /// <summary>应用容器增量（Python: apply）。</summary>
    public void Apply(ulong component, List<Dictionary<string, object?>> deltas, long seq)
    {
        foreach (var delta in deltas)
        {
            var handle = Convert.ToInt64(delta["handle"]);
            var key = (component, handle);
            if (!_containers.TryGetValue(key, out var state))
            {
                state = new Dictionary<string, object?> { ["seq"] = -1L, ["items"] = new Dictionary<int, Dictionary<string, object?>>() };
                _containers[key] = state;
            }
            if (seq < Convert.ToInt64(state["seq"])) continue;
            state["seq"] = seq;
            state["count"] = delta["count"];
            var slots = (Dictionary<int, Dictionary<string, object?>>)state["items"]!;
            foreach (var i in slots.Keys.ToList())
            {
                if (i >= Convert.ToInt32(delta["count"])) slots.Remove(i);
            }
            foreach (var itemObj in (List<object?>)delta["items"]!)
            {
                var pair = (List<object?>)itemObj!;
                var index = (int)pair[0]!;
                var values = (Dictionary<string, object?>)pair[1]!;
                if (!slots.TryGetValue(index, out var item))
                {
                    item = [];
                    slots[index] = item;
                }
                if (values.TryGetValue("Object", out var objNew)
                    && (!item.TryGetValue("Object", out var objOld) || !Equals(objNew, objOld)))
                {
                    item.Clear();   // 槽位复用不得继承旧血量
                }
                foreach (var (k, v) in values) item[k] = v;
                if (!values.TryGetValue("Object", out var obj) || obj is null) continue;
                var objGuid = (ulong)Convert.ToUInt64(obj);
                if (!_objects.TryGetValue(objGuid, out var rec))
                {
                    rec = [];
                    _objects[objGuid] = rec;
                    _objectOrder[objGuid] = _objectOrder.Count;
                }
                foreach (var (name, value) in values)
                {
                    if (name == "Object") continue;
                    if (seq >= (rec.TryGetValue(name, out var cur) ? cur.Seq : -1))
                    {
                        rec[name] = (seq, value);
                        _dirty.Add(objGuid);
                        History?.Add((objGuid, name, value, seq));
                    }
                }
            }
        }
    }

    /// <summary>消费一个 bunch（Python: consume）。</summary>
    public void Consume(BunchView x)
    {
        int start;
        try
        {
            var (exports, s) = ReplicationHeader.ExportsPrefix(
                x.Payload, x.PBits, x.Hx, x.Hm, x.Frags);
            ObserveExports(exports);
            start = s;
        }
        catch (SnaBad)
        {
            Bump("prefix_error");
            return;
        }
        catch (IndexOutOfRangeException)
        {
            Bump("prefix_error");
            return;
        }
        if (x.Open)
        {
            _channels.Remove(x.Ch);
            try
            {
                // 血量广播器是稳定命名 actor；不要用此分支解析动态 Character 变换。
                var (actorGuid, _) = Sna.LoadObject(x.Payload, x.PBits, start, exporting: false);
                if (!IsHealthActor(actorGuid)) return;
                var (info, newStart) = Sna.ParsePrefix(x.Payload, x.PBits, x.Hx, x.Hm, frags: x.Frags);
                start = newStart;
                _channels[x.Ch] = (ulong)info["actor"]!;
            }
            catch (SnaBad)
            {
                Bump("prefix_error");
                return;
            }
            catch (IndexOutOfRangeException)
            {
                Bump("prefix_error");
                return;
            }
        }
        var actor = _channels.TryGetValue(x.Ch, out var ch) ? ch : (ulong?)null;
        if (actor is null) return;
        // 严格块铺砌：绝不为血量式浮点搜索位偏移。
        var blocks = Blocks.Tile(x.Payload, x.PBits, start);
        if (blocks is not null)
        {
            foreach (var block in blocks)
            {
                if (block.Deleted || block.IsActor != 0 || block.HasRep == 0) continue;
                if (block.Sub is not { } sub || !IsHealthComponent(sub, actor.Value)) continue;
                List<Dictionary<string, object?>> deltas;
                try
                {
                    deltas = PropertyHealth.DecodeHealthBlock(x.Payload, block.PayloadStart, block.PayloadEnd);
                }
                catch (BitError)
                {
                    Bump("invalid");
                    continue;
                }
                Bump("exact");
                Stats["items"] = Stats.TryGetValue("items", out var itemsCount) ? itemsCount + deltas.Sum(d => ((List<object?>)d["items"]!).Count) : deltas.Sum(d => ((List<object?>)d["items"]!).Count);
                Apply(sub, deltas, x.Seq);
            }
        }
        if (x.Close) _channels.Remove(x.Ch);
    }

    /// <summary>解析迟到导出；每个 object/field 版本只返回一次（Python: drain）。</summary>
    public List<(ulong Actor, string Name, object? Value, long Seq)> Drain()
    {
        var events = new List<(ulong, string, object?, long)>();
        // 保持 object/field 插入序，但绝不在每个 bunch 后重走未变更历史。
        // 未解析对象在导出时重试（Python 原注释）。
        var dirty = _dirty.OrderBy(o => _objectOrder.TryGetValue(o, out var ord) ? ord : int.MaxValue).ToList();
        _dirty.Clear();
        foreach (var obj in dirty)
        {
            if (!_objects.TryGetValue(obj, out var fields)) continue;
            var actor = ActorOf(obj);
            if (actor is null or 0) continue;
            foreach (var (name, (seq, value)) in fields)
            {
                var key = (obj, name);
                if (seq > (_delivered.TryGetValue(key, out var d) ? d : -1))
                {
                    events.Add((actor.Value, name, value, seq));
                    _delivered[key] = seq;
                }
            }
        }
        return events;
    }

    /// <summary>Object → 所属 Character actor（Python: actor_of）。</summary>
    public ulong? ActorOf(ulong obj)
    {
        if (!_exports.TryGetValue(obj, out var entry)) return null;
        var path = entry.TryGetValue("path", out var p) ? p?.ToString() ?? "" : "";
        var name = path.Contains('.') ? path[(path.LastIndexOf('.') + 1)..] : path;
        if (name is "AttributeCenter" or "GPAttributeSetHealth")
        {
            return entry.TryGetValue("outer", out var o) && o is ulong ou ? ou : null;
        }
        return null;
    }

    /// <summary>完整解析历史（Python: resolved_history）。</summary>
    public Dictionary<ulong, Dictionary<string, List<(long Seq, object? Value)>>> ResolvedHistory()
    {
        var history = new Dictionary<ulong, Dictionary<string, List<(long Seq, object? Value)>>>();
        foreach (var (obj, name, value, seq) in History ?? [])
        {
            var actor = ActorOf(obj);
            if (actor is null) continue;
            if (!history.TryGetValue(actor.Value, out var fields))
            {
                fields = [];
                history[actor.Value] = fields;
            }
            if (!fields.TryGetValue(name, out var points))
            {
                points = [];
                fields[name] = points;
            }
            points.Add((seq, value));
        }
        foreach (var fields in history.Values)
        {
            foreach (var points in fields.Values)
            {
                points.Sort((a, b) => a.Seq.CompareTo(b.Seq));
            }
        }
        return history;
    }
}
