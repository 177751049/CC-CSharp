using CCRadar.Parser.Codec;

namespace CCRadar.Parser.Core;

/// <summary>
/// LiveView 主体：构造、增量路径/类 GUID、各通道 handlers、主轮询。
/// （快照与派生逻辑见 LiveView.Snapshot.cs —— 同一 partial 类。）
/// </summary>
public sealed partial class LiveView
{
    private readonly object _lock = new();

    public FlowLiveLoader Loader { get; }

    /// <summary>guid → 导出路径（Python: _GuidPaths 的增量变更跟踪等价物）。</summary>
    private readonly Dictionary<ulong, string> _guidPaths = new();
    /// <summary>路径变更集（ResolveArchs 消费；Python _resolve_archs 的 len 游标等价——
    /// C# Dictionary 桶序不稳定，不能用索引游标，见 ResolveArchs 注释）。</summary>
    private readonly HashSet<ulong> _guidPathsChanged = new();
    /// <summary>
    /// 路径变更集第二份（ClsToCat 专属消费；Python _GuidPaths._changed /
    /// take_changed 的等价物）。Python 中 _resolve_archs 用 len 游标、
    /// _cls_to_cat 用 take_changed，两者独立；C# 若共用一个集合，
    /// ResolveArchs 每 poll 先清空 → ClsToCat 几乎收不到 AccumulatePaths
    /// 写入的路径 → c2cat 恒空 → FSM/血量/装备链整体死掉（黄金对拍实证）。
    /// </summary>
    private readonly HashSet<ulong> _guidPathsChangedCat = new();
    private bool _guidPathsDirtyAll = true;

    private readonly RepCatalog _psCat;
    private readonly List<RepCmd> _psCmds;
    private readonly List<Dictionary<string, object?>> _psParents;
    private readonly RepCatalog _charCat;
    private readonly List<RepCmd> _charCmds;

    private readonly List<RepCmd>? _fashionCmds;
    private readonly Dictionary<int, string?> _fashionNames;
    private readonly int? _fashionH;
    private readonly HashSet<ulong> _fashionGuids = new();

    public HashSet<ulong> PsArchs { get; } = [131];
    public HashSet<ulong> CharArchs { get; } = [105];
    public HashSet<ulong> BoxArchs { get; } = [];
    public HashSet<ulong> CarryArchs { get; } = [];
    public HashSet<ulong> PkArchs { get; } = [];

    public PickupTracker PickupTrackerInst { get; }
    public DeathBoxTracker BoxTracker { get; }
    public BoxContentsTracker BoxContents { get; }
    public ContainerTracker ContainerTrackerInst { get; }

    /// <summary>actor → 玩家身份（Python: self.players）。</summary>
    public Dictionary<ulong, Dictionary<string, object?>> Players { get; } = new();
    /// <summary>PS actor → {values, seq}（Python: self.player_state）。</summary>
    public Dictionary<ulong, Dictionary<string, object?>> PlayerState { get; } = new();
    private readonly Dictionary<int, ulong> _psChActor = new();
    /// <summary>char_actor → link（Python: self.links）。</summary>
    public Dictionary<ulong, Dictionary<string, object?>> Links { get; } = new();
    /// <summary>slot → 轨迹（Python: self.slots）。</summary>
    public Dictionary<int, List<SlotSample>> Slots { get; } = new();
    /// <summary>box_actor → 盒记录（Python: self.boxes）。</summary>
    public Dictionary<ulong, Dictionary<string, object?>> Boxes { get; } = new();
    /// <summary>carry_actor → char_actor（Python: self.carry_owner）。</summary>
    public Dictionary<ulong, ulong> CarryOwner { get; } = new();

    public int? MraCh { get; private set; }
    public bool MraLocked { get; private set; }

    // ---- 黄金对拍诊断计数器（正式诊断能力，保留）----
    /// <summary>Sna.ParsePrefix 调用次数。</summary>
    public long NPrefixCalls { get; private set; }
    /// <summary>前缀解析抛异常次数。</summary>
    public long NPrefixErrors { get; private set; }
    /// <summary>捕获到的 exports 条数。</summary>
    public long NExportsFound { get; private set; }
    /// <summary>SetGuidPath 成功注册数。</summary>
    public long NGuidPathsSet { get; private set; }
    /// <summary>Open bunch 总数（进 AccumulatePaths 的）。</summary>
    public long NOpenLbs { get; private set; }
    /// <summary>诊断：AI 名字表扫描计数（扫过的 LB / 命中标记 / 解析成功）。</summary>
    public long NMarkerScanned { get; private set; }
    public long NMarkerHits { get; private set; }
    public long NMarkerParsed { get; private set; }
    private int? _selfSlotPose;
    private int _mraIdx;
    private readonly Dictionary<int, int> _spawnIdx = new();
    private readonly Dictionary<long, double> _seqTs = new();
    private int _seqTsIdx;
    private double _lastDataTs;
    public int? SelfSlot { get; private set; }

    /// <summary>derive 产物实体（Python: self.ents）。</summary>
    public List<Dictionary<string, object?>> Ents { get; private set; } = [];
    public HashSet<int> MemberSlots { get; private set; } = [];
    public Dictionary<int, ulong> Claim { get; private set; } = new();

    public HealthBatchTracker HealthBatches { get; } = new();
    public DeathStateTracker LifeTracker { get; } = new();
    private readonly Dictionary<ulong, string> _psProfiles = new();
    public Dictionary<string, int> PsDecodeStats { get; } = new();
    public WeaponTracker WeaponTrackerInst { get; } = new();

    public string? SelfName { get; private set; }
    public string? SelfSrc { get; private set; }
    public int NLb { get; private set; }
    /// <summary>读侧快照缓存修订号（Python: state_version）。</summary>
    public long StateVersion { get; private set; }

    public Dictionary<ulong, Dictionary<string, object?>> FsmState { get; } = new();
    private readonly IReadOnlyDictionary<string, RepCatalog> _subCats;
    private readonly Dictionary<ulong, string> _cls2Cat = new();
    private readonly HashSet<(ulong Guid, string? Path)> _cls2CatMiss = new();
    private long _lastFsmSeq = -1;
    private readonly Dictionary<int, ulong> _fsmChActor = new();

    /// <summary>诊断：FSM 通道归属表快照（黄金对拍用）。</summary>
    public IEnumerable<(int ch, ulong actor)> FsmChActorPairs()
        => _fsmChActor.Select(kv => (kv.Key, kv.Value));

    /// <summary>血量（Python: self.health）。</summary>
    public Dictionary<ulong, Dictionary<string, object?>> Health { get; } = new();
    /// <summary>健康/装备链路诊断计数（黄金对拍用）。</summary>
    public Dictionary<string, long> HealthDbg { get; } = new();

    private void Dbg(string key)
    {
        HealthDbg[key] = HealthDbg.TryGetValue(key, out var c) ? c + 1 : 1;
    }
    public HashSet<ulong> VitalDead { get; } = [];
    public HashSet<ulong> VitalRevived { get; } = [];
    private readonly Dictionary<ulong, HashSet<string>> _vitalZeroSeen = new();

    /// <summary>护甲装备（Python: self.equip）。</summary>
    /// <summary>护甲装备（Python: self.equip）。items 桶键 = uid（可达 2^63+，
    /// Python 任意精度 int —— 必须用 ulong，long 会溢出为负/抛 OverflowException）。</summary>
    public Dictionary<ulong, Dictionary<string, Dictionary<ulong, Dictionary<string, object?>>>> Equip { get; } = new();
    /// <summary>武器 ID（Python: self.weapons）。</summary>
    public Dictionary<ulong, Dictionary<string, object?>> Weapons { get; } = new();

    private readonly Dictionary<int, long[]> _seqsCache = new();
    private readonly Dictionary<string, Dictionary<int, DeathCorrelation>> _deadboxCache = new();

    /// <summary>通道 → 角色本体 actor；actor → (close_seq, close_reason)。</summary>
    public Dictionary<int, ulong> ChActor { get; } = new();

    /// <summary>PlayerState 通道 → 当前 PlayerState actor（Python: ps_ch_actor；身份缓存用）。</summary>
    public IReadOnlyDictionary<int, ulong> PsChActor => _psChActor;
    public Dictionary<ulong, (long Seq, long? Reason)> Gone { get; } = new();
    /// <summary>actor → box_seq（死亡盒 owner 死亡信号）。</summary>
    public Dictionary<ulong, long> BoxDead { get; } = new();

    /// <summary>AI 名字表（当局 LOC_AICharacterNameTable）。</summary>
    public Dictionary<int, string> AiNames { get; } = new();

    /// <summary>field13 小队广播（Python: f13_self/f13_self_idx/f13_tracks/mate_slots/anon_names）。</summary>
    public (long Seq, double Ts, List<double> Loc)? F13Self { get; private set; }
    public int F13SelfIdx { get; private set; }
    public Dictionary<int, List<(long Seq, double Ts, double X, double Y, double Z)>> F13Tracks { get; } = new();
    public Dictionary<int, int> MateSlots { get; private set; } = new();
    public Dictionary<int, Dictionary<string, object?>> AnonNames { get; private set; } = new();
    private int _slotSamplesPrev = -1;
    private double _dbgSelfT;
    private List<double>? _dbgSelfXyz;

