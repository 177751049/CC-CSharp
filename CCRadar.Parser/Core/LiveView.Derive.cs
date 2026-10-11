using CCRadar.Parser.Codec;

namespace CCRadar.Parser.Core;

/// <summary>
/// LiveView 的派生（join/self/ents）与快照部分（Python: _derive/_slot_near_pose/
/// snapshot/_hp_of/_gear_of/_scan_equip_arrays 等）—— 同一 partial 类。
/// </summary>
public sealed partial class LiveView
{
    /// <summary>
    /// 离上行自机坐标最近的槽号；有歧义返回 null（Python: _slot_near_pose 三道闸）。
    /// </summary>
    private int? SlotNearPose(List<double>? loc, long? poseSeq = null,
        double maxCm = 500.0, double marginCm = PoseMarginCm)
    {
        if (loc is null || loc.Count < 2) return null;
        double tx, ty;
        try
        {
            tx = Convert.ToDouble(loc[0]);
            ty = Convert.ToDouble(loc[1]);
        }
        catch
        {
            return null;
        }
        var cand = new List<(double D2, int Slot, int Samples)>();
        var newest = -1L;
        foreach (var (slot, tr) in Slots)
        {
            if (tr.Count == 0) continue;
            var r = tr[^1];
            if (r.Seq > newest) newest = r.Seq;
            var d2 = (r.X - tx) * (r.X - tx) + (r.Y - ty) * (r.Y - ty);
            cand.Add((d2, slot, tr.Count));
        }
        if (cand.Count == 0) return null;
        // ① 位姿陈旧（上行已停发）：不认槽。
        if (poseSeq is not null && newest > 0 && newest - poseSeq.Value > PoseStaleSeq)
        {
            return null;
        }
        cand.Sort((a, b) =>
        {
            var c = a.D2.CompareTo(b.D2);
            return c != 0 ? c : b.Samples.CompareTo(a.Samples);
        });
        var (bd2, bslot, _) = cand[0];
        if (bd2 > maxCm * maxCm) return null;
        var bd = Math.Sqrt(bd2);
        // ③ 粘性：已钉住的槽仍在半径内且新候选没有明显更近 → 保持。
        var prev = _selfSlotPose;
        if (prev is not null)
        {
            foreach (var (d2, slot, _) in cand)
            {
                if (slot == prev)
                {
                    var pd = Math.Sqrt(d2);
                    if (pd <= maxCm && bd + marginCm > pd) return prev;
                    break;
                }
            }
        }
        // ② 歧义（自机与别人贴身）：不猜。
        if (cand.Count > 1)
        {
            var sd = Math.Sqrt(cand[1].D2);
            if (sd < bd + marginCm && sd < bd * 2.0) return null;
        }
        return bslot;
    }

    /// <summary>读取增量解码的角色状态（Python: _fsm）。</summary>
    private IReadOnlyDictionary<ulong, Dictionary<string, object?>> FsmSnapshot() => FsmState;

    /// <summary>最新 seq（Python: latest_seq）。</summary>
    public long? LatestSeq()
    {
        long? hi = null;
        lock (_lock)
        {
            foreach (var tr in Slots.Values)
            {
                if (tr.Count > 0)
                {
                    hi = hi is null ? tr[^1].Seq : Math.Max(hi.Value, tr[^1].Seq);
                }
            }
        }
        return hi;
    }

    /// <summary>角色 actor → (name, kind)（Python: _char_name_kind）。盒子命名复用。</summary>
    private (string? Name, string Kind) CharNameKind(ulong actor)
    {
        if (!Links.TryGetValue(actor, out var lk)) return (null, "unknown");
        string? name = null;
        object? pid = null;
        object? bot = null;
        if (lk.TryGetValue("ps_guid", out var pgObj) && pgObj is ulong pg)
        {
            if (Players.TryGetValue(pg, out var p))
            {
                name = p.TryGetValue("name", out var n) ? n?.ToString() : null;
                pid = p.TryGetValue("player_id", out var pi) ? pi : null;
                bot = p.TryGetValue("bot", out var bo) ? bo : null;
            }
        }
        if (name is null && lk.TryGetValue("owner", out var owObj) && owObj is not null)
        {
            var ownerGuid = Convert.ToUInt64(owObj);
            foreach (var p in Players.Values)
            {
                if (p.TryGetValue("owner_guid", out var og) && og is ulong ogU && ogU == ownerGuid)
                {
                    name = p.TryGetValue("name", out var n) ? n?.ToString() : null;
                    pid = p.TryGetValue("player_id", out var pi) ? pi : null;
                    bot = p.TryGetValue("bot", out var bo) ? bo : null;
                    break;
                }
            }
        }
        var aiKey = lk.TryGetValue("ai_key", out var ak) ? ak : null;
        var isAiCls = (lk.TryGetValue("cls", out var cls) ? cls?.ToString() : "")?.Contains("AI") == true;
        if (name is null && aiKey is not null)
        {
            var key = Convert.ToInt32(aiKey);
            name = (AiNames.TryGetValue(key, out var an) ? an : null) ?? $"AI·{key}_Name";
        }
        string kind;
        if (aiKey is not null || isAiCls) kind = "ai";
        else if (bot is true) kind = "player";
        else kind = "player";
        return (name, kind);
    }

