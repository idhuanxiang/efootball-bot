#!/usr/bin/env python3
"""把 training/negatives/ 的困难负样本并入 training/dataset/，只依赖 Python 标准库。

用法（仓库根目录）：
    python training/prepare_dataset.py
    python training/prepare_dataset.py --pack        # 额外生成 dataset.zip 供云端训练上传
    python training/prepare_dataset.py --dry-run     # 只校验不写文件

设计要点：
  1. 幂等：同名图片/标签已存在就跳过，重复运行不会污染数据集。
  2. 顺序校验：data.yaml / label.txt / models/yolo/classes.txt 的 6 类顺序必须完全一致。
     共享层 MatchBrain 里硬编码了 "main_menu" / "item_row" / "btn_back" / "btn_confirm"，
     类别 id 错位会让整套界面判定失效。
  3. 标签校验：每行 5 列 class cx cy w h，class 在 0..nc-1，坐标 0..1。
     空文件合法（代表这一帧 6 类目标一个都没有）。
  4. 划分：新增帧按文件名稳定排序，每 10 张取 1 张进 val，其余进 train；
     已有 train/val 划分不动。
  5. 不修改、不删除原始图片（negatives/ 与 TestData/ 都是只读来源）。
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import shutil
import sys
import zipfile
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TRAINING = ROOT / "training"
DATASET = TRAINING / "dataset"
NEGATIVES = TRAINING / "negatives"
CLASSES_FILE = ROOT / "src" / "EfootballBot" / "models" / "yolo" / "classes.txt"
LABEL_FILE = TRAINING / "label.txt"
DATA_YAML = TRAINING / "dataset" / "data.yaml"
IMAGE_EXTS = {".jpg", ".jpeg", ".png", ".webp", ".bmp"}
VAL_EVERY = 10  # 每 10 张新增帧取 1 张进 val


def fail(msg: str) -> None:
    print(f"[ERROR] {msg}", file=sys.stderr)
    raise SystemExit(1)


def read_class_order() -> list[str]:
    def from_lines(p: Path) -> list[str]:
        return [x.strip() for x in p.read_text(encoding="utf-8-sig").splitlines() if x.strip()]

    yaml_names: dict[int, str] = {}
    for line in DATA_YAML.read_text(encoding="utf-8-sig").splitlines():
        m = re.match(r"^\s*(\d+)\s*:\s*(\S+)\s*$", line)
        if m:
            yaml_names[int(m.group(1))] = m.group(2)
    yaml_order = [yaml_names[i] for i in sorted(yaml_names)]

    orders = {
        "data.yaml": yaml_order,
        "label.txt": from_lines(LABEL_FILE),
        "classes.txt": from_lines(CLASSES_FILE),
    }
    for p in (DATA_YAML, LABEL_FILE, CLASSES_FILE):
        if not p.is_file():
            fail(f"缺少 {p}")
    first = orders["classes.txt"]
    for name, order in orders.items():
        if order != first:
            fail(f"类别顺序不一致：{name}={order}，classes.txt={first}。"
                 "类别 id 必须与训练时一致，否则运行时分类会全错。")
    if not first:
        fail("classes.txt 为空")
    print(f"[OK] 类别顺序一致（{len(first)} 类）：{' / '.join(first)}")
    return first


def validate_label(p: Path, nc: int) -> list[int]:
    ids: list[int] = []
    for lineno, raw in enumerate(p.read_text(encoding="utf-8-sig").splitlines(), 1):
        line = raw.strip()
        if not line:
            continue
        parts = line.split()
        if len(parts) != 5:
            fail(f"{p.name}:{lineno} 应为 5 列 'class cx cy w h'，实读 {len(parts)} 列：{line}")
        try:
            cls = int(parts[0])
            vals = [float(x) for x in parts[1:]]
        except ValueError:
            fail(f"{p.name}:{lineno} 含非数字：{line}")
        if not 0 <= cls < nc:
            fail(f"{p.name}:{lineno} class={cls} 越界（0..{nc - 1}）")
        if any(not 0.0 <= v <= 1.0 for v in vals):
            fail(f"{p.name}:{lineno} 坐标必须在 0..1：{line}")
        if vals[2] <= 0 or vals[3] <= 0:
            fail(f"{p.name}:{lineno} 宽高必须 > 0：{line}")
        ids.append(cls)
    return ids


def sha256(p: Path) -> str:
    h = hashlib.sha256()
    with p.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def existing_image_hashes() -> set[str]:
    out: set[str] = set()
    for subset in ("train", "val"):
        for p in (DATASET / "images" / subset).glob("*"):
            if p.suffix.lower() in IMAGE_EXTS:
                out.add(sha256(p))
    return out


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--dry-run", action="store_true", help="只校验，不复制/不打包")
    ap.add_argument("--pack", action="store_true", help="额外生成 training/dataset.zip")
    args = ap.parse_args()

    classes = read_class_order()
    nc = len(classes)
    img_dir, lbl_dir = NEGATIVES / "images", NEGATIVES / "labels"
    if not img_dir.is_dir() or not lbl_dir.is_dir():
        fail(f"缺少 {img_dir} 或 {lbl_dir}")

    manifest_path = NEGATIVES / "manifest.json"
    if manifest_path.is_file():
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        if manifest.get("class_order") != classes:
            fail("manifest.json 的 class_order 与 classes.txt 不一致")

    images = sorted(p for p in img_dir.iterdir() if p.suffix.lower() in IMAGE_EXTS)
    if not images:
        fail(f"{img_dir} 里没有图片")

    # 保证增量包再小也有 1 张进 val（否则 val 集看不到新类型，调阈值时容易被误导）
    val_indexes = {i for i in range(len(images)) if i % VAL_EVERY == VAL_EVERY - 1}
    val_indexes.add(len(images) - 1)

    hashes = existing_image_hashes()
    planned: list[tuple[Path, Path, str, list[int]]] = []
    skipped_dup: list[str] = []
    skipped_exists: list[str] = []
    for i, img in enumerate(images):
        label = lbl_dir / f"{img.stem}.txt"
        if not label.is_file():
            fail(f"图片缺少同名标签：{img.name} -> {label.name}")
        ids = validate_label(label, nc)
        digest = sha256(img)
        if digest in hashes:
            skipped_dup.append(img.name)
            continue
        already = any((DATASET / "images" / s / img.name).exists() for s in ("train", "val"))
        if already:
            skipped_exists.append(img.name)
            continue
        subset = "val" if i in val_indexes else "train"
        planned.append((img, label, subset, ids))
        hashes.add(digest)

    print(f"[OK] 标签校验通过：{len(images)} 帧；待并入 {len(planned)}，"
          f"内容重复跳过 {len(skipped_dup)}，同名已存在跳过 {len(skipped_exists)}")
    if skipped_dup:
        print("     内容重复：" + ", ".join(skipped_dup))
    for img, _lbl, subset, _ids in planned:
        print(f"     -> {subset}/{img.name}")

    # 合并后的类别分布（含原数据集）
    dist: Counter[int] = Counter()
    for subset in ("train", "val"):
        for p in (DATASET / "labels" / subset).glob("*.txt"):
            dist.update(validate_label(p, nc))
    for _img, _lbl, _subset, ids in planned:
        dist.update(ids)
    print("[OK] 合并后各类别框数：" + ", ".join(f"{classes[i]}={dist[i]}" for i in range(nc)))
    if dist[classes.index("main_menu")] == 0:
        fail("main_menu 正样本为 0：只补负样本会把这一类训废")

    if args.dry_run:
        print("[DRY-RUN] 未写入任何文件")
        return

    for img, label, subset, _ids in planned:
        (DATASET / "images" / subset).mkdir(parents=True, exist_ok=True)
        (DATASET / "labels" / subset).mkdir(parents=True, exist_ok=True)
        shutil.copy2(img, DATASET / "images" / subset / img.name)
        shutil.copy2(label, DATASET / "labels" / subset / label.name)
    n_train = len(list((DATASET / "images" / "train").glob("*.*")))
    n_val = len(list((DATASET / "images" / "val").glob("*.*")))
    print(f"[OK] 已并入 {len(planned)} 帧；数据集现有 train={n_train} val={n_val}")

    # 图片/标签一一对应检查
    for subset in ("train", "val"):
        stems = {p.stem for p in (DATASET / "images" / subset).iterdir()
                 if p.suffix.lower() in IMAGE_EXTS}
        labels = {p.stem for p in (DATASET / "labels" / subset).glob("*.txt")}
        missing = stems - labels
        if missing:
            fail(f"{subset} 有 {len(missing)} 张图缺标签：" + ", ".join(sorted(missing)[:8]))

    if args.pack:
        out = TRAINING / "dataset.zip"
        with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
            for p in sorted(DATASET.rglob("*")):
                if p.is_file():
                    z.write(p, p.relative_to(DATASET.parent))
        print(f"[OK] 已打包 {out}（{out.stat().st_size / 1024 / 1024:.1f} MB），可上传云端训练")


if __name__ == "__main__":
    main()
