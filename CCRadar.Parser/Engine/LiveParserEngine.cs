using System.Text.Json;
using System.Text.Json.Nodes;
using CCRadar.Parser.Core;

namespace CCRadar.Parser.Engine;

/// <summary>
/// 纯 C# 解析引擎编排层——对应 Python gateway_parser 的实时入口角色：
/// 消费 RealtimeOutput 的 JSONL 事件（udp_plaintext / session_status / session_stopped），
/// 按流路由进 LiveView（内含 FlowLiveLoader 完整解析链），对外提供
/// /api/state 快照与 gateway-parser-status/v5 状态。
/// 与 Python 的差异：无磁盘 run 目录（单进程内存态）；多流按 Python LiveDetector
/// 的"最新战斗流优先"规则选当前视图。
/// </summary>
public sealed class LiveParserEngine
{
    private readonly object _gate = new();

    /// <summary>flowKey → 运行视图（Python: runs；flowKey = 无向端点对）。</summary>
    private readonly Dictionary<string, Run> _runs = [];

    /// <summary>诊断缓存：前若干个 open 逻辑 bunch（重放对拍用；DumpBufferedOpenLbs 消费）。</summary>
    private readonly List<string> _openLbDump = [];
    private bool _dumpDone;
    private Run? _current;

    /// <summary>最近一次 session_status 事件载荷（v5 transport 段来源，Python 由 stdin 喂入后原样回显）。</summary>
    private JsonObject? _lastTransport;
    private string? _stoppedError;

    // ---- 事件序列门控（Python gateway_parser：sequence 单调，断档 reset）----
    private long _lastEnvelopeSeq = -1;
    private int _session;
    /// <summary>(local, remote) → epoch（Python: self.epochs）。</summary>
    private readonly Dictionary<string, int> _epochs = [];
    private int _pollCounter;

    // ---- API 状态层（Python gateway_parser.Engine 的对应字段）----
    /// <summary>速率事件窗口（Python: rate_events deque；2s 滑窗 → 每秒速率）。</summary>
    private readonly Queue<(double Ts, string Kind, int Count)> _rateEvents = new();
    /// <summary>每实体/自身已计入速率的最后 seq（Python: last_pose_keys）。</summary>
    private readonly Dictionary<string, long> _lastPoseKeys = [];
    /// <summary>每实体/自身最后一次姿态出现时刻（Python: last_pose_seen）。</summary>
    private readonly Dictionary<string, double> _lastPoseSeen = [];
    /// <summary>状态变更计数（Python: state_sequence；status.states 的来源）。</summary>
    private long _stateSequence;
    private double _lastDiskWrite;
    private double _lastIdentityWrite;
    private double _lastFlush;
    private JsonObject? _lastState;
    private string? _lastStateJson;
    private JsonObject? _lastNonEmpty;
    /// <summary>逐项统计（Python: stats Counter；键只在实际发生时出现）。</summary>
    private readonly Dictionary<string, long> _stats = [];
    private bool _ended;

    /// <summary>落盘目录（state-latest.json / status-latest.json / identity-cache.json / parser-errors.jsonl）。
    /// 赋值时按 Python Engine.__init__ 的 cache_only 语义清空 parser-errors.jsonl。</summary>
    public string? OutputDirectory
    {
        get => _outputDirectory;
        set
        {
            _outputDirectory = value;
            if (value is not null)
            {
                try { File.WriteAllText(Path.Combine(value, "parser-errors.jsonl"), ""); }
                catch { /* 目录不可写：落盘失败不影响解析 */ }
            }
        }
    }
    private string? _outputDirectory;

    private void Bump(string key, long by = 1)
        => _stats[key] = (_stats.TryGetValue(key, out var v) ? v : 0) + by;

