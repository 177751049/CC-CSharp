using CCRadar.Parser.Codec;

namespace CCRadar.Parser.Core;

/// <summary>
/// inventory_native.py 的 C# 等价实现 —— InventoryItemInfo 原生线格式（运行时 0x123BCD40）。
/// 类特定的可选正文在序列化器未知时显式停住（Opaque），不做扫描/位恢复。
/// </summary>
public static class InventoryNative
{
    /// <summary>
    /// FLootObjRepData 0x116C598C / FItemSpaceRepData 0x116C5C64（Python: loot_box_data）。
    /// 只传输网格几何；FGridSize 其余运行时字段由接收端本地重建，不序列化。
    /// </summary>
    public static List<object?> LootBoxData(BitReader r)
    {
        // 局部辅助：带上限的 i32 计数（Python: count）。
        int Count(int limit)
        {
            var value = r.ReadInt32();
            if (value < 0 || value > limit) throw new BitError("loot space count");
            return value;
        }
        // 有符号字节（Python: signed_byte，0x80 以上为负）。
        int SignedByte()
        {
            var value = r.ReadUInt8();
            return value >= 128 ? (int)value - 256 : (int)value;
        }
        var spaces = new List<object?>();
        for (var i = 0; i < Count(128); i++)
        {
            var space = new Dictionary<string, object?>
            {
                ["space_type"] = r.ReadInt32(),
                ["update_count"] = r.ReadInt32(),
            };
            var grids = new List<object?>();
            for (var g = 0; g < Count(1024); g++)
            {
                var grid = new Dictionary<string, object?>
                {
                    ["id"] = r.ReadInt32(),
                    ["x"] = r.ReadInt32(),
                    ["y"] = r.ReadInt32(),
                    ["width"] = r.ReadInt32(),
                    ["height"] = r.ReadInt32(),
                    ["half_x"] = SignedByte(),
                    ["half_y"] = SignedByte(),
                };
                grids.Add(grid);
            }
            space["grids"] = grids;
            spaces.Add(space);
        }
        return spaces;
    }

    /// <summary>未建模正文 → Opaque（Python: _unsupported）。</summary>
    private static void Unsupported(string name) => throw new RepOpaque($"InventoryItemInfo {name}");

    /// <summary>枪械部件（Python: inventory_gun_part，0x113388C8）：固定字段在前，后跟两个 owner/数量数组。</summary>
    public static Dictionary<string, object?> InventoryGunPart(BitReader r)
    {
        var d = new Dictionary<string, object?>();
        // (偏移, 位宽) 固定字段表 —— 命名沿用 Python 的 field_%x。
        (int Offset, int Bits)[] widths =
        [
            (0x18, 64), (0x20, 16), (0x28, 64), (0x30, 64), (0x38, 16),
            (0x3c, 32), (0x40, 32), (0x44, 16), (0x46, 8), (0x48, 32),
            (0x4c, 32), (0x50, 8), (0x58, 64), (0x60, 32),
        ];
        foreach (var (offset, bits) in widths)
        {
            d[$"field_{offset:x}"] = (long)r.ReadBits(bits);
        }
        var owners = new List<object?>();
        for (var i = 0; i < WeaponNative.Count(r); i++) owners.Add(r.ReadUInt64());
        var counts = new List<object?>();
        for (var i = 0; i < WeaponNative.Count(r); i++) counts.Add(r.ReadUInt32());
        d["owners"] = owners;
        d["counts"] = counts;
        return d;
    }

    /// <summary>枪械物品（Python: inventory_gun，0x113381B8）：与武器组件不同，部件计数是 u8。</summary>
    public static Dictionary<string, object?> InventoryGun(BitReader r)
    {
        var parts = new List<object?>();
        for (var i = 0; i < r.ReadUInt8(); i++) parts.Add(InventoryGunPart(r));
        var d = new Dictionary<string, object?>
        {
            ["parts"] = parts,
            ["dynamic"] = WeaponNative.WeaponDynamic(r),
        };
        foreach (var offset in new[] { 0x60, 0x68, 0x70, 0x88, 0x80, 0xa0, 0xb0, 0xb8, 0x108 })
        {
            d[$"field_{offset:x}"] = r.ReadUInt64();
        }
        d["flag_148"] = r.ReadUInt32() != 0;
        d["field_d8"] = r.ReadUInt64();
        d["field_f0"] = r.ReadUInt64();
        d["custom_name"] = r.ReadFString();
        return d;
    }

