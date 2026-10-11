using System.Text.Json;
using CCRadar.Parser.Codec;

namespace CCRadar.Parser.Core;

/// <summary>保守停住异常（Python: repdec4.Opaque，继承 BitError）。</summary>
public sealed class RepOpaque : BitError
{
    public RepOpaque(string message) : base(message) { }
}

/// <summary>RepLayout 命令（catalog JSON 的 cmds 条目）。</summary>
public sealed class RepCmd
{
    public int Index { get; init; }
    public int Type { get; init; }
    /// <summary>JSON null → 闭包回退 (i+2)（Python: c["end_cmd"] or (i + 2)）。</summary>
    public int? EndCmd { get; init; }
    public int ParentIndex { get; init; }
    /// <summary>对象内偏移（current schema 的 PlayerMatchInfo/ExitState departure 映射用）。</summary>
    public int ObjectOffset { get; init; }
}

/// <summary>RepLayout catalog（一个类的解码布局）。</summary>
public sealed class RepCatalog
{
    /// <summary>类名（repdec6 的 meta 自动挂接用，Python: cat.get("class")）。</summary>
    public string? ClassName { get; init; }
    public List<RepCmd> Cmds { get; init; } = [];
    /// <summary>parents 列表（playerstate/death_box 等上层用；本层不消费）。</summary>
    public List<Dictionary<string, object?>> Parents { get; init; } = [];
    /// <summary>catalog 原始 JSON 中的其他键（class 等）。</summary>
    public Dictionary<string, object?> Extra { get; init; } = new();
}

/// <summary>属性解码事件（Python 的 ev dict，字段名对齐以便 JSON 对拍）。</summary>
public sealed class RepEvent
{
    /// <summary>数组路径 [(handle, element_index), ...]（Python: path）。</summary>
    public List<(long Handle, int Element)> Path { get; init; } = [];
    public long Handle { get; init; }
    public int Type { get; init; }
    public string? TName { get; init; }
    public object? Value { get; init; }
    public int CmdIndex { get; init; }
    public int ParentIndex { get; init; }
    /// <summary>repdec6 include_arrays 时的事件类别：array_begin / array_end。</summary>
    public string? Kind { get; init; }

    /// <summary>转 JSON 字典（键名与 Python 输出一致，供黄金对拍逐行 diff）。</summary>
    public Dictionary<string, object?> ToDictionary() 
    {
        var d = new Dictionary<string, object?>();
        if (Kind is not null) d["kind"] = Kind;
        d["path"] = Path.Select(p => (object?)new List<object?> { p.Handle, p.Element }).ToList();
        d["handle"] = Handle;
        d["type"] = Type;
        d["tname"] = TName;
        d["value"] = Value;
        d["cmd_index"] = CmdIndex;
        d["parent_index"] = ParentIndex;
        return d;
    }
}

/// <summary>
/// repdec4.py 的 C# 等价实现 —— engine-exact FRepLayout::ReceiveProperties 解码器。
///
/// 恢复自二进制（sub_EAE4550 / RepLayout_ReceiveProperties_r @0xeae8ad4）：
///   * property handle  = FArchive vtable+128 = SerializeIntPacked   (LSB continuation)
///   * dynamic ArrayNum = Serialize(&amp;u16, 2)                        (raw uint16)
///   * inner array handles are FLATTENED across elements: element i's inner cmd j
///     has handle i*K + j (K = inner cmd count); a 0 handle closes the array scope.
///   * the receiver walks every cmd in order incrementing CurrentHandle and only
///     consumes the cmd whose handle == ReadHandle.
/// </summary>
public static class RepDec4
{
    // 属性类型名表（Python: TYPE）。
    public static readonly string[] TypeNames =
    [
        "DynArray", "Return", "Property", "Bool", "Float", "Int", "Byte", "Name",
        "Object", "UInt32", "Vector", "Rotator", "Plane", "Vector100", "NetId",
        "RepMovement", "VecNormal", "Vector10", "VectorQ", "String", "UInt64", "NativeBool",
    ];

    /// <summary>位置语义类型集合（Python: POSTYPES，预留）。</summary>
    public static readonly HashSet<int> PosTypes = [10, 13, 15, 17, 18];

