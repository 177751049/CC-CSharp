using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using CCRadar.Core.Infrastructure;

namespace CCRadar.Core.Realtime;

internal enum RealtimeLinkMode
{
    // PC 开热点，手机/平板连接（原模式，PC 天然是网关）。
    Hotspot,
    // B 机与 A 机（PC 端游戏机）同一局域网，B 通过 IP 转发 + ARP 欺骗改道流量。
    Lan
}

internal sealed class RealtimeSession : IAsyncDisposable
{
    private RealtimeOutput? _output;
    private EngineHost? _parser;
    private UdpKeyBroker? _broker;
    private TcpRelayServer? _relay;
    private HotspotTcpRouter? _router;
    private LanForwardingEnabler? _forwarding;
    private ArpSpoofService? _arp;
    private string? _directory;
    private bool _disposed;
    private readonly CancellationTokenSource _statusStop = new();
    private Task? _statusTask;
    public string? DirectoryPath => _directory;
    public int Port => _output?.Port ?? 0;
    public string? WebUrl => _parser?.Url;
    public void OpenBrowser() => _parser?.OpenBrowser();
    public bool Running => !_disposed && _router?.Running == true;
    public RealtimeLinkMode Mode { get; private set; } = RealtimeLinkMode.Hotspot;
    private readonly DateTimeOffset _started = DateTimeOffset.Now;
    public object Snapshot => new { mode = Mode == RealtimeLinkMode.Lan ? "lan" : "hotspot", running = Running,
        events = _output?.Events ?? 0, journalBytes = _output?.JournalBytes ?? 0,
        udpUpPackets = _broker?.UpPackets ?? 0, udpDownPackets = _broker?.DownPackets ?? 0,
        udpUpBytes = _broker?.UpBytes ?? 0, udpDownBytes = _broker?.DownBytes ?? 0,
        plainUp = _broker?.PlainUp ?? 0, plainDown = _broker?.PlainDown ?? 0,
        invalidPackets = _broker?.InvalidPackets ?? 0, pendingPackets = _broker?.PendingCount ?? 0, activeTcp = _relay?.ActiveConnections ?? 0, tgcpEstablished = _relay?.Established ?? 0,
        udpRawDropped = _broker?.RawDroppedPackets ?? 0, udpSpoolDropped = _broker?.SpoolDroppedPackets ?? 0,
        redirectedUp = _router?.RedirectedUp ?? 0, redirectedDown = _router?.RedirectedDown ?? 0, unmappedTcp = _router?.Unmapped ?? 0,
        suppressedOriginalDown = _router?.SuppressedOriginalDown ?? 0,
        bankCandidates = _broker?.CandidateCount ?? 0, udpObserved = _router?.UdpObserved ?? 0, udpPlainPackets = _broker?.PlainPackets ?? 0,
        udpBoundFlows = _broker?.ValidatedFlows ?? 0, outputClients = _output?.ClientCount ?? 0,
        archiveDropped = _output?.ArchiveDropped ?? 0, slowClientsDisconnected = _output?.SlowClients ?? 0,
        archiveError = _output?.ArchiveError, outputListenerError = _output?.ListenerError,
        pendingUdpEvictions = _broker?.PendingEvictions ?? 0, packetSendErrors = _router?.SendErrors ?? 0,
        packetReceiveErrors = _router?.ReceiveErrors ?? 0,
        arpSpoof = _arp is null ? null : new { running = _arp.Running, spoofFrames = _arp.SpoofFrames, restoreFrames = _arp.RestoreFrames,
            targetMac = _arp.TargetMac?.ToString(), gatewayMac = _arp.GatewayMac?.ToString() },
        parser = _parser?.Snapshot };

