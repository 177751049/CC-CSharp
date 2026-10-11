using System.Text.Json;
using CCRadar.Parser.Core;
using CCRadar.Parser.Engine;

namespace CCRadar.Parser.SelfTest;

/// <summary>
/// 端到端黄金对拍：重放实测会话归档（realtime.jsonl 的 udp_plaintext 事件）
/// 喂给纯 C# 引擎，对照 Python 解析器同一会话的 snapshot-final.json。
/// 数据源：C:\Users\Administrator\Desktop\软路由\captures\live-20261010-153810-*/
/// （可用环境变量 CCRADAR_GOLDEN_DIR 覆盖；目录不存在则跳过不计数）。
/// </summary>
public static class GoldenReplay
{
    public static int Passed { get; private set; }
    public static int Failures { get; private set; }

    private static void Check(string name, bool condition, string? detail = null)
    {
        if (condition)
        {
            Passed++;
            Console.WriteLine($"  [通过] {name}");
        }
        else
        {
            Failures++;
            Console.WriteLine($"  [失败] {name}{(detail is null ? "" : $"  ← {detail}")}");
        }
    }

    public static void RunAll()
    {
        var dir = Environment.GetEnvironmentVariable("CCRADAR_GOLDEN_DIR");
        if (string.IsNullOrWhiteSpace(dir))
        {
            dir = @"C:\Users\Administrator\Desktop\软路由\captures\live-20261010-153810-d30e3c2889d44561a733b0c73446178c";
        }
        var journal = Path.Combine(dir, "realtime.jsonl");
        var goldenFinal = Path.Combine(dir, "semantic", "snapshot-final.json");
        if (!File.Exists(journal) || !File.Exists(goldenFinal))
        {
            Console.WriteLine("[G] 黄金对拍：数据源不存在，跳过");
            Console.WriteLine($"    （需要 {journal}）");
            return;
        }

        Console.WriteLine("[G] 端到端黄金对拍（实测会话重放 vs Python snapshot-final.json）");
        var engine = new LiveParserEngine();
        var plaintext = 0;
        long eventCount = 0;

        foreach (var line in File.ReadLines(journal))
        {
            if (line.Length == 0) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl)) continue;
                var type = typeEl.GetString() ?? "";
                var data = root.TryGetProperty("data", out var d) ? d.Clone() : default;
                var seq = root.TryGetProperty("sequence", out var sq) && sq.ValueKind == JsonValueKind.Number ? sq.GetInt64() : (long?)null;
                if (type == "udp_plaintext") plaintext++;
                engine.ConsumeEvent(type, data, seq);
            }
            eventCount++;
        }

        // Python er_radar 在服务 /api/state 前轮询视图；重放结束后统一 poll 排空。
        var view = engine.CurrentView;
        if (view is null)
        {
            Check("引擎产生活跃视图", false, "CurrentView 为 null");
            return;
        }
        view.Poll();
        // 内部管线诊断：定位断点（帧解码 → bunch 产出 → 控制收敛 → 视图提取）。
        Console.WriteLine($"    诊断：NFrames={view.Loader.NFrames} CtrlDone={view.Loader.CtrlDone} " +
            $"PacketErrors={view.Loader.PacketErrors} NReqDecoded={view.Loader.NReqDecoded} " +
            $"channels={view.Loader.B.Count} lbs={view.Loader.B.Values.Sum(l => l.Count)}");
        Console.WriteLine($"          bpNull={view.Loader.NBunchParseNull} bunchesSeen={view.Loader.NBunchesSeen} " +
            $"feedEmitted={view.Loader.NFeedEmitted} reasmErrors={view.Loader.NReasmErrors} dupFeed={view.Loader.DupFeedCount} markerScanned={view.NMarkerScanned} markerHits={view.NMarkerHits} markerParsed={view.NMarkerParsed}");
        Console.WriteLine($"          openLbs={view.NOpenLbs} prefixCalls={view.NPrefixCalls} prefixErrors={view.NPrefixErrors} " +
            $"exports={view.NExportsFound} guidPaths={view.NGuidPathsSet} archs={view.CharArchs.Count} " +
            $"players={view.Players.Count} boxes={view.Boxes.Count} pkArchs={view.PkArchs.Count}");
        // 路径内容诊断：尾部分布 + 样本（与 Python 导出语义对照）。
        Console.WriteLine($"    PS 认领: tried={view.NPlayerOpenTried} noArchPath={view.NPlayerNoArchPath} profileEmpty={view.NPlayerProfileEmpty} ok={view.NPlayerProfileOk}");
        Console.WriteLine($"    Derive 输入: mraLocked={view.MraLocked} mraCh={view.MraCh} slots={view.Slots.Count} " +
            $"slotSamples={view.Slots.Values.Sum(t2 => t2.Count)} claim={view.Claim.Count} fsm={view.FsmState.Count} " +
            $"health={view.Health.Count} equip={view.Equip.Count} f13={view.F13Tracks.Count} psState={view.PlayerState.Count} links={view.Links.Count}");
        Console.WriteLine($"    Ent 过滤: skipMove={view.NEntSkipMove} skipGone={view.NEntSkipGone} skipDisp={view.NEntSkipDisp} added={view.NEntAdded}");
        Console.WriteLine($"    tracker 产出: pickupActive={view.PickupTrackerInst.Active.Count} containerActive={view.ContainerTrackerInst.Active.Count} boxActive={view.BoxTracker.Active.Count} " +
            $"aiNames={view.AiNames.Count} fsm={view.FsmState.Count} health={view.Health.Count} gone={view.Gone.Count}");
        Console.WriteLine("    HealthDbg: " + string.Join(" ", view.HealthDbg.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")));
        Console.WriteLine("    equipKeys: " + string.Join(",", view.Equip.Keys.OrderBy(k => k)));
        Console.WriteLine("    fsmChActor: " + string.Join(",", view.FsmChActorPairs().Select(kv => $"{kv.ch}->{kv.actor}").OrderBy(s => s)));
        foreach (var g in new ulong[] { 978, 1348, 1576, 1690, 1806, 2118, 2460, 38, 632 })
            Console.WriteLine($"    guid{g} → {view.GuidPathOf(g) ?? "（缺失）"}");
        foreach (var (a, er) in view.Equip.OrderBy(kv => kv.Key))
        {
            var seqs = er.TryGetValue("items", out var it2) && it2 is Dictionary<ulong, Dictionary<string, object?>> itd2
                ? string.Join(",", itd2.Values.Select(f => f.TryGetValue("seq", out var sq) ? sq : null)) : "";
            Console.WriteLine($"    equip[{a}]: armor={(er.TryGetValue("armor", out var ar) && ar is Dictionary<ulong, Dictionary<string, object?>> ard ? ard.Count : 0)} " +
                $"items={(er.TryGetValue("items", out var it) && it is Dictionary<ulong, Dictionary<string, object?>> itd ? itd.Count : 0)} seqs={seqs}");
        }
        Console.WriteLine("    Gone 明细: " + string.Join(" | ", view.Gone.Select(kv =>
        {
            var lk = view.Links.TryGetValue(kv.Key, out var l) ? l : null;
            var cls = lk?.TryGetValue("cls", out var c) == true ? c?.ToString() : "?";
            var kind = view.Ents.FirstOrDefault(e2 => e2.TryGetValue("actor", out var a2) && Equals(a2, (object?)kv.Key))?.TryGetValue("kind", out var k2) == true ? k2?.ToString() : "?";
            return $"actor{kv.Key}(seq{kv.Value.Seq},reason{kv.Value.Reason},{kind},{Trim(cls ?? "", 30)})";
        })));
        Console.WriteLine("    Ents kind 分布: " + string.Join(" ", view.Ents.Select(e2 => e2.TryGetValue("kind", out var k3) ? k3?.ToString() : "?").GroupBy(k3 => k3 ?? "?").Select(g3 => $"{g3.Key}×{g3.Count()}")));
        Console.WriteLine("    路径尾部分布: " + string.Join(" ", view.GuidPathTails(12)));
        Console.WriteLine("    archs: Char={0} Ps={1} Box={2} Carry={3} Pk={4}", view.CharArchs.Count, view.PsArchs.Count, view.BoxArchs.Count, view.CarryArchs.Count, view.PkArchs.Count);
        Console.WriteLine("    关键类路径: " + view.GuidPathClassProbe());
        foreach (var r in engine.RunsDebug()) Console.WriteLine("    run: " + r);
        // Python identity-cache 里的关键 guid（flow1 角色/玩家）在我的路径表里是什么？
        foreach (var g in new ulong[] { 38, 632, 746, 862, 978, 4, 520, 636, 2 })
            Console.WriteLine($"    guid{g} → {Trim(view.GuidPathOf(g) ?? "（缺失）", 80)}");
        Console.WriteLine($"    guidPaths 总数: {view.GuidPathCount}");
        // 黄金 guid_paths 对比（Python 全量重放产物）。
        var goldenGpFile = Path.Combine(Path.GetTempPath(), "ccradar_py_replay", "guid_paths_golden.json");
        if (File.Exists(goldenGpFile))
        {
            var gpDoc = JsonDocument.Parse(File.ReadAllText(goldenGpFile)).RootElement;
            // 选与当前视图包数最接近的 flow（battle 流）。
            string? bestKey = null;
            long bestDiff = long.MaxValue;
            foreach (var f in gpDoc.EnumerateObject())
            {
                var pk = f.Value.GetProperty("packets").GetInt64();
                var diff = Math.Abs(pk - 24650);
                if (diff < bestDiff) { bestDiff = diff; bestKey = f.Name; }
            }
            if (bestKey is not null)
            {
                var gpObj = gpDoc.GetProperty(bestKey).GetProperty("guidPaths");
                var mine = view.GuidPathsSnapshot();
                int missing = 0, mismatched = 0, matched = 0;
                var missingSamples = new List<string>();
                foreach (var kv in gpObj.EnumerateObject())
                {
                    var g = ulong.Parse(kv.Name);
                    var pyPath = kv.Value.GetString() ?? "";
                    if (!mine.TryGetValue(g, out var myPath))
                    {
                        missing++;
                        if (missingSamples.Count < 8)
                        {
                            var seen = view.PathFirstSeen.TryGetValue(pyPath, out var fs);
                            missingSamples.Add($"guid{g} [{Trim(pyPath, 50)}] " + (seen ? $"←路径见过但配给了 guid{fs.Guid}(seq{fs.Seq})" : "←路径从未解析到"));
                        }
                    }
                    else if (myPath != pyPath) { mismatched++; if (mismatched <= 3) Console.WriteLine($"    路径不一致 guid{g}: PY={Trim(pyPath, 45)} C#={Trim(myPath, 45)}"); }
                    else matched++;
                }
                Check($"battle 流 guidPaths 覆盖 = Python {gpObj.EnumerateObject().Count()}（匹配 {matched}/缺失 {missing}/不一致 {mismatched}）",
                    missing == 0 && mismatched == 0);
                foreach (var s in missingSamples) Console.WriteLine("    " + s);
            }
        }
        // 转储真实 open bunch 供 Python 并排对拍（engine 在消费期缓存，见 engine.DumpBuffer）。
        var dumpDir = Path.Combine(Path.GetTempPath(), "ccradar_golden_dump");
        Directory.CreateDirectory(dumpDir);
        var dumped = engine.DumpBufferedOpenLbs(6);
        File.WriteAllLines(Path.Combine(dumpDir, "open_lbs.txt"), dumped);
        File.WriteAllLines(Path.Combine(dumpDir, "mispair_dump.jsonl"), view.MispairDump);
        Console.WriteLine($"    错位现场转储: {view.MispairDump.Count} 条 → mispair_dump.jsonl");
        Console.WriteLine($"    已转储 {dumped.Count} 个 Hx=1 open bunch → {dumpDir}");
        // C# 侧并排解析：同一批载荷的 export 数量与样本（与 Python 对拍脚本对照）。
        foreach (var line in dumped)
        {
            var seg = line.Split('|');
            var pbits = int.Parse(seg[5].Split('=')[1]);
            var hx = seg[3].Split('=')[1] != "0";
            var hm = seg[4].Split('=')[1] != "0";
            var payload = Convert.FromHexString(seg[6]);
            try
            {
                var (info, pos) = Sna.ParsePrefix(payload, pbits, hx, hm);
                var exports = info.TryGetValue("exports", out var eo) && eo is List<Dictionary<string, object?>> el ? el : new List<Dictionary<string, object?>>();
                Console.WriteLine($"    [C#] {seg[0]} ch{seg[1]} seq{seg[2]} pbits={pbits}: exports={exports.Count}, 解析至 {pos}");
                foreach (var e in exports.Take(3))
                    Console.WriteLine($"       {StrTrim(e.TryGetValue("path", out var pp) ? pp?.ToString() : null, 70)} guid={(e.TryGetValue("guid", out var gg) ? gg : (object?)null)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    [C#] {seg[0]} ch{seg[1]} seq{seg[2]} pbits={pbits}: 异常 {ex.GetType().Name}: {Trim(ex.Message, 80)}");
            }
        }
        Console.WriteLine("    路径样本: " + string.Join(" | ", view.GuidPathSamples(6).Select(s => s.Length > 90 ? s[..90] : s)));
        Console.WriteLine($"    直接状态: Ents.Count={view.Ents.Count} StateVersion={view.StateVersion}");
        var snap = view.Snapshot();
        foreach (var e2 in view.Ents.Where(x => x.TryGetValue("kind", out var kk) && kk?.ToString() == "ai").Take(4))
            Console.WriteLine($"    AI ent: name={(e2.TryGetValue("name", out var nn) ? nn : (object?)null)} " +
                $"spawn_loc={(e2.TryGetValue("spawn_loc", out var sl2) ? sl2?.ToString()?.Length.ToString() : "null")} " +
                $"spawn_seq={(e2.TryGetValue("spawn_seq", out var ss2) ? ss2 : (object?)null)} slot={(e2.TryGetValue("slot", out var ss3) ? ss3 : (object?)null)}");
        Console.WriteLine("    keep 拒绝: " + (view.SnapKeepDbg ?? "null"));
        Console.WriteLine("    快照丢弃: " + (view.SnapDropStats is not null ? string.Join(" ", view.SnapDropStats.Select(kv => $"{kv.Key}={kv.Value}")) : "null"));
        Console.WriteLine($"    snap: status={(snap.TryGetValue("status", out var st1) ? st1 : (object?)null)} live_active={(snap.TryGetValue("live_active", out var la1) ? la1 : (object?)null)} entities={(snap.TryGetValue("entities", out var ee1) && ee1 is List<object?> el1 ? el1.Count : -1)}");

        // ---- 对照 Python 黄金参照 ----
        var golden = JsonDocument.Parse(File.ReadAllText(goldenFinal)).RootElement;

        var gEntities = golden.GetProperty("entities").GetArrayLength();
        var cEntities = CountOf(snap, "entities");
        Check($"实体数 = Python {gEntities}（C# {cEntities}）", cEntities == gEntities);

        var gNames = golden.GetProperty("names").GetArrayLength();
        var cNames = CountOf(snap, "names");
        Check($"名字数 = Python {gNames}（C# {cNames}）", cNames == gNames);

        // self 坐标（确定性：来自上行位姿/槽位轨迹，允许 0.5 误差）。
        var gSelf = golden.GetProperty("self");
        if (gSelf.ValueKind == JsonValueKind.Array && gSelf.GetArrayLength() == 3)
        {
            var cSelf = snap.TryGetValue("self", out var s) && s is System.Collections.IEnumerable se
                ? se.Cast<object?>().Select(Convert.ToDouble).ToList() : null;
            if (cSelf is null)
            {
                Check("self 坐标存在", false, "C# self 为 null");
            }
            else
            {
                var dx = Math.Abs(cSelf[0] - gSelf[0].GetDouble());
                var dy = Math.Abs(cSelf[1] - gSelf[1].GetDouble());
                var dz = Math.Abs(cSelf[2] - gSelf[2].GetDouble());
                Check($"self 坐标 ±0.5（C# [{cSelf[0]:F1},{cSelf[1]:F1},{cSelf[2]:F1}]）",
                    dx < 0.5 && dy < 0.5 && dz < 0.5,
                    $"差值 dx={dx:F2} dy={dy:F2} dz={dz:F2}");
            }
        }

        // gone 数（角色消失表，确定性）。
        var gGone = golden.TryGetProperty("gone", out var ggone) && ggone.ValueKind == JsonValueKind.Object
            ? ggone.EnumerateObject().Count() : 0;
        var cGone = snap.TryGetValue("gone", out var cgone) && cgone is Dictionary<string, object?> gd ? gd.Count : 0;
        Check($"gone 数 = Python {gGone}（C# {cGone}）", cGone == gGone);

        Console.WriteLine($"    重放事件 {eventCount} 条（udp_plaintext {plaintext}）→ 引擎 {engine.PlaintextPackets} 包");
        Console.WriteLine($"    cursor：C# {DictGet(snap, "cursor")} vs Python {Cursor(golden)}");
        var status = engine.BuildStatus(ended: true);
        Console.WriteLine($"    v5 状态：counts={status["counts"]?.ToJsonString()}");

        // 实体类别分布对照（差异诊断信息）。
        var cKinds = snap.TryGetValue("entities", out var ce) && ce is System.Collections.IEnumerable cee
            ? cee.Cast<object?>().Select(e => (e as Dictionary<string, object?>)?.TryGetValue("kind", out var k) == true ? k?.ToString() : "?")
                .GroupBy(k => k ?? "?").OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key}×{g.Count()}") : Array.Empty<string>();
        var gKinds = golden.GetProperty("entities").EnumerateArray()
            .Select(e => e.TryGetProperty("kind", out var k) ? k.GetString() ?? "?" : "?")
            .GroupBy(k => k).OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key}×{g.Count()}");
        Console.WriteLine($"    实体分布 C#：{string.Join(" ", cKinds)}");
        Console.WriteLine($"    实体分布 PY：{string.Join(" ", gKinds)}");
    }

    /// <summary>集合字段计数（解码层产出的列表元素类型动态，用非泛型枚举）。</summary>
    private static int CountOf(Dictionary<string, object?> dict, string key)
        => dict.TryGetValue(key, out var v) && v is System.Collections.IEnumerable e && v is not string
            ? e.Cast<object?>().Count() : 0;

    private static object? DictGet(Dictionary<string, object?> dict, string key)
        => dict.TryGetValue(key, out var v) ? v : null;

    private static string StrTrim(string? s, int n) => s is null ? "无" : (s.Length > n ? s[..n] : s);
    private static string Trim(string s, int n) => s.Length > n ? s[..n] : s;

    private static long Cursor(JsonElement golden)
        => golden.TryGetProperty("cursor", out var gc) && gc.ValueKind == JsonValueKind.Number ? gc.GetInt64() : -1;
}
