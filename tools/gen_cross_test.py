# -*- coding: utf-8 -*-
"""gen_cross_test.py — 黄金对拍测试向量生成器（Python 权威实现 → C# 解码 diff）。

原理：Python 原版解码器是"权威实现"。本脚本用 Python 构造合法位流（编码方向），
然后调用 Python 的 repdec4/repdec6/sna/blocks/repmove4 产生权威期望输出，
把 (位流, 期望输出, catalog 元数据) 打包成 cross_test.json。
C# 自测程序读取同一 JSON，用自己的解码器解同一批位流，逐项 diff。
任何一项不一致 = 移植语义偏差，立即暴露。

运行：python gen_cross_test.py
输出：cross_test.json（与 SelfTest 项目同目录）
"""
import json
import os
import random
import struct
import sys
from pathlib import Path

ER_ROOT = Path(r"C:\个人文件\B.软路由\Nva本地离线解析-V1.1.0-源码+程序\源码\Parser\ErCore")
os.environ["ER_ROOT"] = str(ER_ROOT)
sys.path.insert(0, str(ER_ROOT / "src"))
sys.path.insert(0, str(ER_ROOT / "src" / "core"))

from codec.bitstream import BitWriter, BitReader  # noqa: E402
import repdec4  # noqa: E402
import repdec6  # noqa: E402
import sna  # noqa: E402
import blocks  # noqa: E402
import repmove4  # noqa: E402

OUT = Path(__file__).resolve().parent.parent / "CCRadar.Parser.SelfTest" / "cross_test.json"

random.seed(1234)


def write_dfm_packed(w: BitWriter, v: int) -> None:
    """read_dfm_packed_int 的编码逆：每字节 bit0=继续标志，bits1..7=数据段。"""
    for group in range(5):
        chunk = (v >> (group * 7)) & 0x7F
        more = (v >> ((group + 1) * 7)) != 0 and group < 4
        w.uint8((chunk << 1) | (1 if more else 0))
        if not more:
            return
    raise AssertionError("value too large")


def write_packed_guid(w: BitWriter, v: int) -> None:
    """sna.packed 的编码逆（FNetworkGUID）：每 8 位 = 7 位数据 + 1 位继续（LSB）。"""
    cnt = 0
    while True:
        chunk = (v >> (7 * cnt)) & 0x7F
        more = (v >> (7 * (cnt + 1))) != 0
        w.bits((chunk << 1) | (1 if more else 0), 8)
        cnt += 1
        if not more:
            return
        if cnt > 5:
            raise AssertionError("guid too large")


def write_fstring_bits(w: BitWriter, text: str) -> None:
    """sna.fstring 的编码逆：ASCII（正长度含结尾 \\0）。"""
    raw = text.encode("utf-8") + b"\x00"
    w.bits(len(raw), 32)
    for b in raw:
        w.bits(b, 8)


def zigzag_encode(s: int) -> int:
    """repmove3.zz 的编码逆（32 位环）。"""
    return ((s << 1) ^ (s >> 31)) & 0xFFFFFFFF


def scalar_value(t: int) -> object:
    """生成一个随机标量值（按类型）。"""
    if t == 3:
        return random.randint(0, 1)
    if t == 4:
        return struct.unpack("<f", struct.pack("<f", random.uniform(-1000.0, 1000.0)))[0]
    if t == 5:
        return struct.unpack("<i", struct.pack("<I", random.randint(0, 0xFFFFFFFF)))[0]
    if t == 6:
        return random.randint(0, 255)
    if t == 9:
        return random.randint(0, 0xFFFFFFFF)
    if t == 20:
        return random.randint(0, 0xFFFFFFFF)
    raise AssertionError(f"unsupported scalar type {t}")


def encode_scalar(w: BitWriter, t: int, v: object) -> None:
    """把标量值按线格式编码。"""
    if t == 3:
        w.bits(int(v), 1)
    elif t == 4:
        w.bits(struct.unpack("<I", struct.pack("<f", float(v)))[0], 32)
    elif t == 5:
        w.bits(int(v) & 0xFFFFFFFF, 32)
    elif t == 6:
        w.bits(int(v), 8)
    elif t == 9:
        w.bits(int(v), 32)
    elif t == 20:
        w.bits(int(v), 64)
    else:
        raise AssertionError(f"unsupported scalar type {t}")


