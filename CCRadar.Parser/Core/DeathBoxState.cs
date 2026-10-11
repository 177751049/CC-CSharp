using System.Text;
using System.Text.Json;
using CCRadar.Parser.Codec;

namespace CCRadar.Parser.Core;

/// <summary>
/// death_box_state.py 的 C# 等价实现 —— 死亡盒 actor 生命周期与已验证复制属性。
/// 默认解码器只读已确立的 AActor 前缀；专用属性需要注入类特定解码器；
/// 未知 handle 上报而非赋予猜测含义。休眠不是移除（Python 原注释）。
/// </summary>
public static class DeathBoxState
{
    /// <summary>
    /// 读 AActor 已知字段直到第一个未知字段，绝不扫描 handle
    /// （Python: actor_prefix）。movement_reader 必须具备该类已验证的量化与
    /// 物理标志；通用 repdec4 移动读取器刻意不作为默认。
    /// </summary>
    public static Dictionary<string, object?> ActorPrefix(
        byte[] payload, int payloadStart, int payloadEnd,
        Func<BitReader, object?>? movementReader = null)
    {
        var r = new BitReader(payload, payloadEnd) { Position = payloadStart };
        var fields = new Dictionary<string, object?>();
        try
        {
            var checksum = r.ReadBit();

            long Handle()
            {
                var h = EngineBunchCodec.ReadDfmPackedInt(r).Value;
                if (checksum != 0) r.ReadBits(32);
                return h;
            }

            var h = Handle();
            var previous = 0L;
            while (h != 0)
            {
                if (h <= previous) throw new BitError("nonmonotonic actor handle");
                previous = h;
                if (h is >= 1 and <= 5)
                {
                    var name = h switch { 1 => "bHidden", 2 => "bNetActive", 3 => "bReplicateMovement", 4 => "bTearOff", _ => "bCanBeDamaged" };
                    fields[name] = r.ReadBit() != 0;
                }
                else if (h is 6 or 15)
                {
                    fields[h == 6 ? "RemoteRole" : "Role"] = r.ReadSerializeInt(4);
                }
                else if (h == 7)
                {
                    if (movementReader is null)
                    {
                        return new Dictionary<string, object?>
                        {
                            ["fields"] = fields, ["status"] = "movement_unknown",
                            ["position"] = r.Position, ["next_handle"] = h,
                        };
                    }
                    fields["ReplicatedMovement"] = movementReader(r);
                }
                else if (h is 8 or 13 or 14 or 16)
                {
                    var name = h switch { 8 => "AttachParent", 13 => "AttachComponent", 14 => "Owner", _ => "Instigator" };
                    fields[name] = EngineBunchCodec.ReadDfmPackedInt(r).Value;
                }
                else if (h is 9 or 10)
                {
                    fields[h == 9 ? "LocationOffset" : "RelativeScale3D"] = RepDec4.Pv(r, 100.0, 30);
                }
                else if (h == 11)
                {
                    fields["RotationOffset"] = RepDec4.RotC(r);
                }
                else if (h == 12)
                {
                    fields["AttachSocket"] = EngineBunchCodec.ReadSerializedFName(r).AsDictionary();
                }
                else
                {
                    return new Dictionary<string, object?>
                    {
                        ["fields"] = fields, ["status"] = "prefix",
                        ["position"] = r.Position, ["next_handle"] = h,
                    };
                }
                h = Handle();
            }
            return new Dictionary<string, object?>
            {
                ["fields"] = fields,
                ["status"] = r.Position == r.BitCount ? "exact" : "tail",
                ["position"] = r.Position, ["next_handle"] = 0L,
            };
        }
        catch (BitError)
        {
            // 畸形前缀不能部分覆盖之前的有效绑定（Python 原注释）。
            return new Dictionary<string, object?>
            {
                ["fields"] = new Dictionary<string, object?>(),
                ["status"] = "error", ["position"] = r.Position,
            };
        }
    }

    /// <summary>路径格式化（(handle,elem) 对序列 → 稳定字符串键）。</summary>
    internal static string FormatPath(IReadOnlyList<(long Handle, int Elem)> path)
        => string.Join(";", path.Select(t => $"{t.Handle},{t.Elem}"));

    /// <summary>GUID 提取（Python: _guid）：dict 取 guid 键，否则整型化。</summary>
    internal static ulong? GuidOf(object? value)
    {
        if (value is Dictionary<string, object?> d)
        {
            value = d.TryGetValue("guid", out var g) ? g : null;
        }
        return value is null ? null : Convert.ToUInt64(value);
    }