    /// <summary>输入事件异常计数（Python: stats["inputErrors"] + parser-errors.jsonl 追加）。</summary>
    public void NoteInputError(string? detail = null)
    {
        lock (_gate)
        {
            Bump("inputErrors");
            if (_outputDirectory is null) return;
            try
            {
                var line = new JsonObject
                {
                    ["ts"] = Now(),
                    ["error"] = detail ?? "input error",
                }.ToJsonString();
                File.AppendAllText(Path.Combine(_outputDirectory!, "parser-errors.jsonl"), line + "\n");
            }
            catch { /* 记录失败不影响解析 */ }
        }
    }

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    private static void TryWrite(string path, string text)
    {
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* 落盘失败不影响解析主流程 */ }
    }

    // ---- v5 stats 计数（对应 parser-result.json 的 stats 段）----
    public long InputEvents { get; private set; }
    public long PlaintextPackets { get; private set; }
    public long UpPackets { get; private set; }
    public long DownPackets { get; private set; }

    /// <summary>用于速率计算的滑动窗口（up/down 包时间戳，秒）。</summary>
    private readonly Queue<double> _upWindow = new();
    private readonly Queue<double> _downWindow = new();

    private sealed class Run
    {
        public required LiveView View { get; init; }
        public double LastTs { get; set; }
        public long Packets { get; set; }
        public string? LastDetail { get; set; }
        /// <summary>流身份（Python: identity = (session, local, remote, epoch)）。</summary>
        public string Key { get; init; } = "";
        public string Local { get; init; } = "";
        public string Remote { get; init; } = "";
        public int Epoch { get; init; }
        /// <summary>最后一次收到包的墙钟时刻（Python: flow.last_received，空闲判定用）。</summary>
        public double LastReceived { get; set; }
        /// <summary>所属会话号（Python: identity[0]）。</summary>
        public int Session { get; init; }
    }

    /// <summary>诊断：所有 run 的拓扑（键/包数/握手/路径数/帧数）。</summary>
    public List<string> RunsDebug()
    {
        lock (_gate)
        {
            return _runs.Select(kv =>
            {
                var v = kv.Value.View;
                return $"{kv.Key} | 包={kv.Value.Packets} ctrl={v.Loader.CtrlDone} 帧N={v.Loader.NFrames} " +
                       $"LB={v.Loader.NFeedEmitted} guidPaths={v.GuidPathCount} players={v.Players.Count}";
            }).ToList();
        }
    }

    /// <summary>当前活跃视图（无流时 null）。</summary>
    public LiveView? CurrentView { get { lock (_gate) return _current?.View; } }

    /// <summary>
    /// 消费一条 RealtimeOutput 事件（envelope 已拆开：type + data + envelopeSeq）。
    /// 语义严格对照 Python gateway_parser.process_event：
    /// sequence 单调门控（断档 reset）、session_started 重置、epoch 世代退役。
    /// </summary>
    public void ConsumeEvent(string type, JsonElement data, long? envelopeSeq = null)
    {
        lock (_gate)
        {
            if (type == "session_started")
            {
                // Python: reset() + session += 1。
                Reset();
                _session++;
                _lastEnvelopeSeq = -1;
            }
            // sequence 单调门控（Python: duplicate/out-of-order 丢弃，gap 触发 reset）。
            if (envelopeSeq is { } seq)
            {
                if (seq < 0) return;
                if (_lastEnvelopeSeq >= 0)
                {
                    if (seq <= _lastEnvelopeSeq) { Bump("duplicateOrOutOfOrder"); return; }   // 重复/乱序
                    if (seq != _lastEnvelopeSeq + 1)
                    {
                        Bump("eventGaps", seq - _lastEnvelopeSeq - 1);
                        Reset();                                                              // 断档：状态不可信，整体重置
                    }
                }
                _lastEnvelopeSeq = seq;
            }
            InputEvents++;
            Bump("inputEvents");
            switch (type)
            {
                case "udp_plaintext":
                    ConsumePlaintext(data, envelopeSeq);
                    break;
                case "session_status":
                    // Python: transport 段 = C# 侧 session_status 载荷原样嵌入。
                    _lastTransport = JsonNode.Parse(data.GetRawText()) as JsonObject;
                    break;
                case "session_stopped":
                    if (data.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0)
                    {
                        _stoppedError = string.Join("; ", errs.EnumerateArray().Select(e => e.ToString()));
                    }
                    _ended = true;
                    break;
            }
        }
    }

    /// <summary>清空所有运行态（Python: reset）。</summary>
    private void Reset()
    {
        _runs.Clear();
        _epochs.Clear();
        _current = null;
        PlaintextPackets = 0;
        UpPackets = 0;
        DownPackets = 0;
        _upWindow.Clear();
        _downWindow.Clear();
        ResetStateCache();
    }

    private void ConsumePlaintext(JsonElement d, long? envelopeSeq)
    {
        // Python: direction = {"up": "request", "down": "response"}[direction]。
        var rawDir = d.GetProperty("direction").GetString();
        var dir = rawDir switch { "up" => "request", "down" => "response", _ => null };
        if (dir is null) return;
        var local = d.GetProperty("local").GetString() ?? "";
        var remote = d.GetProperty("remote").GetString() ?? "";
        var payloadB64 = d.GetProperty("payloadBase64").GetString();
        if (payloadB64 is null) return;
        byte[] payload;
        try { payload = Convert.FromBase64String(payloadB64); }
        catch (FormatException) { return; }
        var ts = d.TryGetProperty("timestampMs", out var tsEl) ? tsEl.GetDouble() / 1000.0 : 0.0;
        if (!double.IsFinite(ts) || ts <= 0) return;

        // epoch 世代：对端换钥（UDP 重连）→ 旧流退役（Python: retire）。
        var pair = $"{local}>{remote}";
        var epoch = d.TryGetProperty("epoch", out var epEl) && epEl.ValueKind == JsonValueKind.Number
            ? epEl.GetInt32() : 1;
        if (_epochs.TryGetValue(pair, out var knownEpoch))
        {
            if (epoch < knownEpoch) { Bump("retiredEpochPackets"); return; }   // 旧世代包：丢弃
            if (epoch > knownEpoch)
            {
                // 退役该 pair 的所有 run（Python: key[1:3] == pair → retire）。
                foreach (var rk in _runs.Where(kv => kv.Key.StartsWith(pair + "#", StringComparison.Ordinal)
                    || kv.Key == pair).Select(kv => kv.Key).ToList())
                {
                    _runs.Remove(rk);
                }
                _epochs[pair] = epoch;
            }
        }
        else
        {
            _epochs[pair] = epoch;
        }

        PlaintextPackets++;
        Bump("plaintextPackets");
        if (dir == "request") { UpPackets++; Bump("upPackets"); NoteTs(_upWindow, ts); }
        else { DownPackets++; Bump("downPackets"); NoteTs(_downWindow, ts); }
        // Python: rate_events 也记录上下行包速率（count=1）。
        lock (_gate) _rateEvents.Enqueue((Now(), dir == "request" ? "up" : "down", 1));

        // 流身份 = (session, local, remote, epoch)（Python: identity）。
        var key = $"{pair}#{_session}#{epoch}";
        if (!_runs.TryGetValue(key, out var run))
        {
            run = new Run { View = new LiveView(), Key = key, Local = local, Remote = remote, Epoch = epoch, Session = _session };
            _runs[key] = run;
        }
        run.Packets++;
        run.LastTs = ts;
        run.LastReceived = Now();   // 墙钟（Python: flow.last_received，空闲判定用）
        run.View.NowTs = ts;
        // 包序号必须用日志 envelope 的 sequence（Python: pending_packets.append((sequence, ...))
        // → consume_packet(seq, ...)）。用内部自增计数器会让 spawn_seq / move_handle_seq /
        // pose_seq 等全部输出偏移（实测 spawn_seq 16838 vs 7139）。无 sequence 时才回退自增。
        run.View.ConsumePacket(envelopeSeq ?? NextSeq(), dir, payload, ts);
        if (!_dumpDone) CacheOpenLb(key, run.View);

        // 当前视图选择（Python LiveDetector 简化）：优先握手完成的战斗流；
        // 同为战斗流时切到最新活跃的。一旦锁定战斗流不回退到非战斗流。
        if (_current is null
            || (run != _current && run.View.Loader.CtrlDone && !_current.View.Loader.CtrlDone)
            || (run != _current && run.View.Loader.CtrlDone && _current.View.Loader.CtrlDone && run.LastTs > _current.LastTs + 10.0))
        {
            _current = run;
        }

        // Python：pending 攒 256 包 poll 一次（max_frames=256）；这里以消费计数近似。
        if (++_pollCounter % 256 == 0) _current?.View.Poll(256);
    }

    private long _seqCounter;
    private long NextSeq() => Interlocked.Increment(ref _seqCounter);

    /// <summary>每个 run 的导出型 bunch 采样（runKey → 行列表，各 3 条）。</summary>
    private readonly Dictionary<string, List<string>> _dumpPerRun = [];

    /// <summary>缓存刚消费包产生的 open 逻辑 bunch（每 run 前 3 个 Hx=1，供 Python 并排对拍）。</summary>
    private void CacheOpenLb(string runKey, LiveView view)
    {
        _dumpPerRun.TryGetValue(runKey, out var list);
        list ??= [];
        if (list.Count < 3)
        {
            // 先取 B（Poll 会清空），再排空视图。
            foreach (var kv in view.Loader.B)
            {
                foreach (var lb in kv.Value)
                {
                    if (lb.Open == 0 || lb.Hx == 0 || list.Count >= 3) continue;
                    list.Add($"ch{kv.Key}|{lb.Seq}|open={lb.Open}|hx={lb.Hx}|hm={lb.Hm}|pbits={lb.PBits}|" +
                        Convert.ToHexString(lb.Payload).ToLowerInvariant());
                }
            }
            _dumpPerRun[runKey] = list;
            if (_dumpPerRun.Count >= 2 && _dumpPerRun.Values.All(l => l.Count >= 3)) _dumpDone = true;
        }
        else if (_dumpPerRun.Count >= 2)
        {
            _dumpDone = true;
        }
        view.Poll();
    }

    /// <summary>取回转储的 open bunch 行（附 run 前缀）。</summary>
    public List<string> DumpBufferedOpenLbs(int n)
        => _dumpPerRun.SelectMany(kv => kv.Value.Select(l => $"{kv.Key}#{l}")).Take(n).ToList();

    private static void NoteTs(Queue<double> window, double ts)
    {
        window.Enqueue(ts);
        while (window.Count > 0 && ts - window.Peek() > 10.0) window.Dequeue();
    }

    /// <summary>双速率（包/秒，10s 窗口）。</summary>
    public (double Up, double Down) Rates()
    {
        lock (_gate)
        {
            var now = Math.Max(_upWindow.Count > 0 ? _upWindow.Last() : 0, _downWindow.Count > 0 ? _downWindow.Last() : 0);
            return (_upWindow.Count / Math.Max(1.0, now - _upWindow.Peek() + 0.001) >= 0 ? _upWindow.Count / 10.0 : 0,
                    _downWindow.Count / 10.0);
        }
    }

    internal static string FlowKey(string local, string remote)
    {
        var a = Normalize(local);
        var b = Normalize(remote);
        return string.CompareOrdinal(a, b) <= 0 ? $"{a}>{b}" : $"{b}>{a}";
    }

    private static string Normalize(string endpoint)
        => endpoint.Replace(" ", "").Replace("：", ":");

    // ---- /api/state（对应 Python gateway_parser.Engine.state：life 分支 + 全量富化）----

    /// <summary>空闲判定秒数（Python: idle_seconds=15）。</summary>
    private const double IdleSeconds = 15.0;

    /// <summary>空状态（Python: Engine.empty）——等待/空闲/停止/错误态下实体清空。</summary>
    private static JsonObject EmptyState(string status) => new()
    {
        ["schema"] = "gateway-state/v5",
        ["self"] = null,
        ["entities"] = new JsonArray(),
        ["names"] = new JsonArray(),
        ["live_active"] = false,
        ["status"] = status,
        ["cursor"] = new JsonObject { ["seq"] = 0, ["live"] = true },
    };

    /// <summary>复位状态缓存（Python: reset → last_state = empty("seeking")）。state_sequence 不回退。</summary>
    private void ResetStateCache()
    {
        _lastState = EmptyState("seeking");
        _lastStateJson = _lastState.ToJsonString();
        _lastNonEmpty = null;
        _rateEvents.Clear();
        _lastPoseKeys.Clear();
        _lastPoseSeen.Clear();
    }

    /// <summary>
    /// 组装 /api/state 应答体（Python: Engine.state）——视图快照 + 顶层身份/状态字段 +
    /// 上行位姿富化 + 逐实体状态标注（loot 身份/元数据、人物姿态时序）+ 状态序号与节流落盘。
    /// </summary>
    public JsonObject State(bool force = false)
    {
        lock (_gate)
        {
            // live 语义：先把当前视图的待处理帧排空再做快照，否则 tail-follow 下
            // 浏览器每次轮询只能看到 256 包批次的滞后状态。
            _current?.View.Poll(256);
            var run = _current;
            JsonObject state;
            if (_stoppedError is not null)
            {
                state = EmptyState("error");
                state["error"] = _stoppedError;
            }
            else if (run is null)
            {
                state = EmptyState(_ended ? "stopped" : "waiting");
            }
            else if (_ended || Now() - run.LastReceived > IdleSeconds)
            {
                // Python: 非回放且（已结束或超时无包）→ 空状态（避免残留过期实体）。
                state = EmptyState(_ended ? "stopped" : "idle");
            }
            else
            {
                state = ToNode(run.View.Snapshot()) as JsonObject ?? [];
                // trail 不进 API 应答（Python: entity.pop("trail")——避免每次轮询重复增长轨迹）。
                if (state["entities"] is JsonArray rawEnts)
                {
                    foreach (var e in rawEnts)
                    {
                        if (e is JsonObject eo) eo.Remove("trail");
                    }
                }
                state["schema"] = "gateway-state/v5";
                state["status"] = "live";
                state["live_active"] = true;
                state["session"] = _session;
                state["epoch"] = run.Epoch;
                state["local"] = run.Local;
                state["remote"] = run.Remote;
                state["timestampMs"] = (long)(run.LastTs * 1000);
                if (state["cursor"] is JsonObject cur) cur["live"] = true;
                // 上行位姿富化：仅 0..0.25s 内有效（Python 同；网页据此覆盖自机朝向）。
                var pose = run.View.Loader.SelfPose;
                var poseAge = Now() - run.View.Loader.SelfPoseT;
                if (pose is not null && poseAge is >= 0 and <= 0.25)
                {
                    state["self_pitch"] = Round1(pose.TryGetValue("pitch", out var pi) ? Convert.ToDouble(pi) : 0);
                    state["self_aim_yaw"] = Round1(pose.TryGetValue("yaw", out var ya) ? Convert.ToDouble(ya) : 0);
                    state["self_aim_age_ms"] = (long)Math.Round(poseAge * 1000);
                    state["self_aim_source"] = "upstream";
                    if (poseAge <= 1.0 && pose.TryGetValue("loc", out var locObj)
                        && locObj is System.Collections.IEnumerable locSeq and not string)
                    {
                        var upLoc = new JsonArray();
                        foreach (var c in locSeq)
                        {
                            if (upLoc.Count >= 3) break;
                            upLoc.Add(Math.Round(Convert.ToDouble(c), 1));
                        }
                        if (upLoc.Count >= 3)
                        {
                            state["self_up_loc"] = upLoc;
                            if (pose.TryGetValue("ts", out var tsv))
                            {
                                try { state["self_up_ts"] = Math.Round(Convert.ToDouble(tsv), 4); }
                                catch { /* 时间戳异常时省略（Python 同） */ }
                            }
                        }
                    }
                }
            }
            var now = Now();
            if (run is not null && state["entities"] is JsonArray ents)
            {
                foreach (var node in ents)
                {
                    if (node is not JsonObject entity) continue;
                    var kind = entity["kind"]?.GetValue<string>();
                    if (kind == "loot")
                    {
                        // Python: identity_status / metadata_status（物资识别与元数据可用性）。
                        var itemIdMissing = entity["item_id"] is null;
                        var lname = entity["name"]?.GetValue<string>();
                        entity["identity_status"] = itemIdMissing
                            ? "missing_item_id"
                            : (lname is null or "" or "物资" ? "name_not_mapped" : "identified");
                        entity["metadata_status"] = entity["price"] is not null ? "available" : "missing";
                    }
                    if (kind is not ("player" or "mate" or "unknown")) continue;
                    // Python: 人物姿态时序标注 + 速率记账（每实体按 seq 增量）。
                    // 注意：此处的实体已是 JsonNode（JsonValue 包装），必须按 JsonValue 取数，
                    // 不能交给按原生 int/long 匹配的 ToSlotIndex（实测恒 null）。
                    var slot = entity["slot"] is JsonValue sv && sv.TryGetValue<int>(out var si) ? si : (int?)null;
                    var track = slot is not null && run.View.Slots.TryGetValue(slot.Value, out var tr) ? tr : null;
                    var poseKey = $"{run.Key}|{entity["key"]?.GetValue<string>()}";
                    if (track is { Count: > 0 })
                    {
                        var last = track[^1];
                        var prev = _lastPoseKeys.TryGetValue(poseKey, out var pv) ? pv : -1;
                        var count = 0;
                        for (var i = track.Count - 1; i >= 0; i--)
                        {
                            if (track[i].Seq > prev) count++; else break;
                        }
                        if (count > 0)
                        {
                            _lastPoseKeys[poseKey] = last.Seq;
                            _lastPoseSeen[poseKey] = now;
                            _rateEvents.Enqueue((now, "characters", count));
                            if (entity["is_bot"]?.GetValue<bool>() != true)
                            {
                                _rateEvents.Enqueue((now, "human", count));
                            }
                        }
                        entity["pose_seq"] = last.Seq;
                        entity["pose_timestamp_ms"] = (long)(last.Ts * 1000);
                        entity["pose_age_ms"] = (long)Math.Round(
                            (now - (_lastPoseSeen.TryGetValue(poseKey, out var seenTs) ? seenTs : now)) * 1000);
                    }
                }
                // 自身位姿采样速率（Python: pose_key = (identity, "self")）。
                var selfKey = $"{run.Key}|self";
                var samples = run.View.Loader.PoseSamples;
                var prevSamples = _lastPoseKeys.TryGetValue(selfKey, out var ps) ? ps : 0;
                if (samples != prevSamples)
                {
                    _lastPoseKeys[selfKey] = samples;
                    _lastPoseSeen[selfKey] = now;
                    _rateEvents.Enqueue((now, "self", (int)Math.Max(0, samples - prevSamples)));
                }
            }
            // 状态变更 → state_sequence++（status.states 来源）与 0.5s 节流落盘。
            var json = state.ToJsonString();
            if (force || json != _lastStateJson)
            {
                _lastStateJson = json;
                _lastState = state;
                if (state["self"] is not null || (state["entities"] is JsonArray ne && ne.Count > 0))
                {
                    _lastNonEmpty = (JsonObject)state.DeepClone();
                }
                _stateSequence++;
                if (_outputDirectory is not null && (force || now - _lastDiskWrite >= 0.5))
                {
                    _lastDiskWrite = now;
                    TryWrite(Path.Combine(_outputDirectory!, "state-latest.json"), json);
                    TryWrite(Path.Combine(_outputDirectory!, "status-latest.json"), BuildStatus(_ended).ToJsonString());
                }
            }
            // 身份缓存（Python: cache_identity，1s 节流；cache_only 模式必写）。
            if (_outputDirectory is not null && (force || now - _lastIdentityWrite >= 1.0))
            {
                _lastIdentityWrite = now;
                TryWrite(Path.Combine(_outputDirectory!, "identity-cache.json"), IdentityCache().ToJsonString());
            }
            return state;
        }
    }

    /// <summary>周期刷新（Python 主循环 ~30Hz flush）：推进状态序号与节流落盘。</summary>
    public void Flush()
    {
        // Python 的 flush 在主循环内以 ~30Hz 调用（time.monotonic() 节流）；
        // tail-follow 每条日志都调会显著抬高 state_sequence，必须同频节流。
        var now = Now();
        if (now - _lastFlush < 1.0 / 30.0) return;
        _lastFlush = now;
        State(force: false);
    }

    /// <summary>
    /// 身份缓存（Python: cache_identity → identity-cache.json）：逐流的
    /// 玩家/角色身份字段 + 通道归属 + 槽位认领，供外部工具做对局身份核对。
    /// </summary>
    public JsonObject IdentityCache()
    {
        lock (_gate)
        {
            string[] playerFields = ["name", "player_id", "bot", "actor_guid", "owner_guid", "channel", "uin",
                "team", "camp", "inner_team", "hero_id", "expert_id", "spawn_seq"];
            string[] linkFields = ["owner", "ps_guid", "spawn_seq", "cls", "ai_key", "hero",
                "move_handle", "move_handle_seq"];
            var flows = new JsonArray();
            foreach (var (_, r) in _runs)
            {
                var players = new JsonObject();
                foreach (var (k, v) in r.View.Players)
                {
                    var o = new JsonObject();
                    foreach (var f in playerFields)
                    {
                        if (v.TryGetValue(f, out var val)) o[f] = ToNode(val);
                    }
                    players[k.ToString()] = o;
                }
                var characters = new JsonObject();
                foreach (var (k, v) in r.View.Links)
                {
                    var o = new JsonObject();
                    foreach (var f in linkFields)
                    {
                        if (v.TryGetValue(f, out var val)) o[f] = ToNode(val);
                    }
                    characters[k.ToString()] = o;
                }
                var playerChannels = new JsonObject();
                foreach (var (k, v) in r.View.PsChActor) playerChannels[k.ToString()] = v;
                var characterChannels = new JsonObject();
                foreach (var (k, v) in r.View.ChActor) characterChannels[k.ToString()] = v;
                var slotClaims = new JsonObject();
                // Python 的 claim 是 actor → slot；C# 内部为 slot → actor，输出时需反置。
                foreach (var (slot, actor) in r.View.Claim) slotClaims[actor.ToString()] = slot;
                flows.Add(new JsonObject
                {
                    ["identity"] = new JsonArray(r.Session, r.Local, r.Remote, r.Epoch),
                    ["players"] = players,
                    ["characters"] = characters,
                    ["playerChannels"] = playerChannels,
                    ["characterChannels"] = characterChannels,
                    ["slotClaims"] = slotClaims,
                });
            }
            return new JsonObject
            {
                ["schema"] = "gateway-identity-cache/v5",
                ["session"] = _session,
                ["restoreSupported"] = false,
                ["flows"] = flows,
            };
        }
    }

    /// <summary>/api/state 应答。</summary>
    public JsonObject? BuildApiState() => State();

    /// <summary>最终快照文本（snapshot-final.json）——Python close() 写入的是 API 状态，不是原始视图快照。</summary>
    public string FinalSnapshotJson()
    {
        lock (_gate)
        {
            var state = State(force: true);
            var final = state["self"] is not null || (state["entities"] is JsonArray a && a.Count > 0)
                ? state : _lastNonEmpty ?? state;
            return final.ToJsonString();
        }
    }

    private static double Round1(double v) => Math.Round(v, 1);

    /// <summary>
    /// Dictionary/list/原始值 → JsonNode（snapshot 投影的通用转换）。
    /// 必须按 Python 鸭子类型处理任意 IDictionary/IEnumerable——
    /// 快照里是 List&lt;double&gt;/List&lt;int&gt;/List&lt;Dictionary&lt;…&gt;&gt; 等具体泛型，
    /// 若只匹配 List&lt;object?&gt; 会被序列化成 "System.Collections…" 字符串
    /// （网页拿不到 entities/self，HTTP 冒烟实证）。
    /// </summary>
    internal static JsonNode? ToNode(object? value)
    {
        if (value is null) return null;
        if (value is string s) return JsonValue.Create(s);
        if (value is bool b) return JsonValue.Create(b);
        if (value is System.Collections.IDictionary dict)
        {
            var obj = new JsonObject();
            foreach (System.Collections.DictionaryEntry entry in dict)
            {
                obj[entry.Key?.ToString() ?? ""] = ToNode(entry.Value);
            }
            return obj;
        }
        if (value is System.Collections.IEnumerable seq)
        {
            var arr = new JsonArray();
            foreach (var item in seq) arr.Add(ToNode(item));
            return arr;
        }
        return value switch
        {
            int i => JsonValue.Create(i),
            long l => JsonValue.Create(l),
            double db => JsonValue.Create(db),
            float f => JsonValue.Create(f),
            ulong ul => JsonValue.Create(ul),
            uint ui => JsonValue.Create(ui),
            short sh => JsonValue.Create(sh),
            ushort us => JsonValue.Create(us),
            byte by => JsonValue.Create(by),
            sbyte sb => JsonValue.Create(sb),
            decimal de => JsonValue.Create(de),
            DateTime dt => JsonValue.Create(dt),
            _ => JsonValue.Create(value.ToString()),
        };
    }

    // ---- gateway-parser-status/v5（对应 Python gateway_parser 的 status 应答）----

    /// <summary>
    /// 构建 v5 状态 JSON（Dashboard / status-latest.json / parser-result.json 同源）。
    /// transport 段 = 最近 session_status 载荷（与 Python 回显行为一致）。
    /// </summary>
    public JsonObject BuildStatus(bool ended = false, double journalReadBytes = 0)
    {
        lock (_gate)
        {
            // rates：2s 滑窗内 rate_events 的 count/2 → 每秒速率（Python: status 的 rates）。
            var now = Now();
            while (_rateEvents.Count > 0 && _rateEvents.Peek().Ts < now - 2.0) _rateEvents.Dequeue();
            var rates = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (_, kind, count) in _rateEvents)
            {
                rates[kind] = (rates.TryGetValue(kind, out var v) ? v : 0) + count / 2.0;
            }
            var ratesNode = new JsonObject();
            foreach (var (k, v) in rates) ratesNode[k] = Math.Round(v, 1);

            // counts：逐 kind 计数 + 计算项（Python: status 的 counts）。
            var state = _lastState ?? EmptyState("waiting");
            var entities = state["entities"] as JsonArray ?? [];
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var e in entities)
            {
                var k = (e as JsonObject)?["kind"]?.GetValue<string>() ?? "unknown";
                counts[k] = (counts.TryGetValue(k, out var v) ? v : 0) + 1;
            }
            string KindOf(JsonNode? e) => (e as JsonObject)?["kind"]?.GetValue<string>() ?? "";
            bool IsBot(JsonNode? e) => (e as JsonObject)?["is_bot"]?.GetValue<bool>() == true;
            var human = entities.Count(e => KindOf(e) is "player" or "mate" && !IsBot(e));
            var bot = entities.Count(e => KindOf(e) == "ai" || IsBot(e));
            var characters = entities.Count(e => KindOf(e) is "player" or "mate" or "unknown");
            var confirmed = entities.Count(e => KindOf(e) is "player" or "mate" or "unknown" && IsBot(e));
            counts["human"] = human;
            counts["bot"] = bot;
            counts["characters"] = characters;
            counts["charactersIncludingSelf"] = characters + (state["self"] is not null ? 1 : 0);
            counts["confirmedBots"] = confirmed;
            counts["suspectedBots"] = confirmed;   // 兼容旧消费方的别名
            var countsNode = new JsonObject();
            foreach (var (k, v) in counts) countsNode[k] = v;

            // unknownLoot：kind=loot 且 item_id 缺失（Python 语义）。
            var missing = entities.Count(e =>
            {
                if (e is not JsonObject eo) return false;
                if (eo["kind"]?.GetValue<string>() != "loot") return false;
                var iid = eo["item_id"];
                if (iid is null) return true;
                if (iid is JsonValue jv)
                {
                    if (jv.TryGetValue<string>(out var s)) return s.Length == 0;
                    if (jv.TryGetValue<double>(out var dd)) return dd == 0;
                }
                return false;
            });

            // flows：与 Python f.summary() 同构的逐流摘要。
            var flows = new JsonArray();
            foreach (var (key, r) in _runs)
            {
                flows.Add(new JsonObject
                {
                    ["flow"] = key,
                    ["local"] = r.Local,
                    ["remote"] = r.Remote,
                    ["epoch"] = r.Epoch,
                    ["packets"] = r.Packets,
                    ["logicalBunches"] = r.View.Loader.NLb,
                    ["packetErrors"] = r.View.Loader.PacketErrors,
                    ["selfPoseSamples"] = r.View.Loader.PoseSamples,
                    ["playerRecords"] = r.View.Players.Count,
                    ["movementSlots"] = r.View.Slots.Count,
                    ["movementSamples"] = r.View.Slots.Values.Sum(t => t.Count),
                    ["mraChannel"] = r.View.MraCh is { } mc ? mc : null,
                    ["handshakeOk"] = r.View.Loader.Hs.Ok,
                    ["handshakeFull"] = r.View.Loader.Hs.Full,
                    ["welcome"] = r.View.Loader.Hs.Welcome,
                });
            }

            // stats：只输出真正发生过的键（Python Counter 语义）。
            var statsNode = new JsonObject();
            foreach (var (k, v) in _stats) statsNode[k] = v;

            var status = new JsonObject
            {
                ["schema"] = "gateway-parser-status/v5",
                ["stats"] = statsNode,
                ["counts"] = countsNode,
                ["unknownLoot"] = missing,
                ["rates"] = ratesNode,
                ["journalReadBytes"] = journalReadBytes,
                ["replay"] = null,
                ["flows"] = flows,
                ["states"] = _stateSequence,   // Python: state_sequence（计数器，非数组）
                ["ended"] = ended || _ended,
                ["error"] = _stoppedError,
            };
            if (_lastTransport is not null) status["transport"] = _lastTransport.DeepClone();
            return status;
        }
    }
}
