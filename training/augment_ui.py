#!/usr/bin/env python3
"""Generate synthetic UI-drift training samples (titlebar / dimmed / OSD) in training/dataset/images/train.

Idempotent: skips any file whose stem already starts with `aug_` and never touches `val/`.
Requires Pillow (installed in CI and Colab; prints a clear message if missing locally).
"""

from __future__ import annotations

import argparse
import collections
import pathlib
import random
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
DEFAULT_DATASET = ROOT / "training" / "dataset"
IMAGE_EXTS = {".png", ".jpg", ".jpeg", ".webp", ".bmp"}


def transform_titlebar(img, labels: list[tuple[int, float, float, float, float]], rng: random.Random):
    from PIL import Image, ImageDraw

    w, h = img.size
    bar_h = rng.randint(32, 42)
    new_h = h + bar_h
    dark = rng.random() < 0.7
    bg = (24, 24, 28) if dark else (242, 242, 246)
    fg = (225, 225, 230) if dark else (30, 30, 36)

    canvas = Image.new("RGB", (w, new_h), bg)
    canvas.paste(img.convert("RGB"), (0, bar_h))
    draw = ImageDraw.Draw(canvas)
    draw.text((12, max(6, bar_h // 2 - 6)), "eFootball", fill=fg)
    # Top-right window control glyphs
    for idx, ch in enumerate(("-", "[]", "X")):
        draw.text((w - 84 + idx * 26, max(6, bar_h // 2 - 6)), ch, fill=fg)

    out_labels = []
    for cls, cx, cy, bw, bh in labels:
        new_cy = (bar_h + cy * h) / new_h
        new_bh = (bh * h) / new_h
        out_labels.append((cls, cx, new_cy, bw, new_bh))
    return canvas, out_labels


def transform_dimmed(img, labels: list[tuple[int, float, float, float, float]], rng: random.Random):
    from PIL import ImageEnhance

    factor = rng.uniform(0.45, 0.65)
    out = ImageEnhance.Brightness(img.convert("RGB")).enhance(factor)
    return out, labels


def transform_osd(img, labels: list[tuple[int, float, float, float, float]], rng: random.Random):
    from PIL import ImageDraw

    out = img.convert("RGB").copy()
    w, h = out.size
    draw = ImageDraw.Draw(out)
    strip_h = max(20, int(h * 0.04))
    x0 = int(w * 0.30)
    x1 = int(w * 0.72)
    draw.rectangle([x0, 0, x1, strip_h], fill=(12, 12, 16))
    fps = rng.choice([60, 120, 144])
    cpu = rng.randint(58, 84)
    gpu = rng.randint(55, 83)
    draw.text((x0 + 10, max(2, strip_h // 2 - 6)), f"FPS {fps}  CPU {cpu}C  GPU {gpu}C", fill=(120, 240, 120))
    return out, labels


def parse_labels(lbl_path: pathlib.Path) -> list[tuple[int, float, float, float, float]]:
    out = []
    if not lbl_path.is_file():
        return out
    for raw in lbl_path.read_text(encoding="utf-8-sig").splitlines():
        parts = raw.strip().split()
        if len(parts) != 5:
            continue
        try:
            out.append((int(parts[0]), float(parts[1]), float(parts[2]), float(parts[3]), float(parts[4])))
        except ValueError:
            continue
    return out


def format_labels(labels: list[tuple[int, float, float, float, float]]) -> str:
    return "".join(f"{c} {cx:.6f} {cy:.6f} {bw:.6f} {bh:.6f}\n" for c, cx, cy, bw, bh in labels)


def main() -> int:
    ap = argparse.ArgumentParser(description="Generate synthetic UI-drift samples for YOLO training.")
    ap.add_argument("--dataset", type=pathlib.Path, default=DEFAULT_DATASET)
    ap.add_argument("--ratio", type=float, default=0.35, help="Fraction of positive train frames to augment")
    ap.add_argument("--seed", type=int, default=0)
    args = ap.parse_args()

    try:
        from PIL import Image
    except ImportError:
        print("[ERROR] Pillow is required for augment_ui.py (pip install pillow)", file=sys.stderr)
        return 1

    img_dir = args.dataset / "images" / "train"
    lbl_dir = args.dataset / "labels" / "train"
    if not img_dir.is_dir() or not lbl_dir.is_dir():
        print(f"[ERROR] Missing train images/labels under {args.dataset}", file=sys.stderr)
        return 1

    positives: list[tuple[pathlib.Path, pathlib.Path, list[tuple[int, float, float, float, float]]]] = []
    for p in sorted(img_dir.iterdir()):
        if not p.is_file() or p.suffix.lower() not in IMAGE_EXTS:
            continue
        if p.stem.startswith("aug_"):
            continue
        lbl = lbl_dir / f"{p.stem}.txt"
        boxes = parse_labels(lbl)
        if boxes:
            positives.append((p, lbl, boxes))

    rng = random.Random(args.seed)
    target_count = min(len(positives), max(0, int(round(len(positives) * args.ratio))))
    chosen = rng.sample(positives, target_count) if target_count > 0 else []
    variants = [("titlebar", transform_titlebar), ("dimmed", transform_dimmed), ("osd", transform_osd)]
    counts = collections.Counter()

    for idx, (img_path, _lbl_path, boxes) in enumerate(chosen):
        vname, vfn = variants[idx % len(variants)]
        out_stem = f"aug_{vname}_{img_path.stem}"
        out_img = img_dir / f"{out_stem}.jpg"
        out_lbl = lbl_dir / f"{out_stem}.txt"
        if out_img.exists():
            counts["skipped_existing"] += 1
            continue
        with Image.open(img_path) as im:
            aug_im, aug_boxes = vfn(im, boxes, rng)
            aug_im.save(out_img, format="JPEG", quality=92)
        out_lbl.write_text(format_labels(aug_boxes), encoding="utf-8")
        counts[vname] += 1

    print(f"[OK] UI drift augmentation on {len(positives)} positives (target={target_count}): {dict(counts)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