def pick_class():
    """从真实 catalog 收集可编码命令，裁剪成合成 catalog（≥8 条）。

    数据驱动的合法做法：decode_block 只消费 catalog.cmds，裁剪后的 catalog
    两边（Python/C#）用同一份 JSON，顺带覆盖 C# 的 catalog 解析路径。
    支持：3/4/5/6/9/20 标量、8(Object=packed guid)、7(SerializedFName hardcoded)、
    2(Property，限 UInt16/Int16/Int8/Int64/Quat/Vector2D，需要 propmeta)。
    """
    cls = repdec4.load_catalogs()
    pm = repdec4.load_propmeta()
    for name, cat in cls.items():
        meta_by_index = {m.get("index"): m for m in pm.get(name, []) if isinstance(m, dict)}
        picked = []
        for c in repdec4.cmd_list(cat):
            t = c["type"]
            if t in (3, 4, 5, 6, 9, 20):
                picked.append((c, ("scalar", None)))
            elif t == 8:
                picked.append((c, ("object", None)))
            elif t == 7:
                picked.append((c, ("fname", None)))
            elif t == 2:
                m = meta_by_index.get(c["index"]) or {}
                pc = m.get("prop_class")
                if pc in ("UInt16Property", "Int16Property", "Int8Property", "Int64Property"):
                    picked.append((c, ("prop", pc)))
                elif pc == "StructProperty" and m.get("struct") in ("Quat", "Vector2D"):
                    picked.append((c, ("struct", m["struct"])))
            if len(picked) >= 12:
                break
        if len(picked) >= 8:
            cmds = [c for c, _ in picked]
            plan = [p for _, p in picked]
            # 合成 catalog：只含裁剪后的 cmds（index/end_cmd/parent_index 原样保留）。
            synthetic = {
                "class": name + "_trimmed",
                "cmds": [{"index": c["index"], "type": c["type"],
                          "end_cmd": c.get("end_cmd"), "parent_index": c["parent_index"],
                          "object_offset": c.get("object_offset", 0)}
                         for c in cmds],
            }
            return name, synthetic, cmds, plan
    raise AssertionError("no encodable commands found in catalog")


def decode_block_with(cat, payload, pbits):
    """用裁剪 catalog（dict 形态）做 Python 侧权威解码。"""
    # repdec4.decode_block 需要 cmd_list(cat)——接受 dict（cat["cmds"]）。
    return repdec4.decode_block(payload, {"payload_start": 0, "payload_end": pbits}, cat, {})


def encode_cmd_value(w: BitWriter, plan_kind, plan_arg, t: int) -> object:
    """按计划编码一个命令值，返回期望 value（与 read_val 语义一致）。"""
    if plan_kind == "scalar":
        v = scalar_value(t)
        encode_scalar(w, t, v)
        return v
    if plan_kind == "object":
        guid = random.randint(1, 0xFFFF)
        write_dfm_packed(w, guid)
        return {"guid": guid}
    if plan_kind == "fname":
        idx = random.randint(0, 0x100)
        w.bit(1)                      # hardcoded = 1
        write_dfm_packed(w, idx)
        return {"hardcoded": True, "hardcoded_index": idx, "text": None,
                "number": 0, "interval": [0, 0]}
    if plan_kind == "prop":
        if plan_arg == "UInt16Property":
            v = random.randint(0, 0xFFFF)
            w.bits(v, 16)
            return v
        if plan_arg == "Int16Property":
            u = random.randint(0, 0xFFFF)
            w.bits(u, 16)
            return u - 65536 if u & 0x8000 else u
        if plan_arg == "Int8Property":
            u = random.randint(0, 255)
            w.bits(u, 8)
            return u - 256 if u & 0x80 else u
        if plan_arg == "Int64Property":
            v = random.randint(0, 0xFFFFFFFF)
            w.bits(v, 64)
            return v
    if plan_kind == "struct":
        if plan_arg == "Quat":
            vals = [struct.unpack("<f", struct.pack("<f", random.uniform(-1.0, 1.0)))[0] for _ in range(4)]
            for v in vals:
                w.bits(struct.unpack("<I", struct.pack("<f", v))[0], 32)
            return vals
        if plan_arg == "Vector2D":
            vals = [struct.unpack("<f", struct.pack("<f", random.uniform(-1000.0, 1000.0)))[0] for _ in range(2)]
            for v in vals:
                w.bits(struct.unpack("<I", struct.pack("<f", v))[0], 32)
            return vals
    raise AssertionError(f"unencodable plan {plan_kind}")


