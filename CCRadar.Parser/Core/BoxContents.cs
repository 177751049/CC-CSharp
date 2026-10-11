using System.Text.Json;
using CCRadar.Parser.Codec;

namespace CCRadar.Parser.Core;

/// <summary>
/// box_contents.py 的 C# 等价实现 —— 来自控制器 full/diff RPC 的容器内容，
/// 以 object 与物品 GID 为键。这是接收到的状态，不是本地玩家执行过搜索或
/// 转移的证据；完整可见列表仍可能含未揭示的占位符（Python 原注释）。
/// </summary>
public static class BoxContents
{
    /// <summary>未揭示占位 item id（Python: PLACEHOLDER_ID）。</summary>
    public const long PlaceholderId = 99999999999;

    private static readonly Lazy<JsonElement> SchemaLazy = new(() =>
        JsonDocument.Parse(AssetCrypto.ReadText(
            Path.Combine(RepDec4.DataRoot, "box_contents_schema_0904.json"))).RootElement.Clone());

    /// <summary>RPC schema（Python: schema，lru_cache）。</summary>
    public static JsonElement Schema => SchemaLazy.Value;

    /// <summary>内容解码失败（Python: ContentsDecodeError，携带 actor）。</summary>
    public sealed class ContentsDecodeError : BitError
    {
        public ulong? Actor { get; }
        public ContentsDecodeError(string message, ulong? actor = null) : base(message) { Actor = actor; }
    }

    /// <summary>
    /// 读 RPC 参数存在位并要求声明的位区间恰好用尽（Python: decode_contents）。
    /// </summary>
    public static Dictionary<string, object?> DecodeContents(byte[] payload, int start, int end, string method)
    {
        ulong? actor = null;
        try
        {
            if (method is not ("full" or "diff" or "diff_space")
                || !(0 <= start && start <= end && end <= payload.Length * 8))
            {
                throw new BitError("contents envelope");
            }
            var r = new BitReader(payload, end) { Position = start };
            actor = r.ReadBit() != 0 ? EngineBunchCodec.ReadDfmPackedInt(r).Value : 0;
            var items = new List<object?>();
            if (r.ReadBit() != 0)
            {
                var size = r.ReadUInt16();
                if (size > 4096) throw new BitError("contents item count");
                for (var i = 0; i < size; i++)
                {
                    var item = InventoryNative.InventoryItem(r);
                    var guid = item.TryGetValue("guid", out var g) ? g : null;
                    if (guid is null or 0UL) throw new BitError("zero item GID");
                    if (method != "full")
                    {
                        item["change"] = (long)r.ReadBits(2);
                        if (Convert.ToInt64(item["change"]) > 2) throw new BitError("unknown item change");
                    }
                    items.Add(item);
                }
            }
            List<object?>? spaces = null;
            if (method is "full" or "diff_space")
            {
                spaces = r.ReadBit() != 0 ? InventoryNative.LootBoxData(r) : [];
            }
            var extra = new Dictionary<string, object?> { ["first"] = false, ["no"] = 0L, ["num"] = 0L };
            if (method != "diff_space" && r.ReadBit() != 0)
            {
                extra = new Dictionary<string, object?>
                {
                    ["first"] = r.ReadBit() != 0,
                    ["no"] = (long)r.ReadUInt8(),
                    ["num"] = (long)r.ReadUInt8(),
                };
            }
            if (r.Position != end) throw new BitError("contents trailing bits");
            if (method == "full")
            {
                var gids = items.Select(i => ((Dictionary<string, object?>)i!)["guid"]).ToHashSet();
                if (gids.Count != items.Count) throw new BitError("duplicate full item GID");
            }
            return new Dictionary<string, object?>
            {
                ["actor"] = actor, ["method"] = method, ["items"] = items,
                ["spaces"] = spaces, ["extra"] = extra,
            };
        }
        catch (BitError exc)
        {
            throw new ContentsDecodeError(exc.Message, actor);
        }
        catch (ArgumentException exc)
        {
            throw new ContentsDecodeError(exc.Message, actor);
        }
        catch (IndexOutOfRangeException exc)
        {
            throw new ContentsDecodeError(exc.Message, actor);
        }
    }
}

/// <summary>容器内容跟踪器（Python: BoxContentsTracker）。</summary>
public sealed class BoxContentsTracker
{
    private readonly RepCatalog _catalog;
    private readonly Dictionary<int, Dictionary<string, object?>?> _meta;
    private readonly Dictionary<int, string> _fields;
    private readonly int _fieldCount;
    private readonly Dictionary<ulong, string> _paths = new();
    private readonly Dictionary<int, ulong> _channels = new();
    private readonly Dictionary<ulong, Dictionary<string, object?>> _states = new();
    private readonly Dictionary<ulong, long> _removed = new();
    private readonly bool _keepHistory;
    private readonly Dictionary<ulong, List<(long Seq, Dictionary<string, object?> State)>> _history = new();

