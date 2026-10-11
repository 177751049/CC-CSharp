using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CCRadar.Parser.Engine;

namespace CCRadar.Core.Realtime;

/// <summary>
/// 解析引擎宿主——纯 C# 进程内实现，直接替代旧的子进程 NvaParser.exe。
/// 行为对齐 Python gateway_parser 的 <c>--journal</c> 模式：
///  tail-follow <c>realtime.jsonl</c>（边写边读），逐行喂给纯 C# <see cref="LiveParserEngine"/>，
///  并起 <see cref="EngineWebServer"/> 提供 /api/* 与网页（/ui /vendor /avatars /m3d /resources）。
/// 对外契约与旧 ParserHost 一致（Port / Url / Snapshot / Error / OpenBrowser / DisposeAsync），
/// 因此 RealtimeSession 无需改动调用方式。
/// </summary>
internal sealed class EngineHost : IAsyncDisposable
{
    private readonly string _webRoot;
    private readonly string? _modelsRoot;
    private readonly AssetStore? _assets;
    private LiveParserEngine? _engine;
    private EngineWebServer? _server;
    private CancellationTokenSource? _stop;
    private Task? _tail;
    private string? _outputDirectory;
    private string? _error;
    private long _sent;
    private bool _disposed;

    public int Port { get; private set; }

    /// <summary>
    /// 网页访问地址。绑定所有网卡时使用局域网 IPv4（局域网其它设备/手机可直接打开），
    /// 否则回落本机回环。本机浏览器访问该地址同样有效。
    /// </summary>
    public string Url => $"http://{AccessHost()}:{Port}/";

    /// <summary>仅本机可用的回环地址（备用）。</summary>
    public string LocalUrl => $"http://127.0.0.1:{Port}/";

    /// <summary>true = 网页端已绑定所有网卡。</summary>
    public bool AllInterfaces => _server?.AllInterfaces ?? false;

    /// <summary>防火墙入站放行的失败原因（null = 已放行或无需放行）。</summary>
    public string? FirewallNote { get; private set; }

    public string? Error => _error;
    public long SentEvents => Interlocked.Read(ref _sent);

    public object Snapshot => new
    {
        port = Port,
        url = Port > 0 ? Url : null,
        host = AccessHost(),
        allInterfaces = AllInterfaces,
        firewall = FirewallNote,
        sentEvents = Interlocked.Read(ref _sent),
        droppedEvents = 0,
        error = Error,
        exited = _disposed,
    };

    private string AccessHost()
    {
        if (!AllInterfaces) return "127.0.0.1";
        return PrimaryLanAddress()?.ToString() ?? "127.0.0.1";
    }