    public LiveView(FlowLiveLoader? loader = null)
    {
        // 对齐 Python gateway_parser.PlaintextLoader：
        //   super().__init__(identity, s2c_table=None, auto_s2c=False, c2s_self=True, request_prefix=False)
        // requestPrefix 必须为 false——否则上行(ch3)自机位姿解码错位、永远解不出（实测 PoseSamples=0）。
        Loader = loader ?? new FlowLiveLoader(autoS2c: false, c2sSelf: true, requestPrefix: false);
        try { Loader.EnableC2sSelf(1); } catch { }
        var catalogs = RepDec4.LoadCatalogs();
        _psCat = catalogs["BP_DFMPlayerState_C"];
        _psCmds = RepDec4.CmdList(_psCat);
        _psParents = _psCat.Parents;
        _charCat = catalogs["BP_DFMCharacter_C"];
        _charCmds = RepDec4.CmdList(_charCat);
        // 0904: 角色时装组件（currentCharacterHeroID = hero 权威源）。
        if (catalogs.TryGetValue("DFMCharacterFashionComponent", out var fc))
        {
            _fashionCmds = RepDec4.CmdList(fc);
            _fashionNames = fc.Parents
                .Select((p, i) => (i, p.TryGetValue("name", out var n) ? n?.ToString() : null))
                .ToDictionary(t => t.i, t => t.Item2);
            ErLive.FashionNamePredicate = c =>
                _fashionNames.TryGetValue(c.ParentIndex, out var pn) && pn == "currentCharacterHeroID";
            _fashionH = ErLive.FashionHeroHandle(_fashionCmds);
        }
        else
        {
            _fashionCmds = null;
            _fashionNames = [];
            _fashionH = null;
        }
        // MoveHandle 判定（er_live.DecodeCharacterIdentity 需要）。
        ErLive.MoveHandlePredicate = c =>
        {
            foreach (var p in _charCat.Parents)
            {
                if (p.TryGetValue("index", out var i) && i is long idx && idx == c.ParentIndex)
                {
                    return p.TryGetValue("name", out var n) && n?.ToString() == "MoveHandle";
                }
            }
            return false;
        };
        PickupTrackerInst = new PickupTracker(PkArchs, LootNames, LootMeta);
        BoxTracker = new DeathBoxTracker(BoxArchs, CarryArchs);
        BoxContents = new BoxContentsTracker();
        ContainerTrackerInst = new ContainerTracker(_guidPaths);
        _subCats = catalogs;
    }

    // ---- 增量 guid 路径 + 类 GUID ----

    private void SetGuidPath(ulong g, string p)
    {
        if (!_guidPaths.TryGetValue(g, out var old) || p.Length > old.Length)
        {
            _guidPaths[g] = p;
            _guidPathsChanged.Add(g);
            _guidPathsChangedCat.Add(g);
        }
    }

    /// <summary>
    /// 无条件写入 GUID 路径（Python: _handle_player_lb 的直接赋值语义）。
    /// 与 _accumulate_paths 的「长路径优先」不同：后来路径直接覆盖。
    /// 分类重扫语义与 Python _resolve_archs 的 len 游标一致——
    /// 新键（len 增长）→ 重扫；已有键仅值变更（len 不变）→ 不重扫，
    /// 但 _GuidPaths.__setitem__ 的 changed 标记仍然生效（喂 ClsToCat）。
    /// </summary>
    private void ForceGuidPath(ulong g, string p)
    {
        if (!_guidPaths.TryGetValue(g, out var old))
        {
            _guidPaths[g] = p;
            _guidPathsChanged.Add(g);       // 新键：len 增长 → 下次 _resolve_archs 扫描
            _guidPathsChangedCat.Add(g);
        }
        else if (old != p)
        {
            _guidPaths[g] = p;              // 仅值变更：len 不变 → archs 不重扫
            _guidPathsChangedCat.Add(g);    // Python __setitem__: 值变更 → changed
        }
    }

    /// <summary>诊断：转储前 n 个 Open bunch（hex 载荷 + 头标志），供 Python 并排对拍。</summary>
    public List<string> DumpOpenLbs(int n, string dir)
    {
        var lines = new List<string>();
        foreach (var kv in Loader.B)
        {
            foreach (var lb in kv.Value)
            {
                if (lb.Open == 0) continue;
                var hex = Convert.ToHexString(lb.Payload).ToLowerInvariant();
                lines.Add($"{kv.Key}|{lb.Seq}|open={lb.Open}|hx={lb.Hx}|hm={lb.Hm}|pbits={lb.PBits}|{hex}");
                if (lines.Count >= n) return lines;
            }
        }
        // B 已被 Poll 清空时退化为空。
        return lines;
    }

    /// <summary>诊断：GUID 路径尾部直方图（前 n 项）。</summary>
    public List<string> GuidPathTails(int n)
    {
        var hist = new Dictionary<string, int>();
        foreach (var p in _guidPaths.Values)
        {
            var tail = p.Contains('.') ? p[(p.LastIndexOf('.') + 1)..] : p;
            if (tail.StartsWith("Default__")) tail = tail[9..];
            hist[tail] = hist.TryGetValue(tail, out var c) ? c + 1 : 1;
        }
        return hist.OrderByDescending(kv => kv.Value).Take(n)
            .Select(kv => kv.Key + "×" + kv.Value).ToList();
    }

    /// <summary>诊断：关键类路径计数（Character/PlayerState/Inventory 系）。</summary>
    public string GuidPathClassProbe()
    {
        int character = 0, playerState = 0, deadBody = 0, pickup = 0, controller = 0;
        string? sampleChar = null, samplePs = null;
        foreach (var p in _guidPaths.Values)
        {
            if (p.Contains("BP_DFMCharacter", StringComparison.Ordinal)) { character++; sampleChar ??= p; }
            if (p.Contains("PlayerState", StringComparison.Ordinal)) { playerState++; samplePs ??= p; }
            if (p.Contains("Inventory_DeadBody", StringComparison.Ordinal)) deadBody++;
            if (p.Contains("InventoryPickup", StringComparison.Ordinal)) pickup++;
            if (p.Contains("PlayerController", StringComparison.Ordinal)) controller++;
        }
        return $"character={character} playerState={playerState} deadBody={deadBody} pickup={pickup} controller={controller} " +
               $"样本Char={sampleChar ?? "无"} 样本PS={samplePs ?? "无"}";
    }

    /// <summary>诊断：PS open bunch 处理计数（尝试/arch 缺失/profile 空）。</summary>
    public long NPlayerOpenTried { get; private set; }
    public long NPlayerNoArchPath { get; private set; }
    public long NPlayerProfileEmpty { get; private set; }
    public long NPlayerProfileOk { get; private set; }

    /// <summary>诊断：路径首见记录（路径 → (guid, LB seq)）——对拍"漏 bunch vs 配错 guid"。</summary>
    public Dictionary<string, (ulong Guid, long Seq)> PathFirstSeen { get; } = [];

    /// <summary>诊断：guid 配错现场转储（目标路径首次出现时记录整个 bunch 的载荷与解析条目）。</summary>
    public static readonly HashSet<string> MispairProbePaths = ["PlayerCharacterSignificanceComponent"];
    public List<string> MispairDump { get; } = [];

    /// <summary>诊断：guidPaths 全表快照。</summary>
    public Dictionary<ulong, string> GuidPathsSnapshot() => new(_guidPaths);

    /// <summary>诊断：指定 guid 的路径（黄金对拍用）。</summary>
    public string? GuidPathOf(ulong g) => _guidPaths.TryGetValue(g, out var p) ? p : null;

    /// <summary>诊断：guidPaths 总数。</summary>
    public int GuidPathCount => _guidPaths.Count;

    /// <summary>诊断：前若干条 GUID 路径样本。</summary>
    public List<string> GuidPathSamples(int n)
        => _guidPaths.Take(n).Select(kv => kv.Key + "=" + kv.Value).ToList();

