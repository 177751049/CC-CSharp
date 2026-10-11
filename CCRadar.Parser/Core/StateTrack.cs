using CCRadar.Parser.Codec;

namespace CCRadar.Parser.Core;

/// <summary>角色轨迹采样（Python slots 值元组 (seq, ts, x, y, z, yaw)）。</summary>
public readonly record struct SlotSample(long Seq, double Ts, double X, double Y, double Z, double? Yaw);

/// <summary>死亡盒采样（Python boxes 值元组 (seq, [x,y,z])）。</summary>
public readonly record struct DeathBoxSample(long Seq, double X, double Y, double Z);

/// <summary>死亡盒关联结果（Python 值元组 (box_seq, dist_cm, [x,y,z])）。</summary>
public readonly record struct DeathCorrelation(long BoxSeq, double DistCm, List<double> Loc);

/// <summary>
/// state_track.py 的 C# 等价实现 —— 角色状态 / 死亡 / 消失的确定性逆向提取。
///
/// 逆向结论（flow 495 实证 + UE4.26 客户端源码，Python 原注释保留）：
/// 1. MRA presence 是「移动驱动」的 —— 蹲点不动的活人也会长时间静默
///    （实测 gap 最长 23510 seq ≈ 7 分钟）。静默时长**不能**判定角色消失。
/// 2. 角色「消失」的权威信号 = 角色自身通道 close bunch 的 close_reason：
///    0=Destroyed、1=Dormancy（保留）、2=LevelUnloaded、3=Relevancy、4=TearOff。
///    close_reason ∈ {0,2,3,4} → 消失；==1 或无 close → 保留。
/// 3. 死亡盒 BP_Inventory_DeadBody_C 的 spawn 是死亡盒的权威位置。
/// 4. 角色状态（FSM/战斗状态）来自 DFMCharacterFSM_Main_C / GPFightStateComponent。
/// </summary>
public static class StateTrack
{
    /// <summary>死亡盒与 slot 最终位置的匹配半径（cm）。实测 0~231cm，取 300 覆盖。</summary>
    public const double DeathMatchCm = 300.0;

    /// <summary>角色消失的 close_reason 集合（1=Dormancy 保留，排除）。</summary>
    public static readonly HashSet<long> GoneCloseReasons = [0, 2, 3, 4];

    /// <summary>「死亡/销毁」类 close_reason（点永久消失）。Relevancy(3) 不在内。</summary>
    public static readonly HashSet<long> DeadCloseReasons = [0, 4];

    /// <summary>EFSMCharacterState 枚举 → 中文显示名（64 值顺序 = 枚举 ordinal）。</summary>
    public static readonly IReadOnlyDictionary<int, string> FsmStateNames = new Dictionary<int, string>
    {
        [0] = "无", [1] = "瞄准", [2] = "探身", [3] = "换弹", [4] = "开火", [5] = "检视武器",
        [6] = "架枪", [7] = "卧倒", [8] = "蹲伏", [9] = "站立", [10] = "冲刺",
        [11] = "滑铲", [12] = "特殊移动", [13] = "游泳", [14] = "冲刺游泳", [15] = "标记",
        [16] = "搜物资", [17] = "背尸体", [18] = "放下尸体", [19] = "救援", [20] = "被救援",
        [21] = "交互", [22] = "精确交互", [23] = "延迟交互", [24] = "游戏操作", [25] = "技能",
        [26] = "双手技能", [27] = "左手技能", [28] = "右手技能", [29] = "腿部技能", [30] = "特殊技能",
        [31] = "动作技能", [32] = "自定义技能1", [33] = "自定义技能2", [34] = "自定义技能3", [35] = "载具",
        [36] = "场景武器", [37] = "使用物品", [38] = "灭火", [39] = "检视高价值物品", [40] = "头盔",
        [41] = "受击", [42] = "濒死", [43] = "死亡", [44] = "过场", [45] = "暗杀",
        [46] = "被暗杀", [47] = "单手手势", [48] = "双手手势", [49] = "表情", [50] = "喷涂",
        [51] = "左臂动画", [52] = "潜水", [53] = "占旗", [54] = "被动移动", [55] = "等待开始",
        [56] = "退出游戏", [57] = "受控", [58] = "冻结", [59] = "地震摔倒", [60] = "推倒",
        [61] = "麻醉摔倒", [62] = "交互表情", [63] = "拖拽救援",
    };

