# -*- coding: utf-8 -*-
"""最小复现：对 seq18922 ch53 的块 [7472-8473] 对比两侧装备扫描的锚点。"""
import json
import sys
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "源码" / "Parser" / "ErCore" / "src" / "core"))

from liveview import _scan_equip_arrays, _EQ_FAMILIES  # noqa: E402

j = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
b = next(x for x in j["extraBunches"] if x.get("source") == "targetseq" and x["seq"] == 18922)
payload = bytes.fromhex(b["hex"])
ps, pe = 7472, 8473

u = np.unpackbits(np.frombuffer(payload, dtype=np.uint8), bitorder="little")
seg = u[ps:pe]
L = len(seg)
print(f"L={L}")


def rd(pos, n):
    v = 0
    for j2 in range(n):
        v |= int(seg[pos + j2]) << j2
    return v


# 锚点（与 _scan_equip_arrays 相同的向量化）。
packed_bits = np.packbits(seg, bitorder="little")
padded = np.pad(packed_bits, (0, 8))
words = np.zeros(len(packed_bits), dtype=np.uint64)
for i in range(8):
    words |= padded[i:i + len(words)].astype(np.uint64) << np.uint64(i * 8)
acc = np.empty(L - 63, dtype=np.uint64)
for phase in range(8):
    count = len(acc[phase::8])
    values = words[:count] >> np.uint64(phase)
    if phase:
        values |= padded[8:8 + count].astype(np.uint64) << np.uint64(64 - phase)
    acc[phase::8] = values

anchors = []
for idx in np.nonzero((acc >= np.uint64(10 ** 10)) & (acc < np.uint64(10 ** 12)))[0]:
    c = int(idx)
    if str(int(acc[c]))[:4] in _EQ_FAMILIES:
        anchors.append(c)
print("anchors:", anchors[:20], "total", len(anchors))

# 手动走第一个锚点的链（复刻 _scan_equip_arrays）。
def packed(pos):
    v = 0
    sh = 0
    for g in range(3):
        if pos + (g + 1) * 8 > L:
            return None, None
        enc = rd(pos + g * 8, 8)
        v |= (enc >> 1) << sh
        sh += 7
        if not (enc & 1):
            return v, (g + 1) * 8
    return None, None


for c in anchors[:4]:
    start = None
    for back in (8, 16):
        h0, g0 = packed(c - back)
        if h0 is not None and c - back + g0 == c \
                and h0 >= 1 and (h0 - 1) % 9 == 0 and (h0 - 1) // 9 <= 40 \
                and int(acc[c]) == rd(c, 64):
            start = c - back
            print(f"anchor c={c} acc={int(acc[c])} start={start} back={back} h0={h0}")
            break
    if start is None:
        print(f"anchor c={c} acc={int(acc[c])} NO-START")
        continue

# 走链句柄序列（Python 视角）。
pos = 25
for step in range(12):
    hh, gg = packed(pos)
    if hh is None:
        print(f"step{step} pos={pos} packed=None")
        break
    print(f"step{step} pos={pos} h={hh} gBits={gg} role={hh % 9 if hh else '-'} k={hh // 9 if hh else '-'}")
    if hh == 0:
        break
    if hh % 9 == 1:
        v = rd(pos + gg, 64)
        if not (10 ** 10 <= v < 10 ** 12) or str(v)[:4] not in _EQ_FAMILIES:
            print("   break: itemid invalid", v)
            break
        pos = pos + gg + 64
    elif hh % 9 == 2:
        v = rd(pos + gg, 64)
        if v < 2 ** 50:
            print("   break: uid invalid", v)
            break
        pos = pos + gg + 64
    elif hh % 9 in (5, 6):
        import struct as _s
        v = _s.unpack("<f", rd(pos + gg, 32).to_bytes(4, "little"))[0]
        print(f"   f32={v}")
        if not (0.0 <= v <= 500.0):
            print("   break: dur invalid", v)
            break
        pos = pos + gg + 32
    else:
        print("   break: role", hh % 9)
        break

els = _scan_equip_arrays(payload, ps, pe)
print("scan elements:", len(els))
for e in els[:5]:
    print("  ", e)
