namespace CCRadar.Core.Infrastructure;

/// <summary>
/// 网页端入站放行规则。绑定所有网卡后，局域网内其它设备（手机/平板/另一台电脑）
/// 访问本机网页端口需要 Windows 防火墙放行，否则表现为"能绑定但外部连不上"。
/// 规则按固定名字幂等重建（端口变化时自动纠正）；尽力而为，失败只回报文本、不阻断主流程。
/// </summary>
internal static class WebFirewallRule
{
    public const string RuleName = "CC雷达 网页端";

    /// <summary>确保存在放行 TCP <paramref name="port"/> 的入站规则。成功返回 null，失败返回原因。</summary>
    public static async Task<string?> EnsureAsync(int port, CancellationToken cancellationToken = default)
    {
        try
        {
            // 先删后加：名字固定，端口变化也能自动纠正（规则不存在时删除报错无害）。
            await ProcessRunner.RunAsync("netsh",
                ["advfirewall", "firewall", "delete", "rule", $"name={RuleName}"], cancellationToken);
            var add = await ProcessRunner.RunAsync("netsh",
                ["advfirewall", "firewall", "add", "rule", $"name={RuleName}",
                 "dir=in", "action=allow", "protocol=TCP", $"localport={port}", "profile=any"],
                cancellationToken);
            if (add.Succeeded) return null;
            var detail = string.IsNullOrWhiteSpace(add.StandardError) ? add.StandardOutput : add.StandardError;
            return detail.Trim();
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