    // 类名别名：0904 更新 PS 类 BP_DFMPlayerState_C -> BP_SOLPlayerState_C，
    // RepLayout 布局未变（80804 流实证 15 人全解出），仅类名/NetGUID 变。
    // 新名缺 catalog 时回退旧名布局，保证新旧流量共用一份解码。
    private static readonly Dictionary<string, string> ClassAlias = new()
    {
        ["BP_SOLPlayerState_C"] = "BP_DFMPlayerState_C",
    };

    // ---- catalog 加载（Python: lru_cache(maxsize=1) → Lazy）----

    private static readonly Lazy<Dictionary<string, RepCatalog>> CatalogsLazy = new(LoadCatalogsCore);
    private static readonly Lazy<Dictionary<string, List<Dictionary<string, object?>>>> PropMetaLazy = new(LoadPropMetaCore);

    /// <summary>数据目录：约定为 程序集目录/Data（源码 JSON 拷入），可用环境变量 CCRADAR_DATA 覆盖。</summary>
    public static string DataRoot { get; } =
        Environment.GetEnvironmentVariable("CCRADAR_DATA")
        ?? Path.Combine(AppContext.BaseDirectory, "Data");

    /// <summary>加载 RepLayout catalogs（Python: load_catalogs）：replayouts_R31 + subobj_replayouts_R32，含类名别名回退。</summary>
    public static Dictionary<string, RepCatalog> LoadCatalogs() => CatalogsLazy.Value;

    /// <summary>加载属性元数据（Python: load_propmeta）：prop_meta_R36.json，含类名别名回退。</summary>
    public static Dictionary<string, List<Dictionary<string, object?>>> LoadPropmeta() => PropMetaLazy.Value;

    private static Dictionary<string, RepCatalog> LoadCatalogsCore()
    {
        var cls = new Dictionary<string, RepCatalog>();
        foreach (var file in new[] { "replayouts_R31.json", "subobj_replayouts_R32.json" })
        {
            var path = Path.Combine(DataRoot, file);
            if (!File.Exists(path)) continue;
            using var doc = JsonDocument.Parse(AssetCrypto.ReadText(path));
            if (!doc.RootElement.TryGetProperty("classes", out var classes)) continue;
            foreach (var prop in classes.EnumerateObject())
            {
                cls[prop.Name] = ParseCatalog(prop.Name, prop.Value);
            }
        }
        foreach (var (newName, oldName) in ClassAlias)
        {
            if (!cls.ContainsKey(newName) && cls.TryGetValue(oldName, out var old))
            {
                cls[newName] = old;
            }
        }
        return cls;
    }

