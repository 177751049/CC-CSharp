using CCRadar.Parser.Codec;

namespace CCRadar.Parser.Core;

/// <summary>
/// pickup_state.py 的 C# 等价实现 —— 地面拾取物跟踪：出生位置、生死周期、
/// 背包转移关联（InventoryGID/item_id 匹配）。
/// </summary>
public sealed class PickupTracker
{
    private readonly RepCatalog _catalog;
    private readonly Dictionary<int, Dictionary<string, object?>?> _meta;
    private readonly RepCatalog _inventoryCatalog;
    private readonly Dictionary<int, Dictionary<string, object?>?> _inventoryMeta;
    private readonly HashSet<ulong> _archs;
    private readonly IReadOnlyDictionary<string, string> _names;
    private readonly IReadOnlyDictionary<string, Dictionary<string, object?>> _itemMeta;
    private readonly Dictionary<ulong, string> _paths = new();
    private readonly Dictionary<int, ulong> _channels = new();
    private readonly Dictionary<ulong, Dictionary<string, object?>> _records = new();
    private readonly Dictionary<ulong, Dictionary<string, object?>> _active = new();
    private readonly Dictionary<ulong, Dictionary<string, object?>> _removed = new();
    private readonly Dictionary<ulong, ulong> _inventoryOwners = new();
    private readonly Dictionary<(ulong Component, int Field), Dictionary<string, object?>> _inventoryArrays = new();
    private readonly HashSet<int> _inventoryChannels = new();
    private readonly Dictionary<int, string> _parentNames;
    private readonly bool _keepHistory;
    private readonly Dictionary<ulong, List<(long Seq, Dictionary<string, object?> Record)>> _history = new();

    /// <summary>0904 档位（InventoryManager 原生正文启用标志；Python: inventory_profile）。</summary>
    public string? InventoryProfile { get; private set; }

    /// <summary>计数器（Python: Counter）。</summary>
    public Dictionary<string, int> Stats { get; } = new();

    public PickupTracker(
        IEnumerable<ulong>? archs = null,
        IReadOnlyDictionary<string, string>? names = null,
        IReadOnlyDictionary<string, Dictionary<string, object?>>? itemMeta = null,
        bool keepHistory = false)
    {
        // Python: self.catalog = 整个 pickup_schema_0904.json —— 顶层含
        // cmds/parents/meta/inventory_catalog/inventory_meta。
        var schemaEl = System.Text.Json.JsonDocument.Parse(AssetCrypto.ReadText(
            Path.Combine(RepDec4.DataRoot, "pickup_schema_0904.json"))).RootElement.Clone();
        _catalog = RepDec4.ParseSchemaCatalogJson(schemaEl, "pickup");
        _meta = JsonHelpers.ToObjectList(schemaEl.GetProperty("meta"))
            .Where(m => m.TryGetValue("index", out var i) && i is long)
            .ToDictionary(m => (int)Convert.ToInt64(m["index"]), m => (Dictionary<string, object?>?)m);
        _parentNames = _catalog.Parents
            .Where(p => p.TryGetValue("index", out var i) && i is long)
            .ToDictionary(p => (int)Convert.ToInt64(p["index"]),
                p => p.TryGetValue("name", out var n) ? n?.ToString() ?? "" : "");
        _inventoryCatalog = RepDec4.ParseSchemaCatalogJson(
            schemaEl.GetProperty("inventory_catalog"), "inventory_catalog");
        _inventoryMeta = JsonHelpers.ToObjectList(schemaEl.GetProperty("inventory_meta"))
            .Where(m => m.TryGetValue("index", out var i) && i is long)
            .ToDictionary(m => (int)Convert.ToInt64(m["index"]), m => (Dictionary<string, object?>?)m);
        _archs = archs is not null ? new HashSet<ulong>(archs) : [];
        _names = names ?? new Dictionary<string, string>();
        _itemMeta = itemMeta ?? new Dictionary<string, Dictionary<string, object?>>();
        _keepHistory = keepHistory;
    }

    private void Bump(string key) => Stats[key] = Stats.TryGetValue(key, out var c) ? c + 1 : 1;