    /// <summary>累积导出路径（Python: _accumulate_paths）。</summary>
    private void AccumulatePaths(IEnumerable<FlowLiveLoader.LB> newLbs)
    {
        foreach (var x in newLbs)
        {
            // MRA 锁定后只承载 handle-21 移动复制，跳过前缀解析（确定性结构规则）。
            if (MraLocked && x.Ch == MraCh) continue;
            // 重组 bunch（frags 非空）payload 同样含导出段——0830 装备链路。
            if (x.Frags is null && (x.Open == 0 || x.Hx == 0)) continue;
            NOpenLbs++;
            try
            {
                NPrefixCalls++;
                var (info, _) = Sna.ParsePrefix(
                    x.Payload, x.PBits, x.Hx != 0, x.Hm != 0,
                    quantizeBit: false, frags: x.Frags);
                if (info.TryGetValue("exports", out var exportsObj) && exportsObj is List<Dictionary<string, object?>> exports)
                {
                    NExportsFound += exports.Count;
                    foreach (var e in exports)
                    {
                        if (e.TryGetValue("path", out var p) && p is string path
                            && e.TryGetValue("guid", out var g) && g is ulong guid)
                        {
                            NGuidPathsSet++;
                            if (!PathFirstSeen.ContainsKey(path))
                            {
                                PathFirstSeen[path] = (guid, x.Seq);
                                if (MispairProbePaths.Contains(path))
                                {
                                    MispairDump.Add(System.Text.Json.JsonSerializer.Serialize(new
                                    {
                                        seq = x.Seq, ch = x.Ch, pbits = x.PBits, hx = x.Hx, hm = x.Hm,
                                        frags = x.Frags?.Select(f => new { start = f.Start, hx = f.Hx, hm = f.Hm }),
                                        hex = Convert.ToHexString(x.Payload).ToLowerInvariant(),
                                        exports = exports.Select(e => new
                                        {
                                            guid = e.TryGetValue("guid", out var eg) ? eg : null,
                                            path = e.TryGetValue("path", out var ep) ? ep : null,
                                            outer = e.TryGetValue("outer", out var eo) ? eo : null,
                                            flags = e.TryGetValue("flags", out var ef) ? ef : null,
                                        }),
                                    }));
                                }
                            }
                            SetGuidPath(guid, path);
                        }
                    }
                }
            }
            catch
            {
                // 重组的运动类 bunch 前缀结构不同，解析越位只影响导出收集，跳过。
            }
        }
    }

    /// <summary>增量类 GUID 分类（Python: _resolve_archs）。
    /// 必须<b>只处理变更集</b>：C# Dictionary 删除/更新会重排桶序，
    /// 索引游标会静默跳过大量条目（黄金对拍实证 4172 路径只分到 5 个 arch）。</summary>
    private void ResolveArchs()
    {
        if (_guidPathsChanged.Count == 0) return;
        foreach (var g in _guidPathsChanged.ToList())
        {
            if (!_guidPaths.TryGetValue(g, out var p)) continue;
            var tail = p.Contains('.') ? p[(p.LastIndexOf('.') + 1)..] : p;
            if (tail.StartsWith("Default__", StringComparison.Ordinal)) tail = tail[9..];
            if (tail == "CharacterFashionComponent") _fashionGuids.Add(g);
            if (tail == "BP_DFMCharacter_C") CharArchs.Add(g);
            else if (tail.StartsWith("BP_DFM", StringComparison.Ordinal)
                     && tail.Contains("Character") && tail.EndsWith("_C"))
            {
                // AI 角色子类也是角色 pawn（属性布局可能不同）。
                CharArchs.Add(g);
            }
            else if (tail == "BP_AZCharacter_C")
            {
                // 核弹局（4928 实证）：真人 Character 换用 BP_AZCharacter_C。
                CharArchs.Add(g);
            }
            else if (tail is "BP_DFMPlayerState_C" or "BP_SOLPlayerState_C") PsArchs.Add(g);
            else if (tail == "BP_Inventory_DeadBody_C") BoxArchs.Add(g);
            else if (tail == "BP_Inventory_CarryBody_C") CarryArchs.Add(g);
            else if (tail.StartsWith("BP_InventoryPickup", StringComparison.Ordinal))
            {
                // 露天物资（两变体共用解析路径，宽前缀匹配）。
                PkArchs.Add(g);
            }
        }
        _guidPathsChanged.Clear();
    }

    // ---- PlayerState 处理 ----

    /// <summary>合并一个 PlayerState 复制增量（Python: _merge_playerstate）。</summary>
    private void MergePlayerstate(ulong actor, IReadOnlyDictionary<int, object?> outFields,
        long seq, int channel, bool opened = false, bool verified = true)
    {
        if (!PlayerState.TryGetValue(actor, out var life))
        {
            life = new Dictionary<string, object?> { ["values"] = new Dictionary<string, object?>(), ["seq"] = new Dictionary<string, long>() };
            PlayerState[actor] = life;
        }
        if (verified) life["schema_verified"] = true;
        var values = (Dictionary<string, object?>)life["values"]!;
        var fieldSeq = (Dictionary<string, long>)life["seq"]!;
        if (!Players.TryGetValue(actor, out var prev))
        {
            prev = new Dictionary<string, object?>
            {
                ["name"] = null, ["player_id"] = null, ["bot"] = null,
                ["actor_guid"] = actor, ["owner_guid"] = null, ["channel"] = channel,
                ["uin"] = null, ["team"] = null, ["camp"] = null, ["inner_team"] = null,
                ["hero_id"] = null, ["expert_id"] = null,
                ["spawn_seq"] = opened ? seq : (object?)null,
                ["life_state"] = values, ["life_seq"] = fieldSeq,
            };
            Players[actor] = prev;
        }
        else
        {
            prev["life_state"] = values;
            prev["life_seq"] = fieldSeq;
            if (opened && (prev.TryGetValue("spawn_seq", out var sp) ? sp : null) is null)
            {
                prev["spawn_seq"] = seq;
            }
        }
        prev["channel"] = channel;

        // Identity 也是 delta-replicated：缺 handle 意味着未变。
        foreach (var (h, key) in new[]
                 {
                     (18, "player_id"), (21, "bot"), (14, "owner_guid"), (28, "uin"),
                     (62, "team"), (63, "camp"), (30, "inner_team"),
                     (137, "hero_id"), (138, "expert_id"),
                 })
        {
            if (outFields.TryGetValue(h, out var v)) prev[key] = v;
        }
        if (outFields.TryGetValue(27, out var nameObj) && nameObj is not null)
        {
            prev["name"] = nameObj;
        }

        // Life 字段各自保留 wire 序列：缺失字段不清除前值、迟到增量不能回滚。
        foreach (var (h, name) in PsLifeFields)
        {
            if (!outFields.TryGetValue(h, out var v)) continue;
            if (fieldSeq.TryGetValue(name, out var fs) && seq < fs) continue;
            values[name] = v;
            fieldSeq[name] = seq;
        }
    }

    /// <summary>按导出类路由 exact 命名 PlayerState（Python: _handle_player_lb）。</summary>
    private void HandlePlayerLb(BunchView x)
    {
        try
        {
            var (exports, start) = ReplicationHeader.ExportsPrefix(
                x.Payload, x.PBits, x.Hx, x.Hm, x.Frags);
            foreach (var entry in exports)
            {
                if (entry.TryGetValue("path", out var p) && p is string path && path.Length > 0
                    && entry.TryGetValue("guid", out var g) && g is ulong guid)
                {
                    // Python _handle_player_lb 是无条件覆盖（非长路径优先）。
                    ForceGuidPath(guid, path);
                }
            }
            ulong actor;
            string profile;
            if (x.Open)
            {
                _psChActor.Remove(x.Ch);
                if (x.Close) return;
                var (info, newStart) = Sna.ParsePrefix(
                    x.Payload, x.PBits, x.Hx, x.Hm,
                    quantizeBit: false, frags: x.Frags);
                start = newStart;
                var arch = info.TryGetValue("arch", out var a1) ? a1 : null;
                NPlayerOpenTried++;
                profile = arch is not null
                    ? PlayerstateProtocol.SelectProfile(
                        _guidPaths.TryGetValue((ulong)Convert.ToUInt64(arch), out var p2) ? p2 : null) ?? ""
                    : "";
                if (arch is null || !_guidPaths.ContainsKey((ulong)Convert.ToUInt64(arch))) NPlayerNoArchPath++;
                if (profile.Length == 0) { NPlayerProfileEmpty++; return; }
                NPlayerProfileOk++;
                actor = (ulong)Convert.ToUInt64(info["actor"]!);
                _psChActor[x.Ch] = actor;
                _psProfiles[actor] = profile;
            }
            else
            {
                if (!_psChActor.TryGetValue(x.Ch, out actor)) return;
                if (!_psProfiles.TryGetValue(actor, out var p3)) return;
                profile = p3;
            }
            var blocks = Blocks.Tile(x.Payload, x.PBits, start, partialOk: true);
            if (blocks is null) return;
            foreach (var block in blocks)
            {
                if (block.IsActor == 0 || block.HasRep == 0 || block.Deleted) continue;
                var decoded = PlayerstateProtocol.DecodePlayerstate(
                    x.Payload, block.PayloadStart, block.PayloadEnd, profile);
                var status = decoded["status"]?.ToString() ?? "";
                PsDecodeStats[status] = PsDecodeStats.TryGetValue(status, out var c) ? c + 1 : 1;
                if (status == "exact")
                {
                    MergeNamedPlayerstate(actor,
                        (Dictionary<string, object?>)decoded["fields"]!, x.Seq, x.Ch,
                        opened: x.Open);
                    continue;
                }
                // 非 exact 块：身份字段（记录最前的纯标量）仍可信，只并身份、
                // 不标 schema_verified、不写生命周期值。
                var identity = decoded.TryGetValue("identity", out var idObj) && idObj is Dictionary<string, object?> idDict
                    ? idDict : new Dictionary<string, object?>();
                if (identity.Count > 0)
                {
                    MergeNamedPlayerstate(actor, identity, x.Seq, x.Ch,
                        opened: x.Open, verified: false);
                }
            }
        }
        catch (Exception ex) when (ex is SnaBad or BitError or IndexOutOfRangeException)
        {
            PsDecodeStats["framing_error"] = PsDecodeStats.TryGetValue("framing_error", out var c2) ? c2 + 1 : 1;
        }
    }

