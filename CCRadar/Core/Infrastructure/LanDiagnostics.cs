using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CCRadar.Core.Infrastructure;

// --check-lan-prerequisites：局域网模式开跑前的环境自检，逐项给出结论与修复建议。
internal static class LanDiagnostics
{
    public static async Task RunAsync(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== 局域网模式环境自检 ===");
        Console.WriteLine($"管理员权限: {(IsAdministrator() ? "是" : "否（ARP 欺骗与 IP 转发都需要管理员权限）")}");

        var npcapOk = ArpSpoofService.IsNpcapAvailable(out var npcapPath, out var npcapError);
        Console.WriteLine($"Npcap: {(npcapOk ? $"可用（{npcapPath}）" : $"缺失（{npcapError}）")}");
        if (!npcapOk)
        {
            Console.WriteLine("  修复：从 https://npcap.com 下载安装，勾选 WinPcap compatible mode。");
        }
        else
        {
            var devices = NpcapNative.ListDevices(out var listError);
            Console.WriteLine($"Npcap 设备: {devices.Count} 个");
            foreach (var device in devices)
            {
                Console.WriteLine($"  - {device.Name}{(device.Description is null ? string.Empty : $"  ({device.Description})")}");
            }
            if (devices.Count == 0 && !string.IsNullOrEmpty(listError))
            {
                Console.WriteLine($"  枚举失败：{listError.Trim()}（通常表示 Npcap 驱动未启动或权限不足，请以管理员重试）");
            }
            else if (devices.Count == 0)
            {
                Console.WriteLine("  未枚举到任何设备：请重装 Npcap 并保持默认选项（含 WinPcap compatible mode）。");
            }
        }

        var choices = LanAdapterResolver.Enumerate();
        Console.WriteLine($"可用取数网卡: {choices.Count} 张");
        foreach (var choice in choices)
        {
            Console.WriteLine($"  - {choice.Adapter.Name}  {choice.Address}  网关 {choice.Gateway}  ifIndex {choice.InterfaceIndex}  ({choice.Adapter.NetworkInterfaceType})");
            var forwarding = await QueryForwardingTextAsync(choice.InterfaceIndex);
            Console.WriteLine($"      IP 转发当前状态: {forwarding ?? "未知"}");
        }
        if (choices.Count == 0)
        {
            Console.WriteLine("  修复：确认本机网卡已连接局域网并获取到私有网段 IPv4 与默认网关。");
        }

        if (args.Length > 0 && IPAddress.TryParse(args[0], out var target))
        {
            Console.WriteLine($"目标游戏电脑: {target}");
            var lan = choices.FirstOrDefault(c => LanAdapterResolver.SameSubnet(target, c.Address, c.Mask));
            if (lan is null)
            {
                Console.WriteLine("  [失败] 目标与本机任何网卡都不同网段。");
                return;
            }
            Console.WriteLine($"  网段校验: 通过（与 {lan.Address} 同网段）");
            Console.WriteLine("  连通性测试（ping 4 次）:");
            using var ping = new Ping();
            var replies = 0;
            for (var i = 0; i < 4; i++)
            {
                try
                {
                    var reply = await ping.SendPingAsync(target, 1000);
                    Console.WriteLine($"    #{i + 1}: {reply.Status}  {(reply.Status == IPStatus.Success ? reply.RoundtripTime + "ms" : string.Empty)}");
                    if (reply.Status == IPStatus.Success) replies++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    #{i + 1}: 异常 {ex.Message}");
                }
                await Task.Delay(200);
            }
            Console.WriteLine(replies > 0
                ? "  连通性: 通过。可进入局域网模式。"
                : "  连通性: 失败。请确认游戏电脑在线、未开严格防火墙（ICMP 被拦不影响解析，但 ARP 必须可达）。");
            var neighbors = await LanAdapterResolver.ListNeighborsAsync(lan.Address, lan.Mask);
            var known = neighbors.FirstOrDefault(n => n.Address.Equals(target));
            Console.WriteLine(known is null
                ? "  ARP 邻居表: 暂无目标记录（首次运行会自动解析）。"
                : $"  ARP 邻居表: {known.Address} → {FormatMac(known.Mac)}（{known.State}）");
        }
        else if (args.Length > 0)
        {
            Console.WriteLine($"忽略无效的目标地址参数: {args[0]}");
        }

        Console.WriteLine("=== 自检完成 ===");
        Console.WriteLine("提示：局域网模式原理是本机对目标电脑做 ARP 改道，停止时自动恢复；仅建议在自有网络环境使用。");
    }

    private static async Task<string?> QueryForwardingTextAsync(int index)
    {
        var result = await ProcessRunner.RunAsync("powershell.exe",
        [
            "-NoProfile", "-NonInteractive", "-Command",
            $"(Get-NetIPInterface -InterfaceIndex {index} -AddressFamily IPv4).Forwarding"
        ]);
        return result.Succeeded ? result.StandardOutput.Trim() : null;
    }

    private static bool IsAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static string FormatMac(PhysicalAddress mac) => string.Join("-", mac.GetAddressBytes().Select(b => b.ToString("X2")));
}
