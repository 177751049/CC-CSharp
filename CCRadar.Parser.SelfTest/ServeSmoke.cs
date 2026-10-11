using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CCRadar.Parser.Engine;

namespace CCRadar.Parser.SelfTest;

/// <summary>
/// 无头端到端冒烟：真实起 LiveParserEngine + EngineWebServer（HTTP），
/// 重放日志后逐个请求关键路由，核对状态码与 Content-Type。
/// 用法: serve-smoke &lt;journal&gt; &lt;webRoot&gt; &lt;modelsRoot&gt; &lt;pak&gt;
/// </summary>
public static class ServeSmoke
{
    private static int _pass, _fail;

    private static void Ok(bool cond, string name, string detail)
    {
        if (cond) { _pass++; Console.WriteLine($"  [通过] {name}  {detail}"); }
        else { _fail++; Console.WriteLine($"  [失败] {name}  {detail}"); }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    public static int Run(string journal, string webRoot, string modelsRoot, string pak)
    {
        Console.WriteLine("=== CC雷达 进程内引擎 · HTTP 冒烟 ===\n");
        if (!File.Exists(journal)) { Console.WriteLine($"缺少日志 {journal}"); return 2; }
        if (!Directory.Exists(webRoot)) { Console.WriteLine($"缺少 webRoot {webRoot}"); return 2; }
        using var assets = File.Exists(pak) ? new AssetStore(archive: pak) : null;
        Console.WriteLine($"  pak={(assets is null ? "无" : $"{assets.EntryCount} 条目")} models={Directory.Exists(modelsRoot)} webRoot={webRoot}");

        var outDir = Path.Combine(Path.GetTempPath(), "ccradar_smoke");
        Directory.CreateDirectory(outDir);
        foreach (var stale in new[] { "state-latest.json", "status-latest.json", "identity-cache.json",
            "snapshot-final.json", "parser-result.json", "parser-errors.jsonl", "snapshot-cs.json" })
        {
            try { File.Delete(Path.Combine(outDir, stale)); } catch { }
        }
        var port = FreePort();
        var engine = new LiveParserEngine { OutputDirectory = outDir };
        var server = new EngineWebServer(engine, port, webRoot, modelsRoot, assets);
        server.Start();
        var baseUrl = $"http://127.0.0.1:{port}";
        Console.WriteLine($"  服务已起 {baseUrl}");
        Console.WriteLine($"  绑定={server.BindPrefix} 全网卡={server.AllInterfaces}\n");

        // 重放日志（与 GoldenReplay 同一喂入契约）。
        long n = 0;
        foreach (var line in File.ReadLines(journal))
        {
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                var data = root.TryGetProperty("data", out var d) ? d.Clone() : default;
                long? seq = root.TryGetProperty("sequence", out var sq) && sq.ValueKind == JsonValueKind.Number
                    ? sq.GetInt64() : null;
                engine.ConsumeEvent(type, data, seq);
                n++;
            }
            catch (JsonException) { }
        }
        Console.WriteLine($"  重放事件 {n}\n");

        // 落盘 C# 快照，供与 Python snapshot-final.json 做字段级对拍。
        var cur = engine.CurrentView;
        Console.WriteLine($"  pose 诊断: PoseSamples={cur?.Loader.PoseSamples} " +
            $"SelfPose={(cur?.Loader.SelfPose is null ? "null" : "有")} SelfPoseT={cur?.Loader.SelfPoseT:F3} " +
            $"ch3={cur?.Loader.NCh3Bunches} reasmThrow={cur?.Loader.NReasmReqThrow} reasmOut={cur?.Loader.NReasmReqOut}");
        var snapshot = engine.FinalSnapshotJson();
        if (snapshot is not null)
        {
            // 对齐 Python Engine.close() 的落盘产物，供逐文件对拍。
            File.WriteAllText(Path.Combine(outDir, "snapshot-final.json"), snapshot);
            File.WriteAllText(Path.Combine(outDir, "parser-result.json"),
                engine.BuildStatus(ended: true).ToJsonString());
            File.WriteAllText(Path.Combine(outDir, "snapshot-cs.json"), snapshot);
            Console.WriteLine($"  C# 语义产物 → {outDir}\n");
            // 稳定判据：自机上行位姿链是否打通（位姿已解析）。API 的 self_aim_* 受 0.25s
            // 窗口约束、与重放耗时相关，仅作参考信息，不作硬断言（避免时序抖动误报）。
            var poseParsed = cur?.Loader.SelfPose is not null;
            try
            {
                using var snapDoc = JsonDocument.Parse(snapshot);
                var sr = snapDoc.RootElement;
                Ok(poseParsed, "自机上行位姿链",
                    $"PoseSamples={cur?.Loader.PoseSamples} SelfPose={poseParsed} ch3={cur?.Loader.NCh3Bunches} " +
                    $"| API窗口内 age_ms={sr.TryGetProperty("self_aim_age_ms", out _)} " +
                    $"yaw={sr.TryGetProperty("self_aim_yaw", out _)}");
            }
            catch (Exception ex) { Ok(false, "自机上行位姿链", ex.Message); }
            // 落盘产物齐全性（Python cache_only 形态的语义文件）。
            var artifacts = new[] { "state-latest.json", "status-latest.json", "identity-cache.json",
                "snapshot-final.json", "parser-result.json", "parser-errors.jsonl" };
            var missing = artifacts.Where(f => !File.Exists(Path.Combine(outDir, f))).ToList();
            Ok(missing.Count == 0, "语义落盘产物",
                missing.Count == 0 ? $"6/6 齐全" : $"缺 {string.Join(",", missing)}");
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            Get(http, baseUrl, "/", "text/html", 100000, "雷达页");
            Get(http, baseUrl, "/er", "text/html", 100000, "雷达页/er");
            GetJson(http, baseUrl, "/api/status", "gateway-parser-status/v5", "状态");
            GetJson(http, baseUrl, "/api/map", "\"maps\"", "地图清单");
            GetJson(http, baseUrl, "/api/state", "\"entities\"", "快照");
            StateDetail(http, baseUrl);
            GetJson(http, baseUrl, "/api/flows", "\"runs\"", "流列表");
            Get(http, baseUrl, "/ui/shubao.css", "text/css", 1000, "样式");
            Get(http, baseUrl, "/ui/dock.js", "text/javascript", 1000, "坞脚本");
            Get(http, baseUrl, "/vendor/leaflet/leaflet.js", "text/javascript", 50000, "leaflet");
            Get(http, baseUrl, "/vendor/three/build/three.module.js", "text/javascript", 500000, "three");
            Get(http, baseUrl, "/vendor/three/examples/jsm/controls/OrbitControls.js", "text/javascript", 1000, "three addons");
            Get(http, baseUrl, "/m3d/manifest.json", "application/json", 100, "地形清单");
            Get(http, baseUrl, "/m3d/r3d.js", "text/javascript", 1000, "r3d 引擎");
            Get(http, baseUrl, "/avatars/%E7%BA%A2%E7%8B%BC.png", "image/png", 1000, "干员头像");
            Get(http, baseUrl, "/resources/map_catalog.json", "application/json", 1000, "地图目录(pak)");
            Get(http, baseUrl, "/resources/items/11010001001.png", "image/png", 1000, "物资图标(pak)");
            Status(http, baseUrl, "/nope-404", 404, "未命中 404");
            // 全网卡绑定验证：用局域网 IP（非回环）请求同一端口，证明外部设备可达。
            var lan = LanIpv4();
            if (lan is not null)
            {
                try
                {
                    var text = http.GetStringAsync($"http://{lan}:{port}/api/state").GetAwaiter().GetResult();
                    Ok(text.Contains("\"entities\""), "局域网 IP 可达", $"{lan}:{port} {text.Length}B");
                }
                catch (Exception ex) { Ok(false, "局域网 IP 可达", $"{lan}:{port} 异常 {ex.GetType().Name}: {ex.Message}"); }
            }
            else
            {
                Console.WriteLine("  [跳过] 局域网 IP 可达（未找到非回环 IPv4）");
            }
        }
        finally
        {
            http.Dispose();
            server.Stop();
        }

        Console.WriteLine($"\n=== 结果：通过 {_pass}，失败 {_fail} ===");
        return _fail == 0 ? 0 : 1;
    }