    /// <summary>
    /// 三点位置检查（Python: _point）。Python 判定是鸭子类型：
    /// isinstance(value, (list, tuple)) 且 len==3 且全是有限数值。
    /// C# 坐标载体多形态（List&lt;object?&gt; / List&lt;double&gt; / double[]——
    /// Sna.ParsePrefix 的 info["loc"] 是 double[]），必须统一按非泛型可枚举读取。
    /// </summary>
    internal static bool IsPoint(object? value)
    {
        if (value is not System.Collections.IEnumerable e || value is string) return false;
        var vals = e.Cast<object?>().Take(4).ToList();
        if (vals.Count != 3) return false;
        foreach (var v in vals)
        {
            // Python: isinstance(v, (int, float))——bool 不是合法坐标。
            if (v is not (long or int or double or float) || v is bool) return false;
            if (!double.IsFinite(Convert.ToDouble(v))) return false;
        }
        return true;
    }

    /// <summary>
    /// 三点位置 → List&lt;double&gt;（不可转换返回 null）。
    /// 配合 IsPoint 使用，消化 List&lt;object?&gt;/List&lt;double&gt;/double[] 多形态。
    /// </summary>
    internal static List<double>? PointAsList(object? value)
    {
        if (!IsPoint(value)) return null;
        return ((System.Collections.IEnumerable)value!).Cast<object?>()
            .Select(Convert.ToDouble).ToList();
    }

