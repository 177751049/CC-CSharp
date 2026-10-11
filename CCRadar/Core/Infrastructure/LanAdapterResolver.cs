using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace CCRadar.Core.Infrastructure;

internal sealed record LanAdapterChoice(
    NetworkInterface Adapter,
    IPAddress Address,
    IPAddress Gateway,
    IPAddress Mask,
    int InterfaceIndex)
{
    public string DisplayName => $"{Adapter.Name}  {Address}  网关 {Gateway}";
}

internal sealed record LanNeighbor(IPAddress Address, PhysicalAddress Mac, string State);

// LAN 模式取数端：B 机的普通网卡（连接路由器/交换机），替代热点虚拟网卡。
internal static class LanAdapterResolver
{
    public static List<LanAdapterChoice> Enumerate()
    {
        var choices = new List<LanAdapterChoice>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up) continue;
            if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var properties = adapter.GetIPProperties();
            var gateway = properties.GatewayAddresses
                .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
            var unicast = properties.UnicastAddresses.FirstOrDefault(u =>
                u.Address.AddressFamily == AddressFamily.InterNetwork &&
                IsPrivateIpv4(u.Address) &&
                u.IPv4Mask is { } mask &&
                mask.GetAddressBytes().Any(b => b != 0));
            if (gateway is null || unicast is null) continue;
            choices.Add(new(adapter, unicast.Address, gateway, unicast.IPv4Mask!, properties.GetIPv4Properties().Index));
        }

        // 热点模式的 Wi-Fi Direct 虚拟网卡不属于 LAN 取数端，排除。
        return choices.Where(c => !c.Adapter.Description.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public static LanAdapterChoice SelectDefault()
    {
        var selected = Enumerate()
            .OrderByDescending(c => c.Adapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
            .FirstOrDefault();
        if (selected.Adapter is null)
        {
            throw new InvalidOperationException("未找到具有 IPv4 地址和默认网关的局域网网卡，无法进入局域网模式。");
        }
        return selected;
    }

    public static bool SameSubnet(IPAddress a, IPAddress b, IPAddress mask)
    {
        var ab = a.GetAddressBytes();
        var bb = b.GetAddressBytes();
        var mb = mask.GetAddressBytes();
        if (ab.Length != 4 || bb.Length != 4 || mb.Length != 4) return false;
        for (var i = 0; i < 4; i++)
        {
            if ((ab[i] & mb[i]) != (bb[i] & mb[i])) return false;
        }
        return true;
    }

    // 解析 arp -a：返回与指定网卡同网段的已知邻居（含状态），供设备选择。
    public static async Task<IReadOnlyList<LanNeighbor>> ListNeighborsAsync(IPAddress local, IPAddress mask, CancellationToken cancellation = default)
    {
        var result = await ProcessRunner.RunAsync("arp.exe", ["-a"], cancellation);
        if (!result.Succeeded) return Array.Empty<LanNeighbor>();
        var neighbors = new List<LanNeighbor>();
        IPAddress? section = null;
        foreach (var raw in result.StandardOutput.Split('\r', '\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var interfaceMatch = Regex.Match(line, @"(\d+\.\d+\.\d+\.\d+)\s+---");
            if (interfaceMatch.Success)
            {
                section = IPAddress.TryParse(interfaceMatch.Groups[1].Value, out var parsed) ? parsed : null;
                continue;
            }
            var entry = Regex.Match(line, @"^(\d+\.\d+\.\d+\.\d+)\s+([0-9a-fA-F]{2}(?:-[0-9a-fA-F]{2}){5})\s+(\S+)");
            if (!entry.Success) continue;
            if (section is not { } sectionAddress || !SameSubnet(sectionAddress, local, mask)) continue;            var address = IPAddress.Parse(entry.Groups[1].Value);
            if (address.Equals(local)) continue;
            neighbors.Add(new(address, PhysicalAddress.Parse(entry.Groups[2].Value.Replace("-", string.Empty)), entry.Groups[3].Value));
        }
        return neighbors
            .GroupBy(n => n.Address)
            .Select(g => g.First())
            .OrderBy(n => n.Address.GetAddressBytes(), ByteArrayComparer.Instance)
            .ToList();
    }

    private static bool IsPrivateIpv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               bytes[0] == 192 && bytes[1] == 168 ||
               bytes[0] == 172 && bytes[1] is >= 16 and <= 31;
    }

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();
        public int Compare(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
            {
                if (x[i] != y[i]) return x[i].CompareTo(y[i]);
            }
            return x.Length.CompareTo(y.Length);
        }
    }
}
