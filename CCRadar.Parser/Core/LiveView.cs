using System.Text.Json;
using CCRadar.Parser.Codec;

namespace CCRadar.Parser.Core;

/// <summary>
/// liveview.py 的 C# 等价实现 —— 实时增量视图（业务大脑）。
///
/// 与 Python 版同构的确定性解析：
///  - 增量重组（保持 reassembler 状态，只处理新增帧）；
///  - 只对新 LB 做 spawn 检测 / MRA 槽位解析（O(新增块)，非全量重扫）；
///  - 类 GUID 从导出路径增量累积解析（不硬编码 105/131）；
///  - join/self/ents 每次 poll 重算（O(links×slots×log)，极廉价）。
///
/// 形态适配：Python 从 IncrementalFlowLoader 的文件 flow 读；C# 宿主直接把
/// 明文包喂给 ConsumePacket（主程序实时链路）——解析语义逐 LB 一致。
/// </summary>
public sealed partial class LiveView
{
    // ---- 常量（Python 模块级常量的忠实移植，注释保留）----

    /// <summary>UE4 WORLD_MAX = 2^21 cm —— 引擎级坐标上限，不是地图边界。</summary>
    public const double WorldMax = 2097152.0;

    /// <summary>成员槽最少采样数。</summary>
    public const int MemberMinPoints = 16;

    /// <summary>槽停更隐藏门限（秒）。用真实时间戳而非 seq 差（seq rate 逐局不同）。</summary>
    public const double StaleHideSec = 300.0;

    /// <summary>上行自机位姿"陈旧"门限（seq 差；≈4.5s）。</summary>
    public const long PoseStaleSeq = 256;

    /// <summary>上行位姿 → 自机槽"无歧义"裕度（cm）。</summary>
    public const double PoseMarginCm = 150.0;

    /// <summary>PVE AI 出生点标记保留时长（秒）。</summary>
    public const double AiSpawnKeepSec = 240.0;

    /// <summary>轨迹保留上限（近期全分辨率 + 老点稀疏化）。</summary>
    public static readonly int TrackHistoryMax =
        Math.Max(1024, ParseEnvInt("ER_TRACK_HISTORY_MAX", 4096));
    public static readonly int TrackHistoryRecent =
        Math.Min(TrackHistoryMax - 256, Math.Max(768, ParseEnvInt("ER_TRACK_HISTORY_RECENT", 3072)));
    public static readonly int SeqTsMax =
        Math.Max(16384, ParseEnvInt("ER_SEQ_TS_MAX", 100000));

    /// <summary>致命部位归零 → 濒死计时 wire 上报宽限窗（秒）。</summary>
    public const double VitalGraceSec = 8.0;

    /// <summary>濒死倒计时"仍在递减"判定窗（秒）。</summary>
    public const double ImpActiveSec = 20.0;

    /// <summary>血条部位（wire 顺序）。</summary>
    public static readonly (string Key, string Wire)[] HpParts =
    [
        ("head", "HeadHealth"), ("thorax", "ThoraxHealth"), ("abdomen", "AbdomenHealth"),
        ("larm", "LeftArmHealth"), ("rarm", "RightArmHealth"),
        ("lleg", "LeftLegHealth"), ("rleg", "RightLegHealth"),
    ];

    /// <summary>部位上限默认值（495 局 wire 实测众数）。</summary>
    public static readonly IReadOnlyDictionary<string, double> HpPartMax =
        new Dictionary<string, double>
        {
            ["head"] = 40.0, ["thorax"] = 50.0, ["abdomen"] = 30.0,
            ["larm"] = 30.0, ["rarm"] = 30.0, ["lleg"] = 40.0, ["rleg"] = 30.0,
        };

    /// <summary>血量属性脏值门（0901 HP 显示不准根因：非规格化浮点）。</summary>
    public const double HpValMin = 0.01;
    public const double HpValMax = 10000.0;

    /// <summary>PVE AI 类名 → 中文兵种名。</summary>
    private static readonly (string Key, string Name)[] AiClsNames =
    [
        ("RPG", "AI·火箭兵"), ("ShielderHeavy", "AI·重盾兵"), ("Shielder", "AI·盾兵"),
        ("Firebat", "AI·火焰兵"), ("Sniper", "AI·狙击手"), ("Boss", "AI·首领"),
        ("Elite", "AI·精英"), ("Medic", "AI·医疗兵"), ("MG", "AI·机枪手"),
    ];