    /// <summary>角色 actor 是否伪真人 bot（Python: _char_is_bot）。</summary>
    private bool CharIsBot(ulong? actor)
    {
        if (actor is null) return false;
        if (!Links.TryGetValue(actor.Value, out var lk)) return false;
        if (lk.TryGetValue("ps_guid", out var pgObj) && pgObj is ulong pg
            && Players.TryGetValue(pg, out var player))
        {
            var hid = (lk.TryGetValue("hero", out var h1) ? h1 : null)
                      ?? (player.TryGetValue("hero_id", out var h2) ? h2 : null);
            var matched = hid is not null && HeroNames.ContainsKey(Convert.ToInt64(hid));
            return ConfirmedPlayerBot(player, matched);
        }
        if (lk.TryGetValue("owner", out var owObj) && owObj is not null)
        {
            var owner = Convert.ToUInt64(owObj);
            foreach (var p in Players.Values)
            {
                if (p.TryGetValue("owner_guid", out var og) && og is ulong ogU && ogU == owner)
                {
                    var hid = (lk.TryGetValue("hero", out var h1) ? h1 : null)
                              ?? (p.TryGetValue("hero_id", out var h2) ? h2 : null);
                    var matched = hid is not null && HeroNames.ContainsKey(Convert.ToInt64(hid));
                    return ConfirmedPlayerBot(p, matched);
                }
            }
        }
        return false;
    }

    /// <summary>死亡盒 Owner 引用 → 最终 Character actor（Python: _box_owner_char）。</summary>
    private ulong? BoxOwnerChar(object? boxOwner)
    {
        if (boxOwner is null) return null;
        var resolved = Convert.ToUInt64(boxOwner);
        var seen = new HashSet<ulong>();
        while (CarryOwner.TryGetValue(resolved, out var next) && seen.Add(resolved))
        {
            resolved = next;
        }
        return Links.ContainsKey(resolved) ? resolved : null;
    }

    /// <summary>只有复制引用才认定死者（Python: _confirmed_box_owner）。</summary>
    private ulong? ConfirmedBoxOwner(IReadOnlyDictionary<string, object?> box)
    {
        var ownerRef = box.TryGetValue("deceased_actor", out var da) && da is not null ? da
            : box.TryGetValue("owner_actor", out var oa) ? oa : null;
        var actor = BoxOwnerChar(ownerRef);
        if (actor is null) return null;
        var boxSpawn = box.TryGetValue("spawn_seq", out var ss) ? Convert.ToInt64(ss)
            : Convert.ToInt64(box["seq"]);
        if (Links.TryGetValue(actor.Value, out var link)
            && link.TryGetValue("spawn_seq", out var sp) && sp is not null
            && Convert.ToInt64(sp) > boxSpawn)
        {
            return null;
        }
        return actor;
    }

    /// <summary>角色关联的 PlayerState life 记录（Python: _player_life_for_actor）。</summary>
    private Dictionary<string, object?>? PlayerLifeForActor(ulong actor)
    {
        var link = Links.TryGetValue(actor, out var l) ? l : new Dictionary<string, object?>();
        if (link.TryGetValue("ps_guid", out var pgObj) && pgObj is ulong pg
            && PlayerState.TryGetValue(pg, out var rec))
        {
            return rec;
        }
        if (!link.TryGetValue("owner", out var owObj) || owObj is null) return null;
        var owner = Convert.ToUInt64(owObj);
        foreach (var (psGuid, player) in Players)
        {
            if (player.TryGetValue("owner_guid", out var og) && og is ulong ogU && ogU == owner
                && PlayerState.TryGetValue(psGuid, out var rec2))
            {
                return rec2;
            }
        }
        return null;
    }

    /// <summary>角色的生死状态（Python: _life_state_for_actor）。</summary>
    private Dictionary<string, object?> LifeStateForActor(ulong actor)
    {
        var link = Links.TryGetValue(actor, out var l) ? l : new Dictionary<string, object?>();
        if (link.TryGetValue("spawn_seq", out var sp) && sp is not null)
        {
            LifeTracker.Spawn(actor, Convert.ToInt64(sp));
        }
        var life = PlayerLifeForActor(actor);
        if (life is not null && life.TryGetValue("schema_verified", out var sv) && sv is true)
        {
            var ps = link.TryGetValue("ps_guid", out var pgObj) ? pgObj : null;
            // PlayerState 可能在替换 Character 后仍存活；后续 PS 复活属于该
            // 序列时点上链接的最新 Character（Python 原注释）。
            var ends = new List<long>();
            foreach (var (a, v) in Links)
            {
                if (a == actor) continue;
                if (ps is not null && v.TryGetValue("ps_guid", out var vpg)
                    && Equals(vpg, ps)
                    && v.TryGetValue("spawn_seq", out var vsp) && vsp is not null
                    && Convert.ToInt64(vsp) > (link.TryGetValue("spawn_seq", out var lsp) ? Convert.ToInt64(lsp) : 0))
                {
                    ends.Add(v.TryGetValue("spawn_seq", out var vs2) ? Convert.ToInt64(vs2) : 0);
                }
            }
            var end = ends.Count > 0 ? ends.Min() : long.MaxValue;
            var values = life.TryGetValue("values", out var vObj) && vObj is Dictionary<string, object?> vd ? vd : new Dictionary<string, object?>();
            var seqsAll = life.TryGetValue("seq", out var sObj) && sObj is Dictionary<string, long> sd
                ? sd : new Dictionary<string, long>();
            var seqs = seqsAll.Where(kv => kv.Value < end).ToDictionary(kv => kv.Key, kv => kv.Value);
            LifeTracker.ObservePlayerstate(actor, values, seqs, verified: true);
        }
        return LifeTracker.Current(actor);
    }

    /// <summary>退出/对局结束移除判定（Python: _actor_removed）。</summary>
    private bool ActorRemoved(ulong actor)
    {
        var life = PlayerLifeForActor(actor);
        if (life is not null && life.TryGetValue("schema_verified", out var sv) && sv is true)
        {
            var spawn = Links.TryGetValue(actor, out var l) && l.TryGetValue("spawn_seq", out var sp) && sp is not null
                ? Convert.ToInt64(sp) : 0;
            var values = life.TryGetValue("values", out var vObj) && vObj is Dictionary<string, object?> vd
                ? vd : new Dictionary<string, object?>();
            var seqs = life.TryGetValue("seq", out var sObj) && sObj is Dictionary<string, long> sd
                ? sd : new Dictionary<string, long>();
            foreach (var (name, terminal) in new[]
                     {
                         ("ExitState", 3L), ("bFinishGame", 1L),
                         ("PlayerMatchInfo.bMatchOver", 1L),
                     })
            {
                if (values.TryGetValue(name, out var v) && Equals(v, terminal)
                    && seqs.TryGetValue(name, out var sq) && sq >= spawn)
                {
                    return true;
                }
            }
        }
        // Python _actor_removed 无 gone 回退：gone（通道 close）只驱动
        // slot_reused/静默逻辑，不驱动 removed——AI_DT 销毁的 AI 实体据此保留。
        return false;
    }

