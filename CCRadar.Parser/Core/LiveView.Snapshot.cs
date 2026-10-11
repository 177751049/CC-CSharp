using System.Text;
using CCRadar.Parser.Codec;

namespace CCRadar.Parser.Core;

/// <summary>
/// LiveView 快照组装（Python: snapshot/_hp_of/_gear_of/_slot_pos/_scan_equip_arrays）
/// —— 同一 partial 类。tail-follow 游标 = 最新 seq。
/// </summary>
public sealed partial class LiveView
{
    /// <summary>
    /// 实体 eid → actor（ulong）。Python 的 actor 是任意精度 int，`isinstance(actor, int)` 通吃；
    /// C# 侧 actor 可能装箱为 ulong（真实 guid 可超 long.MaxValue）/ int / long，必须宽松归一化。
    /// 0 或非法 = 无 actor（匿名槽位实体）。
    /// </summary>
    private static ulong EntityId(Dictionary<string, object?> entity)
    {
        if (!entity.TryGetValue("eid", out var eidObj) || eidObj is null) return 0;
        return eidObj switch
        {
            ulong u => u,
            long l => l > 0 ? (ulong)l : 0,
            int i => i > 0 ? (ulong)i : 0,
            uint ui => ui,
            short sh => sh > 0 ? (ulong)sh : 0,
            byte by => by,
            _ => 0,
        };
    }

    /// <summary>槽位下标归一化（Python 直接比较 int；C# 可能装箱为 int/long/ulong）。</summary>
    internal static int? ToSlotIndex(object? value)
    {
        return value switch
        {
            int i => i,
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            ulong u when u <= int.MaxValue => (int)u,
            _ => null,
        };
    }

    /// <summary>
    /// 装备数组结构判定提取（Python: _scan_equip_arrays 的等价实现）。
    /// 元素 k 字段句柄 = 9k+{1:itemId u64, 2:uid u64, 5:dur_cur f32, 6:dur_max f32}。
    /// 提取 = itemId 锚定（10^10 ≤ v < 10^12 且 1101/1105 段）+ 严格链验证。
    /// 句柄编码 = UE4 串行整数字节形式：每字节 LSB=终止位、高 7 位数据，
    /// packed() 返回 (值, 总位数 8/16/24)——所有字段偏移按位计算。
    /// C# 无 numpy 向量化：按全偏移扫描（正确性优先）。
    /// </summary>
    /// <summary>诊断入口：BunchProbe 并排对拍用（ScanEquipArrays 的公开转发）。</summary>
    internal static List<EquipElement> ScanEquipArraysPublic(byte[] payload, int ps, int pe)
        => ScanEquipArrays(payload, ps, pe);

    internal static List<EquipElement> ScanEquipArrays(byte[] payload, int ps, int pe)
    {
        var result = new List<EquipElement>();
        if (pe - ps < 250) return result;
        // Python: seg = unpackbits(payload)[ps:pe]；pe 超出载荷位长时 seg 被截断。
        var l = Math.Min(pe, payload.Length * 8) - ps;
        if (l < 128) return result;

        // Python 列表索引语义：负索引回绕；越界抛 IndexError。
        int Bi(int pos)
        {
            var idx = pos < 0 ? pos + l : pos;
            if (idx < 0 || idx >= l) throw new IndexOutOfRangeException();
            return idx;
        }

        // 64 位无符号读取——uid 可达 2^63+（Python 任意精度 int），按 ulong 累积
        // 会变负数导致 uid 门恒假、整链断裂（黄金对拍 9 元素 → 0 实证）。
        // 越界即抛（对齐 Python u64v 语义）——MergeEquipScan 捕获后整块丢弃。
        ulong Rd(int pos, int n)
        {
            ulong v = 0;
            for (var j = 0; j < n; j++)
            {
                v |= (ulong)BitPrimitives.ReadBitsAt(payload, ps + Bi(pos + j), 1) << j;
            }
            return v;
        }

        // Python: packed——每字节 LSB=终止位、高 7 位数据；返回 (值, 8/16/24)。
        (ulong V, int Bits)? Packed(int pos)
        {
            ulong v = 0;
            var sh = 0;
            for (var g = 0; g < 3; g++)
            {
                if (pos + (g + 1) * 8 > l) return null;
                var enc = Rd(pos + g * 8, 8);
                v |= (enc >> 1) << sh;
                sh += 7;
                if ((enc & 1) == 0) return (v, (g + 1) * 8);
            }
            return null;
        }

        double F32(int pos)
        {
            // Python f32 = rd(pos,32) → 越界抛；此处经 Rd 同样会抛。
            var bits = (uint)Rd(pos, 32);
            return BitConverter.UInt32BitsToSingle(bits);
        }

        // 锚点：u64-LSBfirst ∈ 装备族（全偏移扫描，Python: 8 相位窗口等价）。
        var anchors = new List<int>();
        for (var c = 0; c + 64 <= l; c++)
        {
            var v = Rd(c, 64);
            if (v is >= 10000000000UL and < 100000000000UL)
            {
                var head = v.ToString()[..4];
                if (Array.IndexOf(EqFamilies, head) >= 0) anchors.Add(c);
            }
        }
        var covered = new List<int>();
        foreach (var c in anchors)
        {
            if (covered.Any(t => Math.Abs(c - t) < 96)) continue;
            // 从锚点反向走链：itemId 句柄（h≡1 mod 9，8/16 位组）。
            int? start = null;
            foreach (var back in new[] { 8, 16 })
            {
                var probe = Packed(c - back);
                if (probe is null) continue;
                var (h0, g0) = probe.Value;
                if (c - back + g0 == c && h0 >= 1 && (h0 - 1) % 9 == 0
                    && (h0 - 1) / 9 <= 40)
                {
                    start = c - back;
                    break;
                }
            }
            if (start is null) continue;
            var pos = start.Value;
            var fields = new Dictionary<long, Dictionary<int, object?>>();
            var order = new List<long>();
            for (var step = 0; step < 40; step++)
            {
                var hh = Packed(pos);
                if (hh is null) break;
                var (h, gBits) = hh.Value;
                if (h == 0)
                {
                    pos += gBits;
                    break;
                }
                var role = (long)(h % 9UL);
                var k = (long)(h / 9UL);
                object? v;
                int p2;
                if (role == 1)
                {
                    var vl = Rd(pos + gBits, 64);
                    if (vl is < 10000000000UL or >= 100000000000UL) break;
                    var head = vl.ToString()[..4];
                    if (Array.IndexOf(EqFamilies, head) < 0) break;
                    v = vl;
                    p2 = pos + gBits + 64;
                }
                else if (role == 2)
                {
                    var uv = Rd(pos + gBits, 64);
                    if (uv < (1UL << 50)) break;
                    v = uv;
                    p2 = pos + gBits + 64;
                }
                else if (role is 5L or 6L)
                {
                    var fv = F32(pos + gBits);
                    if (fv is < 0.0 or > 500.0) break;
                    v = fv;
                    p2 = pos + gBits + 32;
                }
                else break;
                if (!fields.TryGetValue(k, out var fk))
                {
                    fk = [];
                    fields[k] = fk;
                    order.Add(k);
                }
                if (fk.ContainsKey((int)role)) break;   // 同字段重复 = 链非法
                fk[(int)role] = v;
                pos = p2;
            }
            // 组元素：itemId+uid 必备，dur 成对。
            var got = new List<EquipElement>();
            foreach (var k in order)
            {
                var f = fields[k];
                if (f.TryGetValue(1, out var iid) && f.TryGetValue(2, out var uid))
                {
                    got.Add(new EquipElement(
                        Convert.ToInt64(iid), Convert.ToUInt64(uid),
                        f.TryGetValue(5, out var d5) ? Convert.ToDouble(d5) : null,
                        f.TryGetValue(6, out var d6) ? Convert.ToDouble(d6) : null,
                        k));
                }
            }
            if (got.Count >= 2)
            {
                result.AddRange(got);
                covered.Add(c);
            }
        }
        return result;
    }

