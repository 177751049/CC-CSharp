using System.Net;

namespace CCRadar.Core.Models;

public sealed record CaptureRequest(IPAddress TargetAddress, string OutputDirectory, int MaxFileSizeMiB, bool UdpOnly = false);

public sealed record CaptureSnapshot(
    bool IsRunning,
    IPAddress? TargetAddress,
    DateTimeOffset? StartedAt,
    string? OutputDirectory,
    string? Detail);

public sealed record CaptureResult(
    bool Succeeded,
    string Message,
    string? PcapPath,
    string? SessionPcapPath,
    string? ManifestPath,
    long InputPackets,
    long SessionPackets,
    long SessionBytes,
    long DownstreamPackets,
    long DownstreamBytes,
    string? FullIpPcapPath = null,
    long FullIpPackets = 0,
    long TcpPackets = 0,
    long OtherIpPackets = 0,
    long TruncatedPackets = 0);

public sealed record TetheringStatus(
    bool IsAvailable,
    bool IsRunning,
    string ProfileName,
    string Detail);

public sealed record ProbeReport(
    bool IsAdministrator,
    bool HasPktMon,
    bool HasWirelessAdapter,
    bool HasWifiDirectAdapter,
    IReadOnlyList<string> ActiveAdapters);