    /// <summary>语义死亡是显式复制状态，绝不是 close reason（Python: _confirmed_death_seq）。</summary>
    private long? ConfirmedDeathSeq(ulong actor)
    {
        var state = LifeStateForActor(actor);
        return state["dead"] is true && state["seq"] is long s ? s : null;
    }

    /// <summary>槽在确认死亡之后仍有位移 = 槽被复用（Python: _slot_outlived_death）。</summary>
    private bool SlotOutlivedDeath(ulong actor, int slot)
    {
        if (Links.TryGetValue(actor, out var lk) && lk.ContainsKey("move_handle"))
        {
            return false;   // 显式所有权变更不是距离变化
        }
        var seq = ConfirmedDeathSeq(actor);
        var track = Slots.TryGetValue(slot, out var t) ? t : new List<SlotSample>();
        if (seq is null || track.Count == 0 || track[^1].Seq <= seq.Value) return false;
        var lo = 0;
        var hi = track.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (track[mid].Seq <= seq.Value) lo = mid + 1;
            else hi = mid;
        }
        var index = lo - 1;
        if (index < 0) return true;
        var a = track[index];
        var b = track[^1];
        return (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z)
               >= 500.0 * 500.0;
    }

    /// <summary>链接是否真人玩家（Python: _is_human）。</summary>
    private static bool IsHuman(Dictionary<string, object?>? lk,
        IReadOnlyDictionary<ulong, Dictionary<string, object?>> players)
    {
        if (lk is null || lk.Count == 0) return false;
        if ((lk.TryGetValue("ai_key", out var ak) && ak is not null)
            || (lk.TryGetValue("cls", out var cls) && cls?.ToString()?.Contains("AI") == true))
        {
            return false;
        }
        if (lk.TryGetValue("ps_guid", out var pg) && pg is not null)
        {
            var pgU = Convert.ToUInt64(pg);
            if (players.TryGetValue(pgU, out var p))
            {
                return !p.TryGetValue("bot", out var b) || b is null;
            }
        }
        if (lk.TryGetValue("owner", out var ow) && ow is not null)
        {
            var owU = Convert.ToUInt64(ow);
            foreach (var p in players.Values)
            {
                if (p.TryGetValue("owner_guid", out var og) && og is ulong ogU && ogU == owU)
                {
                    return !p.TryGetValue("bot", out var b) || b is null;
                }
            }
        }
        return false;
    }

    // ---- _derive：join / self / ents 廉价重算（Python: _derive 的忠实移植）----

    // ---- Derive 过滤诊断计数器（黄金对拍）----
    public long NEntSkipMove { get; private set; }
    public long NEntSkipGone { get; private set; }
    public long NEntSkipDisp { get; private set; }
    public long NEntAdded { get; private set; }

    private void Derive()
    {
        var explicitClaims = ErLive.ExplicitSlotClaims(Links);
        var reserved = ErLive.ObservedMoveSlots(Links);
        var claim = explicitClaims.ToDictionary(kv => kv.Key, kv => (kv.Value, 0.0));
        // owner_guid → [ps_guid,...]（插入序）。
        var owner2Guid = new Dictionary<ulong, List<ulong>>();
        foreach (var (g, p) in Players)
        {
            if (p.TryGetValue("owner_guid", out var ow) && ow is not null)
            {
                var owU = Convert.ToUInt64(ow);
                if (!owner2Guid.TryGetValue(owU, out var list))
                {
                    list = [];
                    owner2Guid[owU] = list;
                }
                list.Add(g);
            }
        }
        var namedActors = new HashSet<ulong>();
        foreach (var (actor, lk) in Links)
        {
            if ((lk.TryGetValue("ps_guid", out var pg) && pg is not null
                    && pg is ulong pgU && Players.ContainsKey(pgU))
                || (lk.TryGetValue("owner", out var ow) && ow is not null
                    && owner2Guid.ContainsKey(Convert.ToUInt64(ow))))
            {
                namedActors.Add(actor);
            }
        }
        foreach (var (actor, lk) in Links)
        {
            // 身份未解析的链接不参与槽位认领（会抢队友/self 槽，破坏闭世界）。
            // gone 角色不跳过——重生型角色出生点与前任重合，槽位压制由快照层处理。
            if (lk.ContainsKey("move_handle") || !namedActors.Contains(actor)) continue;
            var loc = lk.TryGetValue("loc", out var locObj) ? locObj : null;
            var spawnSeq = lk.TryGetValue("spawn_seq", out var sp) && sp is not null
                ? Convert.ToInt64(sp) : (long?)null;
            if (loc is not List<object?> locList || locList.Count < 3 || spawnSeq is null || Slots.Count == 0)
            {
                continue;
            }
            var hasGone = Gone.TryGetValue(actor, out var g);
            var lx = Convert.ToDouble(locList[0]);
            var ly = Convert.ToDouble(locList[1]);
            var lz = Convert.ToDouble(locList[2]);
            int? best = null;
            double bd = 1e18;
            var bpts = 0;
            foreach (var (slot, tr) in Slots)
            {
                if (reserved.Contains(slot) || tr.Count == 0) continue;
                if (hasGone && tr[0].Seq > g.Seq)
                {
                    // 角色 close 早于槽首点 = 角色从未在此槽活动，认领无效。
                    continue;
                }
                // bisect_left(tr, S, key=seq)。
                var lo = 0;
                var hi = tr.Count;
                while (lo < hi)
                {
                    var mid = (lo + hi) / 2;
                    if (tr[mid].Seq < spawnSeq.Value) lo = mid + 1;
                    else hi = mid;
                }
                var j = lo;
                foreach (var jj in new[] { j - 1, j })
                {
                    if (jj < 0 || jj >= tr.Count) continue;
                    var t = tr[jj];
                    var dx = lx - t.X;
                    var dy = ly - t.Y;
                    var dz = lz - t.Z;
                    var d2 = dx * dx + dy * dy + dz * dz;
                    // 平局打破：采样多者胜（Python 2426 局实证注释）。
                    if (d2 < bd || (d2 == bd && tr.Count > bpts))
                    {
                        bd = d2;
                        best = slot;
                        bpts = tr.Count;
                    }
                }
            }
            if (best is not null && bd < 250000.0)   // 500cm²
            {
                if (!claim.TryGetValue(best.Value, out var prev) || bd < prev.Item2)
                {
                    claim[best.Value] = (actor, bd);
                }
            }
        }
        var members = Slots.Where(kv => kv.Value.Count >= MemberMinPoints).Select(kv => kv.Key).ToHashSet();
        var unclaimed = members.Where(s => !claim.ContainsKey(s)).ToList();
        int? selfSlot = null;
        var claimedActors = claim.Values.Select(v => v.Item1).ToHashSet();
        if (unclaimed.Count == 1 && claimedActors.Count > 0
            && claimedActors.IsSubsetOf(namedActors)
            && claim.Count == members.Count - 1)
        {
            selfSlot = unclaimed[0];
        }
        // 确定性 self_slot（field13 兜底）。
        if (selfSlot is null && F13Self is not null)
        {
            var (fseq, _, floc) = F13Self.Value;
            var fx = floc[0];
            var fy = floc[1];
            int? best = null;
            double bd = 1e18;
            foreach (var (slot, tr) in Slots)
            {
                if (tr.Count < MemberMinPoints) continue;
                if (claim.ContainsKey(slot)) continue;   // 已被认领的槽不能当自机槽
                var lo = 0;
                var hi = tr.Count;
                while (lo < hi)
                {
                    var mid = (lo + hi) / 2;
                    if (tr[mid].Seq < fseq) lo = mid + 1;
                    else hi = mid;
                }
                var j = lo;
                foreach (var jj in new[] { j - 1, j })
                {
                    if (jj < 0 || jj >= tr.Count) continue;
                    var t = tr[jj];
                    var dx = fx - t.X;
                    var dy = fy - t.Y;
                    var d2 = dx * dx + dy * dy;
                    if (d2 < bd) { bd = d2; best = slot; }
                }
            }
            if (best is not null && bd < 90000.0) selfSlot = best;   // 300cm²
        }
        // 队友槽位：field13 非自机轨迹 ↔ 成员槽最近点对齐。
        MateSlots = new Dictionary<int, int>();
        if (F13Tracks.Count > 0 && Slots.Count > 0)
        {
            var mates = new List<(double Bd, int Slot, int Ti)>();
            foreach (var (ti, tr) in F13Tracks)
            {
                if (ti == F13SelfIdx || tr.Count == 0) continue;
                var (fseq, _, fx, fy, _) = tr[^1];
                int? best = null;
                double bd = 1e18;
                foreach (var (slot, mtr) in Slots)
                {
                    if (mtr.Count < MemberMinPoints) continue;
                    var lo = 0;
                    var hi = mtr.Count;
                    while (lo < hi)
                    {
                        var mid = (lo + hi) / 2;
                        if (mtr[mid].Seq < fseq) lo = mid + 1;
                        else hi = mid;
                    }
                    var j = lo;
                    foreach (var jj in new[] { j - 1, j })
                    {
                        if (jj < 0 || jj >= mtr.Count) continue;
                        var t = mtr[jj];
                        var dx = fx - t.X;
                        var dy = fy - t.Y;
                        var d2 = dx * dx + dy * dy;
                        if (d2 < bd) { bd = d2; best = slot; }
                    }
                }
                if (best is not null && bd < 90000.0) mates.Add((bd, best.Value, ti));
            }
            foreach (var (_, slot, ti) in mates.OrderBy(m => m.Bd))
            {
                MateSlots[slot] = ti;   // 距离最近者优先占槽
            }
            // 槽主人 Relevancy 且槽仍在广播 = 槽被复用，剔除旧映射。
            var actorOfSlot = claim.ToDictionary(kv => kv.Key, kv => kv.Value.Item1);
            foreach (var s in MateSlots.Keys.ToList())
            {
                if (!actorOfSlot.TryGetValue(s, out var a)) continue;
                if (!Gone.TryGetValue(a, out var g)) continue;
                var tr = Slots.TryGetValue(s, out var t) ? t : new List<SlotSample>();
                if (g.Reason == 3 && tr.Count > 0 && tr[^1].Seq > g.Seq
                    && !explicitClaims.ContainsKey(s))
                {
                    MateSlots.Remove(s);
                }
            }
        }
        // ---- 权威 self 判定（前移到匿名命名块之前；875 局实证）----
        string? selfName = null;
        ulong? selfPsGuid = null;
        string? selfSrc = null;
        // 真人候选（有名字、非 bot）。channel 缺失不跳过。
        var humans = Players.Where(kv => !kv.Value.TryGetValue("bot", out var b) || b is null)
            .Where(kv => kv.Value.TryGetValue("name", out var n) && n is not null && n.ToString() is { Length: > 0 })
            .ToList();
        if (humans.Count > 0 && humans.All(kv => kv.Value.TryGetValue("channel", out var c) && c is not null))
        {
            var (g, p) = humans.OrderBy(kv => Convert.ToInt32(kv.Value["channel"])).First();
            selfName = p["name"]?.ToString();
            selfPsGuid = g;
            selfSrc = "ch";
        }
        else if (humans.Count > 0)
        {
            // channel 缺失 → 最早 spawn（自己的 PS 必是服务器最先复制的）。
            KeyValuePair<ulong, Dictionary<string, object?>>? bestKv = null;
            var bestSp = long.MaxValue;
            var bestCh = int.MaxValue;
            foreach (var kv in humans)
            {
                var sp = kv.Value.TryGetValue("spawn_seq", out var s1) && s1 is not null ? Convert.ToInt64(s1) : 1L << 30;
                var ch = kv.Value.TryGetValue("channel", out var c1) && c1 is not null ? Convert.ToInt32(c1) : int.MaxValue;
                if (sp < bestSp || (sp == bestSp && ch < bestCh)) { bestSp = sp; bestCh = ch; bestKv = kv; }
            }
            if (bestKv is not null)
            {
                selfName = bestKv.Value.Value["name"]?.ToString();
                selfPsGuid = bestKv.Value.Key;
                selfSrc = "spawn";
            }
        }
        if (selfName is null)
        {
            // 回退：pid 最小真人（旧规则）。
            foreach (var kv in Players.OrderBy(kv =>
                kv.Value.TryGetValue("player_id", out var p) && p is not null ? Convert.ToInt64(p) : 1L << 30))
            {
                if ((!kv.Value.TryGetValue("bot", out var b) || b is null)
                    && kv.Value.TryGetValue("name", out var n) && n is not null)
                {
                    selfName = n.ToString();
                    selfPsGuid = kv.Key;
                    selfSrc = "pid";
                    break;
                }
            }
        }
        // ---- 权威自机身份：上行自机坐标（客户端自报，权威）----
        var pose = Loader.SelfPose;
        if (pose is not null)
        {
            var poseLoc = pose.TryGetValue("loc", out var pl) ? pl as List<object?> : null;
            var poseSeq = pose.TryGetValue("seq", out var ps) ? Convert.ToInt64(ps) : (long?)null;
            var poseLocD = poseLoc is List<object?> pd ? pd.Select(v => Convert.ToDouble(v)).ToList() : null;
            var ps2 = SlotNearPose(poseLocD, poseSeq: poseSeq);
            if (ps2 is not null)
            {
                _selfSlotPose = ps2;
                var pa = claim.TryGetValue(ps2.Value, out var cv) ? cv.Item1 : (ulong?)null;
                var pg = pa is not null && Links.TryGetValue(pa.Value, out var plk)
                    && plk.TryGetValue("ps_guid", out var pgs) ? pgs : null;
                var pp = pg is not null && Players.TryGetValue((ulong)Convert.ToUInt64(pg), out var player)
                    ? player : null;
                if (pp is not null && pp.TryGetValue("name", out var pn) && pn is not null
                    && (!pp.TryGetValue("bot", out var pb) || pb is null))
                {
                    selfName = pn.ToString();
                    selfPsGuid = (ulong)Convert.ToUInt64(pg!);
                    selfSrc = "pose";
                }
            }
        }
        var actor2Slot = new Dictionary<ulong, int>();
        foreach (var (s, (a, _)) in claim) actor2Slot[a] = s;
        // self_slot 覆盖（pose > ps_guid 反查 > 闭世界/f13）。
        selfSlot = _selfSlotPose ?? selfSlot;
        if (_selfSlotPose is null && selfPsGuid is not null)
        {
            foreach (var (a, lk) in Links)
            {
                if (lk.TryGetValue("ps_guid", out var pg) && pg is not null
                    && Convert.ToUInt64(pg) == selfPsGuid.Value
                    && actor2Slot.TryGetValue(a, out var s))
                {
                    selfSlot = s;
                    break;
                }
            }
        }
        // ---- 匿名成员槽命名（协议级：seq 对齐 + pid 偏移 + 唯一配对）----
        AnonNames = new Dictionary<int, Dictionary<string, object?>>();
        if (Players.Count > 0)
        {
            var claimedSlots = claim.Keys.ToHashSet();
            // gone 角色认领的槽：Relevancy/LevelUnloaded + 槽仍广播 → 参与命名；
            // Destroyed/TearOff 不参与。
            foreach (var (s, (a, _)) in claim)
            {
                if (Gone.TryGetValue(a, out var gr) && gr.Reason is not (0 or 4))
                {
                    var tr = Slots.TryGetValue(s, out var t) ? t : null;
                    if (tr is { Count: > 0 } && tr[^1].Seq > gr.Seq)
                    {
                        claimedSlots.Remove(s);
                    }
                }
            }
            if (selfSlot is not null) claimedSlots.Add(selfSlot.Value);
            var linkedPs = new HashSet<ulong>();
            foreach (var (a, lk) in Links)
            {
                if (lk.TryGetValue("ps_guid", out var pg) && pg is not null
                    && Players.ContainsKey(Convert.ToUInt64(pg)))
                {
                    linkedPs.Add(Convert.ToUInt64(pg));
                }
                if (lk.TryGetValue("owner", out var ow) && ow is not null
                    && owner2Guid.TryGetValue(Convert.ToUInt64(ow), out var guids))
                {
                    foreach (var g2 in guids) linkedPs.Add(g2);
                }
            }
            var claimedActors2 = claim.Values.Select(v => v.Item1).ToHashSet();

            bool PsFree(ulong g)
            {
                if (!linkedPs.Contains(g)) return true;
                return Links.All(a2 =>
                {
                    if (!(a2.Value.TryGetValue("ps_guid", out var pg) && pg is not null
                          && Convert.ToUInt64(pg) == g))
                    {
                        var owOk = a2.Value.TryGetValue("owner", out var ow) && ow is not null
                                   && Players.TryGetValue(g, out var pl)
                                   && pl.TryGetValue("owner_guid", out var og)
                                   && og is not null && Convert.ToUInt64(og) == Convert.ToUInt64(ow);
                        if (!owOk) return true;
                    }
                    return !claimedActors2.Contains(a2.Key);
                });
            }

            var anonSlots = members
                .Where(s => !claimedSlots.Contains(s) && Slots.TryGetValue(s, out var t) && t.Count > 0)
                .Select(s => (Slots[s][0].Seq, s))
                .OrderBy(t => t.Item1)
                .ToList();
            var psPool = Players
                .Where(kv => PsFree(kv.Key)
                             && kv.Value.TryGetValue("name", out var n) && n is not null
                             && kv.Value.TryGetValue("spawn_seq", out var sp) && sp is not null)
                .Select(kv => (Convert.ToInt64(kv.Value["spawn_seq"]), kv.Key))
                .OrderBy(t => t.Item1)
                .ToList();
            // 全局最近对优先的一对一分配（±10 窗口）。
            var cands = new List<(long D, int Slot, ulong Guid)>();
            foreach (var (sseq, s) in anonSlots)
            {
                foreach (var (pseq, g) in psPool)
                {
                    var d = Math.Abs(sseq - pseq);
                    if (d < 11) cands.Add((d, s, g));
                }
            }
            cands.Sort((a, b) => a.D != b.D ? a.D.CompareTo(b.D)
                : a.Slot != b.Slot ? a.Slot.CompareTo(b.Slot) : a.Guid.CompareTo(b.Guid));
            var usedS = new HashSet<int>();
            var usedG = new HashSet<ulong>();
            foreach (var (d, s, g) in cands)
            {
                if (usedS.Contains(s) || usedG.Contains(g)) continue;
                AnonNames[s] = Players[g];
                usedS.Add(s);
                usedG.Add(g);
            }
            // 第三梯队：player_id ↔ 槽号同序（高度一致才采信）。
            var pidoff = new Dictionary<long, int>();
            var pidoffTot = 0;
            foreach (var (s2, (a, _)) in claim)
            {
                var pg2 = Links.TryGetValue(a, out var lk2) && lk2.TryGetValue("ps_guid", out var pgv) ? pgv : null;
                var p2d = pg2 is not null && Players.TryGetValue(Convert.ToUInt64(pg2), out var pp2) ? pp2 : null;
                var pid2 = p2d is not null && p2d.TryGetValue("player_id", out var pi2) ? pi2 : null;
                var bot2 = p2d is not null && p2d.TryGetValue("bot", out var bo2) ? bo2 : null;
                if (pid2 is null || bot2 is not null || Convert.ToInt64(pid2) >= 8000) continue;
                var off = Convert.ToInt64(pid2) - s2;
                pidoff[off] = pidoff.TryGetValue(off, out var c2) ? c2 + 1 : 1;
                pidoffTot++;
            }
            if (pidoff.Count > 0)
            {
                var offEntry = pidoff.OrderByDescending(kv => kv.Value).First();
                var off2nd = pidoff.Where(kv => kv.Key != offEntry.Key)
                    .Select(kv => kv.Value).DefaultIfEmpty(0).Max();
                if (offEntry.Value >= 4 && offEntry.Value >= pidoffTot * 0.8 && offEntry.Value > off2nd * 3)
                {
                    var pidUsed = new HashSet<object?>();
                    var claimedUins3 = new HashSet<object?>();
                    foreach (var (_, (a, _)) in claim)
                    {
                        var pg2 = Links.TryGetValue(a, out var lk2) && lk2.TryGetValue("ps_guid", out var p2) ? p2 : null;
                        var p2d = pg2 is not null && Players.TryGetValue(Convert.ToUInt64(pg2), out var pp2) ? pp2 : null;
                        if (p2d is null) continue;
                        if (p2d.TryGetValue("player_id", out var pi2) && pi2 is not null) pidUsed.Add(pi2);
                        if (p2d.TryGetValue("uin", out var ui2) && ui2 is not null) claimedUins3.Add(ui2);
                    }
                    var pidFree = new Dictionary<long, ulong>();
                    foreach (var (g3, p3) in Players)
                    {
                        var pid3 = p3.TryGetValue("player_id", out var pi3) ? pi3 : null;
                        if (pid3 is null || pidUsed.Contains(pid3)
                            || !p3.TryGetValue("name", out var n3) || n3 is null
                            || (p3.TryGetValue("bot", out var b3) && b3 is not null))
                        {
                            continue;
                        }
                        var u3 = p3.TryGetValue("uin", out var ui3) ? ui3 : null;
                        if (u3 is not null && claimedUins3.Contains(u3)) continue;
                        if (!PsFree(g3)) continue;
                        pidFree[Convert.ToInt64(pid3)] = g3;
                    }
                    foreach (var (fs, s2) in anonSlots)
                    {
                        if (usedS.Contains(s2)) continue;
                        if (pidFree.TryGetValue(s2 + offEntry.Key, out var g3))
                        {
                            AnonNames[s2] = Players[g3];
                            usedS.Add(s2);
                            usedG.Add(g3);
                        }
                    }
                }
            }
            // 第二梯队：真空闲 PS 唯一配对。
            var claimedUins = new HashSet<object?>();
            foreach (var (_, (a, _)) in claim)
            {
                var lk2 = Links.TryGetValue(a, out var l2) ? l2 : new Dictionary<string, object?>();
                if (lk2.TryGetValue("ps_guid", out var pg2) && pg2 is not null
                    && Players.TryGetValue(Convert.ToUInt64(pg2), out var p2)
                    && p2.TryGetValue("uin", out var u2) && u2 is not null)
                {
                    claimedUins.Add(u2);
                }
                if (lk2.TryGetValue("owner", out var ow2) && ow2 is not null)
                {
                    var ow2U = Convert.ToUInt64(ow2);
                    foreach (var pIter in Players.Values)
                    {
                        if (pIter.TryGetValue("owner_guid", out var og2) && og2 is ulong ogU2 && ogU2 == ow2U
                            && pIter.TryGetValue("uin", out var u2b) && u2b is not null)
                        {
                            claimedUins.Add(u2b);
                        }
                    }
                }
            }
            var free2 = psPool.Where(tt => !usedG.Contains(tt.Item2))
                .Where(tt =>
                {
                    var u = Players[tt.Item2].TryGetValue("uin", out var u2) ? u2 : null;
                    return u is null || !claimedUins.Contains(u);
                })
                .Select(tt => tt.Item2).ToList();
            var restSlots = anonSlots.Where(t => !usedS.Contains(t.s)).Select(t => t.s).ToList();
            if (free2.Count == 1 && restSlots.Count == 1)
            {
                AnonNames[restSlots[0]] = Players[free2[0]];
            }
        }
        // ---- vital_dead（致命部位归零，诈尸修复注释见 Python）----
        VitalDead.Clear();
        foreach (var (ha, hrec) in Health)
        {
            var p = (Dictionary<string, object?>)hrec["parts"]!;
            if (!_vitalZeroSeen.TryGetValue(ha, out var seen))
            {
                seen = [];
                _vitalZeroSeen[ha] = seen;
            }
            foreach (var k in new[] { "HeadHealth", "ThoraxHealth", "AbdomenHealth" })
            {
                if (!p.TryGetValue(k, out var v) || v is null) continue;
                var dv = Convert.ToDouble(v);
                if (dv <= 0.0) seen.Add(k);
                else if (seen.Contains(k))
                {
                    seen.Remove(k);   // 回升：被救起
                    VitalRevived.Add(ha);
                }
            }
            if (p.TryGetValue("HeadHealth", out var h) && h is not null && Convert.ToDouble(h) <= 0
                || p.TryGetValue("ThoraxHealth", out var t2) && t2 is not null && Convert.ToDouble(t2) <= 0
                || p.TryGetValue("AbdomenHealth", out var ab) && ab is not null && Convert.ToDouble(ab) <= 0)
            {
                VitalDead.Add(ha);
            }
            else
            {
                VitalDead.Remove(ha);
            }
        }
        // 死亡盒 owner 死亡信号（补齐 close_reason 丢失的真人死亡）。
        BoxDead.Clear();
        foreach (var (ba, bx) in Boxes)
        {
            var oc = ConfirmedBoxOwner(bx);
            if (oc is not null)
            {
                bx["confirmed_owner"] = oc;
                var seq = bx.TryGetValue("spawn_seq", out var ss) ? Convert.ToInt64(ss) : Convert.ToInt64(bx["seq"]);
                BoxDead[oc.Value] = Math.Max(seq, BoxDead.TryGetValue(oc.Value, out var old) ? old : -1);
            }
        }
        // 队友绑定双源并集（TeamID 补绑 + f13 轨迹；自机剔除）。
        if (selfPsGuid is not null)
        {
            var selfTeam = Players.TryGetValue(selfPsGuid.Value, out var sp2)
                ? sp2.TryGetValue("team", out var t3) ? t3 : null : null;
            if (selfTeam is not null)
            {
                foreach (var (g, p) in Players)
                {
                    if (g == selfPsGuid.Value) continue;
                    if ((p.TryGetValue("bot", out var b) && b is not null)
                        || !p.TryGetValue("name", out var n) || n is null) continue;
                    if (!Equals(p.TryGetValue("team", out var t4) ? t4 : null, selfTeam)) continue;
                    foreach (var (a, lk) in Links)
                    {
                        if (lk.TryGetValue("ps_guid", out var pg) && pg is not null
                            && Convert.ToUInt64(pg) == g
                            && actor2Slot.TryGetValue(a, out var ms))
                        {
                            if (Gone.TryGetValue(a, out var gr) && gr.Reason is not null
                                && StateTrack.DeadCloseReasons.Contains(gr.Reason.Value))
                            {
                                continue;   // 死亡类 close 的槽不入 mate
                            }
                            if (ms != selfSlot && !MateSlots.ContainsKey(ms))
                            {
                                MateSlots[ms] = -1;   // -1 = team 兜底
                            }
                            break;
                        }
                    }
                }
            }
            if (selfSlot is not null)
            {
                MateSlots.Remove(selfSlot.Value);   // f13 错位局剔除自机槽
            }
        }
        // ---- 实体列表 ----
        var ents = new List<Dictionary<string, object?>>();
        foreach (var (actor, lk) in Links)
        {
            if (lk.TryGetValue("move_handle", out var mh) && mh is long mhL && mhL >= 0
                && !actor2Slot.ContainsKey(actor))
            {
                NEntSkipMove++;
                continue;   // 显式的后续分配已替换该 actor
            }
            var claimedSlot = actor2Slot.TryGetValue(actor, out var cs) ? cs : (int?)null;
            var isSelf = (claimedSlot is not null && selfSlot is not null && claimedSlot == selfSlot)
                         || (!lk.TryGetValue("loc", out var lcl) || lcl is null)
                         && lk.TryGetValue("num_exports", out var ne) && Equals(ne, 28L);
            // gone 角色剔除规则：Relevancy 真人/显式认领者保留，其余剔除。
            if (Gone.TryGetValue(actor, out var grec) && !isSelf
                && (!actor2Slot.TryGetValue(actor, out var gslot) || !MateSlots.ContainsKey(gslot)))
            {
                var keep = grec.Reason == 3
                           && (IsHuman(lk, Players)
                               || explicitClaims.TryGetValue(gslot, out var ea) && ea == actor);
                if (!keep) { NEntSkipGone++; continue; }
            }
            string? name = null;
            object? pid = null;
            object? bot = null;
            object? team = null;
            object? camp = null;
            string? nameSrc = null;
            Dictionary<string, object?>? playerState = null;
            var psGuid = lk.TryGetValue("ps_guid", out var pg2) ? pg2 : null;
            var ps = psGuid is not null && Players.TryGetValue(Convert.ToUInt64(psGuid), out var psP) ? psP : null;
            if (ps is not null)
            {
                name = ps.TryGetValue("name", out var n) ? n?.ToString() : null;
                pid = ps.TryGetValue("player_id", out var pi) ? pi : null;
                bot = ps.TryGetValue("bot", out var bo) ? bo : null;
                team = ps.TryGetValue("team", out var t5) ? t5 : null;
                camp = ps.TryGetValue("camp", out var ca) ? ca : null;
                nameSrc = "ps";
                playerState = ps;
            }
            if (name is null && lk.TryGetValue("owner", out var ow2) && ow2 is not null)
            {
                var ow2U = Convert.ToUInt64(ow2);
                if (owner2Guid.TryGetValue(ow2U, out var gList))
                {
                    foreach (var g in gList)
                    {
                        var p2 = Players[g];
                        name = p2.TryGetValue("name", out var n) ? n?.ToString() : null;
                        pid = p2.TryGetValue("player_id", out var pi) ? pi : null;
                        bot = p2.TryGetValue("bot", out var bo) ? bo : null;
                        team = p2.TryGetValue("team", out var t5) ? t5 : null;
                        camp = p2.TryGetValue("camp", out var ca) ? ca : null;
                        nameSrc = "owner";
                        playerState = p2;
                        break;
                    }
                }
            }
            var aiKey = lk.TryGetValue("ai_key", out var ak2) ? ak2 : null;
            var isAiCls = (lk.TryGetValue("cls", out var cls2) ? cls2?.ToString() : "")?.Contains("AI") == true;
            if (name is null && aiKey is not null)
            {
                var key = Convert.ToInt32(aiKey);
                name = (AiNames.TryGetValue(key, out var an2) ? an2 : null)
                       ?? AiClsName(lk.TryGetValue("cls", out var c3) ? c3?.ToString() : null)
                       ?? $"AI·{key}";
                nameSrc = "ai";
            }
            if (name is null && isAiCls)
            {
                name = AiClsName(lk.TryGetValue("cls", out var c4) ? c4?.ToString() : null);
                if (name is not null) nameSrc = "ai";
            }
            var aslot = actor2Slot.TryGetValue(actor, out var a2s) ? a2s : (int?)null;
            if (nameSrc == "ai" && aslot is not null
                && AnonNames.TryGetValue(aslot.Value, out var anonP))
            {
                // AI 表名兜底的槽若命中 anon_names 的 PS 名 → 改用 PS 名。
                name = anonP.TryGetValue("name", out var n2) ? n2?.ToString() : null;
                pid = anonP.TryGetValue("player_id", out var pi2) ? pi2 : null;
                bot = anonP.TryGetValue("bot", out var bo2) ? bo2 : null;
                team = anonP.TryGetValue("team", out var t6) ? t6 : null;
                camp = anonP.TryGetValue("camp", out var ca2) ? ca2 : null;
                playerState = anonP;
            }
            string kind;
            if (isSelf) kind = "self";
            else if (aiKey is not null || isAiCls) kind = "ai";
            else if (bot is true) kind = "player";
            else kind = "player";
            var disp = isSelf ? (selfName is not null ? $"{selfName}(self)" : "自己") : name;
            if (disp is null && aslot is null
                && (!lk.TryGetValue("loc", out var lcl2) || lcl2 is null))
            {
                NEntSkipDisp++;
                continue;
            }
            object? hid = lk.TryGetValue("hero", out var hh) ? hh : null;
            if (hid is null && lk.TryGetValue("ps_guid", out var pg3) && pg3 is not null
                && Players.TryGetValue(Convert.ToUInt64(pg3), out var psP3)
                && psP3.TryGetValue("hero_id", out var h3))
            {
                hid = h3;
            }
            if (hid is null && lk.TryGetValue("owner", out var ow3) && ow3 is not null
                && owner2Guid.TryGetValue(Convert.ToUInt64(ow3), out var gList3) && gList3.Count > 0
                && Players[gList3[0]].TryGetValue("hero_id", out var h4))
            {
                hid = h4;
            }
            NEntAdded++;
            ents.Add(new Dictionary<string, object?>
            {
                ["actor"] = actor, ["name"] = disp, ["kind"] = kind,
                ["binding_source"] = explicitClaims.ContainsValue(actor) ? "move_handle"
                    : actor2Slot.ContainsKey(actor) ? "spawn_proximity" : null,
                ["slot"] = isSelf ? null : aslot,
                ["spawn_loc"] = lk.TryGetValue("loc", out var l3) ? l3 : null,
                ["spawn_seq"] = lk.TryGetValue("spawn_seq", out var ss3) ? ss3 : null,
                ["player_id"] = pid,
                ["team"] = team, ["camp"] = camp,
                ["is_bot"] = ConfirmedPlayerBot(playerState,
                    hid is not null && HeroNames.ContainsKey(Convert.ToInt64(hid))),
                ["hero"] = hid is not null && HeroNames.TryGetValue(Convert.ToInt64(hid), out var hn) ? hn : null,
            });
        }
        // Relevancy-槽复用的实体名处理（Python: slot_reused）。
        var cur2 = LatestSeq() ?? 0;
        foreach (var ent in ents)
        {
            if (ent["slot"] is not int s || ent["kind"]?.ToString() == "self") continue;
            if (ent["actor"] is not ulong a) continue;
            if (!Gone.TryGetValue(a, out var g) || g.Reason != 3) continue;
            if (explicitClaims.TryGetValue(s, out var ea) && ea == a) continue;
            var tr = Slots.TryGetValue(s, out var t) ? t : null;
            if (tr is { Count: > 0 } && tr[^1].Seq > g.Seq)
            {
                ent["slot_reused"] = true;
                ent["name"] = null;
                if (ent["kind"]?.ToString() == "mate") ent["kind"] = "player";
            }
        }
        lock (_lock)
        {
            var claimMap = new Dictionary<int, ulong>();
            foreach (var (s, (a, _)) in claim) claimMap[s] = a;
            Claim = claimMap;
            SelfSlot = selfSlot;
            SelfName = selfName;
            SelfSrc = selfSrc;
            Ents = ents;
            MemberSlots = members;
        }
    }
}