def build_repdec4_case():
    """repdec4 属性解码用例：handle 1..N 各带一个值，结尾 handle 0。

    expected_events 直接取 Python 权威解码输出（fname 的 interval 等
    解码器填充字段无法预构造）。
    """
    name, cat, cmds, plan = pick_class()
    w = BitWriter()
    for handle, (c, (kind, arg)) in enumerate(zip(cmds, plan), start=1):
        write_dfm_packed(w, handle)
        encode_cmd_value(w, kind, arg, c["type"])
    write_dfm_packed(w, 0)
    payload = w.bytes()
    pbits = w.bit_count
    ev, pos, status = decode_block_with(cat, payload, pbits)
    assert status == "exact", f"python-side status {status}"
    assert len(ev) == len(cmds), f"event count {len(ev)} != {len(cmds)}"
    expected = [dict(e) for e in ev]
    return {
        "kind": "repdec4",
        "class_name": name,
        "cmds": [{"index": c["index"], "type": c["type"],
                  "end_cmd": c.get("end_cmd"), "parent_index": c["parent_index"]}
                 for c in cmds],
        "payload": payload.hex(),
        "payload_bits": pbits,
        "expected_status": status,
        "expected_position": pos,
        "expected_events": expected,
    }


def build_repdec6_case():
    """repdec6 用例：前导 bDoChecksum=0，handle 流同 repdec4。expected 取权威输出。"""
    name, cat, cmds, plan = pick_class()
    w = BitWriter()
    w.bit(0)  # bDoChecksum = 0
    for handle, (c, (kind, arg)) in enumerate(zip(cmds, plan), start=1):
        write_dfm_packed(w, handle)
        encode_cmd_value(w, kind, arg, c["type"])
    write_dfm_packed(w, 0)
    payload = w.bytes()
    pbits = w.bit_count
    ev, pos, status = repdec6.decode_block(payload, {"payload_start": 0, "payload_end": pbits}, cat, {})
    assert status == "exact", f"python-side status {status}"
    expected = [dict(e) for e in ev]
    return {
        "kind": "repdec6",
        "class_name": name,
        "cmds": [{"index": c["index"], "type": c["type"],
                  "end_cmd": c.get("end_cmd"), "parent_index": c["parent_index"]}
                 for c in cmds],
        "payload": payload.hex(),
        "payload_bits": pbits,
        "expected_status": status,
        "expected_position": pos,
        "expected_events": expected,
    }


def build_sna_case():
    """sna.parse_prefix 用例：1 个带路径的 export + 静态 actor（guid&1）。"""
    w = BitWriter()
    # read_exports2: bit(0)=无 replayout 导出, u32 n=1, load_object(exporting=True)
    w.bit(0)
    w.bits(1, 32)
    write_packed_guid(w, 5)          # guid=5
    w.bits(1, 8)                     # ExportFlags: bHasPath（&0xF8 必须为 0）
    write_packed_guid(w, 0)          # outer = 0（终止）
    write_fstring_bits(w, "/Game/Foo.Bar_C")
    # actor：guid=3（&1 → static_actor）
    write_packed_guid(w, 3)
    payload = w.bytes()
    pbits = w.bit_count
    info, stream_start = sna.parse_prefix(
        [int(b) for byte in payload for b in format(byte, "08b")[::-1]],
        True, False)
    assert stream_start == pbits
    assert info["actor"] == 3 and info.get("static_actor") is True
    assert info["exports"] == [{"guid": 5, "outer": 0, "path": "/Game/Foo.Bar_C", "flags": 1}]
    return {
        "kind": "sna",
        "payload": payload.hex(),
        "payload_bits": pbits,
        "has_exports": True,
        "has_must": False,
        "expected_info": {**info, "exports": info["exports"], "must": info["must"]},
        "expected_stream_start": stream_start,
    }


def build_blocks_case():
    """blocks.tile 用例：actor 块 + 属性块 + 子对象删除块。"""
    w = BitWriter()
    # 块 1：has_rep=1, is_actor=1，num_bits=0（tile 对 actor 块也读 NumPayloadBits）
    w.bit(1)
    w.bit(1)
    write_dfm_packed(w, 0)
    # 块 2：has_rep=1, is_actor=0, sub=7, stably=0, cls=9, num_bits=24, 载荷 24 位
    w.bit(1)
    w.bit(0)
    write_packed_guid(w, 7)
    w.bit(0)
    write_packed_guid(w, 9)
    payload_bits = 24
    write_dfm_packed(w, payload_bits)
    body = random.getrandbits(payload_bits)
    w.bits(body, payload_bits)
    # 块 3：sub=11, stably=0, cls=packed(0) → deleted（无 num_bits）
    w.bit(0)
    w.bit(0)
    write_packed_guid(w, 11)
    w.bit(0)
    write_packed_guid(w, 0)  # cls=0 → deleted
    payload = w.bytes()
    pbits = w.bit_count
    tile = blocks.tile(payload, pbits, 0)
    assert tile is not None and len(tile) == 3, f"python-side tile {tile}"
    expected = []
    for blk in tile:
        expected.append({
            "start": blk["start"], "has_rep": blk["has_rep"], "is_actor": blk["is_actor"],
            "sub": blk.get("sub"), "stably": blk.get("stably"), "cls": blk.get("cls"),
            "deleted": blk.get("deleted", False), "num_bits": blk.get("num_bits", 0),
            "payload_start": blk["payload_start"], "payload_end": blk["payload_end"],
        })
    return {
        "kind": "blocks",
        "payload": payload.hex(),
        "payload_bits": pbits,
        "expected_blocks": expected,
    }


