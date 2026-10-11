using System.Diagnostics;
using System.Text.Json;
using CCRadar.Parser.Engine;

namespace CCRadar.Parser.SelfTest;

/// <summary>
/// 状态构建开销基准：重放日志后反复调用 engine.State()，测量单次耗时。
/// 若单次耗时偏高，30Hz 的 Flush 就会持续吃 CPU/GC（VM 上足以拖慢整机网络）。
///   state-bench &lt;journal&gt; [iters]
/// </summary>
internal static class StateBench
{
    public static int Run(string journal, int iters = 300)
    {
        Console.WriteLine($"状态构建基准：{journal}\n");
        var engine = new LiveParserEngine();
        long n = 0;
        foreach (var line in File.ReadLines(journal))
        {
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                var data = root.TryGetProperty("data", out var d) ? d : default;
                long? seq = root.TryGetProperty("sequence", out var sq) && sq.ValueKind == JsonValueKind.Number
                    ? sq.GetInt64() : null;
                engine.ConsumeEvent(type, data, seq);
                n++;
            }
            catch (JsonException) { }
        }
        Console.WriteLine($"  重放事件 {n}");

        var view = engine.CurrentView;
        // 预热
        for (int i = 0; i < 10; i++) engine.State();

        var samples = new double[iters];
        var swAll = Stopwatch.StartNew();
        for (int i = 0; i < iters; i++)
        {
            var sw = Stopwatch.StartNew();
            engine.State();
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }
        swAll.Stop();
        Array.Sort(samples);
        double avg = samples.Average();
        double p50 = samples[iters / 2];
        double p95 = samples[(int)(iters * 0.95)];
        double max = samples[^1];
        Console.WriteLine($"  State() × {iters}：平均 {avg:F2}ms  P50 {p50:F2}ms  P95 {p95:F2}ms  最大 {max:F2}ms");
        Console.WriteLine($"  → 按 30Hz 调用估算占用：平均 {avg * 30:F0}ms/s（{avg * 30 / 10:F1}% 单核）");
        Console.WriteLine($"  总耗时 {swAll.Elapsed.TotalMilliseconds:F0}ms");
        return 0;
    }
}