    /// <summary>当前活跃拾取物（Python: self.active）。</summary>
    public IReadOnlyDictionary<ulong, Dictionary<string, object?>> Active => _active;

    /// <summary>已移除拾取物（Python: self.removed）。</summary>
    public IReadOnlyDictionary<ulong, Dictionary<string, object?>> Removed => _removed;

    private static Dictionary<string, object?> CloneRecord(Dictionary<string, object?> rec)
        => new(rec);

    private void Publish(ulong actor)
    {
        var record = _records[actor];
        if (record.TryGetValue("gone_seq", out var gone) && gone is not null)
        {
            _active.Remove(actor);
            _removed[actor] = record;
        }
        else if (record.ContainsKey("loc") && record["loc"] is not null)
        {
            _removed.Remove(actor);
            _active[actor] = record;
        }
        if (_keepHistory)
        {
            if (!_history.TryGetValue(actor, out var rows))
            {
                rows = [];
                _history[actor] = rows;
            }
            rows.Add((Convert.ToInt64(record["update_seq"]), CloneRecord(record)));
        }
    }

    /// <summary>历史时点查询（Python: at）。</summary>
    public Dictionary<string, object?>? At(ulong actor, long seq)
    {
        var rows = _history.TryGetValue(actor, out var h) ? h : new List<(long, Dictionary<string, object?>)>();
        var lo = 0;
        var hi = rows.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (rows[mid].Item1 <= seq) lo = mid + 1;
            else hi = mid;
        }
        var index = lo - 1;
        return index >= 0 ? rows[index].Item2 : null;
    }

    /// <summary>移除拾取物（Python: remove）。reason 0/property + inventory_transfer → confirmed。</summary>
    public void Remove(ulong actor, long seq, object reason, string evidence)
    {
        if (!_records.TryGetValue(actor, out var rec)) return;
        var lifecycle = Convert.ToInt64(rec.TryGetValue("lifecycle_seq", out var lc) ? lc
            : rec.TryGetValue("seq", out var sq) ? sq : -1);
        if (seq < lifecycle) return;
        rec["gone_seq"] = seq;
        rec["gone_reason"] = reason;
        rec["update_seq"] = Math.Max(seq, Convert.ToInt64(rec["update_seq"]));
        rec["lifecycle_seq"] = seq;
        rec["pickup_confidence"] = "removed";
        rec["removal_evidence"] = evidence;
        if ((reason is 0 or "property") && rec.ContainsKey("inventory_transfer"))
        {
            rec["pickup_confidence"] = "confirmed";
        }
        Bump(evidence);
        Publish(actor);
    }

    /// <summary>通道关闭（Python: close_channel）。</summary>
    public void CloseChannel(int channel, long seq, int? reason)
    {
        if (!_channels.TryGetValue(channel, out var actor)) return;
        _channels.Remove(channel);
        if (reason is 0 or 2 or 4)
        {
            Remove(actor, seq, reason, reason switch { 0 => "destroyed", 2 => "level_unloaded", _ => "tear_off" });
        }
        else
        {
            if (_records.TryGetValue(actor, out var rec) && seq >= Convert.ToInt64(rec["update_seq"]))
            {
                rec["channel_state"] = reason switch { 1 => "dormant", 3 => "irrelevant", _ => "closed" };
                rec["update_seq"] = seq;
                Publish(actor);
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
            foreach (var e in exports)
            {
                if (!e.TryGetValue("path", out var pathObj) || pathObj is not string path) continue;
                if (!e.TryGetValue("guid", out var guidObj) || guidObj is not ulong guid) continue;
                _paths[guid] = path;
                if (path == "InventoryManager" && e.TryGetValue("outer", out var outerObj) && outerObj is not null)
                {
                    _inventoryOwners[guid] = (ulong)Convert.ToUInt64(outerObj);
                }
                if (path.Contains("BP_SOLPlayerState", StringComparison.Ordinal))
                {
                    InventoryProfile = "0904";
                }
                if (path.Contains("BP_InventoryPickup", StringComparison.Ordinal))
                {
                    _archs.Add(guid);
                }
            }
            start = s;
        }
        catch (Exception ex) when (ex is BitError or SnaBad)
        {
            Bump("framing_error");
            return;
        }
        try
        {
            if (x.Open)
            {
                _channels.Remove(x.Ch);
                _inventoryChannels.Remove(x.Ch);
                if (x.Close)
                {
                    if (x.CloseReason == 0)
                    {
                        var r0 = new BitReader(x.Payload, x.PBits) { Position = start };
                        var actor0 = EngineBunchCodec.ReadDfmPackedInt(r0).Value;
                        if (r0.Position == x.PBits)
                        {
                            Remove(actor0, x.Seq, 0, "destruction_info");
                        }
                    }
                    return;
                }
                var (info, newStart) = Sna.ParsePrefix(
                    x.Payload, x.PBits, x.Hx, x.Hm, quantizeBit: true, frags: x.Frags);
                var arch = info.TryGetValue("arch", out var archObj) ? archObj : null;
                if (arch is not ulong ag || !_archs.Contains(ag))
                {
                    var name = arch is ulong g && _paths.TryGetValue(g, out var p) ? p : "";
                    if (!name.Contains("Character", StringComparison.Ordinal)
                        || name.Contains("Weapon", StringComparison.Ordinal))
                    {
                        return;
                    }
                    newStart += 8;
                    _inventoryChannels.Add(x.Ch);
                }
                else
                {
                    var actor = info.TryGetValue("actor", out var a) && a is ulong au ? au : 0UL;
                    if (actor == 0) return;
                    _channels[x.Ch] = actor;
                    if (!_records.TryGetValue(actor, out var rec))
                    {
                        rec = new Dictionary<string, object?>
                        {
                            ["seq"] = x.Seq, ["update_seq"] = x.Seq,
                            ["gone_seq"] = null, ["gone_reason"] = null,
                            ["pickup_confidence"] = "visible",
                        };
                        _records[actor] = rec;
                    }
                    if (x.Seq < Convert.ToInt64(rec["update_seq"])) return;
                    if (rec.TryGetValue("gone_seq", out var gs) && gs is not null)
                    {
                        rec.Clear();
                        rec["seq"] = x.Seq;
                    }
                    rec["update_seq"] = x.Seq;
                    rec["lifecycle_seq"] = x.Seq;
                    rec["gone_seq"] = null;
                    rec["gone_reason"] = null;
                    rec["pickup_confidence"] = "visible";
                    rec["channel_state"] = "open";
                    if (info.TryGetValue("loc", out var locObj) && locObj is double[] loc
                        && loc.Length == 3 && loc.All(double.IsFinite))
                    {
                        rec["loc"] = loc.ToList();
                    }
                    Publish(actor);
                }
                start = newStart;
            }
            var actorCh = _channels.TryGetValue(x.Ch, out var ch) ? ch : (ulong?)null;
            if (actorCh is null && (InventoryProfile != "0904" || !_inventoryChannels.Contains(x.Ch)))
            {
                return;
            }
            var blocks = Blocks.Tile(x.Payload, x.PBits, start);
            if (blocks is null) return;
            foreach (var block in blocks)
            {
                if (block.Deleted) continue;
                var guid = block.Stably != 0 ? block.Sub : block.Cls;
                var gp = guid is ulong g && _paths.TryGetValue(g, out var p2) ? p2 : null;
                if (gp == "InventoryManager" && InventoryProfile == "0904")
                {
                    ApplyInventory(guid!.Value, x.Seq, x.Payload, block);
                }
                else if (actorCh is not null && block.HasRep != 0 && block.IsActor != 0)
                {
                    ApplyBlock(actorCh.Value, x.Seq, x.Payload, block);
                }
            }
        }
        catch (Exception ex) when (ex is BitError or SnaBad)
        {
            Bump("framing_error");
        }
        finally
        {
            if (x.Close)
            {
                CloseChannel(x.Ch, x.Seq, x.CloseReason);
                _inventoryChannels.Remove(x.Ch);
            }
        }
    }

    /// <summary>InventoryManager 的 RepItemArray FastArray（Python: apply_inventory）。</summary>
    private void ApplyInventory(ulong component, long seq, byte[] payload, ContentBlock block)
    {
        if (block.HasRep == 0) return;
        var (_, pos, status) = RepDec6.DecodeBlock(
            payload, block.PayloadStart, block.PayloadEnd, _inventoryCatalog, _inventoryMeta);
        if (status is not ("exact" or "short"))
        {
            Bump("inventory_layout_" + status);
            return;
        }
        var r = new BitReader(payload, block.PayloadEnd) { Position = pos };
        try
        {
            while (r.Position < r.BitCount)
            {
                var field = r.ReadSerializeInt(52);
                var size = EngineBunchCodec.ReadDfmPackedInt(r).Value;
                var end = r.Position + (int)size;
                if (end > r.BitCount) throw new BitError("inventory field length");
                if (field is 7 or 8)
                {
                    var batch = WeaponState.FastArray(payload, r.Position, end,
                        InventoryNative.InventoryItem);
                    var key = (component, field);
                    if (!_inventoryArrays.TryGetValue(key, out var state))
                    {
                        state = new Dictionary<string, object?> { ["seq"] = -1L, ["items"] = new Dictionary<long, Dictionary<string, object?>>() };
                        _inventoryArrays[key] = state;
                    }
                    if (seq >= Convert.ToInt64(state["seq"]))
                    {
                        var items = (Dictionary<long, Dictionary<string, object?>>)state["items"]!;
                        if ((int)batch["base"]! == 0) items.Clear();
                        foreach (var rid in (List<long>)batch["deleted"]!)
                        {
                            items.Remove(rid);
                        }
                        foreach (var itemObj in (List<object?>)batch["items"]!)
                        {
                            var pair = (List<object?>)itemObj!;
                            var rid = (long)pair[0]!;
                            var item = (Dictionary<string, object?>)pair[1]!;
                            items[rid] = item;
                            InventoryTransfer(component, seq, item);
                        }
                        state["seq"] = seq;
                    }
                    Bump("inventory_fast_array");
                }
                r.Position = end;
            }
        }
        catch (RepOpaque)
        {
            Bump("inventory_native_opaque");
        }
        catch (BitError)
        {
            Bump("inventory_error");
        }
    }

    /// <summary>背包转移与拾取物的 GID 关联（Python: inventory_transfer）。</summary>
    private void InventoryTransfer(ulong component, long seq, Dictionary<string, object?> item)
    {
        if (!_inventoryOwners.TryGetValue(component, out var owner) || owner == 0) return;
        var attachPosition = item.TryGetValue("attach_position", out var ap) ? ap : null;
        if (attachPosition is null or 0L or 301L) return;
        foreach (var (actor, rec) in _records)
        {
            if (!rec.TryGetValue("inventory_gid", out var gidObj)
                || gidObj is not ulong gid || gid != (ulong)Convert.ToUInt64(item["guid"]))
            {
                continue;
            }
            if (rec.TryGetValue("seq", out var sq) && Convert.ToInt64(sq) > seq) continue;
            var recItemId = rec.TryGetValue("item_id", out var ii) ? ii?.ToString() : null;
            if (recItemId != item["item_id"].ToString()) continue;
            var previous = rec.TryGetValue("inventory_transfer", out var it) && it is Dictionary<string, object?> pd ? pd : new Dictionary<string, object?>();
            if (seq < Convert.ToInt64(previous.TryGetValue("seq", out var ps) ? ps : -1)) continue;
            rec["inventory_transfer"] = new Dictionary<string, object?>
            {
                ["actor"] = owner,
                ["seq"] = seq,
                ["count"] = item.TryGetValue("count", out var ct) ? ct : 1L,
                ["attach_position"] = item["attach_position"],
            };
            if (rec.TryGetValue("gone_seq", out var gone) && gone is not null
                && rec.TryGetValue("gone_reason", out var gr)
                && gr is 0 or "property")
            {
                rec["pickup_confidence"] = "confirmed";
            }
            rec["update_seq"] = Math.Max(seq, Convert.ToInt64(rec["update_seq"]));
            Bump("inventory_gid_match");
            Publish(actor);
        }
    }

    /// <summary>应用拾取物属性块（Python: apply_block）。</summary>
    private void ApplyBlock(ulong actor, long seq, byte[] payload, ContentBlock block)
    {
        var (ev, _, status) = RepDec6.DecodeBlock(
            payload, block.PayloadStart, block.PayloadEnd, _catalog, _meta,
            valueReader: PickupValues.PickupValue);
        Bump("layout_" + status);
        if (!_records.TryGetValue(actor, out var rec)) return;
        if (seq < Convert.ToInt64(rec["update_seq"])) return;
        // 原生背包正文的未知版本下，早期的独立标量属性仍然有用。除非完整
        // RepLayout 在块边界终止，否则永不信任其后的字段（Python 原注释）。
        var complete = status == "exact";
        var props = ev
            .Where(e => e.Path.Count == 0 && (complete || e.Handle < 67))
            .ToDictionary(
                e => _parentNames.TryGetValue(e.ParentIndex, out var pn) ? pn : "",
                e => e.Value);
        var oldGid = rec.TryGetValue("inventory_gid", out var og) ? og : null;
        if (oldGid is not null && props.TryGetValue("InventoryGID", out var newGid)
            && !Equals(newGid, oldGid))
        {
            var loc = rec.TryGetValue("loc", out var l) ? l : null;
            rec.Clear();
            rec["seq"] = seq;
            rec["loc"] = loc;
            rec["gone_seq"] = null;
            rec["gone_reason"] = null;
            rec["pickup_confidence"] = "visible";
        }
        var mapping = new Dictionary<string, string>
        {
            ["InventoryGID"] = "inventory_gid", ["SourceType"] = "source_type",
            ["StackCount"] = "stack_count", ["Durability"] = "durability",
            ["ItemState"] = "item_state", ["bIsHiddenItem"] = "hidden",
            ["WhoDropThisPlayerId"] = "dropper_id", ["LastOwnedPlayerId"] = "last_owner_id",
        };
        foreach (var (name, key) in mapping)
        {
            if (props.TryGetValue(name, out var v)) rec[key] = v;
        }
        if (props.TryGetValue("InventoryIdName", out var iidName) && iidName is Dictionary<string, object?> iidDict
            && iidDict.TryGetValue("text", out var textObj) && textObj is string iid && iid.All(char.IsAsciiDigit))
        {
            rec["item_id"] = iid;
        }
        if (props.TryGetValue("PickupItemInfo", out var native) && native is Dictionary<string, object?> nativeDict)
        {
            rec["inventory_info"] = native;
            rec["item_id"] = nativeDict.TryGetValue("item_id", out var ni) ? ni?.ToString() : null;
            rec["inventory_gid"] = nativeDict.TryGetValue("guid", out var ng) ? ng : null;
        }
        var itemIdKey = rec.TryGetValue("item_id", out var idObj) ? idObj?.ToString() : null;
        var meta = itemIdKey is not null && _itemMeta.TryGetValue(itemIdKey, out var m)
            ? m : new Dictionary<string, object?>();
        rec["update_seq"] = seq;
        rec["lifecycle_seq"] = seq;
        rec["decode_status"] = status;
        rec["name"] = itemIdKey is not null && _names.TryGetValue(itemIdKey, out var nm) ? nm : null;
        rec["grade"] = meta.TryGetValue("grade", out var g) ? g : null;
        rec["price"] = meta.TryGetValue("price", out var pr) ? pr : null;
        var hidden = rec.TryGetValue("hidden", out var hv) && hv is true;
        var itemState5 = rec.TryGetValue("item_state", out var isv) && Equals(isv, 5L);
        var stackZero = rec.TryGetValue("stack_count", out var sc) && Equals(sc, 0L);
        if (complete && (hidden || itemState5 || stackZero))
        {
            Remove(actor, seq, "property", "item_unavailable");
        }
        else
        {
            if (complete && ((props.TryGetValue("ItemState", out var st) && Equals(st, 1L))
                || (props.TryGetValue("bIsHiddenItem", out var hd) && hd is false)))
            {
                rec["gone_seq"] = null;
                rec["gone_reason"] = null;
                rec["pickup_confidence"] = "visible";
            }
            Publish(actor);
        }
    }
}
