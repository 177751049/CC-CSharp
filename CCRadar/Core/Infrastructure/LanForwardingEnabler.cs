using CCRadar.Core.Models;

namespace CCRadar.Core.Infrastructure;

// LAN 模式的 UDP 转发：热点模式依赖 ICS NAT 转发 UDP，局域网模式由本机接口转发接管。
// 仅打开目标网卡的 Forwarding 开关，停止时恢复原值；不动全局 IPEnableRouter。
internal sealed class LanForwardingEnabler : IAsyncDisposable
{
    private readonly int _interfaceIndex;
    private readonly Action<string, object>? _emit;
    private bool? _original;
    private bool _restored;

    public LanForwardingEnabler(int interfaceIndex, Action<string, object>? emit = null)
    {
        if (interfaceIndex <= 0) throw new ArgumentOutOfRangeException(nameof(interfaceIndex));
        _interfaceIndex = interfaceIndex;
        _emit = emit;
    }

    public async Task StartAsync()
    {
        _original = await QueryForwardingAsync(_interfaceIndex);
        await SetForwardingAsync(_interfaceIndex, true);
        _emit?.Invoke("lan_forwarding_enabled", new { interfaceIndex = _interfaceIndex, wasEnabled = _original });
    }

    private static async Task<bool?> QueryForwardingAsync(int index)
    {
        var result = await ProcessRunner.RunAsync("powershell.exe",
        [
            "-NoProfile", "-NonInteractive", "-Command",
            $"(Get-NetIPInterface -InterfaceIndex {index} -AddressFamily IPv4).Forwarding"
        ]);
        if (!result.Succeeded) return null;
        var text = result.StandardOutput.Trim();
        return text.Equals("Enabled", StringComparison.OrdinalIgnoreCase)
            ? true
            : text.Equals("Disabled", StringComparison.OrdinalIgnoreCase) ? false : null;
    }

    private static async Task SetForwardingAsync(int index, bool enabled)
    {
        var value = enabled ? "Enabled" : "Disabled";
        var result = await ProcessRunner.RunAsync("powershell.exe",
        [
            "-NoProfile", "-NonInteractive", "-Command",
            $"Set-NetIPInterface -InterfaceIndex {index} -AddressFamily IPv4 -Forwarding {value}"
        ]);
        if (!result.Succeeded)
        {
            throw new IOException($"设置网卡 {index} 的 IP 转发失败：{result.StandardError}{result.StandardOutput}".Trim());
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_restored) return;
        _restored = true;
        if (_original is { } original)
        {
            try { await SetForwardingAsync(_interfaceIndex, original); }
            catch { /* 恢复失败不阻断会话收尾；原值已记录在事件流。 */ }
        }
        _emit?.Invoke("lan_forwarding_restored", new { interfaceIndex = _interfaceIndex, value = _original });
    }
}