    /// <summary>AI 出生点保留（LOOT_PICK 系列常量）。</summary>
    public const double LootPickRadiusCm = 120.0;
    public const double LootPickVmax = 80.0;
    public const bool LootPickInfer = true;

    /// <summary>对局销毁判定：实时流超过该秒数无新帧 = 游戏已退出。</summary>
    public const double GameEndStaleSec = 20.0;

    /// <summary>PlayerState 身份 handle（Python: PS_ID_HANDLES）。</summary>
    public static readonly HashSet<int> PsIdHandles = [14, 18, 21, 27, 28, 30, 62, 63, 137, 138];

    /// <summary>PlayerState 生死 handle（Python: PS_LIFE_HANDLES）。</summary>
    public static readonly HashSet<int> PsLifeHandles = [31, 32, 33, 34, 35, 36, 37, 126, 127, 129];

    public static readonly IReadOnlyDictionary<int, string> PsLifeFields = new Dictionary<int, string>
    {
        [31] = "bDead", [32] = "DeadTickNum", [33] = "DeathWaitRescueTime",
        [34] = "DeathWaitRescueStartTime", [35] = "DeathWaitRescueEndTime",
        [36] = "bFinishGame", [37] = "bDeathWaitingRescueEnd",
        [126] = "PlayerGamePlayState", [127] = "CurrentCharacterLiveStatus",
        [129] = "bIsDeadBox",
    };

    public const int PsDeadLiveStatus = 2;
    public static readonly HashSet<long> PsAliveLiveStatuses = [1];

    /// <summary>装备族段（Python: _EQ_FAMILIES）。</summary>
    private static readonly string[] EqFamilies = ["1101", "1105", "1107", "1108", "1109"];

    private static int ParseEnvInt(string name, int defaultValue)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : defaultValue;

    private static readonly bool Debug = Environment.GetEnvironmentVariable("ER_DEBUG") == "1";

    // ---- 名字表（item_names_cn.json，Python 模块级加载逻辑等价）----

    private static readonly Lazy<Dictionary<long, string>> HeroNamesLazy = new(LoadHeroNames);
    private static readonly Lazy<Dictionary<long, string>> WeaponNamesLazy = new(LoadWeaponNames(false));
    private static readonly Lazy<Dictionary<long, string>> WeaponNamesRawLazy = new(LoadWeaponNames(true));
    private static readonly Lazy<Dictionary<string, string>> LootNamesLazy = new(LoadLootNames);
    private static readonly Lazy<Dictionary<string, Dictionary<string, object?>>> LootMetaLazy = new(LoadLootMeta);
    private static readonly Lazy<Dictionary<string, string>> GearNamesLazy = new(LoadGearNames);

    /// <summary>干员名表（HeroId → 姓名），含 override（Python: HERO_NAMES + override）。</summary>
    public static IReadOnlyDictionary<long, string> HeroNames => HeroNamesLazy.Value;
    public static IReadOnlyDictionary<long, string> WeaponNames => WeaponNamesLazy.Value;
    public static IReadOnlyDictionary<long, string> WeaponNamesRaw => WeaponNamesRawLazy.Value;
    public static IReadOnlyDictionary<string, string> LootNames => LootNamesLazy.Value;
    public static IReadOnlyDictionary<string, Dictionary<string, object?>> LootMeta => LootMetaLazy.Value;
    public static IReadOnlyDictionary<string, string> GearNames => GearNamesLazy.Value;

    private static string NameTablePath
        => Path.Combine(RepDec4.DataRoot, "item_names_cn.json");