    /// <summary>未知 FSM 值兜底。</summary>
    public const string FsmStateUnknown = "?";

    /// <summary>wire 类尾名 → 目录类名（subobj_replayouts_R32.json 的 key）。</summary>
    public static readonly IReadOnlyDictionary<string, string> Tail2Cat = new Dictionary<string, string>
    {
        ["GPAttributeSetHealth"] = "GPAttributeSetHealth",
        ["GPAttributeSetSpeed"] = "GPAttributeSetSpeed",
        ["GPAttributeSetOther"] = "GPAttributeSetOther",
        ["HealthComp"] = "SOLHealthDataComponent",
        ["FightStateComp"] = "GPFightStateComponent",
        ["DFMCharacterFSM_Main_C"] = "DFMCharacterFSM_Main_C",
        ["AttributeCenter"] = "DFMCharacterAttributeCenterComponent",
        ["GPRagDollPhysicsComponentKinematicHip"] = "GPRagDollPhysicsComponentKinematicHip",
        // 护甲装备 + 武器组件（6870 局实证：导出路径只带叶子名）。
        ["CharacterEquip"] = "SOLCharacterEquipComponent",
        ["WeaponReplicate"] = "WeaponReplicateComponent",
        // 背包/物品管理器（RepItemArray 携带完整 11 位 item id + 实例 GUID）。
        ["InventoryManager"] = "InventoryManager",
        // 多武器管理器（WeaponIDList/WeaponIdentityList/WeaponSlot = 武器 ID 权威源）。
        ["WeaponManager"] = "MultiWeaponManagerComponent",
        // 角色时装组件（0904 起 currentCharacterHeroID 从 PS h137 移到这里复制）。
        ["CharacterFashionComponent"] = "DFMCharacterFashionComponent",
    };

    /// <summary>
    /// 每 slot 距最后一次采样的 seq 差（Python: slot_staleness）。
    /// 信息字段（蹲点活人也会静默），**不用于移除判定**——移除由 close_reason 驱动。
    /// </summary>
    public static Dictionary<int, long?> SlotStaleness(
        IReadOnlyDictionary<int, List<SlotSample>> slots, long curSeq)
    {
        var output = new Dictionary<int, long?>();
        foreach (var (s, tr) in slots)
        {
            output[s] = tr.Count == 0 ? null : Math.Max(0, curSeq - tr[^1].Seq);
        }
        return output;
    }

    /// <summary>
    /// 死亡盒关联（Python: correlate_death_boxes，带指纹缓存）。
    /// 匹配规则两级：① 盒子晚于最终采样；② 冻结回退——盒子之后全部采样
    /// 都贴着盒子（≤match_cm）同样判死；一旦有采样远离盒子即拒绝。
    /// </summary>
    public static Dictionary<int, DeathCorrelation> CorrelateDeathBoxes(
        IReadOnlyList<DeathBoxSample> boxes,
        IReadOnlyDictionary<int, List<SlotSample>> slots,
        double matchCm = DeathMatchCm,
        Dictionary<string, Dictionary<int, DeathCorrelation>>? cache = null)
    {
        if (cache is null)
        {
            return CorrelateDeathBoxesUncached(boxes, slots, matchCm);
        }
        // 指纹：(slot, 轨迹长度, 最后 seq) + (boxes 数, match_cm)。
        var keyParts = new List<string>();
        foreach (var (s, tr) in slots)
        {
            keyParts.Add($"{s}:{tr.Count}:{(tr.Count > 0 ? tr[^1].Seq : -1)}");
        }
        keyParts.Sort();
        keyParts.Add($"-1:{boxes.Count}:{matchCm}");
        var key = string.Join("|", keyParts);
        if (cache.TryGetValue(key, out var hit)) return hit;
        var result = CorrelateDeathBoxesUncached(boxes, slots, matchCm);
        cache[key] = result;
        // 防膨胀：指纹无界增长时丢弃最旧一半（Python: len > 256 丢一半）。
        if (cache.Count > 256)
        {
            foreach (var k in cache.Keys.Take(128).ToList())
            {
                cache.Remove(k);
            }
        }
        return result;
    }

