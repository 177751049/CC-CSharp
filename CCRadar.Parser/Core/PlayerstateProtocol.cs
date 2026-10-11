using System.Text.Json;
using CCRadar.Parser.Codec;

namespace CCRadar.Parser.Core;

/// <summary>
/// playerstate_protocol.py 的 C# 等价实现 —— 两个实测 profile 的命名字段级
/// PlayerState RepLayout 解码。
///
/// 类导出选择已验证的 catalog，绝不依赖数字 NetGUID 或猜测的属性 handle。
/// current SOL catalog 把 CurrentCharacterLiveStatus 从 legacy h127 移到 h130；
/// current h129 是 int32 PlayerGamePlayState，不是 bIsDeadBox。
/// 调用方剥掉 exports/NewActor 框架后传入 actor 属性的位区间。
/// </summary>
public static class PlayerstateProtocol
{
    private static readonly Dictionary<string, string> Classes = new()
    {
        ["legacy"] = "BP_DFMPlayerState_C",
        ["current"] = "BP_SOLPlayerState_C",
    };

    private static readonly Dictionary<string, string> Packages = new()
    {
        ["legacy"] = "/Game/BluePrints/GameModes/TestGame/BP_DFMPlayerState",
        ["current"] = "/Game/BluePrints/GameModes/TestGame/BP_SOLPlayerState",
    };

