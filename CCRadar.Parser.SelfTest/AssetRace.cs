using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using CCRadar.Parser.Engine;

namespace CCRadar.Parser.SelfTest;

/// <summary>
/// 资源服务探针。
///   asset-race &lt;pak&gt;                            —— AssetStore 并发读取一致性。
///   tile-http  &lt;pak&gt; &lt;webRoot&gt; &lt;modelsRoot&gt;     —— HTTP 层全量瓦片校验（每图每片与 pak 逐字节比对）。
/// </summary>
internal static class AssetRace
{
    public static int Run(string pak)
    {
        Console.WriteLine($"AssetStore 并发读取探针：{pak}\n");

        var keys = new List<string>();
        using (var z = ZipFile.OpenRead(pak))
        {
            foreach (var e in z.Entries)
            {
                if (e.Name.Length == 0) continue;
                if (e.FullName.StartsWith("maps2d/", StringComparison.Ordinal)) keys.Add(e.FullName);
            }
        }
        Console.WriteLine($"  maps2d 瓦片数：{keys.Count}");

        using var store = new AssetStore(archive: pak);

        // 串行参考（单线程读取 = 基准真值）。
        var reference = new Dictionary<string, byte[]>(keys.Count, StringComparer.Ordinal);
        foreach (var k in keys) reference[k] = store.ReadBytes(k);

        int badStructure = 0;
        foreach (var kv in reference)
            if (!IsJpeg(kv.Value)) badStructure++;
        Console.WriteLine($"  参考读取：{reference.Count} 张，JPEG 结构异常 {badStructure}");

        long total = 0, bad = 0;
        var samples = new ConcurrentBag<string>();
        Parallel.ForEach(keys, new ParallelOptions { MaxDegreeOfParallelism = 16 }, k =>
        {
            var got = store.ReadBytes(k);
            var want = reference[k];
            Interlocked.Increment(ref total);
            bool same = got.Length == want.Length && got.AsSpan().SequenceEqual(want);
            if (!same)
            {
                Interlocked.Increment(ref bad);
                if (samples.Count < 6) samples.Add($"{k}: 长度 {got.Length} vs {want.Length}, 头 {Hex(got)}");
            }
        });

        Console.WriteLine($"  并发读取：{total} 次，字节不一致 {bad}");
        foreach (var s in samples) Console.WriteLine("    " + s);

        bool ok = bad == 0 && badStructure == 0;
        Console.WriteLine(ok ? "\n  [通过] AssetStore 并发读取一致" : "\n  [失败] AssetStore 并发读取出现损坏");
        return ok ? 0 : 1;
    }

    /// <summary>HTTP 层：起真实服务，逐张拉取每个地图的每张瓦片并与 pak 逐字节比对；并检查 /api/map 的 tileUrl 已本地化。</summary>
    public static int RunHttp(string pak, string webRoot, string modelsRoot)
    {
        Console.WriteLine("=== 资源服务 · HTTP 全量瓦片校验 ===\n");

        var truth = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var byMap = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        using (var store = new AssetStore(archive: pak))
        using (var z = ZipFile.OpenRead(pak))
        {
            foreach (var e in z.Entries)
            {
                if (e.Name.Length == 0 || !e.FullName.StartsWith("maps2d/", StringComparison.Ordinal)) continue;
                var parts = e.FullName.Split('/');
                if (parts.Length != 3) continue;
                var key = parts[1];
                truth[e.FullName] = store.ReadBytes(e.FullName);
                if (!byMap.TryGetValue(key, out var list)) byMap[key] = list = [];
                list.Add(e.FullName);
            }
        }

        var port = FreePort();
        using var assets = new AssetStore(archive: pak);
        var engine = new LiveParserEngine();
        var server = new EngineWebServer(engine, port, webRoot, modelsRoot, assets);
        server.Start();
        var baseUrl = $"http://127.0.0.1:{port}";
        Console.WriteLine($"  服务已起 {baseUrl}\n");

        int failures = 0;
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
        {
            // 1) /api/map：每张图的 tileUrl 必须被改写为本地 /resources/（否则会退回公网 CDN）。
            var mapText = http.GetStringAsync($"{baseUrl}/api/map").GetAwaiter().GetResult();
            using (var doc = JsonDocument.Parse(mapText))
            {
                foreach (var m in doc.RootElement.GetProperty("maps").EnumerateArray())
                {
                    var key = m.GetProperty("key").GetString();
                    var tileUrl = m.GetProperty("tileUrl").GetString();
                    bool local = tileUrl is not null && tileUrl.StartsWith("/resources/maps2d/", StringComparison.Ordinal);
                    Console.WriteLine($"  {(local ? "[通过]" : "[失败]")} tileUrl 本地化 {key}: {tileUrl}");
                    if (!local) failures++;
                }
            }

            // 2) 每图每瓦片：HTTP 取回字节必须与 pak 完全一致（走 ?v= 版本号路径）。
            foreach (var (key, list) in byMap.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                long ok = 0, mismatch = 0, notFound = 0;
                var badSamples = new ConcurrentBag<string>();
                Parallel.ForEach(list, new ParallelOptions { MaxDegreeOfParallelism = 16 }, file =>
                {
                    var name = file.Split('/')[2];
                    var url = $"{baseUrl}/resources/maps2d/{key}/{name}?v=selftest";
                    try
                    {
                        using var resp = http.GetAsync(url).GetAwaiter().GetResult();
                        if (resp.StatusCode != HttpStatusCode.OK)
                        {
                            Interlocked.Increment(ref notFound);
                            if (badSamples.Count < 3) badSamples.Add($"{name} -> {(int)resp.StatusCode}");
                            return;
                        }
                        var bytes = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                        var want = truth[file];
                        if (bytes.Length == want.Length && bytes.AsSpan().SequenceEqual(want)) Interlocked.Increment(ref ok);
                        else
                        {
                            Interlocked.Increment(ref mismatch);
                            if (badSamples.Count < 3) badSamples.Add($"{name} 长度 {bytes.Length}/{want.Length}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref mismatch);
                        if (badSamples.Count < 3) badSamples.Add($"{name} {ex.Message}");
                    }
                });
                bool pass = mismatch == 0 && notFound == 0;
                if (!pass) failures++;
                Console.WriteLine($"  {(pass ? "[通过]" : "[失败]")} {key}: 瓦片 {list.Count} 一致 {ok} / 404 {notFound} / 不一致 {mismatch}");
                foreach (var s in badSamples) Console.WriteLine("        " + s);
            }
        }

        Console.WriteLine($"\n  合计 {byMap.Values.Sum(v => v.Count)} 张瓦片，失败项 {failures}");
        Console.WriteLine(failures == 0 ? "  [通过] HTTP 全量瓦片校验" : "  [失败] HTTP 全量瓦片校验");
        return failures == 0 ? 0 : 1;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static bool IsJpeg(byte[] b) =>
        b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF && b[^2] == 0xFF && b[^1] == 0xD9;

    private static string Hex(byte[] b) => Convert.ToHexString(b.AsSpan(0, Math.Min(8, b.Length)));
}
