# -*- coding: utf-8 -*-
"""定位 Python 侧 guid_paths 关键路径（CharMoveComp / Ragdoll）首次出现的 bunch，
转储该 bunch 的载荷与 exports，供 C# 侧 Sna.ParsePrefix 对照。

用法: python probe_paths.py <journal.jsonl> <out.json>
"""
import json
import sys
import tempfile
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]          # 项目根（CC雷达/ 的上一级）
SRC = ROOT / "源码" / "Parser"
CORE = SRC / "ErCore" / "src" / "core"
sys.path.insert(0, str(SRC))
sys.path.insert(0, str(CORE))

import gateway_parser as gp          # noqa: E402
from liveview import LiveView, LiveDetector, _scan_equip_arrays   # noqa: E402
import liveview as _lv               # noqa: E402
import sna                           # noqa: E402
import numpy as np                   # noqa: E402

scan_log = []
_orig_scan = _lv._scan_equip_arrays


def _patched_scan(payload, ps, pe):
    r = _orig_scan(payload, ps, pe)
    if len(r) > 0:
        scan_log.append((int(ps), int(pe), len(r)))
    return r


_lv._scan_equip_arrays = _patched_scan

TARGETS = {"CharMoveComp", "Ragdoll"}
TARGET_GUIDS = {2362, 2640}
TARGET_SEQS = {18922, 19616, 55504}
found = []
ps_found = []
extra_found = []
orig_acc = LiveView._accumulate_paths
orig_plb = LiveView._handle_player_lb
orig_det_poll = LiveDetector.poll
orig_fsm = LiveView._decode_fsm_incremental


def _scan_one(x, source):
    """按 LiveDetector.poll / FSM 处理器的口径解析单个 bunch，
    命中目标路径或目标 guid 时转储。"""
    bits = np.unpackbits(np.frombuffer(x.payload, dtype=np.uint8), bitorder="little")[:x.pbits]
    try:
        info, _ss = sna.parse_prefix(bits, x.hx, x.hm, quantize_bit=False, frags=x.frags)
    except Exception:
        return
    exports = info.get("exports", [])
    hit = any(e.get("path") in TARGETS for e in exports) \
        or any(e.get("guid") in TARGET_GUIDS and e.get("path") for e in exports)
    if hit:
        extra_found.append({
            "source": source,
            "seq": x.seq, "ch": x.ch, "pbits": x.pbits,
            "open": int(x.open), "close": int(x.close),
            "hx": int(x.hx), "hm": int(x.hm),
            "frags": [[int(f[0]), int(f[1]), int(f[2])] for f in (x.frags or [])],
            "hex": bytes(x.payload).hex(),
            "exports": exports,
        })


def patched_det_poll(self, max_frames=None, early_frames=None):
    # 先跑原逻辑，再按其口径补扫（path_idx 已推进，需自扫本窗口）。
    r = orig_det_poll(self, max_frames=max_frames, early_frames=early_frames)
    b = self.loader.b
    for ci, lst in b.items():
        idx = self._path_idx.get(ci, 0)
        for x in lst[idx:]:
            if not x.open or not x.hx:
                continue
            _scan_one(x, "detector")
    return r


def patched_fsm(self, new_lbs, actor_by_lb=None):
    # 记录本视图的 catalog id → 类名（repdec6 包装反查用）。
    global _cat_names
    _cat_names = {id(cat): name for name, cat in self._subcats.items()}
    for x in new_lbs:
        if x.seq in TARGET_SEQS:
            # 转储目标 bunch 的 tile 块与 c2cat 路由结果（Python 视角）。
            try:
                from blocks import tile as _tile
                try:
                    bits_ = np.unpackbits(np.frombuffer(x.payload, dtype=np.uint8), bitorder="little")[:x.pbits]
                    info_, ss_ = sna.parse_prefix(bits_, x.hx, x.hm, quantize_bit=False, frags=x.frags)
                    parse_ss = int(ss_)
                except Exception:
                    parse_ss = 0
                    info_ = {}
                blk_ = _tile(x.payload, x.pbits, parse_ss, partial_ok=True) or []
                extra_found.append({
                    "source": "targetseq",
                    "seq": x.seq, "ch": x.ch, "pbits": x.pbits,
                    "open": int(x.open), "close": int(x.close),
                    "hx": int(x.hx), "hm": int(x.hm),
                    "frags": [[int(f[0]), int(f[1]), int(f[2])] for f in (x.frags or [])],
                    "hex": bytes(x.payload).hex(),
                    "actor_ch": self._fsm_ch_actor.get(x.ch),
                    "actor_lb": actor_by_lb.get(id(x)) if actor_by_lb else None,
                    "parse_ss": parse_ss,
                    "blocks": [{
                        "ps": int(b.get("payload_start") or 0), "pe": int(b.get("payload_end") or 0),
                        "is_actor": int(b.get("is_actor") or 0), "has_rep": int(b.get("has_rep") or 0),
                        "stably": int(b.get("stably") or 0),
                        "sub": (int(b["sub"]) if b.get("sub") is not None else None),
                        "cls": (int(b["cls"]) if b.get("cls") is not None else None),
                        "deleted": bool(b.get("deleted")),
                        "cat": self._cls2cat.get(b.get("sub") if b.get("stably") else b.get("cls")),
                        "scan": (lambda _ps, _pe: _safe_scan(x.payload, _ps, _pe))(
                            b.get("payload_start") or 0, b.get("payload_end") or 0)
                            if (b.get("payload_end") or 0) - (b.get("payload_start") or 0) >= 250 else -1,
                    } for b in blk_],
                })
            except Exception as e:
                print(f"capture error seq={x.seq} ch={x.ch}: {type(e).__name__}: {e}", file=sys.stderr)
        if x.hx or x.hm:
            _scan_one(x, "fsm")
    return orig_fsm(self, new_lbs, actor_by_lb)