    /// <summary>无缓存实现（Python: _correlate_death_boxes_uncached，语义完全一致）。</summary>
    public static Dictionary<int, DeathCorrelation> CorrelateDeathBoxesUncached(
        IReadOnlyList<DeathBoxSample> boxes,
        IReadOnlyDictionary<int, List<SlotSample>> slots,
        double matchCm = DeathMatchCm)
    {
        var dead = new Dictionary<int, DeathCorrelation>();
        foreach (var (s, tr) in slots)
        {
            if (tr.Count == 0) continue;
            var last = tr[^1];
            long? best = null;
            var bd = matchCm;
            double bx = 0, by = 0, bz = 0;
            foreach (var box in boxes)
            {
                if (box.Seq >= last.Seq)
                {
                    var d = Math.Sqrt((last.X - box.X) * (last.X - box.X) + (last.Y - box.Y) * (last.Y - box.Y));
                    if (d < bd) { bd = d; best = box.Seq; bx = box.X; by = box.Y; bz = box.Z; }
                    continue;
                }
                // 盒子早于最终采样：检查其后是否全程贴盒（冻结）。
                var moved = false;
                foreach (var samp in tr)
                {
                    if (samp.Seq <= box.Seq) continue;
                    var dd = Math.Sqrt((samp.X - box.X) * (samp.X - box.X) + (samp.Y - box.Y) * (samp.Y - box.Y));
                    if (dd > matchCm) { moved = true; break; }
                }
                if (moved) continue;
                var d2 = Math.Sqrt((last.X - box.X) * (last.X - box.X) + (last.Y - box.Y) * (last.Y - box.Y));
                if (d2 < bd) { bd = d2; best = box.Seq; bx = box.X; by = box.Y; bz = box.Z; }
            }
            if (best is not null)
            {
                dead[s] = new DeathCorrelation(best.Value, bd, [bx, by, bz]);
            }
        }
        return dead;
    }

    /// <summary>
    /// actor 感知死亡盒关联（Python: correlate_death_boxes_owned）：slot 只与
    /// 「属于该 slot 角色」的死亡盒匹配。owner 匹配后，敌人/AI 的盒子不再误杀活人。
    /// </summary>
    public static Dictionary<int, DeathCorrelation> CorrelateDeathBoxesOwned(
        IReadOnlyDictionary<ulong, List<DeathBoxSample>> boxesByOwner,
        IReadOnlyDictionary<int, List<SlotSample>> slots,
        IReadOnlyDictionary<ulong, int> slotOfActor,
        double matchCm = DeathMatchCm,
        Dictionary<string, Dictionary<int, DeathCorrelation>>? cache = null)
    {
        if (cache is null)
        {
            return CorrelateDeathBoxesOwnedUncached(boxesByOwner, slots, slotOfActor, matchCm);
        }
        var keyParts = new List<string>();
        foreach (var (s, tr) in slots)
        {
            keyParts.Add($"{s}:{tr.Count}:{(tr.Count > 0 ? tr[^1].Seq : -1)}");
        }
        keyParts.Sort();
        var nbox = boxesByOwner.Values.Sum(b => b.Count);
        var maxBseq = boxesByOwner.Values.SelectMany(b => b).Select(b => b.Seq).DefaultIfEmpty(-1).Max();
        keyParts.Add($"-1:{nbox}:{maxBseq}:{matchCm}");
        var key = string.Join("|", keyParts);
        if (cache.TryGetValue(key, out var hit)) return hit;
        var result = CorrelateDeathBoxesOwnedUncached(boxesByOwner, slots, slotOfActor, matchCm);
        cache[key] = result;
        if (cache.Count > 256)
        {
            foreach (var k in cache.Keys.Take(128).ToList()) cache.Remove(k);
        }
        return result;
    }

