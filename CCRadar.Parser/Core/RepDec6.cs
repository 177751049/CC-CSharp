using CCRadar.Parser.Codec;

namespace CCRadar.Parser.Core;

/// <summary>
/// repdec6.py 的 C# 等价实现 —— engine-exact FRepLayout::ReceiveProperties（FINAL framing）。
///
/// 恢复自二进制：
///   sub_EAE4550 @0xeae469c : bDoChecksum = Bunch.ReadBit()      ← 一个前导位
///   handle      = FArchive vtable+128 = SerializeIntPacked (LSB continuation)
///   if bDoChecksum: 4 extra bytes (uint32 checksum) after every handle
///   ArrayNum    = Serialize(&amp;uint16, 2)
///   array inner handles are FLATTENED across elements (shared CurrentHandle)
///   receiver walks every cmd incrementing CurrentHandle, consuming only the match
///   stream ends with handle 0
/// </summary>
public static class RepDec6
{
    /// <summary>类名 → {cmd index: prop meta} 缓存（Python: _PM_CACHE）。</summary>
    private static readonly Dictionary<string, Dictionary<int, Dictionary<string, object?>?>> PmCache = new();

    /// <summary>
    /// 类名 → {cmd index: prop meta}（prop_meta_R36.json，懒加载缓存；Python: _cls_meta）。
    /// </summary>
    public static Dictionary<int, Dictionary<string, object?>?> ClsMeta(string? clsName)
    {
        if (clsName is null) return [];
        if (PmCache.TryGetValue(clsName, out var cached)) return cached;
        var meta = new Dictionary<int, Dictionary<string, object?>?>();
        try
        {
            if (RepDec4.LoadPropmeta().TryGetValue(clsName, out var entries))
            {
                foreach (var it in entries)
                {
                    if (it.TryGetValue("index", out var idxObj) && idxObj is long idx)
                    {
                        meta[(int)idx] = it;
                    }
                }
            }
        }
        catch
        {
            meta = [];
        }
        PmCache[clsName] = meta;
        return meta;
    }

    /// <summary>handle 读取（Python: _rh）：packed handle + 可选 32 位校验和。</summary>
    private static long ReadHandle(BitReader r, RepDec4.St st)
    {
        var h = EngineBunchCodec.ReadDfmPackedInt(r).Value;
        if (st.Cs) r.ReadBits(32);
        return h;
    }

    /// <summary>
    /// 属性遍历（Python: repdec6._props_r）：与 repdec4 的差异——
    /// 数组数量上限 4096、产生 array_begin/array_end 事件、值读取器可注入。
    /// </summary>
    private static void PropsR(
        BitReader r, List<RepCmd> cmds, int i0, int i1, RepDec4.St st, long[] cur,
        List<RepEvent> ev, List<(long Handle, int Element)> path,
        Dictionary<int, Dictionary<string, object?>?> meta, int depth,
        RepDec4.RepValueReader? valueReader, bool includeArrays)
    {
        if (depth > 8) throw new BitError("depth");
        var i = i0;
        if (i >= i1) return;
        while (true)
        {
            cur[0]++;
            var c = cmds[i];
            if (st.H != cur[0])
            {
                if (c.Type == 0)
                {
                    i = (c.EndCmd ?? (i + 2)) - 1;
                }
            }
            else
            {
                if (c.Type != 0)
                {
                    var v = valueReader is not null
                        ? valueReader(r, c.Type, meta.TryGetValue(c.Index, out var m) ? m : null)
                        : RepDec4.ReadVal(r, c.Type, meta.TryGetValue(c.Index, out var m2) ? m2 : null);
                    ev.Add(new RepEvent
                    {
                        Path = [.. path], Handle = cur[0], Type = c.Type,
                        TName = c.Type < RepDec4.TypeNames.Length ? RepDec4.TypeNames[c.Type] : null,
                        Value = v, CmdIndex = c.Index, ParentIndex = c.ParentIndex,
                    });
                    st.H = ReadHandle(r, st);
                }
                else
                {
                    var n = r.ReadUInt16();
                    if (n > 4096) throw new BitError($"arraynum {n}");
                    var ae = new RepEvent
                    {
                        Path = [.. path], Handle = cur[0], Type = 0,
                        TName = "DynArray", Value = (long)n,
                        CmdIndex = c.Index, ParentIndex = c.ParentIndex,
                    };
                    if (includeArrays)
                    {
                        ev.Add(new RepEvent
                        {
                            Path = ae.Path, Handle = ae.Handle, Type = ae.Type, TName = ae.TName,
                            Value = ae.Value, CmdIndex = ae.CmdIndex, ParentIndex = ae.ParentIndex,
                            Kind = "array_begin",
                        });
                    }
                    st.H = ReadHandle(r, st);
                    if (st.H != 0)
                    {
                        var end = (c.EndCmd ?? (i + 2)) - 1;
                        var icur = new long[1];
                        for (var e = 0; e < n; e++)
                        {
                            var innerPath = new List<(long Handle, int Element)>(path) { (cur[0], e) };
                            PropsR(r, cmds, i + 1, end, st, icur, ev, innerPath, meta, depth + 1,
                                valueReader, includeArrays);
                        }
                        if (st.H != 0) throw new BitError($"array unterminated h{st.H}");
                    }
                    if (includeArrays)
                    {
                        ev.Add(new RepEvent
                        {
                            Path = ae.Path, Handle = ae.Handle, Type = ae.Type, TName = ae.TName,
                            Value = ae.Value, CmdIndex = ae.CmdIndex, ParentIndex = ae.ParentIndex,
                            Kind = "array_end",
                        });
                    }
                    i = (c.EndCmd ?? (i + 2)) - 1;
                    st.H = ReadHandle(r, st);
                }
            }
            i++;
            if (i >= i1) return;
        }
    }

    /// <summary>
    /// 解码一个属性块（Python: repdec6.decode_block）。
    /// meta 缺省时按 catalog 类名自动挂 prop_meta_R36（0829 装备链路修复）。
    /// </summary>
    public static (List<RepEvent> Events, int Position, string Status) DecodeBlock(
        byte[] payload, int payloadStart, int payloadEnd,
        RepCatalog catalog, Dictionary<int, Dictionary<string, object?>?>? meta = null,
        bool includeArrays = false, RepDec4.RepValueReader? valueReader = null)
    {
        var cmds = RepDec4.CmdList(catalog);
        if (meta is null || meta.Count == 0)
        {
            // 0829 装备链路修复：历史调用全部传 {}，prop_meta 从未接线——
            // StructProperty（GPFastWeaponBPClassPath 等）一律 Opaque，CurrActive
            // WeapInfoArr 的当前武器信息被整块丢弃。无调用方 meta 时按类名自动挂接。
            meta = ClsMeta(catalog.ClassName);
        }
        var r = new BitReader(payload, payloadEnd) { Position = payloadStart };
        var ev = new List<RepEvent>();
        try
        {
            var cs = r.ReadBit() != 0;
            var st = new RepDec4.St(0, cs);
            st.H = ReadHandle(r, st);
            PropsR(r, cmds, 0, cmds.Count, st, [0], ev, [], meta, 0,
                valueReader, includeArrays);
            if (st.H != 0) return (ev, r.Position, "leftover");
        }
        catch (RepOpaque)
        {
            return (ev, r.Position, "opaque");
        }
        catch (BitError)
        {
            return (ev, r.Position, "error");
        }
        if (r.Position == payloadEnd) return (ev, r.Position, "exact");
        return (ev, r.Position, r.Position < payloadEnd ? "short" : "over");
    }
}