def _safe_scan(payload, ps, pe):
    try:
        return len(_scan_equip_arrays(payload, ps, pe))
    except Exception as e:
        return f"ERR:{type(e).__name__}"


_cat_names = {}


def patched(self, new_lbs):
    for x in new_lbs:
        if self.mra_locked and x.ch == self.mra_ch:
            continue
        if not x.frags and (not x.open or not x.hx):
            continue
        bits = np.unpackbits(np.frombuffer(x.payload, dtype=np.uint8), bitorder="little")[:x.pbits]
        try:
            info, _ss = sna.parse_prefix(bits, x.hx, x.hm, quantize_bit=False, frags=x.frags)
        except Exception:
            continue
        paths = [e.get("path") for e in info.get("exports", [])]
        if any(p in TARGETS for p in paths):
            found.append({
                "seq": x.seq, "ch": x.ch, "pbits": x.pbits,
                "open": int(x.open), "close": int(x.close),
                "hx": int(x.hx), "hm": int(x.hm),
                "frags": [[int(f[0]), int(f[1]), int(f[2])] for f in (x.frags or [])],
                "hex": bytes(x.payload).hex(),
                "exports": info.get("exports", []),
            })
    return orig_acc(self, new_lbs)


def patched_plb(self, x):
    from replication_header import exports_prefix as ep
    try:
        exports, _start = ep(x)
        hit = any(e.get("guid") in TARGET_GUIDS and e.get("path") for e in exports)
        if hit:
            ps_found.append({
                "seq": x.seq, "ch": x.ch, "pbits": x.pbits,
                "open": int(x.open), "close": int(x.close),
                "hx": int(x.hx), "hm": int(x.hm),
                "frags": [[int(f[0]), int(f[1]), int(f[2])] for f in (x.frags or [])],
                "hex": bytes(x.payload).hex(),
                "exports": exports,
                "guid_path_before": {g: self.guid_paths.get(g) for g in TARGET_GUIDS},
            })
    except Exception:
        pass
    return orig_plb(self, x)


LiveView._accumulate_paths = patched
LiveView._handle_player_lb = patched_plb
LiveDetector.poll = patched_det_poll
LiveView._decode_fsm_incremental = patched_fsm

health_src_count = {}
orig_rh = LiveView._record_health


def patched_rh(self, actor, name, value, seq, source="attribute_set"):
    health_src_count[source] = health_src_count.get(source, 0) + 1
    return orig_rh(self, actor, name, value, seq, source=source)


LiveView._record_health = patched_rh

import repdec6                        # noqa: E402
cat_blk_count = {}
orig_decode_block = repdec6.decode_block


def patched_decode_block(payload, block, catalog, meta, **kw):
    r = orig_decode_block(payload, block, catalog, meta, **kw)
    key = _cat_names.get(id(catalog), f"cat@{id(catalog) % 10000}")
    ev, _pos, status = r
    cat_blk_count[f"{key}:{status}"] = cat_blk_count.get(f"{key}:{status}", 0) + 1
    return r


repdec6.decode_block = patched_decode_block


def main():
    journal = Path(sys.argv[1])
    out = Path(sys.argv[2])
    tmp = Path(tempfile.mkdtemp(prefix="ccradar_probe_"))
    try:
        engine = gp.Engine(tmp, replay=True)
        n = 0
        for line in journal.read_text(encoding="utf-8", errors="replace").splitlines():
            if not line.strip():
                continue
            try:
                event = json.loads(line)
            except json.JSONDecodeError:
                continue
            try:
                engine.accept(event)
            except Exception as e:
                print("accept error:", type(e).__name__, str(e)[:120], file=sys.stderr)
            n += 1
        # 排空所有 flow（模拟 retire 的排空逻辑）。
        gp_targets = {}
        for key, flow in engine.flows.items():
            while not flow.loader.exhausted():
                flow.view.poll(max_frames=256)
            for g in (2362, 2542, 2640, 978, 1348, 1576, 1690, 1806, 2118, 2460):
                p = flow.view.guid_paths.get(g)
                if p is not None:
                    gp_targets[f"flow{flow.number}_guid{g}"] = p
            print(f"flow{flow.number}: fsm={len(flow.view.fsm_state)} "
                  f"health={len(flow.view.health)} equip={len(flow.view.equip)} "
                  f"aiNames={len(flow.view.ai_names)} players={len(flow.view.players)} "
                  f"healthSrc={health_src_count}")
            print(f"flow{flow.number} equipKeys={sorted(flow.view.equip.keys())}")
            print(f"flow{flow.number} fsmChActor={dict(sorted(flow.view._fsm_ch_actor.items()))}")
            for a, er in sorted(flow.view.equip.items()):
                uids = sorted(er.get("items", {}).keys())
                print(f"  equip[{a}]: armor={len(er.get('armor', {}))} items={len(er.get('items', {}))} uids={uids}")
        print(f"events={n} flows={len(engine.flows)}")
        print("scanLog=" + json.dumps(scan_log))
        print("guid check:", json.dumps(gp_targets, ensure_ascii=False))
        out.write_text(json.dumps({
            "guidPaths": gp_targets,
            "bunches": found,
            "psBunches": ps_found,
            "extraBunches": extra_found,
        }, ensure_ascii=False, indent=1), encoding="utf-8")
        print(f"found {len(found)} acc / {len(ps_found)} ps / {len(extra_found)} extra -> {out}")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    main()
