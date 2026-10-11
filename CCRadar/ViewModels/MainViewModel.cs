using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using CCRadar.Core.Infrastructure;
using CCRadar.Core.Models;
using CCRadar.Core.Realtime;

namespace CCRadar.ViewModels;

internal sealed class MainViewModel : System.ComponentModel.INotifyPropertyChanged
{
    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private RealtimeSession? _live;
    private readonly System.Windows.Threading.Dispatcher _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
    private long _previousUp, _previousDown, _oldKeys, _oldPlain, _oldEstablished, _oldBound;
    private DateTimeOffset _lastRate = DateTimeOffset.Now;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new(name));
    private void Set<T>(ref T field, T value, string name) where T : IEquatable<T>?
    {
        if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name);
    }

    public ObservableCollection<LanAdapterChoice> LanAdapters { get; } = [];
    public LanAdapterChoice? SelectedLanAdapter { get => _selectedLanAdapter; set { _selectedLanAdapter = value; Raise(nameof(SelectedLanAdapter)); } }
    private LanAdapterChoice? _selectedLanAdapter;

    public ObservableCollection<LanNeighbor> LanNeighbors { get; } = [];
    public LanNeighbor? SelectedLanNeighbor { get => _selectedLanNeighbor; set { _selectedLanNeighbor = value; Raise(nameof(SelectedLanNeighbor)); if (value is not null) TargetIp = value.Address.ToString(); } }
    private LanNeighbor? _selectedLanNeighbor;

    public ObservableCollection<HotspotClientDevice> HotspotDevices { get; } = [];
    public HotspotClientDevice? SelectedHotspotDevice { get => _selectedHotspotDevice; set { _selectedHotspotDevice = value; Raise(nameof(SelectedHotspotDevice)); if (value is not null) TargetIp = value.Address.ToString(); } }
    private HotspotClientDevice? _selectedHotspotDevice;

    public bool LanMode { get => _lanMode; set { _lanMode = value; Raise(nameof(LanMode)); Raise(nameof(HotspotMode)); } }
    private bool _lanMode = true;
    public bool HotspotMode { get => !_lanMode; set { _lanMode = !value; Raise(nameof(LanMode)); Raise(nameof(HotspotMode)); } }

    public string TargetIp { get => _targetIp; set { _targetIp = value; Raise(nameof(TargetIp)); } }
    private string _targetIp = string.Empty;

    public bool Running { get => _running; set { _running = value; Raise(nameof(Running)); Raise(nameof(NotRunning)); } }
    private bool _running;
    public bool NotRunning => !_running;

    public string StatusText { get => _statusText; set { _statusText = value; Raise(nameof(StatusText)); } }
    private string _statusText = "待机";

    public bool StatusOk { get => _statusOk; set { _statusOk = value; Raise(nameof(StatusOk)); } }
    private bool _statusOk;

    public string WebUrl { get => _webUrl; set { _webUrl = value; Raise(nameof(WebUrl)); } }
    private string _webUrl = string.Empty;

    public string SessionDirectory { get => _sessionDirectory; set { _sessionDirectory = value; Raise(nameof(SessionDirectory)); } }
    private string _sessionDirectory = string.Empty;

    public string HotspotStatusText { get => _hotspotStatusText; set { _hotspotStatusText = value; Raise(nameof(HotspotStatusText)); } }
    private string _hotspotStatusText = "热点：未查询";

    public string UpRateLabel { get => _upRateLabel; set { _upRateLabel = value; Raise(nameof(UpRateLabel)); } }
    private string _upRateLabel = "0.0";
    public string DownRateLabel { get => _downRateLabel; set { _downRateLabel = value; Raise(nameof(DownRateLabel)); } }
    private string _downRateLabel = "0.0";
    public string EstablishedLabel { get => _establishedLabel; set { _establishedLabel = value; Raise(nameof(EstablishedLabel)); } }
    private string _establishedLabel = "0";
    public string KeysLabel { get => _keysLabel; set { _keysLabel = value; Raise(nameof(KeysLabel)); } }
    private string _keysLabel = "0";
    public string BoundLabel { get => _boundLabel; set { _boundLabel = value; Raise(nameof(BoundLabel)); } }
    private string _boundLabel = "0";
    public string CharactersLabel { get => _charactersLabel; set { _charactersLabel = value; Raise(nameof(CharactersLabel)); } }
    private string _charactersLabel = "0";
    public string PlainLabel { get => _plainLabel; set { _plainLabel = value; Raise(nameof(PlainLabel)); } }
    private string _plainLabel = "0 / 0";
    public string PendingLabel { get => _pendingLabel; set { _pendingLabel = value; Raise(nameof(PendingLabel)); } }
    private string _pendingLabel = "0";
    public string CoordRateLabel { get => _coordRateLabel; set { _coordRateLabel = value; Raise(nameof(CoordRateLabel)); } }
    private string _coordRateLabel = "0.0";
    public string LootLabel { get => _lootLabel; set { _lootLabel = value; Raise(nameof(LootLabel)); } }
    private string _lootLabel = "0";
    public string EventsLabel { get => _eventsLabel; set { _eventsLabel = value; Raise(nameof(EventsLabel)); } }
    private string _eventsLabel = "0";
    public string JournalLabel { get => _journalLabel; set { _journalLabel = value; Raise(nameof(JournalLabel)); } }
    private string _journalLabel = "0.0";
    public string TcpSummaryLabel { get => _tcpSummaryLabel; set { _tcpSummaryLabel = value; Raise(nameof(TcpSummaryLabel)); } }
    private string _tcpSummaryLabel = "TCP 中继：截获 0 | 回程 0 | 未映射(旧连接) 0 | 活动连接 0";

    public ObservableCollection<string> LogLines { get; } = [];

    public MainViewModel()
    {
        _timer = new(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await TickAsync();
        Log("欢迎使用 CC雷达。选择取数模式与设备后启动。");
        _ = RefreshLanAdaptersAsync();
        _ = RefreshHotspotAsync();
        _timer.Start();
    }

    private void Log(string message)
    {
        _dispatcher.BeginInvoke(() =>
        {
            LogLines.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
            while (LogLines.Count > 200) LogLines.RemoveAt(LogLines.Count - 1);
        });
    }

    public async Task RefreshLanAdaptersAsync()
    {
        try
        {
            var choices = await Task.Run(LanAdapterResolver.Enumerate);
            _dispatcher.BeginInvoke(() =>
            {
                LanAdapters.Clear();
                foreach (var choice in choices) LanAdapters.Add(choice);
                SelectedLanAdapter ??= choices.OrderByDescending(c => c.Adapter.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Ethernet).FirstOrDefault();
            });
        }
        catch (Exception ex) { Log($"枚举网卡失败：{ex.Message}"); }
    }

    public async Task RefreshLanNeighborsAsync()
    {
        var lan = SelectedLanAdapter;
        if (lan is null) return;
        try
        {
            var neighbors = await LanAdapterResolver.ListNeighborsAsync(lan.Address, lan.Mask);
            _dispatcher.BeginInvoke(() =>
            {
                LanNeighbors.Clear();
                foreach (var neighbor in neighbors.Where(n => !n.Address.Equals(lan.Gateway))) LanNeighbors.Add(neighbor);
                Log($"已刷新局域网邻居 {LanNeighbors.Count} 台。");
            });
        }
        catch (Exception ex) { Log($"刷新邻居失败：{ex.Message}"); }
    }

    public async Task RefreshHotspotAsync()
    {
        try
        {
            var status = await new WindowsTetheringController().GetStatusAsync();
            _dispatcher.BeginInvoke(() =>
            {
                HotspotStatusText = $"热点：{(status.IsRunning ? "运行中" : "未运行")}（{status.ProfileName}）" +
                    (status.IsRunning ? string.Empty : " — 启动热点模式前请先在 Windows 设置中开启移动热点");
            });
            if (!status.IsRunning) return;
            var devices = await HotspotClientDiscovery.DiscoverAsync();
            _dispatcher.BeginInvoke(() =>
            {
                HotspotDevices.Clear();
                foreach (var device in devices) HotspotDevices.Add(device);
            });
        }
        catch (Exception ex) { Log($"查询热点状态失败：{ex.Message}"); }
    }

    private static IPAddress? ParseIp(string text) =>
        IPAddress.TryParse(text.Trim(), out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? address : null;

    public async Task StartAsync()
    {
        if (Running) return;
        var target = ParseIp(TargetIp);
        if (target is null)
        {
            MessageBox.Show("请输入有效的目标设备 IPv4 地址（例如 192.168.6.100）。", "CC雷达", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var cache = Path.Combine(AppContext.BaseDirectory, "captures", $"live-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(cache);
            SessionDirectory = cache;
            var session = new RealtimeSession();
            if (LanMode)
            {
                var lan = SelectedLanAdapter ?? LanAdapterResolver.SelectDefault();
                if (!LanAdapterResolver.SameSubnet(target, lan.Address, lan.Mask))
                {
                    MessageBox.Show($"目标 {target} 与本机网卡 {lan.Address} 不在同一网段。", "CC雷达", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                await session.StartAsync(target, cache, RealtimeLinkMode.Lan, lan);
                Log($"局域网会话已启动：目标 {target}，网卡 {lan.Address}。请在游戏电脑上进入一局新的对局。");
            }
            else
            {
                await session.StartAsync(target, cache, RealtimeLinkMode.Hotspot);
                Log($"热点会话已启动：目标 {target}。请在设备上重启游戏。");
            }
            _live = session;
            Running = true;
            StatusOk = true;
            StatusText = "运行中";
            WebUrl = session.WebUrl ?? string.Empty;
            session.OpenBrowser();
        }
        catch (Exception ex)
        {
            StatusOk = false;
            StatusText = "启动失败";
            Log($"启动失败：{ex.Message}");
            MessageBox.Show($"启动失败：{ex.Message}", "CC雷达", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public async Task StopAsync()
    {
        var live = _live;
        if (live is null) return;
        _live = null;
        Running = false;
        try
        {
            await live.DisposeAsync();
            Log("会话已停止并保存。");
        }
        catch (Exception ex)
        {
            Log($"停止时出现错误：{ex.Message}");
        }
        StatusOk = false;
        StatusText = "已停止";
    }

    public void OpenWeb()
    {
        if (!string.IsNullOrWhiteSpace(WebUrl))
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(WebUrl) { UseShellExecute = true }); }
            catch (Exception ex) { Log($"打开网页失败：{ex.Message}"); }
        }
        else
        {
            MessageBox.Show("会话尚未启动，暂无网页雷达可打开。", "CC雷达", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private static long Num(JsonElement element, string key)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(key, out var value)) return 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
    }

    private async Task TickAsync()
    {
        var live = _live;
        if (live is null || !Running) return;
        try
        {
            var element = JsonSerializer.SerializeToElement(live.Snapshot);
            var up = Num(element, "udpUpPackets");
            var down = Num(element, "udpDownPackets");
            var dt = Math.Max(0.05, (DateTimeOffset.Now - _lastRate).TotalSeconds);
            UpRateLabel = ((up - _previousUp) / dt).ToString("F1");
            DownRateLabel = ((down - _previousDown) / dt).ToString("F1");
            _previousUp = up; _previousDown = down; _lastRate = DateTimeOffset.Now;
            TcpSummaryLabel = $"TCP 中继：截获 {Num(element, "redirectedUp"):N0} | 回程 {Num(element, "redirectedDown"):N0} | 未映射(旧连接) {Num(element, "unmappedTcp"):N0} | 活动连接 {Num(element, "activeTcp"):N0}";

            var established = Num(element, "tgcpEstablished");
            var keys = Num(element, "bankCandidates");
            var bound = Num(element, "udpBoundFlows");
            var plainUp = Num(element, "plainUp");
            var plainDown = Num(element, "plainDown");
            EstablishedLabel = established.ToString("N0");
            KeysLabel = keys.ToString("N0");
            BoundLabel = bound.ToString("N0");
            PlainLabel = $"{plainUp:N0} / {plainDown:N0}";
            PendingLabel = Num(element, "pendingPackets").ToString("N0");
            EventsLabel = Num(element, "events").ToString("N0");
            JournalLabel = (Num(element, "journalBytes") / 1048576d).ToString("F1");

            if (element.TryGetProperty("parser", out var parser) && parser.ValueKind == JsonValueKind.Object)
            {
                if (parser.TryGetProperty("counts", out var counts) && counts.ValueKind == JsonValueKind.Object)
                {
                    CharactersLabel = Num(counts, "characters").ToString("N0");
                    LootLabel = Num(counts, "loot").ToString("N0");
                }
                if (parser.TryGetProperty("rates", out var rates) && rates.ValueKind == JsonValueKind.Object
                    && rates.TryGetProperty("characters", out var rate) && rate.ValueKind == JsonValueKind.Number)
                {
                    CoordRateLabel = rate.GetDouble().ToString("F1");
                }
                if (parser.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(error.GetString()))
                {
                    Log($"解析后台错误：{error.GetString()}");
                }
            }

            if (keys > _oldKeys) Log($"发现 Key 候选（{keys}），正在验证 UDP…");
            if (established > _oldEstablished) Log($"TGCP 握手建立（{established}），密钥已入池。");
            if (bound > _oldBound) Log($"UDP 流已通过密钥验证（{bound}），开始输出明文。");
            if (plainUp + plainDown > 0 && _oldPlain == 0) Log("解密流水线已贯通，网页雷达开始显示实时数据。");
            _oldKeys = keys; _oldPlain = plainUp + plainDown; _oldEstablished = established; _oldBound = bound;

            if (!live.Running)
            {
                Log("采集链路已停止。");
                await StopAsync();
            }
        }
        catch
        {
            // 单次刷新失败（如网页服务瞬时不可达）不打断轮询。
        }
    }
}