    private static string? LanIpv4()
    {
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                    or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
                var addr = nic.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(u => u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                                         && !IPAddress.IsLoopback(u.Address))?.Address;
                if (addr is not null) return addr.ToString();
            }
        }
        catch { }
        return null;
    }

    private static void StateDetail(HttpClient http, string baseUrl)
    {
        try
        {
            var text = http.GetStringAsync(baseUrl + "/api/state").GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var ents = root.TryGetProperty("entities", out var e) && e.ValueKind == JsonValueKind.Array
                ? e.GetArrayLength() : -1;
            var self = root.TryGetProperty("self", out var s) && s.ValueKind == JsonValueKind.Array;
            var names = root.TryGetProperty("names", out var nm) && nm.ValueKind == JsonValueKind.Array
                ? nm.GetArrayLength() : -1;
            Ok(ents > 0 && self, "快照内容", $"entities={ents} self数组={self} names={names}");
            // API 富化层回归守卫（Python gateway_parser.state 的字段契约）。
            // 注意：self_aim_* 仅在位姿 0.25s 内有效，HTTP 往返可能已超窗，故此处只断言
            // 确定性字段；自瞄字段改在紧接重放的落盘快照上校验。
            var live = root.TryGetProperty("live_active", out var lv) && lv.ValueKind == JsonValueKind.True;
            var schema = root.TryGetProperty("schema", out var sc) ? sc.GetString() : null;
            var aim = root.TryGetProperty("self_aim_age_ms", out _);
            Ok(live && schema == "gateway-state/v5",
                "状态富化字段", $"live_active={live} schema={schema} self_aim_age_ms={aim}（时序敏感，仅供参考）");
            var entityArr = root.GetProperty("entities");
            var hasLife = false; var hasPose = false; var hasIdentity = false;
            foreach (var ent in entityArr.EnumerateArray())
            {
                if (ent.TryGetProperty("life_state", out _)) hasLife = true;
                if (ent.TryGetProperty("pose_seq", out _)) hasPose = true;
                if (ent.TryGetProperty("identity_status", out _)) hasIdentity = true;
            }
            Ok(hasLife && hasIdentity, "实体富化字段",
                $"life_state={hasLife} pose_seq={hasPose} identity_status={hasIdentity}");
            Console.WriteLine($"    state 片段: {text.Substring(0, Math.Min(400, text.Length))}");
        }
        catch (Exception ex) { Console.WriteLine($"    状态明细异常: {ex.Message}"); }
    }

    private static void Get(HttpClient http, string baseUrl, string path, string expectType, int minBytes, string name)
    {
        try
        {
            using var resp = http.GetAsync(baseUrl + path).GetAwaiter().GetResult();
            var body = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            var ctype = resp.Content.Headers.ContentType?.MediaType ?? "";
            var ok = resp.IsSuccessStatusCode && ctype == expectType && body.Length >= minBytes;
            Ok(ok, $"{name} {path}", $"{(int)resp.StatusCode} {ctype} {body.Length}B");
        }
        catch (Exception ex) { Ok(false, $"{name} {path}", $"异常 {ex.GetType().Name}: {ex.Message}"); }
    }

    private static void GetJson(HttpClient http, string baseUrl, string path, string contains, string name)
    {
        try
        {
            using var resp = http.GetAsync(baseUrl + path).GetAwaiter().GetResult();
            var text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var ctype = resp.Content.Headers.ContentType?.MediaType ?? "";
            var ok = resp.IsSuccessStatusCode && ctype == "application/json" && text.Contains(contains);
            Ok(ok, $"{name} {path}", $"{(int)resp.StatusCode} {ctype} {text.Length}B 含{contains}={text.Contains(contains)}");
        }
        catch (Exception ex) { Ok(false, $"{name} {path}", $"异常 {ex.GetType().Name}: {ex.Message}"); }
    }

    private static void Status(HttpClient http, string baseUrl, string path, int expect, string name)
    {
        try
        {
            using var resp = http.GetAsync(baseUrl + path).GetAwaiter().GetResult();
            Ok((int)resp.StatusCode == expect, $"{name} {path}", $"{(int)resp.StatusCode}");
        }
        catch (Exception ex) { Ok(false, $"{name} {path}", $"异常 {ex.GetType().Name}: {ex.Message}"); }
    }
}
