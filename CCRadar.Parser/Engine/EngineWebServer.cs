using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace CCRadar.Parser.Engine;

/// <summary>
/// 纯 C# 模式的网页发布服务——对齐旧 NvaParser.exe（gateway_parser.Handler + er_radar.Handler）
/// 的完整路由：
///   /api/state /api/status /api/map /api/flows /api/select /api/ctrl
///   /resources/*（pak）  /ui/*  /vendor/*  /avatars/*  /m3d/*（glb/manifest 走 models，js 走 web/m3d）
///   /er /er.html / /index.html → 内嵌雷达页
/// 页面在启动时做一次与 Python prepare_page 等价的处理（leaflet → /vendor/leaflet/、标题、NO3D）。
/// </summary>
public sealed class EngineWebServer
{
    private readonly LiveParserEngine _engine;
    private readonly int _port;
    private readonly string _webRoot;
    private readonly string? _modelsRoot;
    private readonly AssetStore? _assets;
    private HttpListener? _listener;
    private Task? _loop;
    private CancellationTokenSource? _stop;

    private byte[] _page = [];
    private JsonObject _mapInfo = new() { ["default"] = "daba", ["maps"] = new JsonArray() };
    private readonly object _initGate = new();
    private bool _initialized;

    public EngineWebServer(LiveParserEngine engine, int port, string webRoot,
        string? modelsRoot = null, AssetStore? assets = null)
    {
        _engine = engine;
        _port = port;
        _webRoot = webRoot;
        _modelsRoot = modelsRoot;
        _assets = assets;
    }

    public string WebRoot => _webRoot;
    public int Port => _port;

    /// <summary>实际生效的绑定前缀（诊断用）。</summary>
    public string BindPrefix { get; private set; } = "";

    /// <summary>true = 已绑定所有网卡（局域网/手机可直接访问）；false = 回落仅本机回环。</summary>
    public bool AllInterfaces { get; private set; }