    /// <summary>
    /// 主用局域网 IPv4：优先带默认网关的以太网卡（与 LAN 取数端同一判据），
    /// 其次任意 Up 的非回环网卡。取不到则返回 null（调用方回落回环）。
    /// </summary>
    private static IPAddress? PrimaryLanAddress()
    {
        try
        {
            var preferred = Infrastructure.LanAdapterResolver.Enumerate()
                .OrderByDescending(c => c.Adapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                .FirstOrDefault();
            if (preferred is not null) return preferred.Address;
        }
        catch
        {
            // 枚举网卡失败（权限/驱动异常）：继续走下面的兜底枚举。
        }
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var address = nic.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork
                                         && !IPAddress.IsLoopback(u.Address))?.Address;
                if (address is not null) return address;
            }
        }
        catch
        {
            // 无可用网卡：调用方回落回环。
        }
        return null;
    }

    public EngineHost()
    {
        var baseDir = AppContext.BaseDirectory;
        _webRoot = Path.Combine(baseDir, "Web");
        var models = Path.Combine(baseDir, "models");
        _modelsRoot = Directory.Exists(models) ? models : null;
        var pak = Path.Combine(baseDir, Core.ProductInfo.AssetPackage);
        _assets = File.Exists(pak) ? new AssetStore(archive: pak) : null;
    }

    public async Task StartAsync(string directory, int port = 17891, bool replay = false, bool cacheOnly = false,
        string? journal = null, string? timeline = null)
    {
        if (!Directory.Exists(_webRoot))
            throw new DirectoryNotFoundException($"缺少网页资源目录 {_webRoot}，请使用完整 {Core.ProductInfo.Name} 发布目录。");
        Directory.CreateDirectory(directory);
        _outputDirectory = directory;
        // OutputDirectory 赋值即按 Python cache_only 语义清空 parser-errors.jsonl。
        _engine = new LiveParserEngine { OutputDirectory = directory };
        // Python Engine.__init__ 清掉上一轮的 snapshot-final.json / parser-result.json。
        foreach (var stale in new[] { "snapshot-final.json", "parser-result.json" })
        {
            try { File.Delete(Path.Combine(directory, stale)); } catch { }
        }
        _server = new EngineWebServer(_engine, port, _webRoot, _modelsRoot, _assets);
        _server.Start();
        Port = port;
        if (_server.AllInterfaces)
        {
            // 绑定全网卡后，局域网设备访问还需 Windows 防火墙放行；失败不影响本机访问。
            FirewallNote = await Infrastructure.WebFirewallRule.EnsureAsync(port);
        }
        _stop = new CancellationTokenSource();
        if (!string.IsNullOrEmpty(journal))
        {
            // 会话首包到达前日志文件可能尚未创建，tail 线程内部自会等待。
            _tail = Task.Run(() => TailJournalAsync(journal!, _stop.Token));
        }
    }

    private async Task TailJournalAsync(string journal, CancellationToken token)
    {
        try
        {
            while (!File.Exists(journal) && !token.IsCancellationRequested)
                await Task.Delay(20, token);
            if (token.IsCancellationRequested) return;
            using var stream = new FileStream(journal, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            while (!token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(token);
                if (line is null)
                {
                    // 已读到当前 EOF：等待写入方追加（tail-follow）。
                    await Task.Delay(30, token);
                    _engine?.Flush();   // Python 主循环 ~30Hz flush：推进状态序号与 state-latest.json
                    continue;
                }
                if (line.Length == 0) continue;
                Feed(line);
                _engine?.Flush();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _error ??= ex.Message; }
    }

    private void Feed(string line)
    {
        Interlocked.Increment(ref _sent);
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            var data = root.TryGetProperty("data", out var d) ? d : default;
            long? seq = root.TryGetProperty("sequence", out var sq) && sq.ValueKind == JsonValueKind.Number
                ? sq.GetInt64() : null;
            _engine!.ConsumeEvent(type, data, seq);
        }
        catch (JsonException) { /* 半行/坏行：忽略，等下一行（Python 同） */ }
        catch (Exception ex) { _error ??= ex.Message; _engine?.NoteInputError(ex.Message); }
    }

    public void OpenBrowser()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Url) { UseShellExecute = true }); }
        catch (Exception ex) { Console.WriteLine($"请在浏览器打开 {Url}（自动打开失败：{ex.Message}）"); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _stop?.Cancel();
        try { if (_tail is not null) await _tail.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception) { /* tail 线程异常已记入 Error */ }
        try
        {
            if (_outputDirectory is not null && _engine is not null)
            {
                // Python Engine.close：落盘 snapshot-final.json 与 parser-result.json。
                var snapshot = _engine.FinalSnapshotJson();
                if (snapshot is not null)
                    await File.WriteAllTextAsync(Path.Combine(_outputDirectory, "snapshot-final.json"), snapshot);
                await File.WriteAllTextAsync(Path.Combine(_outputDirectory, "parser-result.json"),
                    _engine.BuildStatus(ended: true).ToJsonString());
            }
        }
        catch (Exception ex) { _error ??= ex.Message; }
        _server?.Stop();
        _assets?.Dispose();
        _stop?.Dispose();
    }
}
