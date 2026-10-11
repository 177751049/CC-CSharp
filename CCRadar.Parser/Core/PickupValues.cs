using CCRadar.Parser.Codec;

namespace CCRadar.Parser.Core;

/// <summary>
/// 拾取/死亡盒/物品值读取器集合——对应 Python：
/// pickup_state.pickup_value、death_box_protocol.text_value / box_value。
/// </summary>
public static class PickupValues
{
    /// <summary>整数的二进制位长（Python: int.bit_length）。</summary>
    private static int BitLength(long v)
    {
        var n = 0;
        while (v > 0) { n++; v >>= 1; }
        return n;
    }

    /// <summary>
    /// 拾取物属性值读取（Python: pickup_state.pickup_value）：
    /// SourceType 枚举（3 位，MAX=4）、通用 enum_max、InventoryItemInfo 原生正文，
    /// 其余回退通用 read_val。
    /// </summary>
    public static object? PickupValue(BitReader r, int t, Dictionary<string, object?>? meta)
    {
        if (meta is not null)
        {
            if (meta.TryGetValue("name", out var nameObj) && nameObj?.ToString() == "SourceType")
            {
                var value = (long)r.ReadBits(3);   // FEnumProperty EPickupSourceType::MAX=4
                if (value >= 4) throw new BitError("pickup source enum");
                return value;
            }
            if (meta.TryGetValue("enum_max", out var emObj) && emObj is long em && em != 0)
            {
                return r.ReadSerializeInt((int)em);
            }
            if (meta.TryGetValue("struct", out var stObj) && stObj?.ToString() == "InventoryItemInfo")
            {
                return InventoryNative.InventoryItem(r);
            }
        }
        return RepDec4.ReadVal(r, t, meta);
    }

    /// <summary>
    /// FText 线格式（Python: death_box_protocol.text_value）：
    /// u32 flags + u8 history；history 11 = 表驱动 FName + key，
    /// 0 = namespace/key/source 三串，255 = 可选源串，其他 → Opaque。
    /// </summary>
    public static Dictionary<string, object?> TextValue(BitReader r)
    {
        var flags = r.ReadUInt32();
        var history = r.ReadUInt8();
        var output = new Dictionary<string, object?>
        {
            ["flags"] = flags,
            ["history"] = (long)history,
        };
        switch (history)
        {
            case 11:
                output["table"] = EngineBunchCodec.ReadSerializedFName(r).AsDictionary();
                output["key"] = r.ReadFString();
                break;
            case 0:
                output["namespace"] = r.ReadFString();
                output["key"] = r.ReadFString();
                output["source"] = r.ReadFString();
                break;
            case 255:
                var present = r.ReadUInt32();
                if (present is not (0 or 1)) throw new BitError("FText invariant presence");
                output["source"] = present != 0 ? r.ReadFString() : "";
                break;
            default:
                throw new RepOpaque($"FText history {history}");
        }
        return output;
    }

    /// <summary>
    /// 死亡盒/通用属性值读取（Python: death_box_protocol.box_value）。
    /// </summary>
    public static object? BoxValue(BitReader r, int kind, Dictionary<string, object?>? meta)
    {
        meta ??= new Dictionary<string, object?>();
        meta.TryGetValue("struct", out var structObj);
        meta.TryGetValue("prop_class", out var propClassObj);
        var structName = structObj?.ToString();
        var propClass = propClassObj?.ToString();

        if (structName == "LootBoxRepData")
        {
            return InventoryNative.LootBoxData(r);
        }
        if (propClass == "EnumProperty" && meta.TryGetValue("enum_max", out var enumMaxObj)
            && enumMaxObj is long enumMax)
        {
            return (long)r.ReadBits(BitLength(enumMax));
        }
        if (propClass == "TextProperty")
        {
            return TextValue(r);
        }
        if (structName == "GameplayTagContainer")
        {
            // 首位为真 → 空容器；否则 6 位数量 + N 个序列化 FName。
            if (r.ReadBit() != 0) return new List<object?>();
            var n = (int)r.ReadBits(6);
            var tags = new List<object?>();
            for (var i = 0; i < n; i++)
            {
                tags.Add(EngineBunchCodec.ReadSerializedFName(r).AsDictionary());
            }
            return tags;
        }
        if (kind == 15)
        {
            // 死亡盒版 FRepMovement：整数量/速度 + 字节旋转，保留可选物理角速度。
            var flags = r.ReadBits(2);
            var result = new Dictionary<string, object?>
            {
                ["location"] = RepDec4.Pv(r, 1.0, 24),
                ["rotation"] = RepDec4.RotC(r),
                ["velocity"] = RepDec4.Pv(r, 1.0, 24),
                ["physics_sleep"] = (flags & 1) != 0,
                ["rep_physics"] = (flags & 2) != 0,
            };
            if ((flags & 2) != 0)
            {
                result["angular_velocity"] = RepDec4.Pv(r, 1.0, 24);
            }
            return result;
        }
        return PickupValue(r, kind, meta);
    }
}
