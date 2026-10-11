namespace CCRadar.Parser.Core;

/// <summary>
/// death_state.py 的 C# 等价实现 —— 来自已验证复制字段的序号感知角色生死状态。
///
/// GPAttributeSetHealth.IsAlive 与对应 PropertyReplication float 承载
/// ECharacterLiveStatus：1 alive / 2 dead / 3 impending death (downed)。
/// 它们不是布尔值。未知枚举值、actor 通道关闭、零血、倒计时、移动静默或
/// 未绑定的 FSM GUID 词都不能证明死亡（Python 原注释保留）。
///
/// 必须在**每个**逻辑 bunch 后喂 health drain()（或导入 broadcaster 的完整
/// resolved_history()）。大批量后一次性 drain 会在此跟踪器观察到之前丢弃
/// 中间的死亡/复活转换。
/// </summary>
public sealed class DeathStateTracker
{
    /// <summary>生死枚举（Python: LIFE_STATES）。</summary>
    public static readonly IReadOnlyDictionary<long, string> LifeStates =
        new Dictionary<long, string> { [1] = "alive", [2] = "dead", [3] = "downed" };

    private readonly bool _keepHistory;
    private readonly Dictionary<ulong, Dictionary<string, object?>> _events = new();
    private readonly Dictionary<ulong, Dictionary<string, object?>> _raw = new();
    private readonly Dictionary<ulong, Dictionary<string, object?>> _spawns = new();
    private readonly Dictionary<ulong, List<Dictionary<string, object?>>> _history = new();
    private readonly Dictionary<ulong, List<Dictionary<string, object?>>> _rawHistory = new();
    private readonly Dictionary<ulong, List<Dictionary<string, object?>>> _spawnHistory = new();

    /// <summary>计数器（Python: Counter）。</summary>
    public Dictionary<string, int> Stats { get; } = new();

    public DeathStateTracker(bool keepHistory = false)
    {
        _keepHistory = keepHistory;
    }

    private void Bump(string key) => Stats[key] = Stats.TryGetValue(key, out var c) ? c + 1 : 1;

    private static long Stamp(Dictionary<string, object?> e) => Convert.ToInt64(e["seq"]);

    /// <summary>同一序号冲突的优先级（Python: _priority）。</summary>
    private static (int Detailed, int Source, int StatusRank) Priority(Dictionary<string, object?> e)
    {
        // 详细 downed 枚举必须压过同一 bunch 的泛化 bDead=False。
        var field = e["field"]?.ToString();
        var detailed = field is "IsAlive" or "CurrentCharacterLiveStatus" ? 1 : 0;
        var sourceRank = e["source"]?.ToString() switch
        {
            "property_replication" => 3,
            "attribute_set_current" => 2,
            "attribute_set" => 2,
            "playerstate" => 1,
            _ => 0,
        };
        var status = Convert.ToInt64(e["status"]);
        var statusRank = status switch { 1 => 1, 3 => 2, 2 => 3, _ => 0 };
        return (detailed, sourceRank, statusRank);
    }

