namespace CCRadar.Parser.Core;

/// <summary>
/// 业务层统一 bunch 视图 —— Python 业务代码（container_state/playerstate_protocol/
/// liveview 等）消费的 bunch 对象字段集合的 C# 等价物。
///
/// Python 侧这些字段来自 flowlive 加载的重组 bunch；C# 侧由宿主（主程序实时链路
/// 或离线回放）从 EngineStream.ReassembledBunch + 包序号组装，保证业务层移植
/// 与 Python 逻辑同构。
/// </summary>
public sealed class BunchView
{
    /// <summary>open bunch 标志（新通道/新 Actor 流开始）。</summary>
    public bool Open { get; init; }
    /// <summary>close bunch 标志（通道关闭）。</summary>
    public bool Close { get; init; }
    /// <summary>关闭原因（0=干净关闭，2/4=丢弃等）。</summary>
    public int? CloseReason { get; init; }
    /// <summary>通道号（Python: ch）。</summary>
    public int Ch { get; init; }
    /// <summary>bunch 载荷字节。</summary>
    public byte[] Payload { get; init; } = Array.Empty<byte>();
    /// <summary>载荷有效位数（Python: pbits）。</summary>
    public int PBits { get; init; }
    /// <summary>bHasPackageMapExports。</summary>
    public bool Hx { get; init; }
    /// <summary>bHasMustBeMappedGUIDs。</summary>
    public bool Hm { get; init; }
    /// <summary>partial bunch 的 fragment 边界 [(start_bit, hx, hm)]。</summary>
    public List<(int Start, bool Hx, bool Hm)>? Frags { get; init; }
    /// <summary>包序号（Python: seq，事件排序/updated_seq 用）。</summary>
    public long Seq { get; init; }
}