    /// <summary>PS 协议档位（current/legacy/null）。</summary>
    public string? Profile { get; private set; }

    /// <summary>计数器（Python: Counter）。</summary>
    public Dictionary<string, int> Stats { get; } = new();

    public BoxContentsTracker(bool keepHistory = false)
    {
        var schema = BoxContents.Schema;
        _catalog = RepDec4.ParseSchemaCatalogJson(
            schema.GetProperty("controller_catalog"), "controller");
        _meta = JsonHelpers.ToObjectList(schema.GetProperty("controller_meta"))
            .Where(m => m.TryGetValue("index", out var i) && i is long)
            .ToDictionary(m => (int)Convert.ToInt64(m["index"]), m => (Dictionary<string, object?>?)m);
        _fields = schema.GetProperty("methods").EnumerateObject()
            .ToDictionary(p => int.Parse(p.Name), p => p.Value.GetString() ?? "");
        _fieldCount = schema.GetProperty("field_count").GetInt32();
        _keepHistory = keepHistory;
    }

    private void Bump(string key) => Stats[key] = Stats.TryGetValue(key, out var c) ? c + 1 : 1;

    private Dictionary<string, object?> StateFor(ulong actor)
    {
        if (!_states.TryGetValue(actor, out var state))
        {
            state = new Dictionary<string, object?>
            {
                ["seq"] = -1L, ["has_full"] = false, ["valid"] = false,
                ["items"] = new Dictionary<ulong, Dictionary<string, object?>>(),
                ["spaces"] = new List<object?>(),
                ["pending"] = new HashSet<long>(),
                ["expected"] = 0L,
            };
            _states[actor] = state;
        }
        return state;
    }

    private void Publish(ulong actor)
    {
        if (!_keepHistory) return;
        var state = _states[actor];
        if (!_history.TryGetValue(actor, out var rows))
        {
            rows = [];
            _history[actor] = rows;
        }
        var entry = (Convert.ToInt64(state["seq"]), CloneState(state));
        if (rows.Count > 0 && rows[^1].Item1 == entry.Item1) rows[^1] = entry;
        else rows.Add(entry);
    }

    private static Dictionary<string, object?> CloneState(Dictionary<string, object?> s)
    {
        var copy = new Dictionary<string, object?>(s);
        copy["items"] = new Dictionary<ulong, Dictionary<string, object?>>(
            ((Dictionary<ulong, Dictionary<string, object?>>)s["items"]!)
                .ToDictionary(kv => kv.Key, kv => new Dictionary<string, object?>(kv.Value)));
        copy["spaces"] = s["spaces"] is List<object?> sp ? sp.ToList() : new List<object?>();
        copy["pending"] = new HashSet<long>((HashSet<long>)s["pending"]!);
        return copy;
    }

    /// <summary>遗忘一个 actor（Python: forget）。</summary>
    public void Forget(ulong actor, long seq)
    {
        if (seq < (_removed.TryGetValue(actor, out var r) ? r : -1)) return;
        _removed[actor] = seq;
        if (_states.TryGetValue(actor, out var state) && seq >= Convert.ToInt64(state["seq"]))
        {
            state["seq"] = seq;
            state["valid"] = false;
            state["has_full"] = false;
            state["items"] = new Dictionary<ulong, Dictionary<string, object?>>();
            state["deleted"] = true;
            Publish(actor);
        }
    }

