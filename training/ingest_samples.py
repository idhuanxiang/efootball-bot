#!/usr/bin/env python3
"""Clean and ingest newly collected smart-sampling frames into training/dataset/.

Reads only from `known/` to avoid polluting YOLO labels with `review/` or `unknown/`.
Pure Python standard library (compatible with Python 3.10+ including 3.14).
"""

from __future__ import annotations

import argparse
import collections
import hashlib
import json
import pathlib
import shutil
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
DEFAULT_SRC = pathlib.Path(r"C:\Users\20603\Desktop\新建文件夹\data\samples\20261127")
DATASET = ROOT / "training" / "dataset"
IMAGE_EXTS = {".png", ".jpg", ".jpeg", ".webp", ".bmp"}
NEGATIVE_PAGES = {"InMatch", "BootSplash", "TitleScreen"}
NUM_CLASSES = 6
VAL_EVERY = 10


def sha256_file(path: pathlib.Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def parse_dhash(val: str | None) -> int | None:
    if not val:
        return None
    s = val.strip()
    if s.startswith(("0x", "0X")):
        s = s[2:]
    try:
        return int(s, 16)
    except ValueError:
        return None


def hamming(a: int, b: int) -> int:
    return (a ^ b).bit_count()


def existing_dataset_hashes() -> set[str]:
    out: set[str] = set()
    for subset in ("train", "val"):
        img_dir = DATASET / "images" / subset
        if not img_dir.is_dir():
            continue
        for p in img_dir.iterdir():
            if p.suffix.lower() in IMAGE_EXTS:
                out.add(sha256_file(p))
    return out


def sanitize_label_lines(
    lbl_path: pathlib.Path | None,
    page_id: str,
) -> tuple[list[str] | None, int]:
    """Return (sanitized_lines, dropped_bottom_right_btn_back_count) or (None, 0) to skip frame."""
    if lbl_path is None or not lbl_path.is_file():
        if page_id in NEGATIVE_PAGES:
            return [], 0
        return None, 0

    raw_lines = [l.strip() for l in lbl_path.read_text(encoding="utf-8-sig").splitlines() if l.strip()]
    if not raw_lines:
        if page_id in NEGATIVE_PAGES:
            return [], 0
        return None, 0

    kept: list[str] = []
    dropped_br_back = 0
    for line in raw_lines:
        parts = line.split()
        if len(parts) != 5:
            continue
        try:
            cls = int(parts[0])
            cx, cy, w, h = (float(x) for x in parts[1:])
        except ValueError:
            continue
        if not (0 <= cls < NUM_CLASSES):
            continue
        if not (0.0 <= cx <= 1.0 and 0.0 <= cy <= 1.0 and 0.0 < w <= 1.0 and 0.0 < h <= 1.0):
            continue
        # Drop bottom-right btn_back mislabel (cutscene '跳过' or btn_confirm)
        if cls == 1 and cx > 0.65 and cy > 0.78:
            dropped_br_back += 1
            continue
        kept.append(f"{cls} {cx:.6f} {cy:.6f} {w:.6f} {h:.6f}")

    if not kept and page_id not in NEGATIVE_PAGES:
        return None, dropped_br_back
    return kept, dropped_br_back


def uniform_cap(items: list[dict], max_count: int) -> list[dict]:
    if len(items) <= max_count:
        return items
    step = len(items) / max_count
    return [items[int(i * step)] for i in range(max_count)]


def main() -> int:
    ap = argparse.ArgumentParser(description="Clean & ingest smart-sampling frames into training/dataset.")
    ap.add_argument("--src", type=pathlib.Path, default=DEFAULT_SRC, help="Sample root directory")
    ap.add_argument("--max-per-page", type=int, default=45, help="Max samples to keep per page_id")
    ap.add_argument("--near-hamming", type=int, default=4, help="Near-duplicate dhash Hamming threshold")
    ap.add_argument("--dry-run", action="store_true", help="Print stats without copying files")
    args = ap.parse_args()

    known_dir = args.src / "known"
    if not known_dir.is_dir():
        print(f"[ERROR] Missing known/ directory under {args.src}", file=sys.stderr)
        return 1

    existing_hashes = existing_dataset_hashes()
    seen_exact_dhash: set[int] = set()
    page_seen_dhash: dict[str, list[int]] = collections.defaultdict(list)
    by_page: dict[str, list[dict]] = collections.defaultdict(list)

    stats = collections.Counter()
    class_counts = collections.Counter()

    metas = sorted(known_dir.rglob("*.meta.json"))
    for mp in metas:
        stats["scanned"] += 1
        try:
            meta = json.loads(mp.read_text(encoding="utf-8-sig"))
        except Exception:
            stats["bad_json"] += 1
            continue

        if meta.get("rejected_boxes") or meta.get("review_boxes"):
            stats["skipped_review_or_rejected"] += 1
            continue

        page_id = str(meta.get("page_id") or mp.parent.parent.name).strip()
        stem = mp.name[:-len(".meta.json")] if mp.name.endswith(".meta.json") else mp.stem
        img_dir = mp.parent.parent / "images"
        lbl_dir = mp.parent.parent / "labels"

        img_path = None
        for ext in (".png", ".jpg", ".jpeg", ".webp", ".bmp"):
            cand = img_dir / f"{stem}{ext}"
            if cand.is_file():
                img_path = cand
                break
        if img_path is None:
            stats["missing_image"] += 1
            continue

        dh = parse_dhash(meta.get("dhash"))
        if dh is not None:
            if dh in seen_exact_dhash:
                stats["skipped_exact_dhash"] += 1
                continue
            if any(hamming(dh, prev) <= args.near_hamming for prev in page_seen_dhash[page_id]):
                stats["skipped_near_dhash"] += 1
                continue

        digest = sha256_file(img_path)
        if digest in existing_hashes:
            stats["skipped_already_in_dataset"] += 1
            continue

        lbl_path = lbl_dir / f"{stem}.txt"
        lines, dropped_br = sanitize_label_lines(lbl_path, page_id)
        stats["dropped_bottom_right_btn_back"] += dropped_br
        if lines is None:
            stats["skipped_missing_or_empty_label"] += 1
            continue

        if dh is not None:
            seen_exact_dhash.add(dh)
            page_seen_dhash[page_id].append(dh)
        existing_hashes.add(digest)

        by_page[page_id].append({
            "page_id": page_id,
            "stem": stem,
            "img_path": img_path,
            "lines": lines,
        })

    added_train = 0
    added_val = 0
    skipped_existing_name = 0

    for page_id in sorted(by_page):
        items = sorted(by_page[page_id], key=lambda x: x["stem"])
        selected = uniform_cap(items, args.max_per_page)
        stats["capped_out"] += len(items) - len(selected)

        val_indices = {i for i in range(len(selected)) if i % VAL_EVERY == VAL_EVERY - 1}
        if len(selected) >= 5:
            val_indices.add(len(selected) - 1)

        for idx, item in enumerate(selected):
            subset = "val" if idx in val_indices else "train"
            out_stem = f"s20261127_{page_id}_{item['stem']}"
            dst_img = DATASET / "images" / subset / f"{out_stem}{item['img_path'].suffix.lower()}"
            dst_lbl = DATASET / "labels" / subset / f"{out_stem}.txt"

            if dst_img.exists() or dst_lbl.exists():
                skipped_existing_name += 1
                continue

            for line in item["lines"]:
                c = int(line.split()[0])
                class_counts[c] += 1

            if not args.dry_run:
                dst_img.parent.mkdir(parents=True, exist_ok=True)
                dst_lbl.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(item["img_path"], dst_img)
                dst_lbl.write_text("\n".join(item["lines"]) + ("\n" if item["lines"] else ""), encoding="utf-8")

            if subset == "val":
                added_val += 1
            else:
                added_train += 1

    print("=== Sample Ingestion Summary ===")
    print(f"Scanned known/*.meta.json      : {stats['scanned']}")
    print(f"Skipped review/rejected        : {stats['skipped_review_or_rejected']}")
    print(f"Skipped exact dhash dupes      : {stats['skipped_exact_dhash']}")
    print(f"Skipped near dhash (<= {args.near_hamming} bits) : {stats['skipped_near_dhash']}")
    print(f"Skipped already in dataset     : {stats['skipped_already_in_dataset']}")
    print(f"Skipped missing/empty labels   : {stats['skipped_missing_or_empty_label']}")
    print(f"Skipped existing target names  : {skipped_existing_name}")
    print(f"Capped out (>{args.max_per_page}/page)         : {stats['capped_out']}")
    print(f"Dropped bottom-right btn_back  : {stats['dropped_bottom_right_btn_back']}")
    print(f"Added to train                 : {added_train}")
    print(f"Added to val                   : {added_val}")
    print(f"Ingested box counts by class   : {dict(sorted(class_counts.items()))}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