    public void Start()
    {
        EnsureInitialized();
        _stop = new CancellationTokenSource();
        // 优先绑定所有网卡（"http://+:port/" 覆盖全部 IPv4/IPv6 与主机名，局域网其它
        // 设备可直接打开雷达页）；管理员权限缺失或未授予 urlacl 时回落仅本机回环，
        // 避免整个实时链路因端口绑定失败而起不来。
        var attempts = new (string Prefix, bool All)[]
        {
            ($"http://+:{_port}/", true),
            ($"http://127.0.0.1:{_port}/", false),
        };
        Exception? failure = null;
        foreach (var (prefix, all) in attempts)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
                _listener = listener;
                BindPrefix = prefix;
                AllInterfaces = all;
                failure = null;
                break;
            }
            catch (Exception ex)
            {
                failure = ex;
                try { listener.Close(); } catch { }
            }
        }
        if (failure is not null || _listener is null)
            throw new IOException($"网页端口 {_port} 绑定失败：{failure?.Message}", failure);
        _loop = Task.Run(() => AcceptLoop(_stop.Token));
    }

    // ---- 启动初始化：页面准备 + /api/map 列表（等价 Python prepare_page + load_map_info）----

    private void EnsureInitialized()
    {
        lock (_initGate)
        {
            if (_initialized) return;
            _initialized = true;
            BuildPage();
            BuildMapInfo();
        }
    }

    private void BuildPage()
    {
        var htmlPath = Path.Combine(_webRoot, "radar.html");
        if (!File.Exists(htmlPath)) { _page = []; return; }
        var html = File.ReadAllText(htmlPath, Encoding.UTF8);
        // Python prepare_page：leaflet CDN → 本地 vendor；改名标题。
        html = html.Replace("https://unpkg.com/leaflet@1.9.4/dist/", "/vendor/leaflet/");
        html = html.Replace("<title>DFM 战术雷达</title>", "<title>Nva本地离线解析 · 实时状态</title>");
        // 无地形资源 → NO3D=1 隐藏 3D 入口（同一份页面，行为由资产决定）。
        if (!TerrainExists()) html = html.Replace("const NO3D=0;", "const NO3D=1;");
        _page = Encoding.UTF8.GetBytes(html);
    }

    private bool TerrainExists()
    {
        if (_modelsRoot is null) return false;
        var manifest = Path.Combine(_modelsRoot, "manifest.json");
        if (!File.Exists(manifest)) return false;
        try
        {
            var maps = JsonNode.Parse(File.ReadAllText(manifest))?["maps"]?.AsObject();
            if (maps is null) return false;
            foreach (var (_, ent) in maps)
            {
                var file = ent?["file"]?.GetValue<string>();
                if (string.IsNullOrEmpty(file)) continue;
                if (File.Exists(Path.Combine(_modelsRoot, file)) ||
                    File.Exists(Path.Combine(_modelsRoot, file + ".gz"))) return true;
            }
        }
        catch { /* 损坏 manifest 视为无地形 */ }
        return false;
    }

    // 瓦片 URL 的构建版本号（取 exe 写入时间）。构建号一变，URL 即变，浏览器视为新资源，
    // 历史 bug 产出的坏瓦片缓存自动失效——避免"修好了但用户仍看到旧坏图"。
    private static readonly string TileVersion = ComputeTileVersion();
    private static string ComputeTileVersion()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe)) return File.GetLastWriteTimeUtc(exe).Ticks.ToString("x");
        }
        catch { /* 取不到则退回常量 */ }
        return "1";
    }

    private void BuildMapInfo()
    {
        // Python load_map_info：优先 web/map_catalog.json，其次旧单图 map_markers.json。
        var catalog = Path.Combine(_webRoot, "map_catalog.json");
        JsonNode? doc = null;
        if (File.Exists(catalog))
        {
            try { doc = JsonNode.Parse(File.ReadAllText(catalog)); } catch { doc = null; }
        }
        if (doc?["maps"] is JsonArray mapsArr && mapsArr.Count > 0)
        {
            // tileUrl 改写到 pak 的 maps2d 切片（gateway_parser.main 的改写）。
            foreach (var item in mapsArr)
            {
                if (item is not JsonObject m) continue;
                var key = m["key"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(key)) m["tileUrl"] = $"/resources/maps2d/{key}/{{z}}_{{x}}_{{y}}.jpg?v={TileVersion}";
            }
            _mapInfo = new JsonObject
            {
                ["default"] = doc["default"]?.GetValue<string>() ?? "daba",
                // mapsArr 已挂在 doc 上，必须克隆后再挂到新对象（否则"节点已有父节点"）。
                ["maps"] = mapsArr.DeepClone(),
            };
            return;
        }
        var markers = Path.Combine(_webRoot, "map_markers.json");
        if (!File.Exists(markers)) { _mapInfo = new JsonObject { ["default"] = "daba", ["maps"] = new JsonArray() }; return; }
        try
        {
            var mdoc = JsonNode.Parse(File.ReadAllText(markers));
            var info = mdoc?["info"] as JsonObject ?? [];
            var levels = mdoc?["levels"] as JsonArray;
            var poi = new JsonArray();
            if (levels is { Count: > 0 } && levels[0]?["points"] is JsonArray pts)
            {
                foreach (var p in pts)
                {
                    if (p?["lat"] is null || p?["lng"] is null) continue;
                    poi.Add(new JsonObject
                    {
                        ["name"] = p["name"]?.DeepClone(),
                        ["icon"] = p["icon"]?.DeepClone(),
                        ["region"] = p["region"]?.DeepClone(),
                        ["lat"] = p["lat"]?.DeepClone(),
                        ["lng"] = p["lng"]?.DeepClone(),
                    });
                }
            }
            var single = new JsonObject
            {
                ["key"] = "daba", ["name"] = "零号大坝",
                ["bj"] = info["bj"]?.DeepClone() ?? 128,
                ["rotate"] = 0,
                ["width"] = info["width"]?.DeepClone(),
                ["height"] = info["height"]?.DeepClone(),
                ["centerX"] = info["centerX"]?.DeepClone(),
                ["centerY"] = info["centerY"]?.DeepClone(),
                ["minZoom"] = info["minZoom"]?.DeepClone() ?? 3,
                ["initZoom"] = info["initZoom"]?.DeepClone() ?? 3,
                ["initX"] = info["initX"]?.DeepClone(),
                ["initY"] = info["initY"]?.DeepClone(),
                ["tileUrl"] = $"/resources/maps2d/daba/{{z}}_{{x}}_{{y}}.jpg?v={TileVersion}",
                ["tileSize"] = info["tileSize"]?.DeepClone() ?? 256,
                ["maxNativeZoom"] = info["maxNativeZoom"]?.DeepClone() ?? 4,
                ["bounds"] = info["bounds"]?.DeepClone(),
                ["poi"] = poi,
            };
            _mapInfo = new JsonObject { ["default"] = "daba", ["maps"] = new JsonArray(single) };
        }
        catch
        {
            _mapInfo = new JsonObject { ["default"] = "daba", ["maps"] = new JsonArray() };
        }
    }

    private async Task AcceptLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener!.GetContextAsync(); }
            catch (Exception) when (token.IsCancellationRequested) { return; }
            catch (Exception) { continue; }
            _ = Task.Run(() => Handle(ctx), token);
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            var path = Checked(ctx.Request.Url!.AbsolutePath);
            switch (path)
            {
                case "/api/state":
                    StaticServe.WriteJson(ctx, _engine.BuildApiState());
                    return;
                case "/api/status":
                    StaticServe.WriteJson(ctx, _engine.BuildStatus());
                    return;
                case "/api/map":
                    StaticServe.WriteJson(ctx, _mapInfo);
                    return;
                case "/api/flows":
                    StaticServe.WriteJson(ctx, new JsonObject { ["runs"] = new JsonArray() });
                    return;
                case "/api/select":
                    StaticServe.WriteJson(ctx, new JsonObject { ["ok"] = true });
                    return;
                case "/api/ctrl":
                    StaticServe.WriteJson(ctx, new JsonObject { ["ok"] = true });
                    return;
            }
            // 资源前缀（顺序无关，均为前缀短路）。
            if (StaticServe.ServeStore(_assets, ctx, path, "/resources/")) return;
            if (StaticServe.ServeDirectory(ctx, path, "/ui/", Path.Combine(_webRoot, "ui"))) return;
            if (StaticServe.ServeDirectory(ctx, path, "/vendor/", Path.Combine(_webRoot, "vendor"))) return;
            if (StaticServe.ServeDirectory(ctx, path, "/avatars/", Path.Combine(_webRoot, "avatars"), noCache: true)) return;
            if (path.StartsWith("/m3d/", StringComparison.Ordinal))
            {
                // .glb / manifest.json → models 目录；其余（.js 等）→ web/m3d。
                if (path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    if (StaticServe.ServeDirectory(ctx, path, "/m3d/", _modelsRoot ?? "")) return;
                }
                if (StaticServe.ServeDirectory(ctx, path, "/m3d/", Path.Combine(_webRoot, "m3d"))) return;
            }
            // Korr 3D 地形接口：本发布无 korr 资源 → 404（前端回落 2D）。
            if (path.StartsWith("/api/map/", StringComparison.Ordinal))
            {
                StaticServe.WriteBytes(ctx, 404, "text/plain; charset=utf-8", []);
                return;
            }
            if (path is "/" or "/index.html" or "/er" or "/er.html")
            {
                if (_page.Length > 0)
                {
                    StaticServe.WriteBytes(ctx, 200, "text/html; charset=utf-8", _page,
                        cacheControl: "no-store");
                    return;
                }
                StaticServe.WriteBytes(ctx, 200, "text/html; charset=utf-8",
                    "<html><body><h3>CC雷达 C# 引擎运行中</h3><p>/api/state 就绪。缺少 Web/radar.html 静态资源。</p></body></html>"u8.ToArray());
                return;
            }
            StaticServe.WriteBytes(ctx, 404, "text/plain; charset=utf-8", "not found"u8.ToArray());
        }
        catch
        {
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    private static string Checked(string rawPath)
    {
        try { return Uri.UnescapeDataString(new Uri("http://x" + rawPath).AbsolutePath); }
        catch { return rawPath; }
    }

    public void Stop()
    {
        try { _stop?.Cancel(); _listener?.Stop(); _listener?.Close(); } catch { }
    }
}