    /// <summary>应用一条 RPC 消息（Python: apply）。返回是否有变化。</summary>
    public bool Apply(Dictionary<string, object?> message, long seq)
    {
        var actor = (ulong)Convert.ToUInt64(message["actor"]);
        if (actor == 0 || _removed.ContainsKey(actor))
        {
            Bump("null_or_removed_target");
            return false;
        }
        var state = StateFor(actor);
        if (seq < Convert.ToInt64(state["seq"]))
        {
            Bump("stale");
            return false;
        }
        var proposed = CloneState(state);
        var method = message["method"]?.ToString();
        if (method == "full")
        {
            var newItems = new Dictionary<ulong, Dictionary<string, object?>>();
            foreach (var itemObj in (List<object?>)message["items"]!)
            {
                var item = (Dictionary<string, object?>)itemObj!;
                newItems[(ulong)Convert.ToUInt64(item["guid"])] = new Dictionary<string, object?>(item);
            }
            proposed["items"] = newItems;
            proposed["has_full"] = true;
            proposed["valid"] = true;
            proposed["pending"] = new HashSet<long>();
            proposed["expected"] = 0L;
        }
        else
        {
            var items = (Dictionary<ulong, Dictionary<string, object?>>)proposed["items"]!;
            foreach (var itemObj in (List<object?>)message["items"]!)
            {
                var item = (Dictionary<string, object?>)itemObj!;
                var gid = (ulong)Convert.ToUInt64(item["guid"]);
                var change = Convert.ToInt64(item.TryGetValue("change", out var c) ? c : 0);
                if (change == 0) items.Remove(gid);
                else if (change == 1) items[gid] = new Dictionary<string, object?>(item);
                else if (items.ContainsKey(gid)) items[gid] = new Dictionary<string, object?>(item);
                else
                {
                    // Receiver Modified 只更新已存在的 GID。没有基线就无法
                    // 虚构完整容器列表（Python 原注释）。
                    proposed["valid"] = false;
                    Bump("missing_modified_gid");
                }
            }
        }
        if (message["spaces"] is not null)
        {
            proposed["spaces"] = ((List<object?>)message["spaces"]!).ToList();
        }
        var extra = (Dictionary<string, object?>)message["extra"]!;
        var num = Convert.ToInt64(extra["num"]);
        if (num != 0)
        {
            if (Convert.ToInt64(proposed["expected"]) != num)
            {
                proposed["expected"] = num;
                proposed["pending"] = new HashSet<long>();
            }
            if (method != "full")
            {
                ((HashSet<long>)proposed["pending"]!).Add(Convert.ToInt64(extra["no"]));
            }
            var count = Convert.ToInt64(proposed["expected"]);
            var pending = (HashSet<long>)proposed["pending"]!;
            // 间隙中保留不完整标签，即使最后一段已到达。两种编号起点都能精确识别。
            var completeFrom0 = Enumerable.Range(0, (int)count).All(i => pending.Contains(i));
            var completeFrom1 = Enumerable.Range(1, (int)count).All(i => pending.Contains(i));
            if (completeFrom0 || completeFrom1)
            {
                proposed["expected"] = 0L;
                proposed["pending"] = new HashSet<long>();
            }
        }
        proposed["seq"] = seq;
        proposed["extra"] = extra;
        proposed["method"] = method;
        proposed["deleted"] = false;
        _states[actor] = proposed;
        Bump(method ?? "unknown");
        Bump("items");
        Stats["items"] += ((List<object?>)message["items"]!).Count;
        Publish(actor);
        return true;
    }

    /// <summary>消费一个 bunch（Python: consume）。</summary>
    public void Consume(BunchView bunch)
    {
        int start;
        try
        {
            var (exports, s) = ReplicationHeader.ExportsPrefix(
                bunch.Payload, bunch.PBits, bunch.Hx, bunch.Hm, bunch.Frags);
            foreach (var entry in exports)
            {
                if (entry.TryGetValue("path", out var pathObj) && pathObj is string path
                    && entry.TryGetValue("guid", out var guidObj) && guidObj is ulong guid)
                {
                    _paths[guid] = path;
                    var tail = path.Contains('.') ? path[(path.LastIndexOf('.') + 1)..] : path;
                    if (tail.StartsWith("Default__", StringComparison.Ordinal)) tail = tail[9..];
                    if (tail == "BP_SOLPlayerState_C") Profile = "current";
                    else if (tail == "BP_DFMPlayerState_C" && Profile is null) Profile = "legacy";
                }
            }
            start = s;
        }
        catch (Exception ex) when (ex is BitError or SnaBad or IndexOutOfRangeException)
        {
            Bump("framing_error");
            return;
        }
        try
        {
            if (bunch.Open)
            {
                _channels.Remove(bunch.Ch);
                var r = new BitReader(bunch.Payload, bunch.PBits) { Position = start };
                var guid = EngineBunchCodec.ReadDfmPackedInt(r).Value;
                if (bunch.Close)
                {
                    if (bunch.CloseReason == 0 && r.Position == r.BitCount)
                    {
                        Forget(guid, bunch.Seq);
                    }
                    return;
                }
                if (_removed.TryGetValue(guid, out var removedSeq) && bunch.Seq > removedSeq)
                {
                    _removed.Remove(guid);
                }
                var (info, newStart) = Sna.ParsePrefix(
                    bunch.Payload, bunch.PBits, bunch.Hx, bunch.Hm,
                    quantizeBit: true, frags: bunch.Frags);
                var arch = info.TryGetValue("arch", out var a1) ? a1 : null;
                var archPath = arch is ulong ag && _paths.TryGetValue(ag, out var p) ? p : "";
                var tail = archPath.Contains('.') ? archPath[(archPath.LastIndexOf('.') + 1)..] : archPath;
                if (tail.StartsWith("Default__", StringComparison.Ordinal)) tail = tail[9..];
                if (tail == "BP_DFMPlayerController_C")
                {
                    _channels[bunch.Ch] = (ulong)info["actor"]!;
                }
                start = newStart;
            }
            if (Profile != "current" || !_channels.ContainsKey(bunch.Ch)) return;
            var blocks = Blocks.Tile(bunch.Payload, bunch.PBits, start);
            if (blocks is null) return;
            foreach (var block in blocks)
            {
                if (block.IsActor == 0) continue;
                var position = block.PayloadStart;
                if (block.HasRep != 0)
                {
                    var (_, pos, status) = RepDec6.DecodeBlock(
                        bunch.Payload, block.PayloadStart, block.PayloadEnd,
                        _catalog, _meta, valueReader: PickupValues.BoxValue);
                    if (status is not ("exact" or "short")) continue;
                    position = pos;
                }
                var r = new BitReader(bunch.Payload, block.PayloadEnd) { Position = position };
                while (r.Position < r.BitCount)
                {
                    var field = r.ReadSerializeInt(_fieldCount);
                    var size = EngineBunchCodec.ReadDfmPackedInt(r).Value;
                    var end = r.Position + (int)size;
                    if (end > r.BitCount) throw new BitError("controller field length");
                    if (_fields.TryGetValue(field, out var method))
                    {
                        try
                        {
                            var message = BoxContents.DecodeContents(
                                bunch.Payload, r.Position, end, method);
                            Apply(message, bunch.Seq);
                        }
                        catch (BoxContents.ContentsDecodeError exc)
                        {
                            Bump("decode_error");
                            if (exc.Actor is { } ea && _states.TryGetValue(ea, out var st)
                                && bunch.Seq >= Convert.ToInt64(st["seq"]))
                            {
                                st["seq"] = bunch.Seq;
                                st["valid"] = false;
                                Publish(ea);
                            }
                        }
                    }
                    r.Position = end;
                }
            }
        }
        catch (Exception ex) when (ex is BitError or SnaBad or IndexOutOfRangeException)
        {
            Bump("framing_error");
        }
        finally
        {
            if (bunch.Close) _channels.Remove(bunch.Ch);
        }
    }

