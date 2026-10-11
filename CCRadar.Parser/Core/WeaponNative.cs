using CCRadar.Parser.Codec;

namespace CCRadar.Parser.Core;

/// <summary>
/// weapon_native.py 的 C# 等价实现 —— 0904 运行时 CppStructOps 恢复的武器原生序列化器。
///
/// 所有计数先校验再分配；无扫描、无位位置恢复（Python 原注释）。
/// 输出结构与 Python 保持一致（Dictionary 键名相同，喂给 JSON 对拍）。
/// </summary>
public static class WeaponNative
{
    /// <summary>带上限的计数读取（Python: _count）。</summary>
    public static int Count(BitReader r, int limit = 512)
    {
        var n = r.ReadUInt32();
        if (n > limit) throw new BitError($"weapon array count {n}");
        return (int)n;
    }

    /// <summary>
    /// WeaponSkinInfo（Python: weapon_skin，0x1196B0A0）：
    /// bool32 default；只有非默认皮肤才有正文。
    /// </summary>
    public static Dictionary<string, object?> WeaponSkin(BitReader r)
    {
        if (r.ReadUInt32() != 0)
        {
            return new Dictionary<string, object?> { ["default"] = true };
        }
        var names = new[]
        {
            "skin_gid", "skin_id", "appearance_id", "origin_avatar_id",
            "mystical_id", "color_id", "wear", "material_seed",
            "unique_number", "kill_count",
        };
        var d = new Dictionary<string, object?>();
        foreach (var key in names) d[key] = r.ReadUInt64();
        d["custom_name"] = r.ReadFString();
        d["pendant_id"] = r.ReadUInt64();
        d["pendant_guid"] = r.ReadUInt64();
        foreach (var key in new[] { "suit_active", "lock_appearance", "enemy_visible", "teammate_visible" })
        {
            d[key] = r.ReadUInt32() != 0;
        }
        var appearances = new List<object?>();
        for (var i = 0; i < Count(r); i++)
        {
            appearances.Add(new Dictionary<string, object?>
            {
                ["mystical_id"] = r.ReadUInt64(),
                ["material_seed"] = r.ReadUInt64(),
            });
        }
        d["pendant_appearances"] = appearances;
        return d;
    }

    /// <summary>武器部件（Python: weapon_part，0x10B54FD4 → 0x10B55154 父插槽信息）。</summary>
    public static Dictionary<string, object?> WeaponPart(BitReader r)
    {
        var d = new Dictionary<string, object?>
        {
            ["item_id"] = r.ReadUInt64(),
            ["guid"] = r.ReadUInt64(),
            ["node_type"] = r.ReadUInt8(),
        };
        var parent = new Dictionary<string, object?> { ["id"] = r.ReadUInt32() };
        var nameOmitted = r.ReadUInt32();
        var hasGuid = r.ReadUInt32();
        if (nameOmitted == 0)
        {
            parent["name"] = EngineBunchCodec.ReadSerializedFName(r).AsDictionary();
        }
        if (hasGuid != 0)
        {
            parent["guid"] = new List<object?> { r.ReadUInt64(), r.ReadUInt64() };
        }
        parent["index"] = r.ReadUInt32();
        d["parent"] = parent;
        foreach (var key in new[] { "exclude_3p", "exclude_1p", "exclude_ui" })
        {
            d[key] = r.ReadBit() != 0;
        }
        return d;
    }

    /// <summary>
    /// WeaponReplicateExtraData（Python: weapon_extra，0x10B54978）；
    /// FastArraySerializerItem 头不在此序列化。
    /// </summary>
    public static Dictionary<string, object?> WeaponExtra(BitReader r)
    {
        var d = new Dictionary<string, object?>
        {
            ["identity"] = r.ReadUInt32(),
            ["weapon_id"] = r.ReadUInt64(),
        };
        // guid：1 位标志选择 weapon_id 复用或独立 u64。
        d["guid"] = r.ReadBit() != 0 ? d["weapon_id"] : r.ReadUInt64();
        d["equip_position"] = EngineBunchCodec.ReadDfmPackedInt(r).Value;
        d["skill_id"] = EngineBunchCodec.ReadDfmPackedInt(r).Value;
        d["slot"] = r.ReadUInt8();
        var parts = new List<object?>();
        for (var i = 0; i < Count(r); i++) parts.Add(WeaponPart(r));
        d["parts"] = parts;
        // poor_item_id：1 位为真 → 0，否则 u64。
        d["poor_item_id"] = r.ReadBit() != 0 ? 0ul : r.ReadUInt64();
        d["skin"] = WeaponSkin(r);
        d["ammo_logic"] = r.ReadUInt8();
        return d;
    }

    /// <summary>部件节点属性（Python: part_node_attributes，0x1133665C；本协议 name 压缩=1）。</summary>
    public static Dictionary<string, object?> PartNodeAttributes(BitReader r)
    {
        var values = new List<object?>();
        for (var i = 0; i < Count(r); i++) values.Add(r.ReadFloat32());
        var indices = new List<uint>();
        for (var i = 0; i < Count(r); i++) indices.Add(r.ReadUInt32());
        var names = new List<object?>();
        foreach (var idx in indices)
        {
            names.Add(idx == 0
                ? EngineBunchCodec.ReadSerializedFName(r).AsDictionary()
                : new Dictionary<string, object?> { ["registry_index"] = idx });
        }
        return new Dictionary<string, object?> { ["values"] = values, ["names"] = names };
    }

    /// <summary>模块化武器动态属性基座（Python: weapon_dynamic_base，0x113361A4）。</summary>
    public static Dictionary<string, object?> WeaponDynamicBase(BitReader r)
    {
        var guids = new List<object?>();
        for (var i = 0; i < Count(r); i++) guids.Add(r.ReadUInt64());
        var nodeAttributes = new List<object?>();
        for (var i = 0; i < Count(r); i++) nodeAttributes.Add(PartNodeAttributes(r));
        return new Dictionary<string, object?>
        {
            ["part_guids"] = guids,
            ["node_attributes"] = nodeAttributes,
        };
    }

    /// <summary>模块化武器动态属性（Python: weapon_dynamic，0x11335BB4）：基座 + 可选持有者 + 弹药上下文 + 三个过热浮点。</summary>
    public static Dictionary<string, object?> WeaponDynamic(BitReader r)
    {
        var d = WeaponDynamicBase(r);
        // owner：u32 非 0 → 0；否则 u64。
        d["owner"] = r.ReadUInt32() != 0 ? 0ul : r.ReadUInt64();
        var ammo = new List<object?>();
        for (var i = 0; i < Count(r); i++)
        {
            var owners = new List<object?>();
            for (var k = 0; k < Count(r); k++) owners.Add(r.ReadUInt64());
            ammo.Add(new Dictionary<string, object?>
            {
                ["item_id"] = r.ReadUInt64(),
                ["guid"] = r.ReadUInt64(),
                ["count"] = r.ReadInt32(),
                ["owner"] = r.ReadUInt64(),
                ["owners"] = owners,
                ["flag"] = r.ReadUInt32() != 0,
            });
        }
        var overheat = new List<object?>();
        for (var i = 0; i < 3; i++) overheat.Add(r.ReadFloat32());
        d["ammo"] = ammo;
        d["overheat"] = overheat;
        return d;
    }
}
