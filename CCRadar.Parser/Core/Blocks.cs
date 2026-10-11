using CCRadar.Parser.Codec;

namespace CCRadar.Parser.Core;

/// <summary>内容块（UActorChannel::ReadContentBlockPayload 切出的单元）。</summary>
public sealed class ContentBlock
{
    /// <summary>块头起始位。</summary>
    public int Start { get; init; }
    /// <summary>1b bHasRepLayout。</summary>
    public int HasRep { get; init; }
    /// <summary>1b bIsActor（为真时块立即结束，无载荷）。</summary>
    public int IsActor { get; init; }
    /// <summary>packed GUID SubObject（非 actor 块才有）。</summary>
    public ulong? Sub { get; init; }
    /// <summary>1b bStablyNamed（非 actor 块才有；为真块立即结束）。</summary>
    public int? Stably { get; init; }
    /// <summary>packed GUID SubObjectClass。</summary>
    public ulong? Cls { get; init; }
    /// <summary>ClassNetGUID 无效(0) → bObjectDeleted。</summary>
    public bool Deleted { get; init; }
    /// <summary>packed NumPayloadBits（deleted 块为 0）。</summary>
    public long NumBits { get; init; }
    /// <summary>载荷起始位（绝对位）。</summary>
    public int PayloadStart { get; init; }
    /// <summary>载荷结束位（绝对位，不含）。</summary>
    public int PayloadEnd { get; init; }
}

/// <summary>
/// blocks.py 的 C# 等价实现 —— 修正版内容块铺砌解析（含 upstream 的"子对象删除"分支）。
///
/// UActorChannel::ReadContentBlockPayload:
///     RepObj = ReadContentBlockHeader(Bunch, bObjectDeleted, bHasRepLayout)
///         1b bHasRepLayout
///         1b bIsActor ; 若 bIsActor 立即返回
///         packed GUID SubObject
///         1b bStablyNamed ; 若为真立即返回
///         packed GUID SubObjectClass
///         若 ClassNetGUID 无效(0) → bObjectDeleted = true, 直接返回
///     if (bObjectDeleted) { OutPayload.SetData(Bunch, 0); return 0; }  ← 不读 NumPayloadBits
///     packed NumPayloadBits            (可为 0)
///     payload = NumPayloadBits 位
/// </summary>
public static class Blocks
{
    /// <summary>
    /// 切块（Python: tile）。partial_ok=true 时块头校验失败/位流错/尾部对齐残留
    /// 不再丢弃整批，而是返回已成功切出的前缀块——0830 装备链路修复：
    /// reasm 重组的超 bunch（18k~37k bits）尾部有对齐残留，旧逻辑整批 return None
    /// 导致全员装备组件块丢失。调用方需容忍"块列表不完整"（已切出的块本身有效）。
    /// </summary>
    public static List<ContentBlock>? Tile(
        byte[] payload, int pbits, int start = 0,
        int minBlocks = 1, int maxBlocks = 4096, bool partialOk = false)
    {
        var reader = new BitReader(payload, pbits) { Position = start };
        var outList = new List<ContentBlock>();

        List<ContentBlock>? Fail() => partialOk && outList.Count > 0 ? outList : null;

        try
        {
            while (pbits - reader.Position >= 8)
            {
                var s = reader.Position;
                var hasRep = reader.ReadBit();
                var isActor = reader.ReadBit();
                uint? sub = null;
                uint? cls = null;
                int? stably = null;
                var deleted = false;
                if (isActor == 0)
                {
                    (sub, _) = EngineBunchCodec.ReadDfmPackedInt(reader);
                    stably = reader.ReadBit();
                    if (stably == 0)
                    {
                        (cls, _) = EngineBunchCodec.ReadDfmPackedInt(reader);
                        if (cls == 0) deleted = true;
                    }
                }
                if (deleted)
                {
                    outList.Add(new ContentBlock
                    {
                        Start = s, HasRep = hasRep, IsActor = isActor,
                        Sub = sub, Stably = stably, Cls = cls, Deleted = true,
                        NumBits = 0, PayloadStart = reader.Position, PayloadEnd = reader.Position,
                    });
                    if (outList.Count > maxBlocks) return null;
                    continue;
                }
                var (nRaw, _) = EngineBunchCodec.ReadDfmPackedInt(reader);
                long n = nRaw;
                if (reader.Position + n > pbits)
                {
                    if (partialOk)
                    {
                        // 重同步：当前块头非法（错位/残留），回退到本块起点后移 1 bit
                        // 重新找块头。伪块由解码端 handle 校验天然挡住（status 非 exact
                        // 的 ev 不采），位游标单调前进不死循环。
                        reader.Position = s + 1;
                        continue;
                    }
                    return Fail();
                }
                outList.Add(new ContentBlock
                {
                    Start = s, HasRep = hasRep, IsActor = isActor,
                    Sub = sub, Stably = stably, Cls = cls, Deleted = false,
                    NumBits = n, PayloadStart = reader.Position, PayloadEnd = reader.Position + (int)n,
                });
                reader.Position += (int)n;
                if (outList.Count > maxBlocks) return null;
            }
        }
        catch (BitError)
        {
            if (!partialOk) return Fail();
            return outList.Count > 0 ? outList : null;
        }
        if (outList.Count < minBlocks) return null;
        if (pbits - reader.Position >= 8) return Fail();
        return outList;
    }
}