    /// <summary>命名 PS 字段并入 players/player_state（Python: _merge_named_playerstate）。</summary>
    private void MergeNamedPlayerstate(ulong actor, Dictionary<string, object?> fields,
        long seq, int channel, bool opened = false, bool verified = true)
    {
        MergePlayerstate(actor, new Dictionary<int, object?>(), seq, channel,
            opened: opened, verified: verified);
        var life = PlayerState[actor];
        life["profile"] = _psProfiles.TryGetValue(actor, out var prof) ? prof : null;
        var player = Players[actor];
        var identity = new Dictionary<string, string>
        {
            ["Owner"] = "owner_guid", ["PlayerId"] = "player_id", ["bIsABot"] = "bot",
            ["PlayerNamePrivate"] = "name", ["Uin"] = "uin", ["InnerTeamIndex"] = "inner_team",
            ["TeamID"] = "team", ["Camp"] = "camp", ["CampID"] = "camp",
            ["HeroId"] = "hero_id", ["ExpertId"] = "expert_id",
        };
        var fieldSeqDict = (Dictionary<string, long>)life["seq"]!;
        foreach (var (name, value) in fields)
        {
            var hasPrev = fieldSeqDict.TryGetValue(name, out var fs);
            if (hasPrev && seq < fs) continue;
            ((Dictionary<string, object?>)life["values"]!)[name] = value;
            fieldSeqDict[name] = seq;
            if (identity.TryGetValue(name, out var key))
            {
                var v2 = value;
                if (name == "Owner" && v2 is Dictionary<string, object?> d)
                {
                    v2 = d.TryGetValue("guid", out var g) ? g : null;
                }
                player[key] = v2;
            }
        }
    }

    private void HandlePlayerClose(BunchView x)
    {
        if (x.Close) _psChActor.Remove(x.Ch);
    }

    /// <summary>角色 spawn 处理（Python: _handle_char_lb）。</summary>
    private void HandleCharLb(BunchView x)
    {
        (Dictionary<string, object?> Info, int StreamStart) parsed;
        try
        {
            parsed = Sna.ParsePrefix(
                x.Payload, x.PBits, x.Hx, x.Hm, quantizeBit: true, frags: x.Frags);
        }
        catch (SnaBad)
        {
            return;
        }
        var info = parsed.Info;
        var ss = parsed.StreamStart;
        var arch = info.TryGetValue("arch", out var a1) ? a1 : null;
        if (arch is not ulong ag || !CharArchs.Contains(ag)) return;
        // 0904：本 bunch 导出里的时装组件实例 guid（hero 解码路由用）。
        if (info.TryGetValue("exports", out var expObj) && expObj is List<Dictionary<string, object?>> exports)
        {
            foreach (var e in exports)
            {
                if (e.TryGetValue("path", out var p) && p is string path
                    && e.TryGetValue("guid", out var g) && g is ulong guid
                    && path.Contains("CharacterFashion", StringComparison.Ordinal))
                {
                    _fashionGuids.Add(guid);
                }
            }
        }
        var a = info.TryGetValue("actor", out var a2) && a2 is ulong au ? au : 0UL;
        if (a == 0) return;
        var blocks = Blocks.Tile(x.Payload, x.PBits, ss + 8, partialOk: true);
        var identityOut = new Dictionary<long, object?>();
        if (blocks is not null && blocks[0].IsActor == 1 && blocks[0].HasRep == 1)
        {
            identityOut = ErLive.DecodeCharacterIdentity(
                x.Payload, blocks[0].PayloadStart, blocks[0].PayloadEnd, _charCmds);
        }
        // 0904: hero 从时装组件解（先扫描，link 建好后再挂）。
        long? fashionHid = null;
        if (blocks is not null && _fashionGuids.Count > 0)
        {
            foreach (var bk in blocks)
            {
                if (bk.Deleted || bk.Sub is not { } sub || !_fashionGuids.Contains(sub)) continue;
                if (bk.PayloadEnd <= bk.PayloadStart) continue;
                fashionHid = ErLive.WalkFashionHero(
                    x.Payload, bk.PayloadStart, bk.PayloadEnd,
                    _fashionCmds ?? [], c => _fashionNames.TryGetValue(c.ParentIndex, out var pn) && pn == "currentCharacterHeroID",
                    _fashionH ?? -1);
                if (fashionHid is not null) break;
            }
        }
        if (!Links.TryGetValue(a, out var link))
        {
            link = new Dictionary<string, object?>
            {
                ["owner"] = identityOut.TryGetValue(14, out var o14) ? o14 : null,
                ["ps_guid"] = identityOut.TryGetValue(18, out var o18) ? o18 : null,
                ["spawn_seq"] = x.Seq,
                ["loc"] = info.TryGetValue("loc", out var locObj) ? locObj : null,
                ["num_exports"] = info.TryGetValue("num_exports", out var ne) ? ne : null,
                ["cls"] = arch is not null && _guidPaths.TryGetValue(ag, out var cp)
                    ? cp.Contains('.') ? cp[(cp.LastIndexOf('.') + 1)..] : cp : "",
                ["ai_key"] = AiNameTable.ExtractRefKey(x.Payload),
            };
            Links[a] = link;
        }
        else
        {
            if ((link.TryGetValue("owner", out var ow) ? ow : null) is null
                && identityOut.TryGetValue(14, out var o14b))
            {
                link["owner"] = o14b;
            }
            if ((link.TryGetValue("ps_guid", out var pg) ? pg : null) is null
                && identityOut.TryGetValue(18, out var o18b))
            {
                link["ps_guid"] = o18b;
            }
            // AI 名字引用可能晚于首个 spawn 到达。
            if ((!link.TryGetValue("ai_key", out var ak) || ak is null))
            {
                link["ai_key"] = AiNameTable.ExtractRefKey(x.Payload);
            }
        }
        ErLive.MergeCharacterIdentity(link, identityOut, x.Seq);
        LifeTracker.Spawn(a, link.TryGetValue("spawn_seq", out var spSeq) && spSeq is not null
            ? Convert.ToInt64(spSeq) : x.Seq);
        if (fashionHid is not null && (!link.TryGetValue("hero", out var hh) || hh is null))
        {
            link["hero"] = fashionHid;
        }
        // 记录该通道当前角色本体（close 事件归属用）。
        ChActor[x.Ch] = a;
        // 独立 FSM 归属表：close 不清（死亡后子对象状态块仍归原角色）。
        _fsmChActor[x.Ch] = a;
        // 角色重新 spawn（复活）→ 清除消失状态（spawn 晚于 close 才清）。
        if (!Gone.TryGetValue(a, out var ggone) || x.Seq >= ggone.Seq)
        {
            Gone.Remove(a);
        }
    }