    /// <summary>完整字段名单（Python: FIELDS）。</summary>
    public static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "Owner", "PlayerId", "bIsABot", "PlayerNamePrivate", "Uin", "InnerTeamIndex",
        "TeamID", "Camp", "HeroId", "ExpertId", "AccountType", "PlatformID",
        "ForcesType", "TeamIdentity", "bDead", "DeadTickNum", "DeathWaitRescueTime",
        "DeathWaitRescueStartTime", "DeathWaitRescueEndTime", "bFinishGame",
        "bDeathWaitingRescueEnd", "CurrentDeathStartTime", "CurrentCharacterLiveStatus",
        "PlayerGamePlayState", "bIsDeadBox",
        "ExitState",
    };

    // 身份字段白名单 —— 只认「纯标量、且位于 PS 记录最前面」的那批。
    //
    // 为什么单独列：PS 的开局记录很长，current schema 对本地玩家自己那条
    // （实测 170: 21870 bit）会在中段布局不匹配 → status=error/leftover，整块身份
    // 被丢掉，于是自机 PS 无名、_derive 的真人候选把它排除 → 自机名字退化。
    // 但 Owner/PlayerId/PlayerNamePrivate/Uin/团队 这些字段在报错点之前就已完整解出。
    // 所以非 exact 块也导出这批字段，但只用于身份/命名：生死/生命周期仍严格只在
    // exact 时暴露——截断的块可能给出假生死，那正是原严格门要防的。
    public static readonly HashSet<string> IdentityFields = new(StringComparer.Ordinal)
    {
        "Owner", "PlayerId", "bIsABot", "PlayerNamePrivate", "Uin", "InnerTeamIndex",
        "TeamID", "Camp", "HeroId", "ExpertId",
    };

    /// <summary>PlayerMatchInfo 成员偏移表（Python: _MATCH_MEMBERS）。</summary>
    private static readonly Dictionary<int, string> MatchMembers = new()
    {
        [0] = "bMatchOver", [1] = "MatchOverReason", [4] = "MatchTime",
        [8] = "MatchTimeFromBegin", [12] = "bCanEnterScav", [13] = "bLogin",
        [16] = "AvgPing", [20] = "SDPing", [24] = "Over200msCounts",
    };

    /// <summary>
    /// 仅对实测到的类/CDO/包名精确匹配返回 legacy/current（Python: select_profile）。
    /// </summary>
    public static string? SelectProfile(object? exportPath)
    {
        if (exportPath is not string path) return null;
        foreach (var (profile, name) in Classes)
        {
            var package = Packages[profile];
            if (path is not null && (path == name || path == "Default__" + name || path == package
                || path == package + "." + name || path == package + ".Default__" + name))
            {
                return profile;
            }
        }
        return null;
    }

    // ---- schema 构造（Python: _schema，lru_cache(2) → 双缓存）----

    private static readonly Dictionary<string, (RepCatalog Catalog, Dictionary<int, Dictionary<string, object?>> Parents, Dictionary<int, Dictionary<string, object?>?> Meta)> SchemaCache = new();

    /// <summary>构造并缓存 profile 的 (catalog, parents, meta)（Python: _schema 的忠实移植）。</summary>
    public static (RepCatalog Catalog, Dictionary<int, Dictionary<string, object?>> Parents, Dictionary<int, Dictionary<string, object?>?> Meta) Schema(string profile)
    {
        if (SchemaCache.TryGetValue(profile, out var cached)) return cached;

        RepCatalog catalog;
        List<Dictionary<string, object?>> entries;
        if (profile == "legacy")
        {
            catalog = RepDec4.LoadCatalogs()[Classes[profile]];
            entries = RepDec4.LoadPropmeta().TryGetValue(Classes[profile], out var e) ? e : [];
        }
        else if (profile == "current")
        {
            var path = Path.Combine(RepDec4.DataRoot, "playerstate_schema_0904.json");
            using var doc = JsonDocument.Parse(AssetCrypto.ReadText(path));
            var className = Classes[profile];
            catalog = ParseSchemaCatalog(
                className,
                doc.RootElement.GetProperty("classes").GetProperty(className),
                parentsKey: "parents");
            entries = JsonHelpers.ToObjectList(doc.RootElement.GetProperty("meta").GetProperty(className));
        }
        else
        {
            throw new ArgumentException("unknown PlayerState profile");
        }

        var parents = new Dictionary<int, Dictionary<string, object?>>();
        foreach (var parent in catalog.Parents)
        {
            if (parent.TryGetValue("index", out var idxObj) && idxObj is long idx)
            {
                parents[(int)idx] = parent;
            }
        }
        if (Environment.GetEnvironmentVariable("CCRADAR_DEBUG") == "1")
        {
            var probeInfo = "n/a";
            if (catalog.Parents.Count > 0 && catalog.Parents[0].TryGetValue("index", out var probe))
            {
                probeInfo = probe?.GetType().Name + ":" + probe;
            }
            Console.Error.WriteLine($"[schema:{profile}] catalog.Parents={catalog.Parents.Count} parsed={parents.Count} firstIndex={probeInfo}");
        }
        var meta = new Dictionary<int, Dictionary<string, object?>?>();
        foreach (var entry in entries)
        {
            if (entry.TryGetValue("index", out var idxObj) && idxObj is long idx)
            {
                meta[(int)idx] = new Dictionary<string, object?>(entry);
            }
        }

        // Older metadata lists only generic Property commands. Parent names retain
        // the verified ByteProperty enum identities needed for role/gender widths.
        foreach (var command in catalog.Cmds)
        {
            if (!meta.TryGetValue(command.Index, out var record))
            {
                record = [];
                meta[command.Index] = record;
            }
            if (parents.TryGetValue(command.ParentIndex, out var parent))
            {
                record.TryAdd("name", parent.TryGetValue("name", out var n) ? n : null);
                var parentName = parent.TryGetValue("name", out var pn) ? pn?.ToString() : null;
                if (profile == "current" && parentName == "PlayerMatchInfo")
                {
                    var memberOffset = command.ObjectOffset - (parent.TryGetValue("object_offset", out var oo) && oo is long ool ? (int)ool : 0);
                    var member = MatchMembers.TryGetValue(memberOffset, out var m) ? m : null;
                    if (member is not null)
                    {
                        record["departure_name"] = "PlayerMatchInfo." + member;
                    }
                    if (member == "MatchOverReason")
                    {
                        record["departure_bits"] = 4L;
                        record["departure_values"] = Enumerable.Range(0, 9).Select(i => (object?)(long)i).ToList();
                    }
                }
                else if (profile == "current" && parentName == "ExitState")
                {
                    record["departure_bits"] = 3L;
                    record["departure_values"] = new List<object?> { 1L, 2L, 3L, 4L, 5L };
                }
                if (command.Type == 6 && record.TryGetValue("name", out var rn) && rn?.ToString() is "Role" or "RemoteRole")
                {
                    record["enum_max"] = 4L;
                }
                else if (command.Type == 6 && record.TryGetValue("name", out var rn2) && rn2?.ToString() == "PlayerGender")
                {
                    record["enum_max"] = profile == "legacy" ? 3L : 5L;
                }
            }
        }
        var tuple = (catalog, parents, meta);
        SchemaCache[profile] = tuple;
        return tuple;
    }

    /// <summary>schema JSON 的 catalog 解析（与 RepDec4.ParseCatalog 相同结构，parents 键名相同）。</summary>
    private static RepCatalog ParseSchemaCatalog(string className, JsonElement element, string parentsKey)
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
        if (element.TryGetProperty(parentsKey, out var parentsEl))
        {
            foreach (var p in parentsEl.EnumerateArray())
            {
                parents.Add(JsonHelpers.ToDictionary(p));
            }
        }
        return new RepCatalog { ClassName = className, Cmds = cmds, Parents = parents };
    }

    /// <summary>profile 值读取器（Python: _read_value）：departure 枚举严格门 + Role/Gender 宽度 + box_value。</summary>
    private static object? ReadValue(BitReader reader, int kind, Dictionary<string, object?>? meta)
    {
        if (meta is not null && meta.TryGetValue("departure_bits", out var dbObj) && dbObj is long departureBits)
        {
            var value = (long)reader.ReadBits((int)departureBits);
            var allowed = meta.TryGetValue("departure_values", out var dvObj) && dvObj is List<object?> vals
                ? vals.Select(v => v is long l ? l : -1L).ToHashSet()
                : [];
            if (!allowed.Contains(value)) throw new BitError("invalid PlayerState departure enum");
            return value;
        }
        if (kind == 6 && meta is not null && meta.TryGetValue("enum_max", out var emObj) && emObj is long em && em != 0)
        {
            return reader.ReadSerializeInt((int)em);
        }
        return PickupValues.BoxValue(reader, kind, meta ?? new Dictionary<string, object?>());
    }

    /// <summary>
    /// 解码一条 PlayerState 属性块（Python: decode_playerstate）。
    /// 返回 {fields, identity, status, position, profile}。
    ///
    /// fields 只在 exact 块存在：opaque/truncated/leftover 块不是完整有效快照，
    /// 不让部分解码的生命周期值外泄。identity 是更窄的门（见 IDENTITY_FIELDS）：
    /// 位于记录最前面的纯标量身份字段即使非 exact 也导出，因为命名/团队可信而
    /// 生死不可信。数组与多成员结构只走框架不展平。未知 profile 显式空结果。
    /// </summary>
    public static Dictionary<string, object?> DecodePlayerstate(byte[] payload, int start, int end, string? profile)
    {
        var result = new Dictionary<string, object?>
        {
            ["fields"] = new Dictionary<string, object?>(),
            ["identity"] = new Dictionary<string, object?>(),
            ["status"] = "unknown_profile",
            ["position"] = start,
            ["profile"] = profile,
        };
        if (profile is null || !Classes.ContainsKey(profile)) return result;
        if (!(0 <= start && start <= end && end <= payload.Length * 8))
        {
            result["status"] = "error";
            return result;
        }
        var (catalog, parents, meta) = Schema(profile);
        var (events, position, status) = RepDec6.DecodeBlock(
            payload, start, end, catalog, meta, includeArrays: false, valueReader: ReadValue);
        result["status"] = status;
        result["position"] = position;

        var identity = new Dictionary<string, object?>();
        foreach (var ev in events)
        {
            if (ev.Path.Count > 0) continue;
            if (!parents.TryGetValue(ev.ParentIndex, out var parent)) continue;
            var name = parent.TryGetValue("name", out var n) ? n?.ToString() : null;
            var cmdStart = parent.TryGetValue("cmd_start", out var cs) && cs is long csl ? csl : -1;
            var cmdEnd = parent.TryGetValue("cmd_end", out var ce) && ce is long cel ? cel : -1;
            if (name is not null && IdentityFields.Contains(name) && cmdEnd - cmdStart == 1)
            {
                identity[name] = ev.Value;
            }
        }
        result["identity"] = identity;
        if (status != "exact") return result;

        var fields = new Dictionary<string, object?>();
        foreach (var ev in events)
        {
            if (ev.Path.Count > 0) continue;
            if (!parents.TryGetValue(ev.ParentIndex, out var parent)) continue;
            var name = parent.TryGetValue("name", out var n) ? n?.ToString() : null;
            var cmdStart = parent.TryGetValue("cmd_start", out var cs) && cs is long csl ? csl : -1;
            var cmdEnd = parent.TryGetValue("cmd_end", out var ce) && ce is long cel ? cel : -1;
            string? departureName = null;
            if (meta.TryGetValue(ev.CmdIndex, out var record) && record is not null
                && record.TryGetValue("departure_name", out var dn))
            {
                departureName = dn?.ToString();
            }
            if (departureName is not null)
            {
                fields[departureName] = ev.Value;
            }
            if (name is not null && Fields.Contains(name) && cmdEnd - cmdStart == 1)
            {
                fields[name] = ev.Value;
            }
        }
        result["fields"] = fields;
        return result;
    }
}