    /// <summary>容器内容描述（Python: describe）。</summary>
    public Dictionary<string, object?> Describe(
        ulong actor, IReadOnlyDictionary<string, string>? names = null, long? seq = null)
    {
        Dictionary<string, object?>? state = null;
        if (seq is not null)
        {
            var rows = _history.TryGetValue(actor, out var h) ? h : new List<(long, Dictionary<string, object?>)>();
            var lo = 0;
            var hi = rows.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (rows[mid].Item1 <= seq.Value) lo = mid + 1;
                else hi = mid;
            }
            var index = lo - 1;
            state = index >= 0 ? rows[index].Item2 : null;
        }
        else if (_states.TryGetValue(actor, out var cur))
        {
            state = cur;
        }
        if (state is null)
        {
            return new Dictionary<string, object?>
            {
                ["contents_known"] = false, ["contents"] = new List<object?>(),
                ["contents_seq"] = null, ["contents_revealed"] = false,
            };
        }
        var items = new List<object?>();
        foreach (var (gid, item) in (Dictionary<ulong, Dictionary<string, object?>>)state["items"]!)
        {
            var iid = Convert.ToInt64(item.TryGetValue("item_id", out var i) ? i : 0);
            var revealed = iid is not (0 or BoxContents.PlaceholderId);
            var quantity = Convert.ToInt64(item.TryGetValue("count", out var q) ? q : 1);
            items.Add(new Dictionary<string, object?>
            {
                ["gid"] = gid.ToString(),
                ["item_id"] = revealed ? iid.ToString() : null,
                ["name"] = revealed
                    ? (names is not null && names.TryGetValue(iid.ToString(), out var n) ? n : "未识别物品")
                    : "未搜索物品",
                ["count"] = quantity >= 0 ? quantity : null,
                ["revealed"] = revealed,
                ["attach_position"] = item.TryGetValue("attach_position", out var ap) ? ap : null,
            });
        }
        var itemList = items
            .OrderBy(i => Convert.ToInt64(((Dictionary<string, object?>)i!)["attach_position"] ?? 0))
            .ThenBy(i => ((Dictionary<string, object?>)i!)["gid"]?.ToString())
            .ToList();
        var known = Convert.ToBoolean(state["has_full"]) && Convert.ToBoolean(state["valid"])
            && Convert.ToInt64(state["expected"]) == 0
            && (!state.TryGetValue("deleted", out var del) || del is not true);
        return new Dictionary<string, object?>
        {
            ["contents_known"] = known,
            ["contents"] = itemList,
            ["contents_seq"] = state["seq"],
            ["contents_revealed"] = known && itemList.All(i =>
                Convert.ToBoolean(((Dictionary<string, object?>)i!)["revealed"])),
            ["contents_pending"] = Convert.ToInt64(state["expected"]) != 0,
        };
    }
}