    private static Dictionary<long, string> LoadHeroNames()
    {
        var result = new Dictionary<long, string>();
        try
        {
            if (!File.Exists(NameTablePath)) return result;
            using var doc = JsonDocument.Parse(File.ReadAllText(NameTablePath));
            var ops = doc.RootElement.GetProperty("operators");
            if (ops.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in ops.EnumerateObject())
                {
                    TryAddHero(result, prop.Value, prop.Name);
                }
            }
            else if (ops.ValueKind == JsonValueKind.Array)
            {
                foreach (var o in ops.EnumerateArray())
                {
                    TryAddHero(result, o, null);
                }
            }
        }
        catch
        {
            // Python: except Exception: HERO_NAMES = {}
        }
        // 名表覆盖（Python: HERO_NAME_OVERRIDE —— 88000000051「旅人」/36「佐娅」）。
        result[88000000051] = "旅人";
        result[88000000036] = "佐娅";
        return result;
    }

    private static void TryAddHero(Dictionary<long, string> result, JsonElement o, string? key)
    {
        long? hid = null;
        string? nm = null;
        if (o.ValueKind == JsonValueKind.Object)
        {
            if (o.TryGetProperty("hero_id", out var hEl) && hEl.TryGetInt64(out var hl)) hid = hl;
            if (hid is null && key is not null && long.TryParse(key, out var kl)) hid = kl;
            if (o.TryGetProperty("Name", out var nEl) && nEl.ValueKind == JsonValueKind.String) nm = nEl.GetString();
            nm ??= o.TryGetProperty("name", out var n2El) && n2El.ValueKind == JsonValueKind.String ? n2El.GetString() : null;
        }
        if (hid is not null && hid != 0 && !string.IsNullOrEmpty(nm))
        {
            result[hid.Value] = nm;
        }
    }

    private static Dictionary<long, string> LoadWeaponNames(bool raw)
    {
        var result = new Dictionary<long, string>();
        try
        {
            if (!File.Exists(NameTablePath)) return result;
            using var doc = JsonDocument.Parse(File.ReadAllText(NameTablePath));
            var root = doc.RootElement;
            // items 段：11 位 ID（18/21 段）+ 名字黑名单过滤（raw 模式全收）。
            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in items.EnumerateObject())
                {
                    var k = prop.Name;
                    if (k.Length != 11 || (k[..2] != "18" && k[..2] != "21")) continue;
                    if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                    if (!prop.Value.TryGetProperty("name", out var nEl) || nEl.ValueKind != JsonValueKind.String) continue;
                    var nm = nEl.GetString();
                    if (string.IsNullOrEmpty(nm)) continue;
                    if (!long.TryParse(k, out var id)) continue;
                    if (raw)
                    {
                        result[id] = nm;
                        continue;
                    }
                    if (nm.Length < 2 || nm.StartsWith('_') || nm.StartsWith("（废弃）")
                        || nm.StartsWith("（停用") || nm.StartsWith("的")
                        || nm.Contains("虚空") || nm.Contains("枪管") || nm.Contains("护木")
                        || nm.Contains("枪托") || nm.Contains("握把") || nm.Contains("弹匣")
                        || nm.Contains("消音") || nm.Contains("瞄具") || nm.Contains("导轨")
                        || nm.Contains("枪口") || nm.Contains("下挂") || nm.Contains("机匣")
                        || nm.Contains("节套") || nm.Contains("击败") || nm.Contains("外观")
                        || nm.Contains("沿用") || nm.Contains("是一款"))
                    {
                        continue;
                    }
                    result[id] = nm;
                }
            }
            // weapon_names 段（v15 规范名称，优先级更高——覆盖 items 段）。
            if (!raw && root.TryGetProperty("weapon_names", out var canonical)
                && canonical.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in canonical.EnumerateObject())
                {
                    var k = prop.Name;
                    if (k.Length == 11 && k.All(char.IsAsciiDigit)
                        && (k[..2] == "18" || k[..2] == "21")
                        && prop.Value.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(prop.Value.GetString()))
                    {
                        if (long.TryParse(k, out var id)) result[id] = prop.Value.GetString()!.Trim();
                    }
                }
            }
        }
        catch
        {
            result.Clear();
        }
        return result;
    }

    private static Dictionary<string, string> LoadLootNames()
    {
        var result = new Dictionary<string, string>();
        try
        {
            if (!File.Exists(NameTablePath)) return result;
            using var doc = JsonDocument.Parse(File.ReadAllText(NameTablePath));
            var items = doc.RootElement.GetProperty("items");
            if (items.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in items.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Object
                        && prop.Value.TryGetProperty("name", out var nEl)
                        && nEl.ValueKind == JsonValueKind.String)
                    {
                        result[prop.Name] = nEl.GetString()!;
                    }
                }
            }
        }
        catch
        {
            result.Clear();
        }
        return result;
    }

    private static Dictionary<string, Dictionary<string, object?>> LoadLootMeta()
    {
        var result = new Dictionary<string, Dictionary<string, object?>>();
        try
        {
            if (!File.Exists(NameTablePath)) return result;
            using var doc = JsonDocument.Parse(File.ReadAllText(NameTablePath));
            var items = doc.RootElement.GetProperty("items");
            if (items.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in items.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                    if (!prop.Value.TryGetProperty("grade", out var gEl) || gEl.ValueKind != JsonValueKind.Number) continue;
                    var entry = new Dictionary<string, object?>
                    {
                        ["grade"] = gEl.GetInt32(),
                        ["dgjz"] = prop.Value.TryGetProperty("dgjz", out var dEl) ? JsonHelpers.ToObject(dEl) : null,
                        ["price"] = prop.Value.TryGetProperty("price", out var pEl) ? JsonHelpers.ToObject(pEl) : null,
                    };
                    result[prop.Name] = entry;
                }
            }
        }
        catch
        {
            result.Clear();
        }
        return result;
    }

    private static Dictionary<string, string> LoadGearNames()
    {
        var result = new Dictionary<string, string>();
        try
        {
            if (!File.Exists(NameTablePath)) return result;
            using var doc = JsonDocument.Parse(File.ReadAllText(NameTablePath));
            var gear = doc.RootElement.GetProperty("gear_names");
            if (gear.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in gear.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        result[prop.Name] = prop.Value.GetString()!;
                    }
                }
            }
        }
        catch
        {
            result.Clear();
        }
        return result;
    }

    // ---- 模块级辅助函数（Python liveview.py 顶层函数的等价）----

    /// <summary>坐标是否可用（Python: WORLD）：非空、有限、非占位原点、UE4 上限内。</summary>
    public static bool World(object? p)
    {
        // 坐标容器的动态形态：List<object?> / double[] / List<double> 都可能是
        // 槽位轨迹/位姿的载体（Python 列表的 C# 多形态），统一非泛型读取。
        if (p is not System.Collections.IEnumerable e || p is string) return false;
        var vals = e.Cast<object?>().Take(3).ToList();
        if (vals.Count < 3) return false;
        double x, y, z;
        try
        {
            x = Convert.ToDouble(vals[0]);
            y = Convert.ToDouble(vals[1]);
            z = Convert.ToDouble(vals[2]);
        }
        catch
        {
            return false;
        }
        if (x == 0.0 && y == 0.0 && z == 0.0) return false;   // (0,0,0) = 未设置占位
        foreach (var c in new[] { x, y, z })
        {
            if (double.IsNaN(c) || double.IsInfinity(c)) return false;
            if (c < -WorldMax || c > WorldMax) return false;
        }
        return true;
    }

    /// <summary>GPAttributeSetHealth 数值是否可信（Python: health_val_ok 脏值门）。</summary>
    public static bool HealthValOk(string name, object? v)
    {
        if (!name.EndsWith("Health", StringComparison.Ordinal)) return true;
        if (v is not (long or double) || v is bool) return false;
        var d = Convert.ToDouble(v);
        if (double.IsNaN(d) || double.IsInfinity(d)) return false;
        return d == 0.0 || (HpValMin <= d && d <= HpValMax);
    }

    /// <summary>双 float 属性取 CurrentValue（Python: current_health_event）。</summary>
    public static bool CurrentHealthEvent(RepEvent ev, RepCatalog catalog)
    {
        RepCmd? parentCmd = null;
        Dictionary<string, object?>? parent = null;
        foreach (var p in catalog.Parents)
        {
            if (p.TryGetValue("index", out var i) && i is long idx && idx == ev.ParentIndex)
            {
                parent = p;
                break;
            }
        }
        if (parent is null) return false;
        var cmdStart = parent.TryGetValue("cmd_start", out var cs) ? Convert.ToInt64(cs) : -1;
        var cmdEnd = parent.TryGetValue("cmd_end", out var ce) ? Convert.ToInt64(ce) : -1;
        var members = catalog.Cmds.Where(c => c.Index >= cmdStart && c.Index < cmdEnd).ToList();
        return members.Count == 2 && members.All(c => c.Type == 4)
               && ev.CmdIndex == members.Max(c => c.Index);
    }

    /// <summary>AI 角色类名 → 展示名（Python: ai_cls_name）；未命中兵种表统一「AI·士兵」。</summary>
    public static string? AiClsName(string? cls)
    {
        if (string.IsNullOrEmpty(cls)) return null;
        var s = cls.Contains('.') ? cls[(cls.LastIndexOf('.') + 1)..] : cls;
        if (s.StartsWith("Default__", StringComparison.Ordinal)) s = s[9..];
        var low = s.ToLowerInvariant();
        foreach (var (key, nm) in AiClsNames)
        {
            if (low.Contains(key.ToLowerInvariant())) return nm;
        }
        return low.Contains("ai") ? "AI·士兵" : null;
    }

    /// <summary>伪真人 bot 确认（Python: confirmed_player_bot）——无已知干员身份才确认。</summary>
    public static bool ConfirmedPlayerBot(Dictionary<string, object?>? player, bool matchedHero = false)
    {
        if (matchedHero) return false;
        if (player is null) return false;
        if (!player.TryGetValue("bot", out var bObj) || bObj is not true) return false;
        var pid = player.TryGetValue("player_id", out var p) ? p : null;
        var uin = player.TryGetValue("uin", out var u) ? u : null;
        var camp = player.TryGetValue("camp", out var c) ? c : null;
        var signals = 0;
        try { if (Convert.ToInt64(pid) >= 8000) signals++; } catch { }
        if (pid is not null && uin is not null && pid.ToString()?.Trim() == uin.ToString()?.Trim()) signals++;
        try { if (Convert.ToInt64(camp) == 106) signals++; } catch { }
        return signals >= 2;
    }

    /// <summary>停驻拾取候选回放全量版（Python: mark_picked_by_standing）。</summary>
    public static int MarkPickedByStanding(
        IReadOnlyDictionary<ulong, Dictionary<string, object?>> loot,
        IReadOnlyDictionary<int, List<SlotSample>> slots)
    {
        if (!LootPickInfer) return 0;
        var n = 0;
        foreach (var (a, lr) in loot)
        {
            if ((lr.TryGetValue("pickup_candidate_seq", out var pcs) && pcs is not null)
                || (lr.TryGetValue("gone_seq", out var gs) && gs is not null))
            {
                continue;
            }
            var loc = lr["loc"] as List<object?>;
            if (loc is null || loc.Count < 2) continue;
            var lx = Convert.ToDouble(loc[0]);
            var ly = Convert.ToDouble(loc[1]);
            var spawn = Convert.ToInt64(lr["seq"]);
            long? best = null;
            int? bestSlot = null;
            foreach (var (slot, tr) in slots)
            {
                for (var i = 1; i < tr.Count; i++)
                {
                    var q = tr[i];
                    if (q.Seq < spawn) continue;
                    var dx = q.X - lx;
                    var dy = q.Y - ly;
                    if (dx * dx + dy * dy > LootPickRadiusCm * LootPickRadiusCm) continue;
                    var pp = tr[i - 1];
                    var dt = q.Ts - pp.Ts;
                    if (dt <= 0) continue;
                    var v = Math.Sqrt((q.X - pp.X) * (q.X - pp.X)
                        + (q.Y - pp.Y) * (q.Y - pp.Y)
                        + (q.Z - pp.Z) * (q.Z - pp.Z)) / dt;
                    if (v >= LootPickVmax) continue;
                    if (best is null || q.Seq < best)
                    {
                        best = q.Seq;
                        bestSlot = slot;
                    }
                    break;   // 轨迹按 seq 递增，首个命中即本轨迹最早
                }
            }
            if (best is not null)
            {
                lr["pickup_candidate_seq"] = best;
                lr["pickup_candidate_slot"] = bestSlot;
                lr["pickup_confidence"] = "candidate";
                n++;
            }
        }
        return n;
    }

    /// <summary>物资展示字段（Python: loot_display_fields）——实时与游标回放共用。</summary>
    public static Dictionary<string, object?> LootDisplayFields(IReadOnlyDictionary<string, object?> record)
    {
        object? count = record.TryGetValue("stack_count", out var sc) ? sc : null;
        if (count is null && record.TryGetValue("inventory_info", out var iiObj)
            && iiObj is Dictionary<string, object?> ii)
        {
            count = ii.TryGetValue("count", out var c2) ? c2 : null;
        }
        if (count is bool || count is not long || (long)count < 0) count = null;
        var gid = record.TryGetValue("inventory_gid", out var g) ? g : null;
        var itemIdStr = record.TryGetValue("item_id", out var i2) ? i2?.ToString() : null;
        var meta = itemIdStr is not null && LootMeta.TryGetValue(itemIdStr, out var m)
            ? m : new Dictionary<string, object?>();
        return new Dictionary<string, object?>
        {
            ["item_id"] = record.TryGetValue("item_id", out var i3) ? i3 : null,
            ["grade"] = record.TryGetValue("grade", out var gr) ? gr : null,
            ["price"] = record.TryGetValue("price", out var pr) ? pr : null,
            ["price_per_slot"] = meta.TryGetValue("dgjz", out var dg) ? dg : null,
            ["stack_count"] = count,
            ["inventory_gid"] = gid is not null ? gid.ToString() : null,
            ["updated_seq"] = record.TryGetValue("update_seq", out var us) ? us
                : record.TryGetValue("seq", out var sq) ? sq : null,
            ["item_state"] = record.TryGetValue("item_state", out var ist) ? ist : null,
            ["source_type"] = record.TryGetValue("source_type", out var stt) ? stt : null,
            ["channel_state"] = record.TryGetValue("channel_state", out var cst) ? cst : null,
            ["pickup_status"] = record.TryGetValue("pickup_confidence", out var pc) ? pc : "visible",
            ["pickup_candidate_seq"] = record.TryGetValue("pickup_candidate_seq", out var pcs2) ? pcs2 : null,
        };
    }

    /// <summary>
    /// 扫 Pickup actor 属性块里的物资 ID FString（Python: _scan_pickup_item_id）。
    /// FString 绝对位随物资类型浮动，必须任意偏移扫描；命中即返回。
    /// </summary>
    public static string? ScanPickupItemId(byte[] payload, int bitStart, int bitEnd, int lo = 40, int hi = 640)
    {
        var n = bitEnd - bitStart;
        var end = Math.Min(n, hi);
        for (var off = lo; off < end - 48; off++)
        {
            var ln = (int)BitPrimitives.ReadBitsAt(payload, bitStart + off, 32);
            if (ln is < 8 or > 20) continue;
            var baseBit = bitStart + off + 32;
            if (baseBit + ln * 8 > bitStart + n) continue;
            var chars = new char[ln - 1];
            var ok = true;
            for (var k = 0; k < ln; k++)
            {
                var by = (int)BitPrimitives.ReadBitsAt(payload, baseBit + k * 8, 8);
                if (k < ln - 1)
                {
                    if (by is < 48 or > 57) { ok = false; break; }
                    chars[k] = (char)by;
                }
                else if (by != 0) { ok = false; break; }
            }
            if (ok && chars.Length >= 7) return new string(chars);
        }
        return null;
    }

    /// <summary>GPFightStateComponent + FSM 状态 → 前端展示 (label, key)（Python: fight_status）。</summary>
    public static (string? Label, string? Key) FightStatus(
        IReadOnlyDictionary<string, object?>? fight, IEnumerable<object?>? fsmStates = null)
    {
        if (fight is null || fight.Count == 0) return (null, null);
        long? ftype = null;
        if (fight.TryGetValue("CurrentFightStateType", out var cst))
        {
            try { ftype = Convert.ToInt64(cst); } catch { ftype = null; }
        }
        // FSM 死亡(43)/濒死(42) 最高优先。
        var states = new HashSet<long>(
            (fsmStates ?? Enumerable.Empty<object?>())
            .Select(v => { try { return Convert.ToInt64(v); } catch { return -1L; } })
            .Where(v => v >= 0));
        if (states.Contains(43)) return ("死亡", "dead");
        if (states.Contains(42)) return ("倒地", "down");
        if (ftype == 5) return ("倒地", "down");
        if (ftype == 6) return ("救援", "revive");
        return (null, null);
    }

    /// <summary>11 位防具物品 ID → 等级 1~6（Python: armor_level）。</summary>
    public static int ArmorLevel(object? itemId)
    {
        try
        {
            var s = Convert.ToInt64(itemId).ToString();
            if (s.Length == 11 && s.StartsWith("110", StringComparison.Ordinal))
            {
                if (int.TryParse(s.Substring(6, 2), out var lv) && lv is >= 1 and <= 6) return lv;
                return 0;
            }
            return 0;
        }
        catch
        {
            return 0;
        }
    }
}