    private static Dictionary<int, DeathCorrelation> CorrelateDeathBoxesOwnedUncached(
        IReadOnlyDictionary<ulong, List<DeathBoxSample>> boxesByOwner,
        IReadOnlyDictionary<int, List<SlotSample>> slots,
        IReadOnlyDictionary<ulong, int> slotOfActor,
        double matchCm)
    {
        var dead = new Dictionary<int, DeathCorrelation>();
        foreach (var (s, tr) in slots)
        {
            if (tr.Count == 0) continue;
            ulong? actor = null;
            foreach (var (a, sl) in slotOfActor)
            {
                if (sl == s) { actor = a; break; }
            }
            if (actor is null) continue;
            if (!boxesByOwner.TryGetValue(actor.Value, out var boxes) || boxes.Count == 0) continue;
            var last = tr[^1];
            long? best = null;
            var bd = matchCm;
            double bx = 0, by = 0, bz = 0;
            foreach (var box in boxes)
            {
                if (box.Seq < last.Seq) continue;   // 盒子早于最终采样 → 角色之后还在动
                var d = Math.Sqrt((last.X - box.X) * (last.X - box.X) + (last.Y - box.Y) * (last.Y - box.Y));
                if (d < bd) { bd = d; best = box.Seq; bx = box.X; by = box.Y; bz = box.Z; }
            }
            if (best is not null)
            {
                dead[s] = new DeathCorrelation(best.Value, bd, [bx, by, bz]);
            }
        }
        return dead;
    }

    /// <summary>
    /// 从角色通道的 close bunch 提取「已消失」的角色（Python: extract_gone_actors）。
    /// 返回 {actor_guid: (close_seq, close_reason)}。语义：按事件 seq 排序取最后一个
    /// 事件决定在场/消失；通道关闭即清空归属（通道复用防误归）。
    /// </summary>
    public static Dictionary<ulong, (long Seq, long? Reason)> ExtractGoneActors(
        IEnumerable<BunchView> bunches, IReadOnlySet<ulong> charArchs)
    {
        var chActor = new Dictionary<int, ulong>();   // 通道 → 当前角色本体 actor
        var actorEvents = new Dictionary<ulong, List<(long Seq, string Kind, long? Reason)>>();
        foreach (var x in bunches)
        {
            if (x.Open)
            {
                try
                {
                    var (info, _) = Sna.ParsePrefix(
                        x.Payload, x.PBits, x.Hx, x.Hm, quantizeBit: true, frags: x.Frags);
                    var arch = info.TryGetValue("arch", out var a1) ? a1 : null;
                    var actor = info.TryGetValue("actor", out var a2) ? a2 : null;
                    if (arch is ulong g && charArchs.Contains(g) && actor is ulong a)
                    {
                        chActor[x.Ch] = a;
                        if (!actorEvents.TryGetValue(a, out var evs))
                        {
                            evs = [];
                            actorEvents[a] = evs;
                        }
                        evs.Add((x.Seq, "open", null));
                    }
                }
                catch (SnaBad)
                {
                    continue;
                }
            }
            else if (x.Close)
            {
                var reason = x.CloseReason;
                if (reason is null || !GoneCloseReasons.Contains(reason.Value)) continue;   // Dormancy = 不构成消失
                // 关闭即清空通道归属：通道关闭后同通道的后续 close 是子对象的
                // Dormancy/Destroyed，不能再归到已关闭角色头上。
                if (!chActor.TryGetValue(x.Ch, out var a2)) continue;
                chActor.Remove(x.Ch);
                if (!actorEvents.TryGetValue(a2, out var evs2))
                {
                    evs2 = [];
                    actorEvents[a2] = evs2;
                }
                evs2.Add((x.Seq, "close", reason));
            }
        }
        // 判定：按 seq 排序，最后一个事件是 close → 消失。
        var gone = new Dictionary<ulong, (long, long?)>();
        foreach (var (a, evs) in actorEvents)
        {
            evs.Sort((e1, e2) => e1.Seq.CompareTo(e2.Seq));
            if (evs.Count > 0 && evs[^1].Kind == "close")
            {
                gone[a] = (evs[^1].Seq, evs[^1].Reason);
            }
        }
        return gone;
    }

