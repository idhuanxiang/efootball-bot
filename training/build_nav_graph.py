#!/usr/bin/env python3
"""Combine gamepad/keyboard transitions with cursor focus_shift metadata into navigation graphs.

Pure Python standard library. Outputs:
  - training/out/navigation_paths.json
  - training/out/navigation_paths.md
"""

from __future__ import annotations

import argparse
import collections
import datetime as dt
import json
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
DEFAULT_SRC = pathlib.Path(r"C:\Users\20603\Desktop\新建文件夹\data\samples\20261127")
OUT_DIR = ROOT / "training" / "out"


def parse_ts(s: str | None) -> dt.datetime | None:
    if not s:
        return None
    for fmt in ("%Y-%m-%d %H:%M:%S.%f", "%Y-%m-%d %H:%M:%S"):
        try:
            return dt.datetime.strptime(s.strip(), fmt)
        except ValueError:
            continue
    return None


def main() -> int:
    ap = argparse.ArgumentParser(description="Build combined gamepad + cursor navigation paths.")
    ap.add_argument("--src", type=pathlib.Path, default=DEFAULT_SRC)
    ap.add_argument("--out-dir", type=pathlib.Path, default=OUT_DIR)
    args = ap.parse_args()

    trans_file = args.src / "transitions.jsonl"
    if not trans_file.is_file():
        print(f"[ERROR] Missing {trans_file}", file=sys.stderr)
        return 1

    # Load all meta.json indexed by page_id with timestamp and focus_shift
    metas_by_page: dict[str, list[tuple[dt.datetime, dict]]] = collections.defaultdict(list)
    all_metas: list[tuple[dt.datetime, dict]] = []
    for mp in sorted(args.src.rglob("*.meta.json")):
        try:
            m = json.loads(mp.read_text(encoding="utf-8-sig"))
        except Exception:
            continue
        ts = parse_ts(m.get("captured_at"))
        if ts is None:
            continue
        pid = str(m.get("page_id") or "")
        metas_by_page[pid].append((ts, m))
        all_metas.append((ts, m))

    for pid in metas_by_page:
        metas_by_page[pid].sort(key=lambda x: x[0])
    all_metas.sort(key=lambda x: x[0])

    def find_closest_meta(page_id: str, target_ts: dt.datetime, max_sec: float = 1.5) -> dict | None:
        cands = metas_by_page.get(page_id, [])
        best_dt = max_sec
        best_meta = None
        for ts, m in cands:
            diff = abs((ts - target_ts).total_seconds())
            if diff <= best_dt:
                best_dt = diff
                best_meta = m
        return best_meta

    inter_edges: dict[tuple[str, str, str, str], int] = collections.Counter()
    intra_cursor: dict[tuple[str, str, str, str], list[tuple[float, float]]] = collections.defaultdict(list)

    matched_meta_count = 0
    total_transitions = 0

    for line in trans_file.read_text(encoding="utf-8-sig").splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            rec = json.loads(line)
        except Exception:
            continue
        total_transitions += 1
        ts = parse_ts(rec.get("Timestamp"))
        from_page = str(rec.get("FromPage") or "Unknown")
        action = str(rec.get("InputAction") or "OCR/Auto")
        to_page = str(rec.get("ToPage") or "Unknown")

        focus_dir = rec.get("FocusDirection") or ""
        focused_text = rec.get("FocusedText") or ""
        fx = rec.get("FocusX")
        fy = rec.get("FocusY")

        if not focus_dir and ts is not None:
            meta = find_closest_meta(to_page, ts)
            if meta is not None:
                fs = meta.get("focus_shift") or {}
                if fs.get("Moved") or fs.get("Direction"):
                    matched_meta_count += 1
                    focus_dir = str(fs.get("Direction") or "")
                    focused_text = str(fs.get("FocusedText") or "")
                    fx = fs.get("NewX")
                    fy = fs.get("NewY")

        if from_page != to_page:
            inter_edges[(from_page, action, focus_dir, to_page)] += 1
        elif focus_dir or focused_text:
            intra_cursor[(to_page, action, focus_dir, focused_text or "(unknown)")].append(
                (float(fx or 0.0), float(fy or 0.0))
            )

    # Also collect standalone focus_shift events directly from meta.json even without transition line
    for _, m in all_metas:
        fs = m.get("focus_shift") or {}
        if fs.get("Moved") or fs.get("Direction"):
            pid = str(m.get("page_id") or "")
            fdir = str(fs.get("Direction") or "")
            ftxt = str(fs.get("FocusedText") or "(unknown)")
            fx = float(fs.get("NewX") or 0.0)
            fy = float(fs.get("NewY") or 0.0)
            key = (pid, "SampleFocus", fdir, ftxt)
            if not any(k[0] == pid and k[2] == fdir and k[3] == ftxt and k[1] != "SampleFocus" for k in intra_cursor):
                intra_cursor[key].append((fx, fy))

    args.out_dir.mkdir(parents=True, exist_ok=True)
    json_out = {
        "total_transitions": total_transitions,
        "matched_focus_events": matched_meta_count,
        "inter_page_edges": [
            {
                "from_page": k[0],
                "action": k[1],
                "focus_direction": k[2] or None,
                "to_page": k[3],
                "count": cnt,
            }
            for k, cnt in sorted(inter_edges.items(), key=lambda x: (-x[1], x[0]))
        ],
        "intra_page_cursor_targets": [
            {
                "page_id": k[0],
                "action": k[1],
                "focus_direction": k[2],
                "focused_text": k[3],
                "count": len(pts),
                "avg_x": round(sum(p[0] for p in pts) / len(pts), 4),
                "avg_y": round(sum(p[1] for p in pts) / len(pts), 4),
            }
            for k, pts in sorted(intra_cursor.items(), key=lambda x: (-len(x[1]), x[0]))
        ],
    }

    (args.out_dir / "navigation_paths.json").write_text(
        json.dumps(json_out, ensure_ascii=False, indent=2), encoding="utf-8"
    )

    md_lines = [
        "# 手柄操作 + 光标移动综合导航路径",
        "",
        f"- 总跳转记录：`{total_transitions}` 条 | 关联光标位移：`{matched_meta_count}` 次",
        f"- 跨页面唯一路径：`{len(inter_edges)}` 条 | 页内光标焦点落点：`{len(intra_cursor)}` 类",
        "",
        "## 1. 跨页面跳转路径（按键 + 页面切换）",
        "",
        "| 起点页面 | 操作 | 光标方向 | 终点页面 | 次数 |",
        "|---|---|---|---|---:|",
    ]
    for e in json_out["inter_page_edges"][:60]:
        md_lines.append(
            f"| `{e['from_page']}` | `{e['action']}` | `{e['focus_direction'] or '-'}` | `{e['to_page']}` | {e['count']} |"
        )

    md_lines += [
        "",
        "## 2. 页内光标移动拓扑（方向 + 焦点词 + 归一化坐标）",
        "",
        "| 页面 | 触发方式 | 移动方向 | 焦点词 | 均值坐标 (X, Y) | 次数 |",
        "|---|---|---|---|---|---:|",
    ]
    for c in json_out["intra_page_cursor_targets"][:80]:
        md_lines.append(
            f"| `{c['page_id']}` | `{c['action']}` | `{c['focus_direction'] or '-'}` | {c['focused_text']} | ({c['avg_x']:.3f}, {c['avg_y']:.3f}) | {c['count']} |"
        )

    (args.out_dir / "navigation_paths.md").write_text("\n".join(md_lines) + "\n", encoding="utf-8")

    print("=== Navigation Path Extraction Summary ===")
    print(f"Total transitions        : {total_transitions}")
    print(f"Matched focus events     : {matched_meta_count}")
    print(f"Unique inter-page edges  : {len(inter_edges)}")
    print(f"Unique intra-page cursor : {len(intra_cursor)}")
    print(f"Written                  : {args.out_dir / 'navigation_paths.json'}")
    print(f"Written                  : {args.out_dir / 'navigation_paths.md'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