    private static Dictionary<string, List<Dictionary<string, object?>>> LoadPropMetaCore()
    {
        var pm = new Dictionary<string, List<Dictionary<string, object?>>>();
        var path = Path.Combine(DataRoot, "prop_meta_R36.json");
        if (File.Exists(path))
        {
            using var doc = JsonDocument.Parse(AssetCrypto.ReadText(path));
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                pm[prop.Name] = JsonHelpers.ToObjectList(prop.Value);
            }
        }
        foreach (var (newName, oldName) in ClassAlias)
        {
            if (!pm.ContainsKey(newName) && pm.TryGetValue(oldName, out var old))
            {
                pm[newName] = old;
            }
        }
        return pm;
    }

    /// <summary>catalog JSON → RepCatalog（cmds 按 index 排序语义由 CmdList 保证）。</summary>
    private static RepCatalog ParseCatalog(string className, JsonElement element)
    {
        var cmds = new List<RepCmd>();
        if (element.TryGetProperty("cmds", out var cmdsEl))
        {
            foreach (var c in cmdsEl.EnumerateArray())
            {
                cmds.Add(new RepCmd
                {
                    Index = c.GetProperty("index").GetInt32(),
                    Type = c.GetProperty("type").GetInt32(),
                    EndCmd = c.TryGetProperty("end_cmd", out var ec) && ec.ValueKind == JsonValueKind.Number ? ec.GetInt32() : null,
                    ParentIndex = c.TryGetProperty("parent_index", out var pi) && pi.ValueKind == JsonValueKind.Number ? pi.GetInt32() : 0,
                    ObjectOffset = c.TryGetProperty("object_offset", out var oo) && oo.ValueKind == JsonValueKind.Number ? oo.GetInt32() : 0,
                });
            }
        }
        var parents = new List<Dictionary<string, object?>>();
        if (element.TryGetProperty("parents", out var parentsEl))
        {
            foreach (var p in parentsEl.EnumerateArray())
            {
                parents.Add(JsonHelpers.ToDictionary(p));
            }
        }
        var extra = new Dictionary<string, object?>();
        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Name is "cmds" or "parents") continue;
            extra[prop.Name] = prop.Name == "class" ? prop.Value.GetString() : JsonHelpers.ToObject(prop.Value);
        }
        return new RepCatalog
        {
            ClassName = element.TryGetProperty("class", out var clsEl) ? clsEl.GetString() : className,
            Cmds = cmds,
            Parents = parents,
            Extra = extra,
        };
    }

    /// <summary>命令按 index 升序（Python: cmd_list）。</summary>
    public static List<RepCmd> CmdList(RepCatalog catalog)
        => catalog.Cmds.OrderBy(c => c.Index).ToList();

    /// <summary>
    /// 从 schema JSON 的 catalog 元素构造 RepCatalog（box_contents / playerstate /
    /// pickup / death_box 各 schema 共用；cmds + parents 结构）。
    /// </summary>
    public static RepCatalog ParseSchemaCatalogJson(JsonElement element, string fallbackClassName)
    {
        var cmds = new List<RepCmd>();
        if (element.TryGetProperty("cmds", out var cmdsEl))
        {
            foreach (var c in cmdsEl.EnumerateArray())
            {
                cmds.Add(new RepCmd
                {
                    Index = c.GetProperty("index").GetInt32(),
                    Type = c.GetProperty("type").GetInt32(),
                    EndCmd = c.TryGetProperty("end_cmd", out var ec) && ec.ValueKind == JsonValueKind.Number ? ec.GetInt32() : null,
                    ParentIndex = c.TryGetProperty("parent_index", out var pi) && pi.ValueKind == JsonValueKind.Number ? pi.GetInt32() : 0,
                    ObjectOffset = c.TryGetProperty("object_offset", out var oo) && oo.ValueKind == JsonValueKind.Number ? oo.GetInt32() : 0,
                });
            }
        }
        var parents = new List<Dictionary<string, object?>>();
        if (element.TryGetProperty("parents", out var parentsEl))
        {
            foreach (var p in parentsEl.EnumerateArray())
            {
                parents.Add(JsonHelpers.ToDictionary(p));
            }
        }
        return new RepCatalog
        {
            ClassName = element.TryGetProperty("class", out var clsEl) ? clsEl.GetString() : fallbackClassName,
            Cmds = cmds,
            Parents = parents,
        };
    }

    // ---- 值类型读取 ----

    /// <summary>量化三元组（Python: pv）：h=SerializeInt(mb)，w=h+2，bias=1&lt;&lt;(h+1)，3×(bits(w)-bias)/s。</summary>
    public static double[] Pv(BitReader r, double s, int mb)
    {
        var h = r.ReadSerializeInt(mb);
        var w = h + 2;
        var bias = 1L << (h + 1);
        var result = new double[3];
        for (var i = 0; i < 3; i++)
        {
            result[i] = ((long)r.ReadBits(w) - bias) / s;
        }
        return result;
    }

    /// <summary>压缩旋转（Python: rot_c）：每轴 1 个非零位 + 有值时 w 位 × 360/(1&lt;&lt;w)。</summary>
    public static double[] RotC(BitReader r, int w = 8)
    {
        var u = 360.0 / (1 << w);
        var result = new double[3];
        for (var i = 0; i < 3; i++)
        {
            result[i] = r.ReadBit() != 0 ? r.ReadBits(w) * u : 0.0;
        }
        return result;
    }

    private static double[] StQuat(BitReader r) => [r.ReadFloat32(), r.ReadFloat32(), r.ReadFloat32(), r.ReadFloat32()];

    private static double[] StVec2d(BitReader r) => [r.ReadFloat32(), r.ReadFloat32()];

    /// <summary>
    /// GPFastWeaponBPClassPath::NetSerialize（Python: st_fastweapbp，0829 反汇编定案）：
    /// u8 type; type!=0 → u64 fast_id（type=1=连接级类注册 ID，type=2/3 内联武器类 ID）；
    /// type==0 → 全路径序列化（流内未观测——出现即 Opaque 保守停住，之前的 ev 保留、
    /// 仅块标记 opaque，不猜位宽）。
    /// </summary>
    public static Dictionary<string, object?> FastWeaponBp(BitReader r)
    {
        var t = r.ReadUInt8();
        if (t == 0) throw new RepOpaque("FastWeaponBPClassPath type=0");
        return new Dictionary<string, object?> { ["type"] = (long)t, ["fast_id"] = r.ReadUInt64() };
    }

    /// <summary>
    /// InvMgrRepData::NetSerialize（Python: st_invmgrrep，0825 反汇编 @0xf841a24 定版布局）：
    /// bool32 + 6×U64raw + u32 f50 + u32 N1 + N1×{u32 a, u32 b, u32 N2,
    /// N2×{u32×5, u8, u8}}。a=槽类别（跨局恒定），b=递增计数器。背包物品权威源。
    /// </summary>
    private static Dictionary<string, object?> StInvmgrrep(BitReader r)
    {
        var outDict = new Dictionary<string, object?>
        {
            ["flags"] = r.ReadUInt32(),
        };
        var u64s = new List<object?>();
        for (var i = 0; i < 6; i++) u64s.Add(r.ReadUInt64());
        outDict["u64s"] = u64s;
        outDict["f50"] = r.ReadUInt32();

        var groups = new List<object?>();
        var n1 = r.ReadUInt32();
        if (n1 > 32) throw new BitError("inventory group count");
        for (var i = 0; i < n1; i++)
        {
            var g = new Dictionary<string, object?>
            {
                ["a"] = r.ReadUInt32(),
                ["b"] = r.ReadUInt32(),
            };
            var n2 = r.ReadUInt32();
            if (n2 > 128) throw new BitError("inventory item count");
            g["n2"] = n2;
            var items = new List<object?>();
            for (var k = 0; k < n2; k++)
            {
                var item = new List<object?>
                {
                    r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(),
                    r.ReadUInt8(), r.ReadUInt8(),
                };
                items.Add(item);
            }
            g["items"] = items;
            groups.Add(g);
        }
        outDict["groups"] = groups;
        return outDict;
    }

    // 结构体反序列化器表（Python: STRUCTS）。
    private static readonly Dictionary<string, Func<BitReader, object?>> Structs = new()
    {
        ["Quat"] = StQuat,
        ["Vector2D"] = StVec2d,
        ["GPFastWeaponBPClassPath"] = FastWeaponBp,
        ["WeaponReplicateExtraData"] = WeaponNative.WeaponExtra,
        ["WeaponSkinInfo"] = WeaponNative.WeaponSkin,
        ["ModularWeaponDynamicAttributesBase"] = WeaponNative.WeaponDynamicBase,
        ["ModularWeaponDynamicAttrbuites"] = WeaponNative.WeaponDynamic,
        ["InvMgrRepData"] = StInvmgrrep,
    };

    /// <summary>通用 Property 值读取（Python: read_prop，按 prop_class 分派）。</summary>
    private static object? ReadProp(BitReader r, Dictionary<string, object?>? meta)
    {
        if (meta is null) throw new RepOpaque("Property(no meta)");
        var pc = meta.TryGetValue("prop_class", out var pcObj) ? pcObj?.ToString() : null;
        switch (pc)
        {
            case "MapProperty":
            case "SetProperty":
                // Map/Set 不在线上（Python 返回 None → JSON null）。
                return null;
            case "Int64Property":
                return r.ReadUInt64();
            case "UInt16Property":
                return (long)r.ReadBits(16);
            case "Int16Property":
            {
                var v = (long)r.ReadBits(16);
                return (v & 0x8000) != 0 ? v - 65536 : v;
            }
            case "Int8Property":
            {
                var v = r.ReadUInt8();
                return (v & 0x80) != 0 ? (long)v - 256 : (long)v;
            }
            case "StructProperty":
            {
                var structName = meta.TryGetValue("struct", out var sObj) ? sObj?.ToString() : null;
                if (structName is not null && Structs.TryGetValue(structName, out var fn)) return fn(r);
                throw new RepOpaque($"struct {structName}");
            }
            default:
                throw new RepOpaque($"propclass {pc}");
        }
    }

    /// <summary>按类型编号读值（Python: read_val）。值类型与 Python/JSON 输出语义对齐。</summary>
    public static object? ReadVal(BitReader r, int t, Dictionary<string, object?>? meta = null)
    {
        switch (t)
        {
            case 3:
            case 21:
                return r.ReadBit() != 0;
            case 4: return r.ReadFloat32();
            case 5: return (long)r.ReadInt32();
            case 6: return (long)r.ReadUInt8();
            case 7: return EngineBunchCodec.ReadSerializedFName(r).AsDictionary();
            case 8: return new Dictionary<string, object?> { ["guid"] = EngineBunchCodec.ReadDfmPackedInt(r).Value };
            case 9: return (long)r.ReadUInt32();
            case 10: return new List<object?> { r.ReadFloat32(), r.ReadFloat32(), r.ReadFloat32() };
            case 11: return RotC(r);
            case 12: return new List<object?> { r.ReadFloat32(), r.ReadFloat32(), r.ReadFloat32(), r.ReadFloat32() };
            case 13: return Pv(r, 100.0, 30);
            case 14:
            {
                var (n, _) = EngineBunchCodec.ReadDfmPackedInt(r);
                if (n > 64) throw new BitError("netid");
                var list = new List<object?>();
                for (var i = 0; i < n; i++) list.Add(r.ReadUInt8());
                return list;
            }
            case 15:
            {
                // FRepMovement：2 位物理标志 + 量化位置/旋转/速度。
                var f = r.ReadBits(2);
                var loc = Pv(r, 10.0, 24);
                var rot = RotC(r);
                var vel = Pv(r, 10.0, 24);
                return new Dictionary<string, object?>
                {
                    ["location"] = loc, ["rotation"] = rot, ["vel"] = vel,
                };
            }
            case 16:
            {
                var list = new List<object?>();
                for (var i = 0; i < 3; i++) list.Add((long)r.ReadBits(16) - 32768);
                return list;
            }
            case 17: return Pv(r, 10.0, 24);
            case 18: return Pv(r, 1.0, 20);
            case 19: return r.ReadFString();
            case 20: return r.ReadUInt64();
            case 2: return ReadProp(r, meta);
            default:
                throw new BitError($"type {t}");
        }
    }

    /// <summary>递归解码状态（Python: St，h=当前 handle；repdec6 另有 cs 校验标志）。</summary>
    public sealed class St
    {
        public long H { get; set; }
        /// <summary>repdec6 的 bDoChecksum：为真时每个 handle 后补读 32 位。</summary>
        public bool Cs { get; init; }
        public St(long h) { H = h; }
        public St(long h, bool cs) { H = h; Cs = cs; }
    }

    /// <summary>
    /// 属性遍历（Python: _props_r 的忠实移植）：
    /// 逐命令推进 CurrentHandle（cur[0]），只消费 handle == ReadHandle 的命令；
    /// 动态数组的内层 handle 跨元素展平（i*K + j），0 handle 关闭数组作用域。
    /// </summary>
    private static void PropsR(
        BitReader r, List<RepCmd> cmds, int i0, int i1, St st, long[] cur,
        List<RepEvent> ev, List<(long Handle, int Element)> path,
        Dictionary<int, Dictionary<string, object?>?> meta, int depth,
        RepValueReader? valueReader = null)
    {
        if (depth > 8) throw new BitError("depth");
        var i = i0;
        if (i >= i1) return;
        while (true)
        {
            cur[0]++;
            var c = cmds[i];
            if (st.H != cur[0])
            {
                // handle 不匹配：数组命令跳过其内部范围（闭包 end_cmd 或 i+2）。
                if (c.Type == 0)
                {
                    i = (c.EndCmd ?? (i + 2)) - 1;
                }
            }
            else
            {
                if (c.Type != 0)
                {
                    var v = valueReader is not null
                        ? valueReader(r, c.Type, meta.TryGetValue(c.Index, out var m) ? m : null)
                        : ReadVal(r, c.Type, meta.TryGetValue(c.Index, out var m2) ? m2 : null);
                    ev.Add(new RepEvent
                    {
                        Path = [.. path], Handle = cur[0], Type = c.Type,
                        TName = c.Type < TypeNames.Length ? TypeNames[c.Type] : null,
                        Value = v, CmdIndex = c.Index, ParentIndex = c.ParentIndex,
                    });
                    st.H = EngineBunchCodec.ReadDfmPackedInt(r).Value;
                }
                else
                {
                    // 动态数组：raw u16 ArrayNum + 展平内层 handle。
                    var n = r.ReadUInt16();
                    st.H = EngineBunchCodec.ReadDfmPackedInt(r).Value;
                    if (st.H != 0)
                    {
                        if (n > 2048) throw new BitError($"arraynum {n}");
                        var end = (c.EndCmd ?? (i + 2)) - 1;
                        var icur = new long[1];
                        for (var e = 0; e < n; e++)
                        {
                            var innerPath = new List<(long Handle, int Element)>(path) { (cur[0], e) };
                            PropsR(r, cmds, i + 1, end, st, icur, ev, innerPath, meta, depth + 1, valueReader);
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

    /// <summary>meta 参数：{cmd index → prop meta 或 null}（Python: meta.get(index)）。</summary>
    public delegate object? RepValueReader(BitReader r, int kind, Dictionary<string, object?>? meta);

    /// <summary>
    /// 解码一个属性块（Python: decode_block）。
    /// 返回 (事件列表, 结束位位置, 状态)：exact / short / over / opaque / error / leftover-hN。
    /// </summary>
    public static (List<RepEvent> Events, int Position, string Status) DecodeBlock(
        byte[] payload, int payloadStart, int payloadEnd,
        RepCatalog catalog, Dictionary<int, Dictionary<string, object?>?> meta)
    {
        var cmds = CmdList(catalog);
        var r = new BitReader(payload, payloadEnd) { Position = payloadStart };
        var ev = new List<RepEvent>();
        try
        {
            var (h0, _) = EngineBunchCodec.ReadDfmPackedInt(r);
            var st = new St(h0);
            PropsR(r, cmds, 0, cmds.Count, st, [0], ev, [], meta, 0);
            if (st.H != 0) return (ev, r.Position, $"leftover-h{st.H}");
        }
        catch (RepOpaque)
        {
            return (ev, r.Position, "opaque");
        }
        catch (BitError)
        {
            return (ev, r.Position, "error");
        }
        if (r.Position == payloadEnd) return (ev, r.Position, "exact");
        return (ev, r.Position, r.Position < payloadEnd ? "short" : "over");
    }
}

/// <summary>JSON 元数据转换工具（catalog/meta 解析共用）。</summary>
public static class JsonHelpers
{
    /// <summary>JSON 对象 → Dictionary（数值转 long/double，数组转 List，嵌套递归）。</summary>
    public static Dictionary<string, object?> ToDictionary(JsonElement element)
    {
        var d = new Dictionary<string, object?>();
        foreach (var prop in element.EnumerateObject())
        {
            d[prop.Name] = ToObject(prop.Value);
        }
        return d;
    }

    /// <summary>JSON 数组 → List&lt;object?&gt;。</summary>
    public static List<Dictionary<string, object?>> ToObjectList(JsonElement element)
    {
        var list = new List<Dictionary<string, object?>>();
        foreach (var item in element.EnumerateArray())
        {
            list.Add(ToDictionary(item));
        }
        return list;
    }

    /// <summary>任意 JSON 值 → CLR 对象（与 Python json.loads 的类型映射一致）。</summary>
    public static object? ToObject(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => ToDictionary(e),
        JsonValueKind.Array => e.EnumerateArray().Select(ToObject).ToList(),
        JsonValueKind.String => e.GetString(),
        // 注意：必须显式 (object?) 装箱——否则三元两分支 long/double 会被编译器
        // 提升为 double，所有整数丢失 long 语义（下游 is long 判断全部失效）。
        JsonValueKind.Number => e.TryGetInt64(out var l) ? (object?)l : (object?)e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}