    /// <summary>InventoryItemInfo（Python: inventory_item）—— 背包物品权威源。</summary>
    public static Dictionary<string, object?> InventoryItem(BitReader r)
    {
        var d = new Dictionary<string, object?>
        {
            // item_id = 高位 u32 × 10000 + 低位 u32（Python: r.uint32()*10000 + r.uint32()）。
            ["item_id"] = (long)r.ReadUInt32() * 10000 + r.ReadUInt32(),
            ["guid"] = r.ReadUInt64(),
            ["attach_position"] = r.ReadUInt32(),
            ["space_location"] = new List<object?> { r.ReadUInt8(), r.ReadUInt8(), r.ReadUInt8(), r.ReadUInt8() },
            ["flag_78"] = r.ReadUInt32() != 0,
            ["field_79"] = r.ReadUInt8(),
        };
        (int Offset, int Bits)[] fields =
        [
            (0x608, 32), (0x60c, 8), (0x620, 64), (0x63c, 32),
            (0x640, 64), (0x628, 8), (0x630, 64), (0x638, 32), (0x5f0, 32),
        ];
        foreach (var (offset, bits) in fields)
        {
            d[$"field_{offset:x}"] = (long)r.ReadBits(bits);
        }
        foreach (var key in new[] { "ids_5e0", "ids_5f8" })
        {
            var list = new List<object?>();
            for (var i = 0; i < WeaponNative.Count(r); i++) list.Add(r.ReadUInt64());
            d[key] = list;
        }
        if (r.ReadBit() != 0)
        {
            d["count"] = r.ReadInt32();
            d["max_count"] = r.ReadInt32();
            var stackGuids = new List<object?>();
            for (var i = 0; i < WeaponNative.Count(r); i++) stackGuids.Add(r.ReadUInt64());
            d["stack_guids"] = stackGuids;
            var stackCounts = new List<object?>();
            for (var i = 0; i < WeaponNative.Count(r); i++) stackCounts.Add(r.ReadInt32());
            d["stack_counts"] = stackCounts;
        }
        if (r.ReadBit() != 0)
        {
            d["durability"] = r.ReadFloat32();
            d["max_durability"] = r.ReadFloat32();
        }
        if (r.ReadBit() != 0)
        {
            d["armor"] = r.ReadFloat32();
            d["max_armor"] = r.ReadFloat32();
        }
        if (r.ReadBit() != 0) d["gun"] = InventoryGun(r);
        if (r.ReadBit() != 0) Unsupported("archive");
        if (r.ReadBit() != 0)
        {
            var keychain = new List<object?>();
            for (var i = 0; i < WeaponNative.Count(r); i++)
            {
                var shape = new List<object?>();
                for (var k = 0; k < 3; k++) shape.Add(r.ReadUInt8());
                var occupied = new List<object?>();
                for (var k = 0; k < WeaponNative.Count(r); k++) occupied.Add(r.ReadUInt8());
                var reserved = new List<object?>();
                for (var k = 0; k < WeaponNative.Count(r); k++) reserved.Add(r.ReadUInt8());
                keychain.Add(new Dictionary<string, object?>
                {
                    ["shape"] = shape, ["occupied"] = occupied, ["reserved"] = reserved,
                });
            }
            d["keychain_spaces"] = keychain;
        }
        if (r.ReadBit() != 0)
        {
            d["name_538"] = r.ReadFString();
            d["id_548"] = r.ReadUInt64();
        }
        d["id_648"] = r.ReadUInt64();
        if (r.ReadBit() != 0)
        {
            d["field_6d8"] = r.ReadUInt32();
            d["field_6d0"] = r.ReadUInt64();
            d["field_6e8"] = r.ReadUInt64();
        }
        if (r.ReadBit() != 0)
        {
            var ids = new List<object?>();
            for (var i = 0; i < WeaponNative.Count(r); i++) ids.Add(r.ReadUInt64());
            d["ids_570"] = ids;
            var map = new List<object?>();
            for (var i = 0; i < WeaponNative.Count(r); i++)
            {
                map.Add(new List<object?> { r.ReadUInt64(), r.ReadUInt64() });
            }
            d["map_580"] = map;
            d["kill_index"] = r.ReadInt32();
            d["evacuate_count"] = r.ReadUInt64();
        }
        if (r.ReadBit() != 0)
        {
            var named = new List<object?>();
            for (var i = 0; i < WeaponNative.Count(r); i++)
            {
                named.Add(new List<object?> { r.ReadUInt64(), r.ReadFString() });
            }
            d["named_ids"] = named;
        }
        var set750 = new List<object?>();
        for (var i = 0; i < WeaponNative.Count(r); i++) set750.Add(r.ReadUInt32());
        d["set_750"] = set750;
        if (r.ReadBit() != 0)
        {
            d["flags_7a0"] = new List<object?> { r.ReadUInt32() != 0, r.ReadUInt32() != 0 };
            d["names_7a8"] = new List<object?> { r.ReadFString(), r.ReadFString() };
            d["ids_7c8"] = new List<object?> { r.ReadUInt64(), r.ReadUInt64() };
        }
        d["field_7d8"] = r.ReadInt32();
        d["flag_6a0"] = r.ReadBit() != 0;
        if (r.ReadBit() != 0)
        {
            d["fields_6a4"] = new List<object?> { r.ReadInt32(), r.ReadInt32() };
        }
        if (r.ReadBit() != 0)
        {
            d["fields_6ac"] = new List<object?> { r.ReadInt32(), r.ReadInt32(), r.ReadBit() != 0 };
        }
        // 多态扩展：计数非 0 → 未建模（与 Python 一致）。
        if (WeaponNative.Count(r) != 0) Unsupported("polymorphic extensions");
        if (r.ReadBit() != 0)
        {
            d["fields_7dc"] = new List<object?> { r.ReadInt32(), r.ReadUInt8() };
        }
        if (r.ReadBit() != 0) d["name_7e8"] = r.ReadFString();
        if (r.ReadBit() != 0) d["field_7f8"] = r.ReadInt32();
        if (r.ReadBit() != 0) d["field_7fc"] = r.ReadInt32();
        return d;
    }
}