    /// <summary>
    /// 从角色通道的子对象块解码 FSM / 战斗状态（Python: extract_fsm_state）。
    /// 返回 {actor_guid: {"fsm_states": [...], "fight": {...}}}。
    /// 子对象块按 cls guid 匹配目录；actor 归属用同通道最近的角色 spawn。
    /// </summary>
    public static Dictionary<ulong, Dictionary<string, object?>> ExtractFsmState(
        IEnumerable<BunchView> bunches,
        IReadOnlyDictionary<ulong, string> guidPaths,
        IReadOnlyDictionary<string, RepCatalog> catalogs)
    {
        // cls guid → catalog name。
        var guid2Cat = new Dictionary<ulong, string>();
        foreach (var (g, p) in guidPaths)
        {
            var t = p.Contains('.') ? p[(p.LastIndexOf('.') + 1)..] : p;
            if (t.StartsWith("Default__", StringComparison.Ordinal)) t = t[9..];
            if (Tail2Cat.TryGetValue(t, out var catName)) guid2Cat[g] = catName;
        }

        // 通道 → 角色 actor guid（该通道最近一次角色 spawn）。
        var chActor = new Dictionary<int, ulong>();
        foreach (var x in bunches)
        {
            if (!x.Open) continue;
            try
            {
                var (info, _) = Sna.ParsePrefix(
                    x.Payload, x.PBits, x.Hx, x.Hm, quantizeBit: true, frags: x.Frags);
                if (info.TryGetValue("actor", out var a) && a is ulong actorGuid)
                {
                    chActor[x.Ch] = actorGuid;
                }
            }
            catch (SnaBad)
            {
                continue;
            }
        }

        var state = new Dictionary<ulong, Dictionary<string, object?>>();
        foreach (var x in bunches)
        {
            if (!chActor.TryGetValue(x.Ch, out var actor)) continue;
            var blocks = Blocks.Tile(x.Payload, x.PBits, 0);
            if (blocks is null) continue;
            foreach (var k in blocks)
            {
                if (k.IsActor != 0 || k.Deleted) continue;
                if (k.Cls is not { } cg || !guid2Cat.TryGetValue(cg, out var catName)) continue;
                if (!catalogs.TryGetValue(catName, out var cat)) continue;
                var (ev, _, status) = RepDec6.DecodeBlock(
                    x.Payload, k.PayloadStart, k.PayloadEnd, cat, []);
                if (status is not ("exact" or "short")) continue;
                if (!state.TryGetValue(actor, out var rec))
                {
                    rec = new Dictionary<string, object?>
                    {
                        ["fsm_states"] = new List<object?>(),
                        ["fight"] = new Dictionary<string, object?>(),
                    };
                    state[actor] = rec;
                }
                var par = cat.Parents.ToDictionary(
                    p => Convert.ToInt32(p.TryGetValue("index", out var i) ? i : -1),
                    p => p.TryGetValue("name", out var n) ? n?.ToString() ?? "?" : "?");
                foreach (var e in ev)
                {
                    var nm = par.TryGetValue(e.ParentIndex, out var pn) ? pn : "?";
                    if (catName == "DFMCharacterFSM_Main_C" && nm == "R_ActiveStates")
                    {
                        ((List<object?>)rec["fsm_states"]!).Add(e.Value);
                    }
                    else if (catName == "GPFightStateComponent")
                    {
                        ((Dictionary<string, object?>)rec["fight"]!)[nm] = e.Value;
                    }
                }
            }
        }
        return state;
    }
}