    public async Task StartAsync(IPAddress target, string directory, RealtimeLinkMode mode = RealtimeLinkMode.Hotspot,
        LanAdapterChoice? lanChoice = null, IReadOnlyCollection<IPAddress>? excludedTargets = null, int outputPort = 17890, int webPort = 17891)
    {
        Mode = mode;
        _directory = directory;
        Directory.CreateDirectory(directory);
        File.Delete(Path.Combine(directory, "realtime-result.json"));
        try
        {
            IPAddress localAddress, mask, realGateway = IPAddress.None;
            int interfaceIndex;
            NetworkInterface lanAdapter = null!;
            if (mode == RealtimeLinkMode.Hotspot)
            {
                var adapter = HotspotPktMonComponentResolver.FindActiveHotspotAdapter();
                var adapterMask = adapter.Adapter.GetIPProperties().UnicastAddresses.First(u => u.Address.Equals(adapter.Address)).IPv4Mask;
                localAddress = adapter.Address;
                mask = adapterMask!;
                interfaceIndex = adapter.InterfaceIndex;
            }
            else
            {
                var lan = lanChoice ?? LanAdapterResolver.SelectDefault();
                localAddress = lan.Address;
                mask = lan.Mask;
                interfaceIndex = lan.InterfaceIndex;
                realGateway = lan.Gateway;
                lanAdapter = lan.Adapter;
            }

            var targetBytes = target.GetAddressBytes();
            var localBytes = localAddress.GetAddressBytes();
            var maskBytes = mask.GetAddressBytes();
            if (targetBytes.Length != 4 || target.Equals(localAddress) ||
                Enumerable.Range(0, 4).Any(i => (targetBytes[i] & maskBytes[i]) != (localBytes[i] & maskBytes[i])))
            {
                throw new ArgumentException(mode == RealtimeLinkMode.Hotspot
                    ? "实时设备地址须位于当前热点 IPv4 子网。"
                    : $"游戏电脑地址须与本机网卡 {localAddress} 处于同一 IPv4 网段。");
            }

            // 局域网模式：先建归档事件流，再打开接口转发（替代热点 ICS 的 UDP 转发），
            // 欺骗生效后流量才不中断。
            _output = new(directory, outputPort); _output.Start();
            if (mode == RealtimeLinkMode.Lan)
            {
                _forwarding = new LanForwardingEnabler(interfaceIndex, _output.Publish);
                await _forwarding.StartAsync();
            }

            _parser = new();
            await _parser.StartAsync(Path.Combine(directory, "semantic"), webPort, cacheOnly: true,
                journal: Path.Combine(directory, "realtime.jsonl"));
            _broker = new(directory, _output.Publish, excludedHosts: excludedTargets);
            _relay = new(localAddress, 0, endpoint => _router?.Resolve(endpoint), _output.Publish, _broker.Add);
            _relay.Start();
            _router = new(target, localAddress, mask, _relay.Port, interfaceIndex, _output.Publish, _broker.Observe, excludedTargets);
            var metadata = new { schema = "gateway-session/v5", mode = mode == RealtimeLinkMode.Lan ? "realtime-journal-lan" : "realtime-journal",
                startedAt = _started,
                targetAddress = target.ToString(), gateway = localAddress.ToString(), realGateway = mode == RealtimeLinkMode.Lan ? realGateway.ToString() : null,
                output = $"tcp://127.0.0.1:{Port}",
                framing = "UTF-8 JSONL", payloadEncoding = "base64", tgcpProfile = "mode3-md5-cbc", dhPrivateSource = "fresh-PC-generated", phoneMemoryInput = false,
                web = WebUrl, stateSchema = "gateway-state/v5" };
            await File.WriteAllTextAsync(Path.Combine(directory, "realtime-session.json"), JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
            _output.Publish("session_started", metadata);
            await _router.StartAsync();

            // 欺骗放在路由器就绪之后：A 的流量一改道，WinDivert/转发链路必须已就位。
            if (mode == RealtimeLinkMode.Lan)
            {
                _arp = new(lanAdapter!, localAddress, realGateway, target, _output.Publish);
                await _arp.StartAsync();
            }

            _statusTask = ReportStatusAsync();
        }
        catch { await DisposeAsync(); throw; }
    }

    private async Task ReportStatusAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try { while (await timer.WaitForNextTickAsync(_statusStop.Token)) _output?.Publish("session_status", Snapshot); }
        catch (OperationCanceledException) when (_statusStop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        var errors = new List<string>();
        _statusStop.Cancel();
        if (_statusTask is not null) try { await _statusTask; } catch (Exception ex) { errors.Add(ex.Message); }
        _statusStop.Dispose();
        if (_relay is not null) try { await _relay.DisposeAsync(); } catch (Exception ex) { errors.Add(ex.Message); }
        if (_router is not null) try { await _router.DisposeAsync(); } catch (Exception ex) { errors.Add(ex.Message); }
        // 局域网模式恢复顺序：先撤 ARP 欺骗（流量回真实路径），再关接口转发。
        if (_arp is not null) try { await _arp.DisposeAsync(); } catch (Exception ex) { errors.Add(ex.Message); }
        if (_forwarding is not null) try { await _forwarding.DisposeAsync(); } catch (Exception ex) { errors.Add(ex.Message); }
        try { _output?.Publish("session_stopped", new { snapshot = Snapshot, errors }); }
        catch (Exception ex) { errors.Add(ex.Message); }
        if (_output is not null) try { await _output.DisposeAsync(); } catch (Exception ex) { errors.Add(ex.Message); }
        if (_parser is not null)
        {
            try { await _parser.DisposeAsync(); } catch (Exception ex) { errors.Add(ex.Message); }
            if (_parser.Error is not null) errors.Add(_parser.Error);
        }
        if (_directory is not null) await File.WriteAllTextAsync(Path.Combine(_directory, "realtime-result.json"),
            JsonSerializer.Serialize(new { startedAt = _started, stoppedAt = DateTimeOffset.Now, snapshot = Snapshot, errors }, new JsonSerializerOptions { WriteIndented = true }));
        if (errors.Count > 0) throw new IOException("实时模式停止时出现错误：" + string.Join("; ", errors));
    }
}