    private static bool DictEqual(Dictionary<string, object?> a, Dictionary<string, object?> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (k, v) in a)
        {
            if (!b.TryGetValue(k, out var v2)) return false;
            if (!Equals(v, v2)) return false;
        }
        return true;
    }

    /// <summary>建立新的 Character 世代；重复/过旧的 open 无操作（Python: spawn）。</summary>
    public bool Spawn(ulong actor, long seq)
    {
        if (_spawns.TryGetValue(actor, out var previous) && seq <= Convert.ToInt64(previous["seq"]))
        {
            return false;
        }
        var record = new Dictionary<string, object?>
        {
            ["seq"] = seq,
            ["generation"] = previous is not null ? Convert.ToInt64(previous["generation"]) + 1 : 1L,
        };
        _spawns[actor] = record;
        if (_keepHistory)
        {
            if (!_spawnHistory.TryGetValue(actor, out var rows))
            {
                rows = [];
                _spawnHistory[actor] = rows;
            }
            rows.Add(new Dictionary<string, object?>(record));
        }
        foreach (var mapping in new[] { _events, _raw })
        {
            if (mapping.TryGetValue(actor, out var ev) && Convert.ToInt64(ev["seq"]) < seq)
            {
                mapping.Remove(actor);
            }
        }
        Bump("spawns");
        return true;
    }

    /// <summary>
    /// 接受一个已验证的数值生死枚举；返回是否被接受（Python: observe）。
    /// 只接受数值枚举值；解码布尔字段用 observe_playerstate。
    /// 未知/非有限值永远不成为死亡或复活证据。
    /// </summary>
    public bool Observe(ulong actor, object? status, long seq,
        string source = "property_replication", string field = "IsAlive")
    {
        // bool 是 int 的子类但语义不同（Python: isinstance(status, bool) 拒绝）。
        if (status is bool || status is not (long or int or double))
        {
            Bump("invalid");
            return false;
        }
        double asDouble = status switch
        {
            long l => l,
            int i => i,
            double d => d,
            _ => double.NaN,
        };
        if (!double.IsFinite(asDouble))
        {
            Bump("invalid");
            return false;
        }
        if (_spawns.TryGetValue(actor, out var boundary) && seq < Convert.ToInt64(boundary["seq"]))
        {
            Bump("before_spawn");
            return false;
        }
        _events.TryGetValue(actor, out var previous);
        _raw.TryGetValue(actor, out var rawPrevious);
        var newestSeq = Math.Max(
            previous is not null ? Convert.ToInt64(previous["seq"]) : -1,
            rawPrevious is not null ? Convert.ToInt64(rawPrevious["seq"]) : -1);
        if (seq < newestSeq)
        {
            Bump("stale");
            return false;
        }
        var raw = new Dictionary<string, object?>
        {
            ["seq"] = seq, ["status"] = status,
            ["source"] = source, ["field"] = field,
        };
        if (rawPrevious is null || seq >= Convert.ToInt64(rawPrevious["seq"]))
        {
            _raw[actor] = raw;
            if (_keepHistory)
            {
                if (!_rawHistory.TryGetValue(actor, out var history))
                {
                    history = [];
                    _rawHistory[actor] = history;
                }
                if (history.Count > 0 && Convert.ToInt64(history[^1]["seq"]) == seq)
                {
                    history[^1] = new Dictionary<string, object?>(raw);
                }
                else
                {
                    history.Add(new Dictionary<string, object?>(raw));
                }
            }
        }
        var statusLong = Convert.ToInt64(Math.Round(asDouble));
        // 精确枚举判定：非整数或表外值都算 unknown（与 Python status not in LIFE_STATES 一致）。
        if (status is double && Math.Abs(asDouble - statusLong) > 0 || !LifeStates.ContainsKey(statusLong))
        {
            Bump("unknown");
            return false;
        }
        var ev = new Dictionary<string, object?>(raw) { ["status"] = statusLong };
        if (previous is not null && seq == Convert.ToInt64(previous["seq"]))
        {
            if (DictEqual(ev, previous))
            {
                Bump("duplicate");
                return false;
            }
            if (!Equals(ev["status"], previous["status"]))
            {
                Bump("same_seq_conflict");
            }
            if (Priority(ev).CompareTo(Priority(previous)) <= 0)
            {
                return false;
            }
        }
        _events[actor] = ev;
        if (_keepHistory)
        {
            if (!_history.TryGetValue(actor, out var history))
            {
                history = [];
                _history[actor] = history;
            }
            if (history.Count > 0 && Convert.ToInt64(history[^1]["seq"]) == seq)
            {
                history[^1] = new Dictionary<string, object?>(ev);
            }
            else
            {
                history.Add(new Dictionary<string, object?>(ev));
            }
        }
        Bump("accepted");
        return true;
    }

    /// <summary>消费每个 bunch 的 health drain 产生的 (actor, field, value, seq)（Python: observe_health_events）。</summary>
    public int ObserveHealthEvents(
        IEnumerable<(ulong Actor, string Field, object? Value, long Seq)> events,
        string source = "property_replication")
    {
        var accepted = 0;
        foreach (var (actor, field, value, seq) in events.OrderBy(e => e.Seq))
        {
            if (field == "IsAlive")
            {
                accepted += Observe(actor, value, seq, source: source, field: field) ? 1 : 0;
            }
        }
        return accepted;
    }

    /// <summary>按 wire 顺序导入完整 HealthBatchTracker.resolved_history()（Python: observe_health_history）。</summary>
    public int ObserveHealthHistory(
        IReadOnlyDictionary<ulong, Dictionary<string, List<(long Seq, object? Value)>>> history,
        string source = "property_replication")
    {
        var events = new List<(ulong, string, object?, long)>();
        foreach (var (actor, fields) in history)
        {
            if (fields.TryGetValue("IsAlive", out var points))
            {
                foreach (var (seq, value) in points)
                {
                    events.Add((actor, "IsAlive", value, seq));
                }
            }
        }
        return ObserveHealthEvents(events, source: source);
    }

    /// <summary>仅接受通过 schema 验证的 PS 生死字段（Python: observe_playerstate）。</summary>
    public int ObservePlayerstate(
        ulong actor, IReadOnlyDictionary<string, object?> values,
        IReadOnlyDictionary<string, long> fieldSeq, bool verified = false)
    {
        if (!verified)
        {
            Bump("unverified_playerstate");
            return 0;
        }
        var events = new List<(long Seq, string Field, object? Value)>();
        foreach (var field in new[] { "CurrentCharacterLiveStatus", "bDead", "bIsDeadBox" })
        {
            if (!values.TryGetValue(field, out var value) || !fieldSeq.TryGetValue(field, out var seq)) continue;
            if (field == "bDead")
            {
                if (value is not bool b) continue;
                value = b ? 2L : 1L;
            }
            else if (field == "bIsDeadBox")
            {
                if (value is not true) continue;
                value = 2L;
            }
            events.Add((seq, field, value));
        }
        var accepted = 0;
        foreach (var (seq, field, value) in events.OrderBy(e => e.Seq))
        {
            accepted += Observe(actor, value, seq, source: "playerstate", field: field) ? 1 : 0;
        }
        return accepted;
    }

    /// <summary>结果组装（Python: _result）。</summary>
    private static Dictionary<string, object?> Result(
        Dictionary<string, object?>? ev, Dictionary<string, object?>? raw,
        Dictionary<string, object?>? spawn)
    {
        if (spawn is not null)
        {
            if (ev is not null && Convert.ToInt64(ev["seq"]) < Convert.ToInt64(spawn["seq"])) ev = null;
            if (raw is not null && Convert.ToInt64(raw["seq"]) < Convert.ToInt64(spawn["seq"])) raw = null;
        }
        var status = ev is not null ? Convert.ToInt64(ev["status"]) : (long?)null;
        return new Dictionary<string, object?>
        {
            ["state"] = status is not null && LifeStates.TryGetValue(status.Value, out var s) ? s : "unknown",
            ["status"] = status,
            ["seq"] = ev?["seq"],
            ["source"] = ev?["source"],
            ["field"] = ev?["field"],
            ["spawn_seq"] = spawn?["seq"],
            ["generation"] = spawn is not null ? spawn["generation"] : 0L,
            ["confirmed"] = ev is not null,
            ["dead"] = status == 2,
            ["downed"] = status == 3,
            ["raw_status"] = raw?["status"],
            ["raw_seq"] = raw?["seq"],
            ["raw_source"] = raw?["source"],
            ["raw_field"] = raw?["field"],
        };
    }

    /// <summary>当前语义状态、来源与最新原始观察（Python: current）。</summary>
    public Dictionary<string, object?> Current(ulong actor)
        => Result(
            _events.TryGetValue(actor, out var e) ? e : null,
            _raw.TryGetValue(actor, out var r) ? r : null,
            _spawns.TryGetValue(actor, out var s) ? s : null);

    /// <summary>历史中 seq 之前最近的事件（Python: _before，bisect_right 语义）。</summary>
    private static Dictionary<string, object?>? Before(
        IReadOnlyList<Dictionary<string, object?>> history, long seq)
    {
        // bisect_right(history, seq, key=stamp) - 1：最后一个 stamp <= seq。
        var lo = 0;
        var hi = history.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (Stamp(history[mid]) <= seq) lo = mid + 1;
            else hi = mid;
        }
        return lo - 1 >= 0 ? history[lo - 1] : null;
    }

    /// <summary>回放游标处的状态，不借用未来信息（Python: at）。</summary>
    public Dictionary<string, object?> At(ulong actor, long seq)
    {
        if (!_keepHistory)
        {
            throw new ArgumentException("historical life queries require keep_history=True");
        }
        var spawns = _spawnHistory.TryGetValue(actor, out var sp) ? sp : new List<Dictionary<string, object?>>();
        if (spawns.Count > 0 && seq < Convert.ToInt64(spawns[0]["seq"]))
        {
            return Result(null, null, null);
        }
        return Result(
            Before(_history.TryGetValue(actor, out var h) ? h : new List<Dictionary<string, object?>>(), seq),
            Before(_rawHistory.TryGetValue(actor, out var rh) ? rh : new List<Dictionary<string, object?>>(), seq),
            Before(spawns, seq));
    }

    /// <summary>当前死亡证据的序号；其他状态返回 null（Python: death_seq）。</summary>
    public long? DeathSeq(ulong actor, long? seq = null)
    {
        var state = seq is null ? Current(actor) : At(actor, seq.Value);
        return state["dead"] is true && state["seq"] is long s ? s : null;
    }
}