    /// <summary>偏移向量全零判定（Python: all(v == 0 for v in offset)）。</summary>
    internal static bool AllZero(object? value)
    {
        if (value is not System.Collections.IEnumerable e || value is string) return false;
        try
        {
            return e.Cast<object?>().All(v => v is (long or int or double or float)
                                              && Convert.ToDouble(v) == 0.0);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// JSON 安全的死亡盒信息（Python: box_entity），实时与回放视图共用。
    /// </summary>
    public static Dictionary<string, object?> BoxEntity(
        ulong actor, IReadOnlyDictionary<string, object?> record, List<double> position,
        ulong? owner = null, string? name = null, List<double>? origin = null,
        IReadOnlyDictionary<int, string>? aiNames = null)
    {
        var replicatedName = record.TryGetValue("player_name", out var pn) ? pn?.ToString() ?? "" : "";
        var aiKey = AiNameTable.ExtractRefKey(Encoding.ASCII.GetBytes(replicatedName));
        bool? isAi = record.TryGetValue("is_ai", out var iaObj) ? iaObj as bool? : null;
        if (aiKey is not null)
        {
            isAi = true;
            name = (aiNames is not null && aiNames.TryGetValue(aiKey.Value, out var an) ? an : null) ?? "人机死亡盒";
        }
        else if (replicatedName.Length > 0
                 && !replicatedName.Contains("LOCTABLE", StringComparison.Ordinal)
                 && !replicatedName.Contains("/Game/", StringComparison.Ordinal))
        {
            name = replicatedName;
        }
        var carried = record.TryGetValue("carried", out var cd) && cd is true;
        var searchers = record.TryGetValue("searchers", out var se) && se is List<object?> sel && sel.Count > 0;
        var looters = record.TryGetValue("looters", out var lo) && lo is List<object?> lol && lol.Count > 0;
        var empty = record.TryGetValue("empty", out var em) && em is true;
        var looted = record.TryGetValue("looted", out var ld) && ld is true;
        var status = carried ? "box_carried"
            : searchers || looters ? "box_searching"
            : empty ? "box_empty"
            : looted ? "box_looted" : null;
        var label = status switch
        {
            "box_carried" => "搬运中", "box_searching" => "搜刮中",
            "box_empty" => "已搜空", "box_looted" => "已搜刮", _ => null,
        };
        var properties = record.TryGetValue("properties", out var propsObj) && propsObj is Dictionary<string, object?> props
            ? props : new Dictionary<string, object?>();
        var invGid = properties.TryGetValue("InventoryGID", out var ig) && ig is not null ? ig.ToString() : null;
        var deceasedUin = record.TryGetValue("deceased_uin", out var du) && du is not null ? du.ToString() : null;
        return new Dictionary<string, object?>
        {
            ["key"] = "box-" + actor, ["eid"] = actor, ["ch"] = actor, ["slot"] = null,
            ["kind"] = "box", ["name"] = string.IsNullOrEmpty(name) ? "死亡盒" : name,
            ["world"] = position.ToList(), ["xyz"] = position.ToList(),
            ["status"] = label, ["status_key"] = status,
            ["rel"] = origin is not null ? position.Zip(origin).Select(p => p.First - p.Second).ToList() : null,
            ["yaw"] = null, ["trail"] = null, ["owner_actor"] = owner,
            ["owner_dead"] = null,
            ["owner_binding"] = record.ContainsKey("deceased_actor") ? "dead_body_character"
                : owner is not null ? "actor_owner" : null,
            ["box_status"] = record.TryGetValue("box_status", out var bs) ? bs : null,
            ["empty"] = record.TryGetValue("empty", out var e2) ? e2 : null,
            ["looted"] = record.TryGetValue("looted", out var l2) ? l2 : null,
            ["carried"] = record.TryGetValue("carried", out var c2) ? c2 : null,
            ["is_ai"] = isAi,
            ["searching_count"] = record.TryGetValue("searchers", out var s2) && s2 is List<object?> sl ? sl.Count : 0,
            ["looting_count"] = record.TryGetValue("looters", out var l3) && l3 is List<object?> ll ? ll.Count : 0,
            ["contents_known"] = record.TryGetValue("contents_known", out var ck) && ck is true,
            ["inventory_gid"] = invGid,
            ["deceased_uin"] = deceasedUin,
            ["decode_status"] = record.TryGetValue("decode_status", out var ds) ? ds : null,
            ["location_source"] = record.TryGetValue("location_source", out var ls) ? ls : null,
            ["box_seq"] = record.TryGetValue("update_seq", out var us) ? us : null,
        };
    }
}

/// <summary>
/// 死亡盒专用属性解码器（Python: death_box_protocol.DeathBoxDecoder）。
/// 一个连接拥有自己的 profile；共享缓存只含不可变 catalog。
/// </summary>
public sealed class DeathBoxDecoder
{
    private static readonly Lazy<Dictionary<string, RepCatalog>> CurrentLazy = new(LoadSchema("death_box_schema_0904.json"));
    private static readonly Lazy<Dictionary<string, RepCatalog>> LegacyLazy = new(LoadSchema("death_box_schema_legacy.json"));

    private static Dictionary<string, RepCatalog> LoadSchema(string file)
    {
        using var doc = JsonDocument.Parse(AssetCrypto.ReadText(Path.Combine(RepDec4.DataRoot, file)));
        var result = new Dictionary<string, RepCatalog>();
        foreach (var prop in doc.RootElement.GetProperty("classes").EnumerateObject())
        {
            result[prop.Name] = RepDec4.ParseSchemaCatalogJson(prop.Value, prop.Name);
        }
        return result;
    }

    /// <summary>当前档位（Python: self.profile）。</summary>
    public string? Profile { get; private set; }

    private static (RepCatalog Cat, Dictionary<int, Dictionary<string, object?>?> Meta) ClassOf(
        Dictionary<string, RepCatalog> schema, string name, string profile)
    {
        var cat = schema.TryGetValue(name, out var c) ? c : new RepCatalog { ClassName = name };
        var cache = profile == "current" ? DeathBoxMetaCache.Instance.Current : DeathBoxMetaCache.Instance.Legacy;
        var meta = cache.TryGetValue(cat.ClassName ?? name, out var m) ? m : [];
        return (cat, meta);
    }

    private sealed class DeathBoxMetaCache
    {
        public static readonly DeathBoxMetaCache Instance = new();
        public Dictionary<string, Dictionary<int, Dictionary<string, object?>?>> Current { get; }
        public Dictionary<string, Dictionary<int, Dictionary<string, object?>?>> Legacy { get; }

        public DeathBoxMetaCache()
        {
            Current = LoadMeta("death_box_schema_0904.json");
            Legacy = LoadMeta("death_box_schema_legacy.json");
        }

        private static Dictionary<string, Dictionary<int, Dictionary<string, object?>?>> LoadMeta(string file)
        {
            using var doc = JsonDocument.Parse(AssetCrypto.ReadText(Path.Combine(RepDec4.DataRoot, file)));
            var result = new Dictionary<string, Dictionary<int, Dictionary<string, object?>?>>();
            foreach (var clsProp in doc.RootElement.GetProperty("classes").EnumerateObject())
            {
                var meta = new Dictionary<int, Dictionary<string, object?>?>();
                if (clsProp.Value.TryGetProperty("meta", out var metaEl))
                {
                    foreach (var m in JsonHelpers.ToObjectList(metaEl))
                    {
                        if (m.TryGetValue("index", out var i) && i is long idx)
                        {
                            meta[(int)idx] = m;
                        }
                    }
                }
                result[clsProp.Name] = meta;
            }
            return result;
        }
    }

    /// <summary>观察 exports 更新 profile（Python: observe_exports）。</summary>
    public void ObserveExports(IEnumerable<Dictionary<string, object?>> exports)
    {
        foreach (var entry in exports)
        {
            var path = entry.TryGetValue("path", out var p) ? p?.ToString() : null;
            if (path is "Default__BP_SOLPlayerState_C" or "BP_SOLPlayerState_C")
            {
                Profile = "current";
            }
            else if (path is "Default__BP_DFMPlayerState_C" or "BP_DFMPlayerState_C")
            {
                Profile = "legacy";
            }
        }
    }

    private Dictionary<string, object?> Decode(
        byte[] payload, int payloadStart, int payloadEnd, string name, string profile)
    {
        var (cat, meta) = ClassOf(
            profile == "current" ? CurrentLazy.Value : LegacyLazy.Value, name, profile);
        if (cat.Cmds.Count == 0)
        {
            return new Dictionary<string, object?>
            {
                ["fields"] = new Dictionary<string, object?>(),
                ["status"] = "unknown_class", ["profile"] = profile,
            };
        }
        var (events, position, status) = RepDec6.DecodeBlock(
            payload, payloadStart, payloadEnd, cat, meta,
            includeArrays: true, valueReader: PickupValues.BoxValue);
        var output = new Dictionary<string, object?>
        {
            ["fields"] = new Dictionary<string, object?>(),
            ["arrays"] = new List<object?>(),
            ["status"] = status, ["profile"] = profile, ["position"] = position,
        };
        if (status != "exact") return output;
        var fields = (Dictionary<string, object?>)output["fields"]!;
        var arrays = (List<object?>)output["arrays"]!;
        foreach (var ev in events)
        {
            if (!TryFieldPath(cat, profile, ev.CmdIndex, out var fieldName)) continue;
            if (ev.Kind is not null || ev.Path.Count > 0)
            {
                var d = ev.ToDictionary();
                d["name"] = fieldName;
                arrays.Add(d);
            }
            else
            {
                fields[fieldName] = ev.Value;
                if (DeathBoxProtocolAliases.TryGetValue(fieldName, out var alias))
                {
                    fields[alias] = ev.Value;
                }
            }
        }
        return output;
    }

    private static bool TryFieldPath(RepCatalog cat, string profile, int cmdIndex, out string fieldName)
    {
        // meta[cmd_index]['field_path'] 用 '.' 连接（Python: '.'.join(record['field_path'])）。
        fieldName = "";
        var cache = profile == "current" ? DeathBoxMetaCache.Instance.Current : DeathBoxMetaCache.Instance.Legacy;
        var meta = cache.TryGetValue(cat.ClassName ?? "", out var m2) ? m2 : [];
        if (meta.TryGetValue(cmdIndex, out var record) && record is not null
            && record.TryGetValue("field_path", out var fp) && fp is List<object?> parts)
        {
            fieldName = string.Join(".", parts.Select(p => p?.ToString() ?? ""));
            return true;
        }
        return false;
    }

    /// <summary>别名表（Python: ALIASES）。</summary>
    private static readonly Dictionary<string, string> DeathBoxProtocolAliases = new()
    {
        ["AttachmentReplication.AttachParent"] = "AttachParent",
        ["AttachmentReplication.AttachComponent"] = "AttachComponent",
        ["AttachmentReplication.LocationOffset"] = "LocationOffset",
        ["AttachmentReplication.RotationOffset"] = "RotationOffset",
        ["DeadBodyBoxInfo.DeadBodyCharacterOwner"] = "DeadBodyCharacterOwner",
        ["DeadBodyBoxInfo.CharacterUin"] = "CharacterUin",
        ["DeadBodyBoxInfo.CarryBoxCharacterUin"] = "CarryBoxCharacterUin",
        ["DeadBodyBoxInfo.DeadBodyBoxStatus"] = "DeadBodyBoxStatus",
    };

    /// <summary>按类名解码（Python: __call__——profile 未定时双档尝试消歧）。</summary>
    public Dictionary<string, object?> DecodeClass(
        byte[] payload, int payloadStart, int payloadEnd, string className)
    {
        var name = className.Contains('.') ? className[(className.LastIndexOf('.') + 1)..] : className;
        if (name.StartsWith("Default__", StringComparison.Ordinal)) name = name[9..];
        if (Profile is not null)
        {
            return Decode(payload, payloadStart, payloadEnd, name, Profile);
        }
        var candidates = new[] { "legacy", "current" }
            .Select(p => Decode(payload, payloadStart, payloadEnd, name, p))
            .ToList();
        var exact = candidates.Where(c => c["status"]?.ToString() == "exact").ToList();
        if (exact.Count == 1)
        {
            Profile = exact[0]["profile"]?.ToString();
            return exact[0];
        }
        if (exact.Count == 2)
        {
            var f0 = (Dictionary<string, object?>)exact[0]["fields"]!;
            var f1 = (Dictionary<string, object?>)exact[1]["fields"]!;
            var a0 = (List<object?>)exact[0]["arrays"]!;
            var a1 = (List<object?>)exact[1]["arrays"]!;
            if (f0.Count == f1.Count && f0.All(kv => f1.TryGetValue(kv.Key, out var v) && Equals(v, kv.Value))
                && a0.Count == 0 && a1.Count == 0)
            {
                var common = new Dictionary<string, object?>(exact[0]) { ["profile"] = "common" };
                return common;
            }
        }
        return new Dictionary<string, object?>
        {
            ["fields"] = new Dictionary<string, object?>(),
            ["arrays"] = new List<object?>(),
            ["status"] = exact.Count > 0 ? "ambiguous_profile" : "unresolved_profile",
        };
    }
}

/// <summary>死亡盒生命周期跟踪器（Python: DeathBoxTracker）。</summary>
public sealed class DeathBoxTracker
{
    private readonly HashSet<ulong> _boxArchs;
    private readonly HashSet<ulong> _carryArchs;
    private readonly DeathBoxDecoder _decodeProperties;
    private readonly Dictionary<ulong, string> _paths = new();
    private readonly Dictionary<int, ulong> _channels = new();
    private readonly Dictionary<int, long> _channelSeq = new();
    private readonly Dictionary<ulong, Dictionary<string, object?>> _records = new();
    private readonly Dictionary<ulong, Dictionary<string, object?>> _active = new();
    private readonly Dictionary<ulong, Dictionary<string, object?>> _removed = new();
    private readonly Dictionary<ulong, long> _tombstones = new();
    private readonly bool _keepHistory;
    private readonly Dictionary<ulong, List<(long Seq, Dictionary<string, object?> Record)>> _history = new();

    /// <summary>计数器（Python: Counter）。</summary>
    public Dictionary<string, int> Stats { get; } = new();

    /// <summary>当前活跃死亡盒（Python: self.active）。</summary>
    public IReadOnlyDictionary<ulong, Dictionary<string, object?>> Active => _active;

    /// <summary>通道 → actor 快照（Python: self.channels 的只读暴露）。</summary>
    public IReadOnlyDictionary<int, ulong> Channels2 => _channels;

    /// <summary>已移除死亡盒数（Python: len(self.removed)）。</summary>
    public int RemovedCount => _removed.Count;

    public DeathBoxTracker(
        IEnumerable<ulong>? boxArchs = null, IEnumerable<ulong>? carryArchs = null,
        bool keepHistory = false, DeathBoxDecoder? decodeProperties = null)
    {
        _boxArchs = boxArchs is not null ? new HashSet<ulong>(boxArchs) : [];
        _carryArchs = carryArchs is not null ? new HashSet<ulong>(carryArchs) : [];
        _decodeProperties = decodeProperties ?? new DeathBoxDecoder();
        _keepHistory = keepHistory;
    }

    private void Bump(string key) => Stats[key] = Stats.TryGetValue(key, out var c) ? c + 1 : 1;

    private static Dictionary<string, object?> CloneRecord(Dictionary<string, object?> rec) => new(rec);

    private void Publish(ulong actor)
    {
        var rec = _records[actor];
        if (rec.TryGetValue("gone_seq", out var gone) && gone is not null)
        {
            _active.Remove(actor);
            _removed[actor] = rec;
        }
        else if (DeathBoxState.IsPoint(rec.TryGetValue("loc", out var loc) ? loc : null))
        {
            _removed.Remove(actor);
            _active[actor] = rec;
        }
        if (_keepHistory)
        {
            if (!_history.TryGetValue(actor, out var rows))
            {
                rows = [];
                _history[actor] = rows;
            }
            var value = (Convert.ToInt64(rec["update_seq"]), CloneRecord(rec));
            if (rows.Count > 0 && rows[^1].Item1 == value.Item1) rows[^1] = value;
            else rows.Add(value);
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
        return lo - 1 >= 0 ? rows[lo - 1].Item2 : null;
    }

    /// <summary>打开一个死亡盒/搬运盒 actor（Python: open_actor）。</summary>
    public bool OpenActor(ulong actor, int channel, long seq, string kind,
        object? loc, string className = "")
    {
        if (seq < (_channelSeq.TryGetValue(channel, out var cs) ? cs : -1)) return false;
        _channelSeq[channel] = seq;
        _channels.Remove(channel);
        if (seq <= (_tombstones.TryGetValue(actor, out var ts) ? ts : -1)) return false;
        if (_records.TryGetValue(actor, out var rec) && seq < Convert.ToInt64(rec["update_seq"]))
        {
            return false;
        }
        if (rec is null || (rec.TryGetValue("gone_seq", out var gs) && gs is not null))
        {
            var generation = rec is not null && rec.TryGetValue("generation", out var g) ? Convert.ToInt64(g) + 1 : 1;
            // Python: 'spawn_loc': list(loc) if _point(loc) else None——loc 可能是
            // double[]（ParsePrefix 原生形态），统一经 PointAsList 消化。
            var spawnLoc = DeathBoxState.PointAsList(loc);
            rec = new Dictionary<string, object?>
            {
                ["actor"] = actor, ["spawn_seq"] = seq,
                ["spawn_loc"] = spawnLoc is not null ? spawnLoc.Cast<object?>().ToList() : null,
                ["generation"] = generation, ["properties"] = new Dictionary<string, object?>(),
                ["arrays"] = new Dictionary<string, Dictionary<string, object?>>(),
                ["contents_known"] = false,
            };
            _records[actor] = rec;
        }
        _channels[channel] = actor;
        rec["seq"] = seq;
        rec["update_seq"] = seq;
        rec["lifecycle_seq"] = seq;
        rec["kind"] = kind;
        rec["class_name"] = className;
        rec["channel_state"] = "open";
        rec["gone_seq"] = null;
        rec["gone_reason"] = null;
        if (DeathBoxState.PointAsList(loc) is { } locList)
        {
            rec["loc"] = locList;
            rec["location_source"] = "spawn";
        }
        Publish(actor);
        return true;
    }

    /// <summary>移除（Python: remove）。reason 0 写墓碑。</summary>
    public void Remove(ulong actor, long seq, long reason, string evidence)
    {
        if (reason == 0)
        {
            _tombstones[actor] = Math.Max(seq, _tombstones.TryGetValue(actor, out var t) ? t : -1);
        }
        if (!_records.TryGetValue(actor, out var rec) || seq < Convert.ToInt64(rec["update_seq"]))
        {
            return;
        }
        rec["gone_seq"] = seq;
        rec["gone_reason"] = reason;
        rec["update_seq"] = seq;
        rec["lifecycle_seq"] = seq;
        rec["removal_evidence"] = evidence;
        foreach (var (channel, value) in _channels.Where(kv => kv.Value == actor).ToList())
        {
            _channels.Remove(channel);
            _channelSeq[channel] = Math.Max(seq, _channelSeq.TryGetValue(channel, out var cs) ? cs : -1);
        }
        Bump(evidence);
        Publish(actor);
    }

    /// <summary>通道关闭（Python: close_channel）。</summary>
    public void CloseChannel(int channel, long seq, int? reason)
    {
        if (seq < (_channelSeq.TryGetValue(channel, out var cs) ? cs : -1)) return;
        _channelSeq[channel] = seq;
        if (!_channels.Remove(channel, out var actor)) return;
        if (!_records.TryGetValue(actor, out var rec) || seq < Convert.ToInt64(rec["update_seq"]))
        {
            return;
        }
        if (reason is 0 or 2 or 4)
        {
            Remove(actor, seq, reason.Value, reason switch { 0 => "destroyed", 2 => "level_unloaded", _ => "tear_off" });
        }
        else
        {
            rec["update_seq"] = seq;
            rec["lifecycle_seq"] = seq;
            rec["channel_state"] = reason switch { 1 => "dormant", 3 => "irrelevant", _ => "closed" };
            Publish(actor);
        }
    }

    /// <summary>
    /// 接受来自已验证解码器的命名字段，包括零值清除（Python: apply_properties）。
    /// Owner 是网络所有者引用；专用解码器可提供 DeadBodyCharacterOwner 与
    /// OwnerPlayerState 作为显式身份字段。空/已搜/隐藏是保留状态，不推断 actor 销毁。
    /// </summary>
    public void ApplyProperties(ulong actor, long seq,
        IReadOnlyDictionary<string, object?> fields,
        string status = "exact", IEnumerable<Dictionary<string, object?>>? arrays = null)
    {
        if (!_records.TryGetValue(actor, out var rec)
            || (rec.TryGetValue("gone_seq", out var gone) && gone is not null)
            || seq < Convert.ToInt64(rec["update_seq"]))
        {
            return;
        }
        if (status == "error") return;
        var properties = (Dictionary<string, object?>)rec["properties"]!;
        foreach (var (k, v) in fields) properties[k] = v;
        var arraysDict = (Dictionary<string, Dictionary<string, object?>>)rec["arrays"]!;
        foreach (var ev in arrays ?? Enumerable.Empty<Dictionary<string, object?>>())
        {
            // path 元素 = (handle, element) 对（RepEvent.Path 的 JSON 形态）。
            var path = (ev.TryGetValue("path", out var p) && p is List<object?> pl
                ? pl.Select(v => v is List<object?> pair && pair.Count == 2
                    ? (Handle: Convert.ToInt64(pair[0]), Elem: (int)Convert.ToInt64(pair[1]))
                    : default((long Handle, int Elem))).Where(t => t != default).ToList()
                : new List<(long Handle, int Elem)>());
            var cmdIndex = ev.TryGetValue("cmd_index", out var ci) ? Convert.ToInt64(ci) : 0;
            var kind = ev.TryGetValue("kind", out var kd) ? kd?.ToString() : null;
            if (kind == "array_begin")
            {
                // 数组作用域键 = (cmd_index, 打开时的父路径)；Python: key=(cmd_index, path)。
                var parentKey = DeathBoxState.FormatPath(path);
                var key = $"{cmdIndex}|{parentKey}";
                var length = Convert.ToInt64(ev["value"]);
                if (!arraysDict.TryGetValue(key, out var st))
                {
                    st = new Dictionary<string, object?>
                    {
                        ["name"] = ev.TryGetValue("name", out var n) ? n : null,
                        ["handle"] = ev.TryGetValue("handle", out var hd) ? hd : null,
                        ["items"] = new Dictionary<long, Dictionary<string, object?>>(),
                    };
                    arraysDict[key] = st;
                }
                st["length"] = length;
                st["items"] = new Dictionary<long, Dictionary<string, object?>>(
                    ((Dictionary<long, Dictionary<string, object?>>)st["items"]!)
                        .Where(kv => kv.Key < length).ToDictionary(kv => kv.Key, kv => kv.Value));
            }
            else if (kind is null && path.Count > 0)
            {
                // 匹配最近的已打开数组作用域；一个 actor 块是原子的。
                // 条件：array_key 的父路径 == 当前 path[:-1]，且数组 handle == path[-1][0]。
                var parentKey = DeathBoxState.FormatPath(path[..^1]);
                var lastHandle = path[^1].Handle;
                var lastElem = path[^1].Elem;
                foreach (var (arrayKey, st2) in arraysDict.Reverse())
                {
                    var parts = arrayKey.Split('|');
                    if (parts.Length != 2) continue;
                    if (parts[0] != cmdIndex.ToString() || parts[1] != parentKey) continue;
                    if (Convert.ToInt64(st2["handle"]!) != lastHandle) continue;
                    var items = (Dictionary<long, Dictionary<string, object?>>)st2["items"]!;
                    if (!items.TryGetValue(lastElem, out var slot))
                    {
                        slot = [];
                        items[lastElem] = slot;
                    }
                    slot[ev.TryGetValue("name", out var n2) ? n2?.ToString() ?? "" : ""] = ev["value"];
                    break;
                }
            }
        }
        foreach (var st3 in arraysDict.Values)
        {
            var name3 = st3.TryGetValue("name", out var n3) ? n3?.ToString() : null;
            if (name3 is "CurrentSearchingPlayers" or "CurrentLootingPlayers")
            {
                var values = ((Dictionary<long, Dictionary<string, object?>>)st3["items"]!)
                    .OrderBy(kv => kv.Key).Where(kv => kv.Value.Count > 0)
                    .Select(kv => kv.Value.Values.First()).ToList();
                rec[name3 == "CurrentSearchingPlayers" ? "searchers" : "looters"] = values;
            }
        }
        var refs = new Dictionary<string, string>
        {
            ["Owner"] = "owner_actor",
            ["AttachParent"] = "attach_parent",
            ["AttachComponent"] = "attach_component",
            ["DeadBodyCharacterOwner"] = "deceased_actor",
            ["OwnerPlayerState"] = "owner_playerstate",
            ["RelatedXXBody"] = "related_body",
        };
        foreach (var (name, target) in refs)
        {
            if (fields.TryGetValue(name, out var v))
            {
                rec[target] = DeathBoxState.GuidOf(v);
            }
        }
        var scalar = new Dictionary<string, string>
        {
            ["CharacterUin"] = "deceased_uin",
            ["CarryBoxCharacterUin"] = "carrier_uin",
            ["PlayerName_Buffer"] = "player_name",
            ["DeadBodyBoxStatus"] = "box_status",
            ["bIsEmpty"] = "empty",
            ["bLooted"] = "looted",
            ["bHidden"] = "hidden",
            ["bIsCarrySelfBySomeOne"] = "carried",
            ["bIsAI"] = "is_ai",
        };
        foreach (var (name, target) in scalar)
        {
            if (fields.TryGetValue(name, out var v))
            {
                rec[target] = v;
            }
        }
        if (fields.TryGetValue("ReplicatedMovement", out var mv) && mv is Dictionary<string, object?> mvDict
            && DeathBoxState.PointAsList(mvDict.TryGetValue("location", out var lc) ? lc : null) is { } mvLoc)
        {
            rec["loc"] = mvLoc;
            rec["location_source"] = "replicated_movement";
        }
        rec["update_seq"] = seq;
        rec["decode_status"] = status;
        rec["location_offset"] = properties.TryGetValue("LocationOffset", out var lo2) ? lo2
            : properties.TryGetValue("AttachmentReplication.LocationOffset", out var lo3) ? lo3 : null;
        rec["kill_info"] = properties.Where(kv => kv.Key.StartsWith("KillInfo.", StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key.Split('.', 2)[1], kv => kv.Value);
        Publish(actor);
    }

    /// <summary>
    /// 解析复制的挂接边（Python: world_position）；所有权绝不作为搬运者使用。
    /// </summary>
    public List<double>? WorldPosition(
        ulong actor, IReadOnlyDictionary<ulong, List<double>>? characterPositions = null,
        long? atSeq = null, HashSet<ulong>? seen = null)
    {
        seen ??= [];
        if (!seen.Add(actor)) return null;
        var rec = atSeq is not null ? At(actor, atSeq.Value)
            : _active.TryGetValue(actor, out var a) ? a : null;
        if (rec is null || (rec.TryGetValue("gone_seq", out var gone) && gone is not null)) return null;
        var parent = rec.TryGetValue("attach_parent", out var ap) && ap is ulong apU ? apU : (ulong?)null;
        List<double>? position = parent is not null && characterPositions is not null
            && characterPositions.TryGetValue(parent.Value, out var p) ? p : null;
        if (position is null && parent is not null)
        {
            position = WorldPosition(parent.Value, characterPositions, atSeq, seen);
        }
        var offset = rec.TryGetValue("location_offset", out var off) ? off : null;
        // Python: offset is None or all(v == 0 for v in offset)——offset 是
        // LocationOffset 属性（Pv 输出，double[] 多形态），统一非泛型零值判定。
        if (position is not null && (offset is null || DeathBoxState.AllZero(offset)))
        {
            return position.ToList();
        }
        // 非零插槽/旋转偏移需要其组件变换；保留显式复制的世界点而不是猜测。
        return DeathBoxState.PointAsList(rec.TryGetValue("loc", out var l) ? l : null);
    }

    /// <summary>消费一个 bunch（Python: consume）。</summary>
    public void Consume(BunchView x)
    {
        int start;
        try
        {
            var (exports, s) = ReplicationHeader.ExportsPrefix(
                x.Payload, x.PBits, x.Hx, x.Hm, x.Frags);
            _decodeProperties.ObserveExports(exports);
            foreach (var value in exports)
            {
                var path = value.TryGetValue("path", out var p) ? p?.ToString() : null;
                if (path is null) continue;
                if (value.TryGetValue("guid", out var gObj) && gObj is ulong guid)
                {
                    _paths[guid] = path;
                    var name = path.Contains('.') ? path[(path.LastIndexOf('.') + 1)..] : path;
                    if (name.StartsWith("Default__", StringComparison.Ordinal)) name = name[9..];
                    if (name == "BP_Inventory_DeadBody_C") _boxArchs.Add(guid);
                    else if (name == "BP_Inventory_CarryBody_C") _carryArchs.Add(guid);
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
            if (x.Seq < (_channelSeq.TryGetValue(x.Ch, out var cs) ? cs : -1)) return;
            if (x.Open)
            {
                _channels.Remove(x.Ch);
                _channelSeq[x.Ch] = x.Seq;
                if (x.Close)
                {
                    if (x.CloseReason == 0)
                    {
                        var r0 = new BitReader(x.Payload, x.PBits) { Position = start };
                        var actor0 = EngineBunchCodec.ReadDfmPackedInt(r0).Value;
                        if (r0.Position == r0.BitCount)
                        {
                            Remove(actor0, x.Seq, 0, "destruction_info");
                        }
                    }
                    return;
                }
                var (info, newStart) = Sna.ParsePrefix(
                    x.Payload, x.PBits, x.Hx, x.Hm, quantizeBit: true, frags: x.Frags);
                start = newStart;
                var arch = info.TryGetValue("arch", out var a1) ? a1 : null;
                if (arch is not ulong ag || (!_boxArchs.Contains(ag) && !_carryArchs.Contains(ag)))
                {
                    return;
                }
                var actor = info.TryGetValue("actor", out var a2) && a2 is ulong au ? au : 0UL;
                if (!OpenActor(actor, x.Ch, x.Seq,
                        _boxArchs.Contains(ag) ? "box" : "carry",
                        info.TryGetValue("loc", out var loc) ? loc : null,
                        _paths.TryGetValue(ag, out var cls) ? cls : ""))
                {
                    return;
                }
            }
            var actorCh = _channels.TryGetValue(x.Ch, out var ch) ? ch : (ulong?)null;
            if (actorCh is null) return;
            var blocks = Blocks.Tile(x.Payload, x.PBits, start);
            if (blocks is null) return;
            foreach (var block in blocks)
            {
                if (block.HasRep != 0 && block.IsActor != 0)
                {
                    var className = _records.TryGetValue(actorCh.Value, out var r)
                        ? r.TryGetValue("class_name", out var cn) ? cn?.ToString() ?? "" : "" : "";
                    var decoded = _decodeProperties.DecodeClass(
                        x.Payload, block.PayloadStart, block.PayloadEnd, className);
                    var status = decoded["status"]?.ToString() ?? "";
                    Bump("layout_" + status);
                    var fieldsDict = decoded.TryGetValue("fields", out var f) && f is Dictionary<string, object?> fd
                        ? fd : new Dictionary<string, object?>();
                    var arraysList = decoded.TryGetValue("arrays", out var arr) && arr is List<object?> al
                        ? al.Select(i => (Dictionary<string, object?>)i!) : Enumerable.Empty<Dictionary<string, object?>>();
                    ApplyProperties(actorCh.Value, x.Seq, fieldsDict,
                        status: status, arrays: arraysList);
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
            }
        }
    }
}
