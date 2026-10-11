using CCRadar.Parser.Codec;

namespace CCRadar.Parser.Core;

/// <summary>
/// container_state.py 的 C# 等价实现 —— 普通容器出生位置跟踪。
/// 未知内容保持显式未知；只接受导出的容器类名与解码出的 actor 位置。
/// </summary>
public sealed class ContainerTracker
{
    private readonly Dictionary<ulong, string> _paths;
    private readonly Dictionary<int, ulong> _channels = new();
    private readonly Dictionary<ulong, Dictionary<string, object?>> _active = new();

    /// <summary>当前活跃容器（Python: self.active；键为 actor GUID）。</summary>
    public IReadOnlyDictionary<ulong, Dictionary<string, object?>> Active => _active;

    public ContainerTracker(Dictionary<ulong, string> paths)
    {
        _paths = paths;
    }

    /// <summary>消费一个 bunch（Python: consume）。解析失败静默忽略（与 Python 一致）。</summary>
    public void Consume(BunchView x)
    {
        try
        {
            if (x.Open && x.Close)
            {
                // open+close：干净关闭且载荷只剩 actor GUID → 删除该容器。
                if (x.CloseReason == 0)
                {
                    var (_, start) = ReplicationHeader.ExportsPrefix(
                        x.Payload, x.PBits, x.Hx, x.Hm, x.Frags);
                    var r = new BitReader(x.Payload, x.PBits) { Position = start };
                    var actor = EngineBunchCodec.ReadDfmPackedInt(r).Value;
                    if (r.Position == x.PBits)
                    {
                        _active.Remove(actor);
                    }
                }
                return;
            }
            if (x.Open)
            {
                _channels.Remove(x.Ch);
                var (info, _) = Sna.ParsePrefix(
                    x.Payload, x.PBits, x.Hx, x.Hm,
                    quantizeBit: true, frags: x.Frags);
                var arch = info.TryGetValue("arch", out var archObj) ? archObj : null;
                var path = arch is ulong g && _paths.TryGetValue(g, out var p) ? p : "";
                // 类名尾段（Python: path.rsplit('.', 1)[-1].rsplit('/', 1)[-1]）。
                var tail = path.Split('/').Last().Split('.').Last();
                if (!(tail.StartsWith("BP_InteractorContainer_", StringComparison.Ordinal)
                    || tail.StartsWith("BP_LootContainer_", StringComparison.Ordinal)))
                {
                    return;
                }
                var actor = info.TryGetValue("actor", out var actorObj) ? actorObj : null;
                var loc = info.TryGetValue("loc", out var locObj) ? locObj as double[] : null;
                if (actor is not ulong a || a == 0 || loc is null || loc.Length != 3
                    || loc.Any(v => !double.IsFinite(v)))
                {
                    return;
                }
                _channels[x.Ch] = a;
                _active[a] = new Dictionary<string, object?>
                {
                    ["key"] = "container-" + a,
                    ["eid"] = a,
                    ["ch"] = x.Ch,
                    ["kind"] = "container",
                    ["name"] = "搜索容器",
                    ["world"] = loc.ToList(),
                    ["xyz"] = loc.ToList(),
                    ["yaw"] = null,
                    ["class_path"] = path,
                    ["contents"] = new List<object?>(),
                    ["contents_known"] = false,
                    ["status"] = "内容未知",
                    ["decode_status"] = "spawn_location_only",
                    ["updated_seq"] = x.Seq,
                };
            }
            else if (x.Close)
            {
                var actor = _channels.TryGetValue(x.Ch, out var ch) ? ch : (ulong?)null;
                _channels.Remove(x.Ch);
                if (x.CloseReason is 0 or 2 or 4)
                {
                    if (actor is not null) _active.Remove(actor.Value);
                }
                else if (actor is not null && _active.TryGetValue(actor.Value, out var entry))
                {
                    entry["channel_state"] = "dormant";
                }
            }
        }
        catch (SnaBad)
        {
            return;
        }
        catch (BitError)
        {
            return;
        }
        catch (ArgumentException)
        {
            return;
        }
        catch (IndexOutOfRangeException)
        {
            return;
        }
    }
}