    private void HandleCharDelta(BunchView x)
    {
        if (!ChActor.TryGetValue(x.Ch, out var actor)) return;
        try
        {
            var (_, start) = ReplicationHeader.ExportsPrefix(
                x.Payload, x.PBits, x.Hx, x.Hm, x.Frags);
            var blocks = Blocks.Tile(x.Payload, x.PBits, start);
            if (blocks is null) return;
            foreach (var block in blocks)
            {
                if (block.IsActor != 0 && block.HasRep != 0)
                {
                    var fields = ErLive.DecodeCharacterIdentity(
                        x.Payload, block.PayloadStart, block.PayloadEnd, _charCmds);
                    if (Links.TryGetValue(actor, out var link))
                    {
                        ErLive.MergeCharacterIdentity(link, fields, x.Seq);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is SnaBad or IndexOutOfRangeException)
        {
            return;
        }
    }

    /// <summary>死亡盒内容块 handle 14(Owner) = 死亡者引用（Python: _decode_box_owner）。</summary>
    private ulong? DecodeBoxOwner(BunchView x, int ss)
    {
        var blocks = Blocks.Tile(x.Payload, x.PBits, ss);
        if (blocks is null || blocks.Count == 0) return null;
        var k = blocks[0];
        if (k.IsActor != 1 || k.HasRep != 1) return null;
        var r = new BitReader(x.Payload, k.PayloadEnd) { Position = k.PayloadStart };
        try
        {
            r.ReadBit();   // bDoChecksum
            var h = EngineBunchCodec.ReadDfmPackedInt(r).Value;
            while (h != 0)
            {
                if (h is >= 1 and <= 5) r.ReadBit();
                else if (h == 6) ErLive.SerInt(r, 4);
                else if (h == 7) return null;   // ReplicatedMovement（死亡盒不复制）
                else if (h == 8) EngineBunchCodec.ReadDfmPackedInt(r);       // AttachParent
                else if (h is 9 or 10) RepDec4.ReadVal(r, 13, null);         // Vector100
                else if (h == 11) RepDec4.ReadVal(r, 11, null);              // Rotator
                else if (h == 12) EngineBunchCodec.ReadSerializedFName(r);   // Name
                else if (h == 13) EngineBunchCodec.ReadDfmPackedInt(r);      // AttachComponent
                else if (h == 14) return EngineBunchCodec.ReadDfmPackedInt(r).Value;   // Owner = 死者
                else if (h == 15) ErLive.SerInt(r, 4);                       // Role
                else if (h == 16) EngineBunchCodec.ReadDfmPackedInt(r);      // Instigator
                else return null;   // h17+，非 AActor 基础
                h = EngineBunchCodec.ReadDfmPackedInt(r).Value;
            }
            return null;
        }
        catch (BitError)
        {
            return null;
        }
    }

    private void HandleBoxLb(BunchView x)
    {
        BoxTracker.Consume(x);
        SyncBoxes();
    }

    private void SyncBoxes()
    {
        Boxes.Clear();
        CarryOwner.Clear();
        foreach (var (actor, rec) in BoxTracker.Active)
        {
            var kind = rec.TryGetValue("kind", out var k) ? k?.ToString() : null;
            if (kind == "box") Boxes[actor] = rec;
            else if (kind == "carry")
            {
                CarryOwner[actor] = rec.TryGetValue("deceased_actor", out var d) && d is not null
                    ? (ulong)Convert.ToUInt64(d)
                    : rec.TryGetValue("owner_actor", out var o) && o is not null
                        ? (ulong)Convert.ToUInt64(o) : 0UL;
            }
        }
    }

    private void HandlePickupLb(BunchView x)
    {
        PickupTrackerInst.Consume(x);
    }

    private void HandlePickupClose(BunchView x)
    {
        PickupTrackerInst.CloseChannel(x.Ch, x.Seq, x.CloseReason);
    }

    /// <summary>角色通道 close：按 close_reason 判定消失（Python: _handle_char_close）。</summary>
    private void HandleCharClose(BunchView x)
    {
        if (!x.Close) return;
        var reason = x.CloseReason;
        // reason=1 (Dormancy) 是子对象休眠，不是角色本体消失。
        if (reason is null || !StateTrack.GoneCloseReasons.Contains(reason.Value)) return;
        // 关闭即清空通道归属：通道关闭后的后续 close 是子对象 close，不能归到
        // 已关闭角色头上（防 Relevancy 误判成 Destroyed）。
        if (!ChActor.Remove(x.Ch, out var actor)) return;
        // 最后 close 胜出；防御早序 close 不得覆盖晚序 close。
        if (Gone.TryGetValue(actor, out var prev) && x.Seq < prev.Seq) return;
        Gone[actor] = (x.Seq, reason);
    }

    /// <summary>MRA 通道的 handle-21 移动帧 → 槽位轨迹（Python: _handle_mra_lb）。</summary>
    private void HandleMraLb(BunchView x)
    {
        var ts = _seqTs.TryGetValue(x.Seq, out var t) ? t : 0.0;
        List<ContentBlock>? blk;
        try
        {
            blk = Blocks.Tile(x.Payload, x.PBits, 0);
        }
        catch
        {
            return;
        }
        if (blk is null) return;
        foreach (var k in blk)
        {
            if (k.HasRep == 0 || k.Deleted || k.IsActor == 0) continue;
            var r0 = new BitReader(x.Payload, k.PayloadEnd) { Position = k.PayloadStart };
            Dictionary<string, object?> fr;
            try
            {
                if (r0.ReadBit() != 0) continue;
                var h = EngineBunchCodec.ReadDfmPackedInt(r0).Value;
                if (h != 21) continue;
                (fr, _) = RepMove4.ReadFrame4(x.Payload, r0.Position, k.PayloadEnd);
            }
            catch
            {
                continue;
            }
            foreach (var (i, it) in (Dictionary<int, Dictionary<string, object?>>)fr["items"]!)
            {
                if (!it.TryGetValue("pos", out var p) || p is not List<object?> pl || !World(pl))
                {
                    continue;
                }
                var yaw = it.TryGetValue("rot", out var rot) && rot is List<object?> rl && rl.Count > 1
                    ? Convert.ToDouble(rl[1])
                    : (double?)null;
                if (!Slots.TryGetValue(i, out var list))
                {
                    list = [];
                    Slots[i] = list;
                }
                list.Add(new SlotSample(x.Seq, ts,
                    Convert.ToDouble(pl[0]), Convert.ToDouble(pl[1]),
                    Convert.ToDouble(pl[2]), yaw));
            }
        }
    }

    // ---- 主轮询 ----

    /// <summary>消费一个明文包（宿主适配入口）。</summary>
    public int ConsumePacket(long seq, string dir, byte[] raw, double ts)
        => Loader.ConsumePacket(seq, dir, raw, ts);

    /// <summary>当前数据时间（宿主每包更新；live≈墙钟，重放=journal 时间戳）。
    /// 快照层的 game-end stale 判定用它代替墙钟，保证重放与实时语义一致。</summary>
    public double NowTs { get; set; }

    /// <summary>主轮询（Python: poll）。返回是否有输入变化。</summary>
    public bool Poll(int? maxFrames = null)
    {
        var bDict = Loader.B;
        List<FlowLiveLoader.LB> newLbs;
        lock (_lock)
        {
            // 更新 seq→ts（增量），记最新帧到达时刻（对局销毁判定）。
            for (var i = _seqTsIdx; i < Loader.SeqTs.Count; i++)
            {
                var (seq, ts) = Loader.SeqTs[i];
                _seqTs.TryAdd(seq, ts);
                if (ts > _lastDataTs) _lastDataTs = ts;
            }
            _seqTsIdx = Loader.SeqTs.Count;
            // 1) 累积导出路径 + 解析类 GUID。
            newLbs = [];
            foreach (var (ci, lst) in bDict)
            {
                var idx = _spawnIdx.TryGetValue(ci, out var si) ? si : 0;
                if (idx < lst.Count)
                {
                    newLbs.AddRange(lst.Skip(idx));
                    _spawnIdx[ci] = lst.Count;
                }
            }
            // spawn/close 生命周期必须按全局 seq 顺序处理（Python: (seq, ch) 排序注释）。
            newLbs.Sort((a, b2) => a.Seq != b2.Seq ? a.Seq.CompareTo(b2.Seq) : a.Ch.CompareTo(b2.Ch));
            AccumulatePaths(newLbs);
            ResolveArchs();
            // 1.5) 当局 AI 名字表。
            foreach (var x in newLbs)
            {
                // 子序列搜索：Array.IndexOf(byte[], byte[]) 不做子序列匹配（恒 -1），
                // 必须 MemoryExtensions.IndexOf(Span)。对应 Python: b"LOC_AI..." in payload。
                NMarkerScanned++;
                if (x.Payload.AsSpan().IndexOf(EncodingHead) >= 0)
                {
                    NMarkerHits++;
                    foreach (var (k, nm) in AiNameTable.ParseTableInstance(x.Payload))
                    {
                        AiNames[k] = nm;
                    }
                    if (AiNames.Count > 0) NMarkerParsed++;
                }
            }
            // 1.6) field13 小队广播（不能按 open 过滤；解码自带 field_index 校验）。
            foreach (var x in newLbs)
            {
                if (x.PBits < 64) continue;
                if (MraLocked && x.Ch == MraCh) continue;
                Dictionary<string, object?>? dec;
                try
                {
                    dec = Field13.Decode(x.Payload, x.PBits);
                }
                catch
                {
                    continue;
                }
                if (dec is null
                    || dec["locs"] is not List<object?> locs || locs.Count == 0)
                {
                    continue;
                }
                if (dec["vals64"] is List<object?> vals64 && vals64.Any(v =>
                        Field13.SelfWire.Contains(Convert.ToUInt64(v))))
                {
                    F13SelfIdx = Field13.SelfIndex(vals64.Select(v => Convert.ToUInt64(v)));
                }
                var si = Math.Min(F13SelfIdx, locs.Count - 1);
                if (F13Self is null || x.Seq >= F13Self.Value.Seq)
                {
                    var locList = locs[si] as List<object?>;
                    if (locList is not null)
                    {
                        F13Self = (x.Seq, _seqTs.TryGetValue(x.Seq, out var fts) ? fts : 0.0,
                            locList.Select(v => Convert.ToDouble(v)).ToList());
                    }
                }
                // 全量帧才追加全轨迹（增量帧无 vals64 无法定位下标）。
                if (dec["vals64"] is List<object?>)
                {
                    var ts2 = _seqTs.TryGetValue(x.Seq, out var t2) ? t2 : 0.0;
                    for (var i = 0; i < locs.Count; i++)
                    {
                        if (locs[i] is not List<object?> loc) continue;
                        if (!F13Tracks.TryGetValue(i, out var track))
                        {
                            track = [];
                            F13Tracks[i] = track;
                        }
                        track.Add((x.Seq, ts2, Convert.ToDouble(loc[0]),
                            Convert.ToDouble(loc[1]), Convert.ToDouble(loc[2])));
                    }
                }
            }
            // 2) spawn / close 检测。
            var actorByLb = new Dictionary<int, ulong>();
            foreach (var lb in newLbs)
            {
                var x = lb.ToView();
                WeaponTrackerInst.Consume(x);
                PickupTrackerInst.Consume(x);
                ContainerTrackerInst.Consume(x);
                var closingBox = BoxTracker.Channels2.TryGetValue(x.Ch, out var cb) ? cb : (ulong?)null;
                BoxTracker.Consume(x);
                BoxContents.Consume(x);
                if (!x.Open && x.Close && x.CloseReason is 0 or 2 or 4 && closingBox is not null)
                {
                    BoxContents.Forget(closingBox.Value, x.Seq);
                }
                if (MraLocked && x.Ch == MraCh) continue;
                // PlayerState 属性在普通 (open=0) bunch 上继续；生命周期拆分前路由。
                if (!x.Close) HandlePlayerLb(x);
                if (x.Open)
                {
                    ChActor.Remove(x.Ch);
                    _fsmChActor.Remove(x.Ch);
                    HandleCharLb(x);
                }
                else if (x.Close)
                {
                    HandlePlayerClose(x);
                    HandleCharClose(x);
                }
                else
                {
                    HandleCharDelta(x);
                }
                // 保存在本包时刻的所有权（同批后到的 open 会复用通道给别的 actor）。
                // 键必须是 LB 对象本身的哈希——DecodeFsmIncremental 用同一 LB 的
                // GetHashCode 查询（Python: actor_by_lb[id(x)]，x 是同一 LB）；
                // 若用 ToView() 产生的 BunchView 建键，查询恒失配 → FSM/装备的
                // actor 归属全部退化为通道回退（黄金对拍 equip 13→6 实证）。
                if (_fsmChActor.TryGetValue(x.Ch, out var fa))
                {
                    actorByLb[System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(lb)] = fa;
                }
            }
            // 3) MRA 通道锁定 + 槽位解析。
            if (!MraLocked)
            {
                var allB = Loader.B.SelectMany(kv => kv.Value).Select(lb => lb.ToView()).ToList();
                var (mraCh, mraN) = ErLive.FindMraChannel(allB);
                if (mraCh is not null && mraN >= 16)
                {
                    MraCh = mraCh;
                    MraLocked = true;
                    _mraIdx = 0;
                }
            }
            if (MraLocked && MraCh is not null && Loader.B.TryGetValue(MraCh.Value, out var mraList))
            {
                for (var i = _mraIdx; i < mraList.Count; i++)
                {
                    HandleMraLb(mraList[i].ToView());
                }
                _mraIdx = mraList.Count;
            }
            NLb = Loader.NLb;
            // 4) FSM/血量增量解码（必须在 _derive 之前——vital_dead 从 health 读）。
            if (newLbs.Count > 0)
            {
                var actorByLbByKey = actorByLb.ToDictionary(
                    kv => kv.Key, kv => kv.Value);
                DecodeFsmIncremental(newLbs, actorByLbByKey);
                DecodeHealthBatches(newLbs);
                SyncBoxes();
            }
            // 5) 重算 join/self/ents —— 仅当有输入变化。
            var curSlotSamples = Slots.Values.Sum(tr => tr.Count);
            var dirty = newLbs.Count > 0 || curSlotSamples != _slotSamplesPrev;
            _slotSamplesPrev = curSlotSamples;
            if (dirty)
            {
                Derive();
                LootPickScan();
                CompactHistories();
            }
            CompactConsumed();
        }
        var changed = newLbs.Count > 0;
        if (changed) StateVersion++;
        return changed;
    }

    private static readonly byte[] EncodingHead = EncodingAscii("LOC_AICharacterNameTable.LOC_AICharacterNameTable\0");

    private static byte[] EncodingAscii(string s) => System.Text.Encoding.ASCII.GetBytes(s);

    /// <summary>loader.b 的视图（给 MRA 探测等需要全量通道的场景）。</summary>
    private List<BunchView> AllBunchViews()
        => Loader.B.SelectMany(kv => kv.Value).Select(lb => lb.ToView()).ToList();

    /// <summary>各内容解码器累计的"分帧/前缀"错误数（Python: decode_error_count）。</summary>
    public int DecodeErrorCount()
    {
        var n = 0;
        foreach (var st in new[]
                 {
                     PsDecodeStats, HealthBatches.Stats, LifeTracker.Stats,
                     WeaponTrackerInst.Stats, PickupTrackerInst.Stats,
                     BoxTracker.Stats, BoxContents.Stats,
                 })
        {
            foreach (var k in new[] { "framing_error", "prefix_error", "error", "layout_error" })
            {
                n += st.TryGetValue(k, out var v) ? v : 0;
            }
        }
        return n;
    }

    /// <summary>丢弃已消费 wire 记录（Python: _compact_consumed）。</summary>
    private void CompactConsumed()
    {
        // Python: self.loader.seq_ts.clear()——清的是 LOADER 的帧时间戳列表；
        // view 的 _seq_ts 保留（快照的 spawn-age/静默计算依赖它）。
        Loader.SeqTs.Clear();
        _seqTsIdx = 0;
        if (!MraLocked) return;
        foreach (var (channel, records) in Loader.B.ToList())
        {
            var consumed = _spawnIdx.TryGetValue(channel, out var si) ? si : 0;
            if (channel == MraCh)
            {
                consumed = Math.Min(consumed, _mraIdx);
                _mraIdx -= consumed;
            }
            if (consumed > 0)
            {
                records.RemoveRange(0, Math.Min(consumed, records.Count));
                _spawnIdx[channel] = Math.Max(0, (_spawnIdx.TryGetValue(channel, out var s2) ? s2 : 0) - consumed);
            }
            if (records.Count == 0)
            {
                Loader.B.Remove(channel);
                _spawnIdx.Remove(channel);
            }
        }
    }

    /// <summary>有界轨迹压缩（Python: _compact_track）——保留近期尾段，老点稀疏化。</summary>
    private static bool CompactTrack(List<SlotSample> track)
    {
        if (track.Count <= TrackHistoryMax) return false;
        var recentN = TrackHistoryRecent;
        var old = track.Take(track.Count - recentN).ToList();
        var recent = track.Skip(track.Count - recentN).ToList();
        var oldBudget = TrackHistoryMax - recentN;
        if (old.Count > oldBudget)
        {
            var stride = (old.Count + oldBudget - 1) / oldBudget;
            old = old.Where((_, i) => i % stride == 0).ToList();
        }
        var merged = old.Concat(recent).ToList();
        track.Clear();
        track.AddRange(merged);
        return true;
    }

    /// <summary>长时间运行的轨迹/时间索引压缩（Python: _compact_histories）。</summary>
    private bool CompactHistories()
    {
        var compacted = false;
        foreach (var (slot, track) in Slots.ToList())
        {
            if (CompactTrack(track))
            {
                compacted = true;
                _seqsCache.Remove(slot);
                _pkChk[slot] = track.Count;
            }
        }
        foreach (var track in F13Tracks.Values)
        {
            if (CompactTrack13(track)) compacted = true;
        }
        if (_seqTs.Count > SeqTsMax)
        {
            var latest = _seqTs.Keys.Max();
            var cutoff = latest - SeqTsMax;
            foreach (var seq in _seqTs.Keys.Where(s => s < cutoff).ToList())
            {
                _seqTs.Remove(seq);
            }
        }
        return compacted;
    }

    private static bool CompactTrack13(List<(long Seq, double Ts, double X, double Y, double Z)> track)
    {
        if (track.Count <= TrackHistoryMax) return false;
        var recentN = TrackHistoryRecent;
        var old = track.Take(track.Count - recentN).ToList();
        var recent = track.Skip(track.Count - recentN).ToList();
        var oldBudget = TrackHistoryMax - recentN;
        if (old.Count > oldBudget)
        {
            var stride = (old.Count + oldBudget - 1) / oldBudget;
            old = old.Where((_, i) => i % stride == 0).ToList();
        }
        var merged = old.Concat(recent).ToList();
        track.Clear();
        track.AddRange(merged);
        return true;
    }

    /// <summary>从移动记录拾取候选（Python: _loot_pick_scan），不宣告移除。</summary>
    private readonly Dictionary<int, int> _pkChk = new();

    private void LootPickScan()
    {
        if (!LootPickInfer || PickupTrackerInst.Active.Count == 0) return;
        var r2 = LootPickRadiusCm * LootPickRadiusCm;
        foreach (var (s, tr) in Slots)
        {
            var i0 = _pkChk.TryGetValue(s, out var chk) ? chk : 1;
            if (i0 >= tr.Count)
            {
                _pkChk[s] = tr.Count;
                continue;
            }
            for (var i = Math.Max(1, i0); i < tr.Count; i++)
            {
                var q = tr[i];
                var pp = tr[i - 1];
                var dt = q.Ts - pp.Ts;
                if (dt <= 0) continue;
                var v = Math.Sqrt((q.X - pp.X) * (q.X - pp.X)
                    + (q.Y - pp.Y) * (q.Y - pp.Y)
                    + (q.Z - pp.Z) * (q.Z - pp.Z)) / dt;
                if (v >= LootPickVmax) continue;
                foreach (var (a, lr) in PickupTrackerInst.Active.ToList())
                {
                    if ((lr.TryGetValue("pickup_candidate_seq", out var pcs) && pcs is not null)
                        || (lr.TryGetValue("gone_seq", out var gs) && gs is not null))
                    {
                        continue;
                    }
                    if (q.Seq < Convert.ToInt64(lr["seq"])) continue;
                    if (lr.TryGetValue("loc", out var locObj) && locObj is List<object?> loc && loc.Count >= 2)
                    {
                        var lx = Convert.ToDouble(loc[0]);
                        var ly = Convert.ToDouble(loc[1]);
                        var dx = q.X - lx;
                        var dy = q.Y - ly;
                        if (dx * dx + dy * dy <= r2)
                        {
                            lr["pickup_candidate_seq"] = q.Seq;
                            lr["pickup_candidate_slot"] = s;
                            lr["pickup_confidence"] = "candidate";
                        }
                    }
                }
            }
            _pkChk[s] = tr.Count;
        }
    }

    private string? Tail2CatName(ulong g)
    {
        if (!_guidPaths.TryGetValue(g, out var p)) return null;
        var t = p.Contains('.') ? p[(p.LastIndexOf('.') + 1)..] : p;
        if (t.StartsWith("Default__", StringComparison.Ordinal)) t = t[9..];
        return StateTrack.Tail2Cat.TryGetValue(t, out var cat) ? cat : null;
    }

    /// <summary>guid → 目录名（Python: _cls_to_cat；两条路线都要建——cls 与 stably-named sub）。</summary>
    private Dictionary<ulong, string> ClsToCat()
    {
        var cat = _cls2Cat;
        // 消费 ClsToCat 专属变更集（Python: paths.take_changed()）。
        // 不可与 ResolveArchs 的 _guidPathsChanged 共用——它每 poll 被先清空。
        var changed = _guidPathsDirtyAll
            ? _guidPaths.Keys.ToList()
            : _guidPathsChangedCat.ToList();
        _guidPathsChangedCat.Clear();
        _guidPathsDirtyAll = false;
        foreach (var g in changed)
        {
            if (cat.ContainsKey(g)) continue;
            _guidPaths.TryGetValue(g, out var p);
            if (_cls2CatMiss.Contains((g, p))) continue;
            var cm = Tail2CatName(g);
            if (cm is not null && _subCats.ContainsKey(cm))
            {
                cat[g] = cm;
            }
            else
            {
                // 负缓存：guid_paths[g] 与 TAIL2CAT 都不变 → 未命中恒未命中。
                _cls2CatMiss.Add((g, p));
            }
        }
        return cat;
    }

    private void DecodeHealthBatches(IEnumerable<FlowLiveLoader.LB> bunches)
    {
        foreach (var bunch in bunches)
        {
            HealthBatches.Consume(bunch.ToView());
            // 每 bunch drain：同一 poll 内的死亡+复活不能塌缩成一个迟到的字段值。
            foreach (var (actor, name, value, seq) in HealthBatches.Drain())
            {
                Dbg("health:propRep");
                RecordHealth(actor, name, value, seq, source: "property_replication");
            }
        }
    }

    /// <summary>血量记录（Python: _record_health）——含脏值门与 revive 检测。</summary>
    private void RecordHealth(ulong actor, string name, object? value, long seq, string source = "attribute_set")
    {
        if (value is not (long or double) || value is bool) { Dbg("rh:typeReject"); return; }
        var d = Convert.ToDouble(value);
        if (!double.IsFinite(d) || d < 0) { Dbg("rh:finiteReject"); return; }
        if (source != "property_replication" && !HealthValOk(name, value)) { Dbg("rh:valOkReject"); return; }
        var partWires = HpParts.Select(p => p.Wire).ToHashSet();
        if (partWires.Contains(name) && d > 500) { Dbg("rh:partReject"); return; }
        if (HpParts.Select(p => "Max" + p.Wire).Contains(name) && d is < 5 or > 500) { Dbg("rh:maxReject"); return; }
        Dbg("rh:stored");
        if (!Health.TryGetValue(actor, out var rec))
        {
            rec = new Dictionary<string, object?> { ["parts"] = new Dictionary<string, object?>(), ["impending"] = null, ["seq"] = seq };
            Health[actor] = rec;
        }
        if (!rec.TryGetValue("part_seq", out var seenObj))
        {
            seenObj = new Dictionary<string, long>();
            rec["part_seq"] = seenObj;
        }
        var seen = (Dictionary<string, long>)seenObj!;
        if (seen.TryGetValue(name, out var old) && seq < old) return;
        seen[name] = seq;
        if (!rec.TryGetValue("sources", out var srcObj))
        {
            srcObj = new Dictionary<string, string>();
            rec["sources"] = srcObj;
        }
        ((Dictionary<string, string>)srcObj!)[name] = source;
        rec["seq"] = Math.Max(seq, Convert.ToInt64(rec["seq"]));
        var v = d;
        if (name == "IsAlive")
        {
            var before = LifeTracker.Current(actor);
            if (LifeTracker.Observe(actor, value, seq, source: source))
            {
                if (before["dead"] is true && (v == 1 || v == 3))
                {
                    rec["revive_seq"] = seq;
                }
            }
        }
        else if (name == "ImpendingDeathHealth")
        {
            if (rec.TryGetValue("impending", out var imp) && imp is not null && v < Convert.ToDouble(imp))
            {
                rec["imp_chg_seq"] = seq;
            }
            rec["impending"] = v;
        }
        else
        {
            var parts = (Dictionary<string, object?>)rec["parts"]!;
            var previous = parts.TryGetValue(name, out var pv) ? pv : null;
            if ((name is "HeadHealth" or "ThoraxHealth" or "AbdomenHealth")
                && previous is not null && Convert.ToDouble(previous) == 0 && v > 0)
            {
                rec["revive_seq"] = seq;
            }
            parts[name] = v;
        }
    }

    /// <summary>增量解码新增子对象块 → 角色状态 + 血量（Python: _decode_fsm_incremental）。</summary>
    private void DecodeFsmIncremental(
        List<FlowLiveLoader.LB> newLbs, IReadOnlyDictionary<int, ulong>? actorByLb = null)
    {
        try
        {
            var c2cat = ClsToCat();
            if (c2cat.Count == 0) return;
            foreach (var x in newLbs)
            {
                // 含导出的 bunch：块流从 parse_prefix 的 ss 起（tile(0) 会错位）。
                var ss = 0;
                if (x.Hx != 0 || x.Hm != 0)
                {
                    try
                    {
                        var (prefInfo, ssParsed) = Sna.ParsePrefix(
                            x.Payload, x.PBits, x.Hx != 0, x.Hm != 0,
                            quantizeBit: false, frags: x.Frags);
                        ss = ssParsed;
                        if (prefInfo.TryGetValue("exports", out var expObj) && expObj is List<Dictionary<string, object?>> exports)
                        {
                            foreach (var e in exports)
                            {
                                if (e.TryGetValue("path", out var p) && p is string path
                                    && e.TryGetValue("guid", out var g) && g is ulong guid)
                                {
                                    SetGuidPath(guid, path);
                                    if (!c2cat.ContainsKey(guid))
                                    {
                                        var cm = Tail2CatName(guid);
                                        if (cm is not null) c2cat[guid] = cm;
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                        ss = 0;
                    }
                }
                // partial_ok：重组超 bunch 尾部对齐残留，严格模式丢全员装备组件块。
                var blocks = Blocks.Tile(x.Payload, x.PBits, ss, partialOk: true);
                if (blocks is null) continue;
                // Python: actor = actor_by_lb.get(id(x)) if actor_by_lb is not None
                //         else self._fsm_ch_actor.get(x.ch)
                // 注意：actor_by_lb 提供时只查它，查不中即 None（不回退通道表）。
                ulong? actor = actorByLb is not null
                    ? (actorByLb.TryGetValue(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(x), out var a1) ? a1 : null)
                    : (_fsmChActor.TryGetValue(x.Ch, out var a2) ? a2 : null);
                if (actor is null) continue;

                void MergeEquipScan(ContentBlock kk)
                {
                    if (kk.PayloadEnd - kk.PayloadStart < 250) return;
                    List<Dictionary<string, object?>> els;
                    try
                    {
                        els = ScanEquipArrays(x.Payload, kk.PayloadStart, kk.PayloadEnd)
                            .Select(e => new Dictionary<string, object?>
                            {
                                ["item_id"] = e.ItemId, ["uid"] = e.Uid,
                                ["dur"] = e.Dur, ["dur_max"] = e.DurMax, ["k"] = e.K,
                            }).ToList();
                    }
                    catch
                    {
                        return;
                    }
                    if (els.Count == 0) return;
                    if (!Equip.TryGetValue(actor.Value, out var erec))
                    {
                        erec = new Dictionary<string, Dictionary<ulong, Dictionary<string, object?>>>
                        {
                            ["armor"] = [], ["items"] = [],
                        };
                        Equip[actor.Value] = erec;
                    }
                    var items = erec["items"];
                    foreach (var el in els)
                    {
                        // uid 可超 long.MaxValue —— 必须 ToUInt64（黄金对拍实证溢出）。
                        var uid = Convert.ToUInt64(el["uid"]);
                        if (!items.TryGetValue(uid, out var f))
                        {
                            f = new Dictionary<string, object?> { ["seq"] = x.Seq };
                            items[uid] = f;
                        }
                        f["seq"] = x.Seq;
                        f["item_id"] = el["item_id"];
                        if (el.TryGetValue("dur", out var dur) && dur is not null) f["dur"] = Convert.ToDouble(dur);
                        if (el.TryGetValue("dur_max", out var dmx) && dmx is not null) f["dur_max"] = Convert.ToDouble(dmx);
                    }
                }

                Dictionary<string, object?>? fsmRec = null;
                foreach (var k in blocks)
                {
                    if (k.Deleted) continue;
                    if (k.IsActor != 0)
                    {
                        // actor 块 payload 也可能被 tile 错位吞进装备数组。
                        MergeEquipScan(k);
                        continue;
                    }
                    var cg = k.Stably != 0 ? k.Sub : k.Cls;
                    var catname = cg is not null && c2cat.TryGetValue(cg.Value, out var cn) ? cn : null;
                    if (catname != null) Dbg("cat:" + catname);
                    // 临时诊断：装备组件块路由失败现场（路径尾是 CharacterEquip 但 c2cat 未映射）。
                    if (catname is null && cg is not null
                        && Tail2CatName(cg.Value) == "SOLCharacterEquipComponent")
                    {
                        Dbg($"equipRouteMiss:guid{cg.Value}");
                    }
                    if (catname == "SOLCharacterEquipComponent" && actor is not null)
                    {
                        Dbg($"equipRouteHit:actor{actor.Value}");
                    }
                    if (catname == "WeaponReplicateComponent") continue;   // WeaponTracker 负责
                    if (catname is null)
                    {
                        MergeEquipScan(k);
                        continue;
                    }
                    if (!_subCats.TryGetValue(catname, out var cat))
                    {
                        MergeEquipScan(k);
                        continue;
                    }
                    List<RepEvent> ev;
                    string status;
                    try
                    {
                        (ev, _, status) = RepDec6.DecodeBlock(
                            x.Payload, k.PayloadStart, k.PayloadEnd, cat, []);
                    }
                    catch
                    {
                        continue;
                    }
                    if (catname == "SOLCharacterEquipComponent")
                    {
                        Dbg($"equipBlk:status{status}:ev{ev.Count}:actor{actor}:bits{k.PayloadStart}-{k.PayloadEnd}:seq{x.Seq}");
                    }
                    if (ev.Count == 0) MergeEquipScan(k);
                    if (status is not ("exact" or "short"))
                    {
                        // 装备组件放宽——error/leftover 块的已解 ev 位流合法。
                        if (catname is not ("SOLCharacterEquipComponent"
                            or "WeaponReplicateComponent" or "MultiWeaponManagerComponent"))
                        {
                            continue;
                        }
                        if (ev.Count == 0) continue;
                    }
                    var par = cat.Parents.ToDictionary(
                        p => Convert.ToInt32(p.TryGetValue("index", out var i) ? i : -1),
                        p => p.TryGetValue("name", out var n) ? n?.ToString() ?? "?" : "?");
                    fsmRec ??= FsmState.TryGetValue(actor.Value, out var existing)
                        ? existing
                        : FsmState[actor.Value] = new Dictionary<string, object?>
                        {
                            ["fsm_states"] = new List<object?>(),
                            ["fight"] = new Dictionary<string, object?>(),
                        };
                    foreach (var e in ev)
                    {
                        var nm = par.TryGetValue(e.ParentIndex, out var pn) ? pn : "?";
                        if (catname == "DFMCharacterFSM_Main_C" && nm == "R_ActiveStates")
                        {
                            // 保留最近 32 条状态 FGuid words（Python: del lst[:-32]）。
                            if (!fsmRec.TryGetValue("raw_state_guid_words", out var rawObj)
                                || rawObj is not List<object?> raw)
                            {
                                raw = [];
                                fsmRec["raw_state_guid_words"] = raw;
                            }
                            raw.Add(e.Value);
                            if (raw.Count > 32) raw.RemoveRange(0, raw.Count - 32);
                        }
                        else if (catname == "GPFightStateComponent")
                        {
                            ((Dictionary<string, object?>)fsmRec["fight"]!)[nm] = e.Value;
                        }
                        else if (catname == "DFMCharacterFashionComponent")
                        {
                            // 0904 起 currentCharacterHeroID 从 PS h137 移到时装组件。
                            if (nm == "currentCharacterHeroID" && e.Value is long hid
                                && hid is >= 88000000001L and <= 88000000099L)
                            {
                                if (Links.TryGetValue(actor.Value, out var lk)
                                    && (!lk.TryGetValue("hero", out var hero) || hero is null))
                                {
                                    lk["hero"] = hid;
                                }
                            }
                        }
                        else if (catname == "GPAttributeSetHealth")
                        {
                            // Health/MaxHealth 型双 float 取 CurrentValue。
                            var cheOk = CurrentHealthEvent(e, cat);
                            if (cheOk) Dbg("health:cheOk");
                            else Dbg("health:cheMiss");
                            if (cheOk)
                            {
                                RecordHealth(actor.Value, nm, e.Value, x.Seq);
                            }
                        }
                        else if (catname == "SOLCharacterEquipComponent")
                        {
                            // 护甲装备（头甲/防弹衣等级与耐久）。
                            if (!Equip.TryGetValue(actor.Value, out var erec2))
                            {
                                erec2 = new Dictionary<string, Dictionary<ulong, Dictionary<string, object?>>>
                                {
                                    ["armor"] = [], ["items"] = [],
                                };
                                Equip[actor.Value] = erec2;
                            }
                            if (e.Path.Count == 0) continue;
                            var ei = (ulong)e.Path[^1].Element;
                            var slot = e.CmdIndex is >= 13 and <= 30 ? "armor" : "items";
                            var bucket = erec2[slot];
                            if (!bucket.TryGetValue(ei, out var f2))
                            {
                                f2 = new Dictionary<string, object?> { ["seq"] = x.Seq };
                                bucket[ei] = f2;
                            }
                            f2["seq"] = x.Seq;
                            var v2 = e.Value;
                            if (slot == "items")
                            {
                                if (e.TName == "UInt64" && v2 is long lv2 && lv2 is >= 10000000000L and < 100000000000L)
                                {
                                    f2["item_id"] = v2;   // 11 位物品 ID
                                }
                                else if (e.TName == "Float" && v2 is double dv2)
                                {
                                    f2["dur"] = dv2;
                                }
                            }
                            else
                            {
                                if (e.TName == "UInt64" && v2 is long lv3 && lv3 is >= 10000000000L and < 100000000000L)
                                {
                                    f2["item_id"] = lv3;
                                }
                                else if (e.TName == "Float" && v2 is double dv3 && !f2.ContainsKey("dur"))
                                {
                                    f2["dur"] = dv3;
                                }
                            }
                        }
                        else if (catname == "MultiWeaponManagerComponent")
                        {
                            // 武器 ID 列表（u64 完整武器模板 ID）。
                            if (!Weapons.TryGetValue(actor.Value, out var wrec))
                            {
                                Weapons[actor.Value] = wrec = [];
                            }
                            var nm2 = par.TryGetValue(e.ParentIndex, out var pn2) ? pn2 : "?";
                            if (nm2 == "HadFirstEquipWeaponID")
                            {
                                wrec["first"] = e.Value;
                            }
                            else if (nm2 is "WeaponIDList" or "WeaponIdentityList")
                            {
                                if (!wrec.TryGetValue("ids", out var idsObj) || idsObj is not List<object?> ids)
                                {
                                    ids = [];
                                    wrec["ids"] = ids;
                                }
                                if (e.Value is long iv && iv > 0) ids.Add((List<object?>)[x.Seq, iv]);
                                if (ids.Count > 16) ids.RemoveRange(0, ids.Count - 16);
                            }
                        }
                    }
                }
                // 块间隙扫描——tile 错位丢失的装备数组常落在相邻块之间或尾部残留。
                try
                {
                    var last = ss;
                    var gaps = new List<(int G0, int G1)>();
                    foreach (var k in blocks)
                    {
                        if (k.Deleted) continue;
                        if (k.PayloadStart > last + 250) gaps.Add((last, k.PayloadStart));
                        last = Math.Max(last, k.PayloadEnd);
                    }
                    if (x.PBits - last > 250) gaps.Add((last, x.PBits));
                    foreach (var (g0, g1) in gaps)
                    {
                        MergeEquipScan(new ContentBlock
                        {
                            PayloadStart = g0, PayloadEnd = g1,
                        });
                    }
                }
                catch
                {
                    // Python: except Exception: pass
                }
            }
        }
        catch
        {
            // Python: 外层 except Exception: pass
        }
    }

    private static List<Dictionary<string, object?>>? SnaLastExports;

    /// <summary>装备数组结构判定提取结果行（Python _scan_equip_arrays 元素）。
    /// uid 用 ulong——uid 可达 2^63+（Python 任意精度 int），long 会溢出为负。</summary>
    public readonly record struct EquipElement(long ItemId, ulong Uid, double? Dur, double? DurMax, long K);

    /// <summary>Sna.ParsePrefix 的导出捕获（_decode_fsm_incremental 用）。</summary>
    internal static List<Dictionary<string, object?>>? SnaLastExportsField
    {
        get => SnaLastExports;
        set => SnaLastExports = value;
    }
}
