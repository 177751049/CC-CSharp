using System.Text.Json;
using CCRadar.Parser.Core;

namespace CCRadar.Parser.SelfTest;

/// <summary>
/// Python 并排对拍探针：读取 tools/probe_paths.py 转储的目标 bunch
/// （hex 载荷 + Python 侧 exports），用 C# 的 Sna.ParsePrefix /
/// ReplicationHeader.ExportsPrefix 重新解析并逐条对比。
/// 用法: SelfTest probe-bunches &lt;probe_out.json&gt;
/// </summary>
public static class BunchProbe
{
    private static string Gr(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetUInt64().ToString() : "null";

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : "null";

    public static int Run(string path)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var doc = JsonDocument.Parse(File.ReadAllText(path));
        // acc 目标 bunch（bunches）+ FSM 扫描/目标 seq 现场(extraBunches)。
        var all = new List<JsonElement>();
        if (doc.RootElement.TryGetProperty("bunches", out var bArr))
        {
            all.AddRange(bArr.EnumerateArray());
        }
        if (doc.RootElement.TryGetProperty("extraBunches", out var eArr))
        {
            all.AddRange(eArr.EnumerateArray());
        }
        var failures = 0;
        foreach (var b in all)
        {
            var seq = b.GetProperty("seq").GetInt64();
            var ch = b.GetProperty("ch").GetInt32();
            var pbits = b.GetProperty("pbits").GetInt32();
            var hx = b.GetProperty("hx").GetInt32() != 0;
            var hm = b.GetProperty("hm").GetInt32() != 0;
            var payload = Convert.FromHexString(b.GetProperty("hex").GetString()!);
            var frags = new List<(int Start, bool Hx, bool Hm)>();
            foreach (var f in b.GetProperty("frags").EnumerateArray())
            {
                frags.Add((f[0].GetInt32(), f[1].GetInt32() != 0, f[2].GetInt32() != 0));
            }
            Console.WriteLine($"--- seq={seq} ch={ch} pbits={pbits} hx={hx} hm={hm} frags={frags.Count}");

            // 目标 seq 模式：对比 ParsePrefix 的 ss 与 Tile 块列表。
            if (b.TryGetProperty("parse_ss", out var pySsEl))
            {
                var pySs = pySsEl.GetInt32();
                int csSs = 0;
                List<ContentBlock>? csBlocks = null;
                try
                {
                    var (info, ss) = Sna.ParsePrefix(payload, pbits, hx, hm, quantizeBit: false, frags: frags);
                    csSs = ss;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  C# ParsePrefix 异常 {ex.GetType().Name}: {ex.Message}");
                }
                try
                {
                    csBlocks = Blocks.Tile(payload, pbits, csSs, partialOk: true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  C# Tile 异常 {ex.GetType().Name}: {ex.Message}");
                }
                Console.WriteLine($"  PY ss={pySs} blocks={(b.TryGetProperty("blocks", out var pb) ? pb.GetArrayLength() : -1)}" +
                    $" | C# ss={csSs} blocks={csBlocks?.Count ?? -1}");
                // 扫描器分歧定位：对 PY scan>0 而 C# scan=0 的块，复刻锚点扫描并打印。
                if (b.TryGetProperty("blocks", out var pyBlocks2))
                {
                    foreach (var e in pyBlocks2.EnumerateArray())
                    {
                        var pyScan = e.TryGetProperty("scan", out var sc) ? sc.GetInt32() : -2;
                        var bps = e.GetProperty("ps").GetInt32();
                        var bpe = e.GetProperty("pe").GetInt32();
                        if (pyScan <= 0 || bpe - bps < 250) continue;
                        var csScan = LiveView.ScanEquipArraysPublic(payload, bps, bpe).Count;
                        if (csScan == pyScan) continue;
                        Console.WriteLine($"  扫描分歧 blk[{bps}-{bpe}] PY={pyScan} C#={csScan} —— 锚点 dump:");
                        var l2 = bpe - bps;
                        long Rd2(int pos, int n)
                        {
                            long v = 0;
                            for (var j = 0; j < n; j++)
                                v |= (long)CCRadar.Parser.Codec.BitPrimitives.GetBit(payload, bps + pos + j) << j;
                            return v;
                        }
                        var printed = 0;
                        for (var c = 0; c + 64 <= l2 && printed < 8; c++)
                        {
                            var v = Rd2(c, 64);
                            if (v is >= 10000000000L and < 100000000000L)
                            {
                                var head = v.ToString()[..4];
                                if (head is "1101" or "1105" or "1107" or "1108" or "1109")
                                {
                                    Console.WriteLine($"    anchor c={c} v={v}");
                                    printed++;
                                }
                            }
                        }
                    }
                }
                if (b.TryGetProperty("blocks", out var pyBlocks) && csBlocks is not null)
                {
                    var pyList = pyBlocks.EnumerateArray().ToList();
                    var n = Math.Max(pyList.Count, csBlocks.Count);
                    for (var i = 0; i < n; i++)
                    {
                        string py = "—", cs = "—";
                        if (i < pyList.Count)
                        {
                            var e = pyList[i];
                            var scan = e.TryGetProperty("scan", out var sc) ? sc.GetInt32() : -2;
                            py = $"[{e.GetProperty("ps").GetInt32()}-{e.GetProperty("pe").GetInt32()}]" +
                                 $"act{e.GetProperty("is_actor").GetInt32()}rep{e.GetProperty("has_rep").GetInt32()}" +
                                 $"sub={Gr(e, "sub")}scan={scan}";
                        }
                        if (i < csBlocks.Count)
                        {
                            var k = csBlocks[i];
                            var csScan = k.PayloadEnd - k.PayloadStart >= 250
                                ? LiveView.ScanEquipArraysPublic(payload, k.PayloadStart, k.PayloadEnd).Count : -1;
                            cs = $"[{k.PayloadStart}-{k.PayloadEnd}]act{k.IsActor}rep{k.HasRep}" +
                                 $"sub={k.Sub}scan={csScan}";
                        }
                        var mark = py == cs ? " " : "!";
                        Console.WriteLine($"   {mark} [{i}] PY={py} | C#={cs}");
                    }
                }
                continue;
            }

            // Python 侧 exports（转储原样）。
            var pyExports = new List<string>();
            foreach (var e in b.GetProperty("exports").EnumerateArray())
            {
                var p = e.TryGetProperty("path", out var pe) && pe.ValueKind == JsonValueKind.String
                    ? pe.GetString() : null;
                var g = e.TryGetProperty("guid", out var ge) && ge.ValueKind == JsonValueKind.Number
                    ? ge.GetUInt64().ToString() : "?";
                pyExports.Add($"guid{g}={p ?? "（null）"}");
            }

            // C# ParsePrefix（quantizeBit=false，与 _accumulate_paths 一致）。
            List<string> csExports = new();
            try
            {
                var (info, _) = Sna.ParsePrefix(payload, pbits, hx, hm, quantizeBit: false, frags: frags);
                if (info.TryGetValue("exports", out var eo) && eo is List<Dictionary<string, object?>> el)
                {
                    foreach (var e in el)
                    {
                        var p = e.TryGetValue("path", out var pp) ? pp?.ToString() : null;
                        var g = e.TryGetValue("guid", out var gg) ? gg : null;
                        csExports.Add($"guid{g}={p ?? "（null）"}");
                    }
                }
                Console.WriteLine($"  C# ParsePrefix: {csExports.Count} 条");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  C# ParsePrefix: 异常 {ex.GetType().Name}: {ex.Message}");
            }

            // C# ExportsPrefix（与 _handle_player_lb 一致）。
            List<string> epExports = new();
            try
            {
                var (exports, pos) = ReplicationHeader.ExportsPrefix(payload, pbits, hx, hm, frags);
                foreach (var e in exports)
                {
                    var p = e.TryGetValue("path", out var pp) ? pp?.ToString() : null;
                    var g = e.TryGetValue("guid", out var gg) ? gg : null;
                    epExports.Add($"guid{g}={p ?? "（null）"}");
                }
                Console.WriteLine($"  C# ExportsPrefix: {epExports.Count} 条, pos={pos}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  C# ExportsPrefix: 异常 {ex.GetType().Name}: {ex.Message}");
            }

            // Python 打印（对比基准）。
            Console.WriteLine($"  PY exports: {pyExports.Count} 条");
            var nMax = Math.Max(pyExports.Count, Math.Max(csExports.Count, epExports.Count));
            for (var i = 0; i < nMax; i++)
            {
                var py = i < pyExports.Count ? pyExports[i] : "—";
                var cs = i < csExports.Count ? csExports[i] : "—";
                var ep = i < epExports.Count ? epExports[i] : "—";
                var mark = (py == cs && py == ep) ? " " : "!";
                if (mark == "!" || i < 8) Console.WriteLine($"   {mark} [{i}] PY={py} | PP={cs} | EP={ep}");
            }
            if (pyExports.Count != csExports.Count || pyExports.Count != epExports.Count
                || !pyExports.SequenceEqual(csExports) || !pyExports.SequenceEqual(epExports)) failures++;
        }
        Console.WriteLine($"=== 对比完成：{failures} 个 bunch 不一致 ===");
        return failures;
    }
}