def build_repmove4_case_v2():
    """repmove4.read_frame4 用例（v2，全部用 ser_int 逆函数逐位写入）。"""
    def write_serint(w: BitWriter, v: int, vmax: int) -> None:
        """sna.ser_int 的编码逆（最大值受限自适应宽度）。

        关键：ser_int 的停机条件用**累积值 acc**（读侧边读边累积），
        写侧必须逐位模拟同一状态机，否则位宽与读侧不一致。
        """
        mask = 1
        acc = 0
        shift = 0
        while (acc + mask) < vmax:
            b = (v >> shift) & 1
            w.bit(b)
            if b:
                acc |= mask
            shift += 1
            mask <<= 1

    w = BitWriter()
    w.bits(1, 32)          # C = 1
    w.bits(1, 32)          # mask：item0 present
    # 组头：flags=3 → 3 个位置分量 + 1 个标量分量，全部 NB=12 位
    w.bits(3, 2)
    NB = 12
    W = 32 - NB
    raws = [0xABC, 0x111, 0x222, 0x333]
    for r in raws:
        w.bits(NB, 5)
        w.bits(r, NB)
    # item0：sel=0 → mask=0x41（0x40 scalar + 0x01 pos/pv + 0x001 f52/rot——
    # 0x01 与 0x001 是同一位，两个分支都命中）
    w.bits(0, 2)
    w.bits(0x12345, W)                       # scalar: v = low | val3
    for k in range(3):
        w.bits([0x23456, 0x34567, 0x45678 & 0xFFFFF][k], W)
    # pv：h=10（serint 24），mx=1<<12，bias=1<<11，3 个分量（serint mx）
    write_serint(w, 10, 24)
    for delta in (100, -200, 300):
        write_serint(w, delta + (1 << 11), 1 << 12)
    # f52(2 bits) + rot 3×(1 非零位 + u8)
    w.bits(2, 2)
    for k in range(3):
        w.bit(1)
        w.bits(0x40 + k, 8)
    # 帧尾
    w.bit(0)               # bit_a
    w.bit(0)               # has_bits
    w.bit(0)               # has_embedded
    w.bits(0xA5A5A5A5, 32)  # tail32
    payload = w.bytes()
    pbits = w.bit_count

    # Python 权威解码
    fr, _r = repmove4.read_frame4(payload, 0, pbits)
    assert fr["stopped"] is None, f"python stopped: {fr['stopped']}"
    item = fr["items"][0]
    assert item["sel"] == 0 and item["mask"] == 0x41, f"sel/mask {item.get('sel')}/{item.get('mask')}"
    expected = {
        "count": fr["count"],
        "present": fr["present"],
        "items": {str(k): v for k, v in fr["items"].items()},
        "bit_a": fr["bit_a"],
        "has_bits": fr["has_bits"],
        "has_embedded": fr["has_embedded"],
        "tail32": fr["tail32"],
        "pos": fr["pos"],
        "left": fr["left"],
        "stopped": None,
    }
    return {
        "kind": "repmove4",
        "payload": payload.hex(),
        "payload_bits": pbits,
        "start": 0,
        "end": pbits,
        "expected_frame": expected,
    }


def main():
    import repmove3  # noqa: F401  (repmove4 依赖)
    cases = []
    cases.append(build_repdec4_case())
    cases.append(build_repdec6_case())
    cases.append(build_sna_case())
    cases.append(build_blocks_case())
    cases.append(build_repmove4_case_v2())
    OUT.write_text(json.dumps(cases, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"OK: {len(cases)} cases -> {OUT}")
    for c in cases:
        print(" -", c["kind"], c.get("class_name", ""))


if __name__ == "__main__":
    main()