    // ---- 快照 ----

    private long[] SeqsOf(int slot, List<SlotSample> tr)
    {
        if (_seqsCache.TryGetValue(slot, out var cached) && cached.Length == tr.Count) return cached;
        var arr = tr.Select(t => t.Seq).ToArray();
        _seqsCache[slot] = arr;
        return arr;
    }

    /// <summary>槽位时点位置/朝向/轨迹（Python: _slot_pos；tail-follow 快速路径）。</summary>
    private (List<double>? Pos, double? Yaw, List<List<double>>? Trail) SlotPos(
        int? slot, List<SlotSample>? tr, long cur)
    {
        if (slot is null || tr is null || tr.Count == 0) return (null, null, null);
        var n = tr.Count;
        if (tr[^1].Seq <= cur)
        {
            var rec = tr[^1];
            var j0 = n > 160 ? n - 160 : 0;
            var trail = new List<List<double>>();
            for (var i = j0; i < n; i += 4) trail.Add([tr[i].X, tr[i].Y, tr[i].Z]);
            return ([rec.X, rec.Y, rec.Z], rec.Yaw, trail);
        }
        var lo = 0;
        var hi = n;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (tr[mid].Seq <= cur) lo = mid + 1;
            else hi = mid;
        }
        var j = lo - 1;
        if (j < 0) return (null, null, null);
        var rec2 = tr[j];
        var jj = Math.Min(j + 2, n);
        var trail2 = new List<List<double>>();
        for (var i = Math.Max(0, jj - 160); i < jj; i += 4) trail2.Add([tr[i].X, tr[i].Y, tr[i].Z]);
        return ([rec2.X, rec2.Y, rec2.Z], rec2.Yaw, trail2);
    }

    /// <summary>血量快照（Python: _hp_of）——出口防御性校验。</summary>
    private Dictionary<string, object?>? HpOf(ulong? actor)
    {
        if (actor is null) return null;
        if (!Health.TryGetValue(actor.Value, out var hrec)) return null;
        var parts = (hrec.TryGetValue("parts", out var p) && p is Dictionary<string, object?> pd)
            ? pd : new Dictionary<string, object?>();
        var output = new Dictionary<string, object?>();
        var total = parts.TryGetValue("Health", out var t) ? t : null;
        var maximum = parts.TryGetValue("MaxHealth", out var m) ? m : null;
        if (total is (long or double) && double.IsFinite(Convert.ToDouble(total))
            && Convert.ToDouble(total) >= 0)
        {
            double? mx = null;
            if (maximum is (long or double) && double.IsFinite(Convert.ToDouble(maximum))
                && Convert.ToDouble(maximum) > 0
                && Convert.ToDouble(maximum) >= Convert.ToDouble(total))
            {
                mx = Convert.ToDouble(maximum);
            }
            output["total"] = new List<object?>
            {
                Math.Round(Convert.ToDouble(total), 2), mx,
            };
            output["source"] = hrec.TryGetValue("sources", out var srcs)
                && srcs is Dictionary<string, object?> sd
                && sd.TryGetValue("Health", out var sh) ? sh : "attribute_set";
            output["seq"] = hrec.TryGetValue("part_seq", out var ps)
                && ps is Dictionary<string, long> psd
                && psd.TryGetValue("Health", out var ph) ? (object?)ph : null;
        }
        foreach (var (pk, wn) in HpParts)
        {
            var cv = parts.TryGetValue(wn, out var cvObj) ? cvObj : null;
            var mv = parts.TryGetValue("Max" + wn, out var mvObj) ? mvObj : null;
            if (cv is null && mv is null) continue;
            double mx2 = mv is (long or double) && Convert.ToDouble(mv) is >= 5.0 and <= 500.0
                ? Convert.ToDouble(mv) : HpPartMax[pk];
            double? cvd = null;
            if (cv is (long or double))
            {
                var d = Convert.ToDouble(cv);
                if (!double.IsFinite(d) || d < 0.0 || d > mx2
                    || (d > 0.0 && d < HpValMin))
                {
                    cvd = null;   // 脏值兜底：视作未上报
                }
                else cvd = d;
            }
            if (cvd is null) continue;
            output[pk] = new List<object?>
            {
                Math.Round(cvd.Value, 1), Math.Round(mx2, 1),
            };
        }
        return output.Count > 0 ? output : null;
    }

    /// <summary>装备快照（Python: _gear_of）——头甲/防弹衣等级 + 耐久 + 背包/胸挂名 + 首装备武器。</summary>
    private (int Hl, int Vl, string? WName, List<object?>? HDur, List<object?>? VDur,
        string? Bp, string? Cr) GearOf(ulong? actor,
        Dictionary<ulong, (int Hl, int Vl, string? WName, List<object?>? HDur, List<object?>? VDur,
            string? Bp, string? Cr)> cache)
    {
        if (actor is null) return (0, 0, null, null, null, null, null);
        if (cache.TryGetValue(actor.Value, out var hit)) return hit;
        var hl = 0;
        var vl = 0;
        var bestH = -1L;
        var bestV = -1L;
        string? bpId = null;
        string? crId = null;
        var bestBp = -1L;
        var bestCr = -1L;
        var hdurC = new List<double>();
        var vdurC = new List<double>();
        var hdurMaxC = new List<double>();
        var vdurMaxC = new List<double>();
        if (Equip.TryGetValue(actor.Value, out var erec))
        {
            foreach (var slotName in new[] { "armor", "items" })
            {
                if (!erec.TryGetValue(slotName, out var bucket)) continue;
                foreach (var f in bucket.Values)
                {
                    var iid = f.TryGetValue("item_id", out var i) ? i?.ToString() : "";
                    var dur = f.TryGetValue("dur", out var d) ? d : null;
                    var dmx = f.TryGetValue("dur_max", out var dm) ? dm : null;
                    if (dmx is (long or double) && Convert.ToDouble(dmx) is >= 0 and <= 500)
                    {
                        if (iid.StartsWith("1101", StringComparison.Ordinal)) hdurMaxC.Add(Convert.ToDouble(dmx));
                        else if (iid.StartsWith("1105", StringComparison.Ordinal)) vdurMaxC.Add(Convert.ToDouble(dmx));
                    }
                    if (dur is (long or double) && Convert.ToDouble(dur) >= 0)
                    {
                        if (iid.StartsWith("1101", StringComparison.Ordinal)) hdurC.Add(Convert.ToDouble(dur));
                        else if (iid.StartsWith("1105", StringComparison.Ordinal)) vdurC.Add(Convert.ToDouble(dur));
                    }
                    if (iid.Length != 11
                        || !(iid.StartsWith("1101") || iid.StartsWith("1105")
                             || iid.StartsWith("1107") || iid.StartsWith("1108")))
                    {
                        continue;
                    }
                    var sq = f.TryGetValue("seq", out var s) ? Convert.ToInt64(s) : 0L;
                    if (iid.StartsWith("1107") || iid.StartsWith("1108"))
                    {
                        // 胸挂/背包只取名字；换装取 seq 最新。
                        if (iid.StartsWith("1108"))
                        {
                            if (sq >= bestBp) { bpId = iid; bestBp = sq; }
                        }
                        else if (sq >= bestCr) { crId = iid; bestCr = sq; }
                        continue;
                    }
                    if (!int.TryParse(iid.Substring(6, 2), out var lv) || lv is < 1 or > 6) continue;
                    if (iid.StartsWith("1101"))
                    {
                        if (sq >= bestH) { hl = lv; bestH = sq; }
                    }
                    else if (sq >= bestV) { vl = lv; bestV = sq; }
                }
            }
        }
        // 耐久合成：cur 优先装备记录（全员可达），max 用 health Max（缺席时 dur_max 兜底）。
        var parts = Health.TryGetValue(actor.Value, out var hrec)
            && hrec.TryGetValue("parts", out var pp) && pp is Dictionary<string, object?> pd
            ? pd : new Dictionary<string, object?>();
        var hh = parts.TryGetValue("HelmetArmorHealth", out var h1) ? h1 : null;
        var hm = parts.TryGetValue("MaxHelmetArmorHealth", out var h2) ? h2 : null;
        var ah = parts.TryGetValue("ArmorHealth", out var a1) ? a1 : null;
        var am = parts.TryGetValue("MaxArmorHealth", out var a2) ? a2 : null;
        double? hmD = hm is (long or double) ? Convert.ToDouble(hm)
            : hdurMaxC.Count > 0 ? hdurMaxC.Max() : null;
        double? amD = am is (long or double) ? Convert.ToDouble(am)
            : vdurMaxC.Count > 0 ? vdurMaxC.Max() : null;
        double? hcD = hh is (long or double) ? Convert.ToDouble(hh)
            : hdurC.Count > 0
                ? hdurC.Where(d => hmD is null || d <= Math.Max(hmD.Value, 1) * 1.05)
                    .DefaultIfEmpty(0).Max()
                : null;
        double? vcD = ah is (long or double) ? Convert.ToDouble(ah)
            : vdurC.Count > 0
                ? vdurC.Where(d => amD is null || d <= Math.Max(amD.Value, 1) * 1.05)
                    .DefaultIfEmpty(0).Max()
                : null;
        List<object?>? hdur = hcD is not null || hmD is not null
            ? [hcD is not null ? Math.Round(hcD.Value, 1) : (object?)null,
               hmD is not null ? Math.Round(hmD.Value, 1) : (object?)null]
            : null;
        List<object?>? vdur = vcD is not null || amD is not null
            ? [vcD is not null ? Math.Round(vcD.Value, 1) : (object?)null,
               amD is not null ? Math.Round(amD.Value, 1) : (object?)null]
            : null;
        var wid = Weapons.TryGetValue(actor.Value, out var wrec)
            && wrec.TryGetValue("first", out var wf) ? wf : null;
        string? wname = null;
        if (wid is not null && long.TryParse(wid.ToString(), out var widL))
        {
            WeaponNames.TryGetValue(widL, out wname);
        }
        var bp = bpId is not null && GearNames.TryGetValue(bpId, out var bpN) ? bpN : null;
        var cr = crId is not null && GearNames.TryGetValue(crId, out var crN) ? crN : null;
        var tuple = (hl, vl, wname, hdur, vdur, bp, cr);
        cache[actor.Value] = tuple;
        return tuple;
    }

    /// <summary>倒地兜底（Python: _dying_fallback）。</summary>
    private (string? Label, string? Key) DyingFallback(ulong actor)
    {
        var state = LifeStateForActor(actor);
        return state["downed"] is true ? ("倒地", "down") : (null, null);
    }

    /// <summary>
    /// 快照（Python: snapshot；tail-follow 游标 = 最新 seq）。light=true 时跳过
    /// FSM（admin 总览只需 ents 数/self 名）。
    /// </summary>
    /// <summary>诊断：最近一次快照组装的丢弃原因计数。</summary>
    public Dictionary<string, int>? SnapDropStats;

    /// <summary>诊断：最近一个 spawn-keep 拒绝的判定数值。</summary>
    public string? SnapKeepDbg;

    public Dictionary<string, object?> Snapshot(bool light = false)
    {
        var cur = LatestSeq() ?? 0;
        var outEnts = new List<Dictionary<string, object?>>();
        var deadSlots = new HashSet<int>();
        // 黄金对拍：快照丢弃原因计数
        int nDropDeath = 0, nDropNoPos = 0, nDropSpawnKeep = 0, nDropStale = 0, nKeep = 0;
        List<double>? selfXyz = null;
        double selfYaw = 0.0;
        string? selfName = null;
        var names = new List<Dictionary<string, object?>>();
        Dictionary<string, object?> meta;
        var goneCount = 0;
        ulong? selfActor = null;

        lock (_lock)
        {
            var silentMap = StateTrack.SlotStaleness(Slots, cur);
            // 一个死亡判定共享给标记移除与盒子命名；倒地/待机不进此表。
            var deadMap = new Dictionary<int, (long Seq, double V, object? None)>();
            foreach (var (slot, actor) in Claim)
            {
                var deathSeq = ConfirmedDeathSeq(actor);
                if (deathSeq is not null && !SlotOutlivedDeath(actor, slot))
                {
                    deadMap[slot] = (deathSeq.Value, 0.0, null);
                }
            }
            var fsm = light ? new Dictionary<ulong, Dictionary<string, object?>>() : FsmSnapshot();
            var gearCache = new Dictionary<ulong,
                (int Hl, int Vl, string? WName, List<object?>? HDur, List<object?>? VDur,
                    string? Bp, string? Cr)>();

            List<double>? SelfActorPos() => null;
            var selfEnt = Ents.FirstOrDefault(e => e["kind"]?.ToString() == "self");
            selfActor = selfEnt is not null && selfEnt.TryGetValue("actor", out var sa)
                ? Convert.ToUInt64(sa) : null;
            if (selfActor is null && SelfSlot is not null)
            {
                foreach (var (slot2, actor2) in Claim)
                {
                    if (slot2 == SelfSlot) { selfActor = actor2; break; }
                }
            }
            if (selfEnt is not null)
            {
                (selfXyz, var yaw0, _) = SlotPos(SelfSlot,
                    SelfSlot is not null ? Slots.GetValueOrDefault(SelfSlot.Value) : null, cur);
                selfYaw = yaw0 ?? 0.0;
                selfName = selfEnt["name"]?.ToString();
            }
            else
            {
                // self 实体不存在：self_slot 已由 field13 兜底确定。
                if (SelfSlot is not null)
                {
                    (selfXyz, var yaw0, _) = SlotPos(SelfSlot.Value,
                        Slots.GetValueOrDefault(SelfSlot.Value), cur);
                    selfYaw = yaw0 ?? 0.0;
                }
                if (SelfName is not null) selfName = SelfName + "(self)";
            }
            // 自机坐标兜底：field13"我"轨迹（闭世界失效对局）。
            if (selfXyz is null && F13Self is not null)
            {
                selfXyz = F13Self.Value.Loc.ToList();
            }
            // 自机坐标无效时回退到轨迹里最近一个有效采样。
            if (selfXyz is not null && !World(selfXyz))
            {
                selfXyz = null;
                if (SelfSlot is not null && Slots.TryGetValue(SelfSlot.Value, out var selfTr))
                {
                    foreach (var rec in selfTr.AsEnumerable().Reverse())
                    {
                        var cand = new List<double> { rec.X, rec.Y, rec.Z };
                        if (World(cand)) { selfXyz = cand; break; }
                    }
                }
            }
            // 2s 一条的跳变诊断（Python: _dbg_self_xyz）。
            try
            {
                if (_dbgSelfXyz is not null && selfXyz is not null
                    && World(selfXyz) && World(_dbgSelfXyz))
                {
                    var jump = Math.Sqrt(
                        (selfXyz[0] - _dbgSelfXyz[0]) * (selfXyz[0] - _dbgSelfXyz[0])
                        + (selfXyz[1] - _dbgSelfXyz[1]) * (selfXyz[1] - _dbgSelfXyz[1]));
                    var nowDbg = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
                    if (Debug && jump > 20000.0 && nowDbg - _dbgSelfT > 2.0)
                    {
                        _dbgSelfT = nowDbg;
                    }
                }
                if (selfXyz is not null && World(selfXyz)) _dbgSelfXyz = selfXyz.ToList();
            }
            catch
            {
                // 诊断日志永不影响主流程。
            }
            var curTs0 = _seqTs.TryGetValue(cur, out var ct) ? ct : 0.0;
            foreach (var e in Ents)
            {
                var kind = e["kind"]?.ToString();
                if (kind == "self") continue;
                var slot = e.TryGetValue("slot", out var sObj) && sObj is int s2 ? s2 : (int?)null;
                var actor = e.TryGetValue("actor", out var aObj) ? Convert.ToUInt64(aObj) : 0UL;
                var slotReused = e.TryGetValue("slot_reused", out var sr) && sr is true
                                 || SlotOutlivedDeath(actor, slot ?? -1);
                var hasGoneE = Gone.TryGetValue(actor, out var goneE);
                var outOfRange = hasGoneE && goneE.Reason == 3 && !slotReused;
                if (ConfirmedDeathSeq(actor) is not null || ActorRemoved(actor))
                {
                    if (slot is not null && !slotReused) deadSlots.Add(slot.Value);
                    nDropDeath++;
                    continue;
                }
                var (pos, yaw, trail) = SlotPos(slot,
                    slot is not null ? Slots.GetValueOrDefault(slot.Value) : null, cur);
                // 静态 spawn 标记（无槽位实体的唯一坐标来源）。
                double? spAge = null;
                if (e.TryGetValue("spawn_seq", out var spSeqObj) && spSeqObj is not null)
                {
                    var spSeq = Convert.ToInt64(spSeqObj);
                    var spTs = _seqTs.TryGetValue(spSeq, out var st2) ? st2 : 0.0;
                    if (spTs > 0 && curTs0 > 0) spAge = Math.Max(0.0, curTs0 - spTs);
                }
                // spawn_loc 可能是 CondVector 的 double[] 或 List<object?>（Python 动态列表的两种 C# 形态）。
                var spawnLoc = e.TryGetValue("spawn_loc", out var sl) ? sl switch
                {
                    List<object?> lol => lol,
                    double[] da => da.Select(d => (object?)d).ToList(),
                    IEnumerable<double> de => de.Select(d => (object?)d).ToList(),
                    _ => null,
                } : null;
                var entName = e.TryGetValue("name", out var en) ? en?.ToString() : null;
                var entKind = e["kind"]?.ToString() ?? "";
                if (pos is null && slot is null && spawnLoc is not null
                    && !string.IsNullOrEmpty(entName)
                    && e.TryGetValue("spawn_seq", out var ss3) && ss3 is not null)
                {
                    var spawnSeq = Convert.ToInt64(ss3);
                    bool keep;
                    if (entKind == "ai")
                    {
                        keep = (spAge is not null && spAge <= AiSpawnKeepSec)
                               || (spAge is null && cur - spawnSeq < 600);
                    }
                    else
                    {
                        keep = cur - spawnSeq < 600;
                    }
                    if (!keep)
                    {
                        SnapKeepDbg = $"ent{actor}: spAge={spAge?.ToString("F1") ?? "null"} curTs0={curTs0} cur={cur} spawnSeq={spawnSeq} keepSec={AiSpawnKeepSec}";
                        nDropSpawnKeep++;
                        continue;
                    }
                    var sp = spawnLoc.Select(v => Convert.ToDouble(v)).ToList();
                    var (gh, gv, gw, hd, vd, bp, cr) = GearOf(actor, gearCache);
                    var cw = WeaponState.WeaponFields(WeaponTrackerInst, actor, WeaponNames);
                    var ent = new Dictionary<string, object?>
                    {
                        ["key"] = "spawn-" + actor, ["name"] = entName, ["kind"] = entKind,
                        ["xyz"] = sp, ["world"] = sp.ToList(),
                        ["rel"] = selfXyz is not null
                            ? new List<double> { sp[0] - selfXyz[0], sp[1] - selfXyz[1], sp[2] - selfXyz[2] }
                            : null,
                        ["yaw"] = null, ["slot"] = null, ["ch"] = actor, ["eid"] = actor,
                        ["trail"] = null,
                        ["team"] = e.TryGetValue("team", out var tm) ? tm : null,
                        ["camp"] = e.TryGetValue("camp", out var cm) ? cm : null,
                        ["is_bot"] = e.TryGetValue("is_bot", out var ib) ? ib : false,
                        ["helmet"] = gh, ["vest"] = gv, ["weapon"] = gw,
                        ["helmet_dur"] = hd, ["vest_dur"] = vd,
                        ["bp"] = bp, ["cr"] = cr,
                        ["spawn_mark"] = true,
                        ["age_sec"] = spAge is not null ? Math.Round(spAge.Value, 1) : null,
                        ["out_of_range"] = false,
                        ["hp"] = HpOf(actor),
                    };
                    foreach (var kv in cw) ent[kv.Key] = kv.Value;
                    outEnts.Add(ent);
                    if (!string.IsNullOrEmpty(entName))
                    {
                        names.Add(new Dictionary<string, object?>
                        {
                            ["ch"] = null, ["name"] = entName, ["kind"] = entKind,
                        });
                    }
                    continue;
                }
                if (pos is null) { nDropNoPos++; continue; }
                var rel = selfXyz is not null
                    ? new List<double> { pos[0] - selfXyz[0], pos[1] - selfXyz[1], pos[2] - selfXyz[2] }
                    : null;
                var silent = slot is not null && silentMap.TryGetValue(slot.Value, out var sil)
                    ? sil : null;
                var isMate = slot is not null && MateSlots.ContainsKey(slot.Value);
                // 槽停更隐藏（真实时间戳门控；死亡豁免）。
                var trStale = slot is not null ? Slots.GetValueOrDefault(slot.Value) : null;
                var staleTs = trStale is { Count: > 0 } ? trStale[^1].Ts : 0.0;
                var curTs = _seqTs.TryGetValue(cur, out var cts) ? cts : 0.0;
                var silentSec = staleTs > 0 && curTs > 0 ? curTs - staleTs : 0.0;
                if (silentSec > StaleHideSec && (slot is null || !deadMap.ContainsKey(slot.Value)))
                {
                    nDropStale++;
                    continue;
                }
                if (slotReused)
                {
                    // 槽被复用：退化为匿名未知玩家槽。
                    var anonKind2 = isMate ? "mate" : "unknown";
                    outEnts.Add(new Dictionary<string, object?>
                    {
                        ["key"] = $"slot-{slot}", ["name"] = "未知玩家", ["kind"] = anonKind2,
                        ["xyz"] = pos, ["world"] = pos.ToList(), ["rel"] = rel, ["yaw"] = yaw,
                        ["slot"] = slot, ["ch"] = slot, ["eid"] = 0, ["trail"] = trail,
                        ["dead"] = false, ["out_of_range"] = false, ["hp"] = null,
                        ["silent_seq"] = silent ?? 0, ["death_box"] = null,
                    });
                    continue;
                }
                var isDead = slot is not null && deadMap.ContainsKey(slot.Value);
                if (isDead)
                {
                    if (slot is not null) deadSlots.Add(slot.Value);
                    nDropDeath++;
                    continue;   // 死亡落点由死亡盒表达
                }
                var kind2 = isMate ? "mate" : entKind;
                var fsmRec = fsm.TryGetValue(actor, out var fr) ? fr : new Dictionary<string, object?>();
                var (sLbl, sKey) = FightStatus(
                    fsmRec.TryGetValue("fight", out var fObj) && fObj is Dictionary<string, object?> fd
                        ? fd : new Dictionary<string, object?>(),
                    fsmRec.TryGetValue("fsm_states", out var fsObj) && fsObj is List<object?> fs
                        ? fs : null);
                // FSM 无状态时血量兜底；已判死者不再标倒地。
                if (sKey is null && !isDead)
                {
                    (sLbl, sKey) = DyingFallback(actor);
                }
                var (gh2, gv2, gw2, hd2, vd2, bp2, cr2) = GearOf(actor, gearCache);
                var cw2 = WeaponState.WeaponFields(WeaponTrackerInst, actor, WeaponNames);
                var fsmStates = fsmRec.TryGetValue("fsm_states", out var fs2b) && fs2b is List<object?> fs2l
                    ? fs2l : new List<object?>();
                var fightDict = fsmRec.TryGetValue("fight", out var fg2) && fg2 is Dictionary<string, object?> fg2d
                    ? fg2d : new Dictionary<string, object?>();
                var ent2 = new Dictionary<string, object?>
                {
                    ["key"] = $"slot-{slot}", ["name"] = entName, ["kind"] = kind2,
                    ["xyz"] = pos, ["world"] = pos.ToList(), ["rel"] = rel, ["yaw"] = yaw,
                    ["slot"] = slot, ["ch"] = slot, ["eid"] = actor, ["trail"] = trail,
                    ["dead"] = isDead,
                    ["out_of_range"] = outOfRange,
                    ["hp"] = HpOf(actor),
                    ["helmet"] = gh2, ["vest"] = gv2, ["weapon"] = gw2,
                    ["helmet_dur"] = hd2, ["vest_dur"] = vd2,
                    ["bp"] = bp2, ["cr"] = cr2,
                    ["status"] = sLbl, ["status_key"] = sKey,
                    ["silent_seq"] = silent ?? 0,
                    ["death_box"] = slot is not null && deadMap.TryGetValue(slot.Value, out var db)
                        ? db.Seq : null,
                    ["fsm_states"] = fsmStates,
                    ["fight"] = fightDict,
                    ["team"] = e.TryGetValue("team", out var t7) ? t7 : null,
                    ["camp"] = e.TryGetValue("camp", out var c7) ? c7 : null,
                    ["is_bot"] = e.TryGetValue("is_bot", out var ib2) ? ib2 : false,
                    ["hero"] = e.TryGetValue("hero", out var h7) ? h7 : null,
                };
                foreach (var kv in cw2) ent2[kv.Key] = kv.Value;
                outEnts.Add(ent2);
                if (!string.IsNullOrEmpty(entName))
                {
                    names.Add(new Dictionary<string, object?>
                    {
                        ["ch"] = slot, ["name"] = entName, ["kind"] = kind2,
                    });
                }
            }
            var bound = outEnts.Where(x => x.TryGetValue("slot", out var s) && s is not null)
                .Select(x => (int)x["slot"]!).ToHashSet();
            // 消失角色认领的槽：关闭后仍有新采样 = 复用，解除压制上屏（匿名）。
            var claimantOf = new Dictionary<int, ulong>();
            foreach (var (slot2, actor2) in Claim) claimantOf[slot2] = actor2;
            foreach (var (s, tr) in Slots)
            {
                if (bound.Contains(s) || s == SelfSlot || !MemberSlots.Contains(s)
                    || deadSlots.Contains(s))
                {
                    continue;
                }
                var ca = claimantOf.TryGetValue(s, out var c3) ? c3 : (ulong?)null;
                var reused2 = ca is not null && SlotOutlivedDeath(ca.Value, s);
                if (ca is not null && (ConfirmedDeathSeq(ca.Value) is not null || ActorRemoved(ca.Value))
                    && !reused2)
                {
                    continue;
                }
                if (reused2) ca = null;
                if (tr.Count == 0) continue;
                SlotSample rec;
                if (tr[^1].Seq <= cur) rec = tr[^1];
                else
                {
                    var lo = 0;
                    var hi = tr.Count;
                    while (lo < hi)
                    {
                        var mid = (lo + hi) / 2;
                        if (tr[mid].Seq <= cur) lo = mid + 1;
                        else hi = mid;
                    }
                    var j = lo - 1;
                    if (j < 0) continue;
                    rec = tr[j];
                }
                var silent = silentMap.TryGetValue(s, out var sil2) ? sil2 : null;
                var isMate = MateSlots.ContainsKey(s);
                var isDead = deadMap.ContainsKey(s);
                var staleTs2 = tr[^1].Ts;
                var curTs2 = _seqTs.TryGetValue(cur, out var cts2) ? cts2 : 0.0;
                var silentSec2 = staleTs2 > 0 && curTs2 > 0 ? curTs2 - staleTs2 : 0.0;
                if (silentSec2 > StaleHideSec && !isDead && !isMate) continue;
                var anonP = reused2 ? null
                    : AnonNames.TryGetValue(s, out var ap2) ? ap2 : null;
                if (isDead)
                {
                    deadSlots.Add(s);
                    continue;
                }
                string anonName;
                string anonKind;
                if (anonP is not null)
                {
                    anonName = anonP.TryGetValue("name", out var an) ? an?.ToString() ?? "未知玩家" : "未知玩家";
                    anonKind = "player";
                }
                else
                {
                    anonName = "未知玩家";
                    anonKind = "unknown";
                }
                if (isMate) anonKind = "mate";
                var (dsLbl, dsKey) = (!isDead && ca is not null)
                    ? DyingFallback(ca.Value) : ((string?)null, (string?)null);
                var goneCa = ca is not null ? Gone.GetValueOrDefault(ca.Value) : default;
                var oor = goneCa.Reason == 3 && !reused2 && !isDead;
                outEnts.Add(new Dictionary<string, object?>
                {
                    ["key"] = $"anon-{s}", ["name"] = anonName, ["kind"] = anonKind,
                    ["xyz"] = new List<double> { rec.X, rec.Y, rec.Z },
                    ["world"] = new List<double> { rec.X, rec.Y, rec.Z },
                    ["rel"] = null, ["yaw"] = rec.Yaw, ["slot"] = s, ["ch"] = s,
                    ["eid"] = 0, ["trail"] = null,
                    ["dead"] = isDead,
                    ["out_of_range"] = oor,
                    ["status"] = dsLbl, ["status_key"] = dsKey,
                    ["hp"] = HpOf(ca),
                    ["team"] = anonP is not null && anonP.TryGetValue("team", out var at) ? at : null,
                    ["camp"] = anonP is not null && anonP.TryGetValue("camp", out var ac) ? ac : null,
                    ["is_bot"] = ConfirmedPlayerBot(anonP,
                        anonP is not null && anonP.TryGetValue("hero_id", out var ah)
                        && ah is not null && HeroNames.ContainsKey(Convert.ToInt64(ah))),
                    ["hero"] = anonP is not null && anonP.TryGetValue("hero_id", out var ah2)
                        && ah2 is not null && HeroNames.TryGetValue(Convert.ToInt64(ah2), out var ahn)
                        ? ahn : null,
                    ["silent_seq"] = silent ?? 0,
                    ["death_box"] = deadMap.TryGetValue(s, out var db2) ? db2.Seq : null,
                });
            }
            var filtered = new List<Dictionary<string, object?>>();
            foreach (var entity in outEnts)
            {
                // eid 装箱类型必须宽松：actor 是 ulong（>long.MaxValue 的真实 guid），
                // 若只认 `is long` 则恒假 → life_* 富化与死亡/移除过滤整段失效
                // （实测：快照缺 life_state/life_status/life_seq/life_source 且多留死亡实体）。
                var eid = EntityId(entity);
                if (eid > 0)
                {
                    var life = LifeStateForActor(eid);
                    entity["life_state"] = life["state"];
                    entity["life_status"] = life["status"];
                    entity["life_seq"] = life["seq"];
                    entity["life_source"] = life["source"];
                    if (life["downed"] is true)
                    {
                        entity["status"] = "倒地";
                        entity["status_key"] = "down";
                    }
                    if (entity["kind"]?.ToString() != "self"
                        && (life["dead"] is true || ActorRemoved(eid)))
                    {
                        continue;
                    }
                }
                if ((entity.TryGetValue("key", out var k) ? k?.ToString() : "")?.StartsWith("anon-") == true
                    && entity.TryGetValue("slot", out var sl3) && ToSlotIndex(sl3) is { } s3
                    && deadMap.ContainsKey(s3))
                {
                    continue;
                }
                filtered.Add(entity);
            }
            outEnts = filtered;
            nKeep = outEnts.Count;
            SnapDropStats = new Dictionary<string, int>
            {
                ["dropDeath"] = nDropDeath, ["dropNoPos"] = nDropNoPos,
                ["dropSpawnKeep"] = nDropSpawnKeep, ["dropStale"] = nDropStale,
                ["keep"] = nKeep,
            };
            var positions = new Dictionary<ulong, List<double>>();
            foreach (var (slot, actor) in Claim)
            {
                if (Slots.TryGetValue(slot, out var t) && t.Count > 0)
                {
                    positions[actor] = [t[^1].X, t[^1].Y, t[^1].Z];
                }
            }
            foreach (var (actor, record) in Boxes)
            {
                if ((record.TryGetValue("hidden", out var hd3) && hd3 is true)
                    || (record.TryGetValue("gone_seq", out var gs3) && gs3 is not null))
                {
                    continue;
                }
                var posW = BoxTracker.WorldPosition(actor, positions);
                // 位置载体多形态：WorldPosition 返回 List<double>，record["loc"]
                // 可能是 List<double>/List<object?>（Python 动态列表的 C# 形态）。
                var posObj = posW is not null ? (object?)posW.ToList()
                    : record.TryGetValue("loc", out var l4) ? l4 : null;
                var posD = DeathBoxState.PointAsList(posObj);
                if (posD is null || !World(posD)) continue;
                var owner = ConfirmedBoxOwner(record);
                string? name2 = null;
                if (owner is not null) (name2, _) = CharNameKind(owner.Value);
                if (string.IsNullOrEmpty(name2))
                {
                    name2 = record.TryGetValue("player_name", out var pn2) ? pn2?.ToString() : null;
                    if (!string.IsNullOrEmpty(name2)
                        && (name2!.Contains("LOCTABLE") || name2.Contains("/Game/")))
                    {
                        var key = AiNameTable.ExtractRefKey(Encoding.ASCII.GetBytes(name2));
                        name2 = key is not null && AiNames.TryGetValue(key.Value, out var an3) ? an3 : null;
                    }
                }
                var entity = DeathBoxState.BoxEntity(
                    actor, record, posD, owner, name2, selfXyz, AiNames);
                foreach (var kv in BoxContents.Describe(actor, LootNames)) entity[kv.Key] = kv.Value;
                if (owner is not null)
                {
                    if (record.TryGetValue("deceased_actor", out var da) && da is not null)
                    {
                        entity["is_bot"] = CharIsBot(owner);
                        if (entity.TryGetValue("is_ai", out var ia) && ia is null
                            && CharNameKind(owner.Value).Kind == "ai")
                        {
                            entity["is_ai"] = true;
                        }
                    }
                    var ownerLife = LifeStateForActor(owner.Value);
                    entity["owner_dead"] = ownerLife["confirmed"] is true
                        ? ownerLife["dead"] : null;
                }
                outEnts.Add(entity);
            }
            // Python: time.time() - last_data_ts <= 20——live 下墙钟≈数据钟；
            // 重放/对拍时用引擎注入的数据时间 NowTs（宿主每包更新），语义一致。
            var lootVisible = _lastDataTs <= 0.0
                              || NowTs - _lastDataTs <= GameEndStaleSec;
            if (lootVisible)
            {
                foreach (var container in ContainerTrackerInst.Active.Values)
                {
                    var c = new Dictionary<string, object?>(container);
                    // world 是 CondVector 输出转表（List<double> 多形态），统一非泛型读取。
                    var cw = DeathBoxState.PointAsList(c.TryGetValue("world", out var w) ? w : null);
                    if (cw is not null && selfXyz is not null)
                    {
                        c["rel"] = cw.Select((v, i) => v - selfXyz[i]).ToList();
                    }
                    else
                    {
                        c["rel"] = null;
                    }
                    outEnts.Add(c);
                }
                foreach (var (la, lr) in PickupTrackerInst.Active)
                {
                    // Python: lp = lr["loc"]（动态列表直接下标）；C# loc 载体是
                    // List<double>（PickupState 存储形态），必须 PointAsList 消化。
                    var lpD = DeathBoxState.PointAsList(lr.TryGetValue("loc", out var lpObj) ? lpObj : null);
                    if (lpD is null) continue;
                    var lootEnt = new Dictionary<string, object?>
                    {
                        ["key"] = "loot-" + la,
                        ["name"] = (lr.TryGetValue("name", out var ln) ? ln?.ToString() : null) ?? "物资",
                        ["kind"] = "loot", ["xyz"] = lpD, ["world"] = lpD.ToList(),
                        ["rel"] = selfXyz is not null
                            ? new List<double> { lpD[0] - selfXyz[0], lpD[1] - selfXyz[1], lpD[2] - selfXyz[2] }
                            : null,
                        ["yaw"] = null, ["slot"] = null, ["ch"] = la, ["eid"] = la, ["trail"] = null,
                    };
                    foreach (var kv in LootDisplayFields(lr)) lootEnt[kv.Key] = kv.Value;
                    outEnts.Add(lootEnt);
                }
            }
            var healthBatchesStats = new Dictionary<string, object?>();
            foreach (var kv in HealthBatches.Stats) healthBatchesStats[kv.Key] = (long)kv.Value;
            var lifeStats = new Dictionary<string, object?>();
            foreach (var kv in LifeTracker.Stats) lifeStats[kv.Key] = (long)kv.Value;
            var psStats = new Dictionary<string, object?>();
            foreach (var kv in PsDecodeStats) psStats[kv.Key] = (long)kv.Value;
            var weaponStats = new Dictionary<string, object?>();
            foreach (var kv in WeaponTrackerInst.Stats) weaponStats[kv.Key] = (long)kv.Value;
            var pickupStats = new Dictionary<string, object?>();
            foreach (var kv in PickupTrackerInst.Stats) pickupStats[kv.Key] = (long)kv.Value;
            var boxStats = new Dictionary<string, object?>();
            foreach (var kv in BoxTracker.Stats) boxStats[kv.Key] = (long)kv.Value;
            var boxContentsStats = new Dictionary<string, object?>();
            foreach (var kv in BoxContents.Stats) boxContentsStats[kv.Key] = (long)kv.Value;
            meta = new Dictionary<string, object?>
            {
                ["mra_ch"] = MraCh, ["n_lb"] = NLb,
                ["bindings_move_handle"] = Claim.Values.Count(a =>
                    Links.TryGetValue(a, out var l) && l.ContainsKey("move_handle")),
                ["health_batches"] = healthBatchesStats,
                ["life_decode"] = lifeStats,
                ["playerstate_decode"] = psStats,
                ["weapon_decode"] = weaponStats,
                ["pickup_decode"] = pickupStats,
                ["box_decode"] = boxStats,
                ["box_contents_decode"] = boxContentsStats,
                ["boxes_removed"] = BoxTracker.RemovedCount,
                ["loot_gone"] = !lootVisible,
                ["loot_confirmed_gone"] = PickupTrackerInst.Removed.Count,
                ["loot_confirmed_pickups"] = PickupTrackerInst.Removed.Values
                    .Count(r => r.TryGetValue("pickup_confidence", out var pc) && Equals(pc, "confirmed")),
                ["loot_pickup_candidates"] = PickupTrackerInst.Active.Values
                    .Count(r => r.TryGetValue("pickup_candidate_seq", out var pcs) && pcs is not null),
            };
            goneCount = Gone.Count;
        }
        foreach (var entity in outEnts)
        {
            if (entity.TryGetValue("hp", out var hp) && hp is Dictionary<string, object?> hpDict
                && hpDict.Count > 0)
            {
                var isOperator = (entity.TryGetValue("kind", out var k2) && k2?.ToString() is "player" or "mate" or "self")
                                 || (entity.TryGetValue("hero", out var h7) && h7 is not null);
                entity["hp"] = PropertyHealth.OperatorHealth(hpDict, isOperator);
            }
        }
        var selfHp = selfActor is not null ? HpOf(selfActor) : null;
        if (selfHp is not null && selfHp.Count > 0)
        {
            selfHp = PropertyHealth.OperatorHealth(selfHp, true);
        }
        return new Dictionary<string, object?>
        {
            ["self"] = selfXyz, ["self_yaw"] = selfYaw, ["self_name"] = selfName,
            ["self_src"] = SelfSrc,
            ["self_hp"] = selfHp,
            ["self_life"] = selfActor is not null ? LifeStateForActor(selfActor.Value) : null,
            ["live_active"] = true, ["origin"] = new List<int> { 0, 0 },
            ["entities"] = outEnts, ["names"] = names,
            ["flow"] = "live",
            ["meta"] = meta,
            ["gone"] = goneCount,
            ["cursor"] = new Dictionary<string, object?>
            {
                ["seq"] = cur, ["lo"] = null, ["hi"] = cur, ["speed"] = 0, ["live"] = true,
            },
        };
    }
}
