#!/usr/bin/env python3
"""精细分析采集帧之间的相似度：区分「真重复」和「同页面不同细节」。

dHash 会把同一页面的不同焦点状态/闪烁相位当成重复，但那些帧对训练有价值。
本脚本对每组近重复帧计算：
  - 全图像素差异率（ resized 64x36 后逐像素比较，>阈值算不同）
  - 各 YOLO 目标区域的差异（目标框位置/数量变化）
输出分组报告，辅助人工判断哪些该保留。
只读，不写任何文件。
"""
import argparse, re, itertools
from pathlib import Path
from PIL import Image, ImageChops

IMG_EXTS = {".jpg", ".jpeg", ".png"}


def fingerprint(p: Path, size=(64, 36)):
    im = Image.open(p).convert("RGB").resize(size)
    return list(im.getdata())


def diff_ratio(a, b, threshold=24):
    """逐像素平均绝对差 > threshold 的像素占比。"""
    n = len(a); diff = 0
    for (r1, g1, b1), (r2, g2, b2) in zip(a, b):
        if abs(r1-r2) + abs(g1-g2) + abs(b1-b2) > threshold:
            diff += 1
    return diff / n if n else 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--source", required=True)
    args = ap.parse_args()
    src = Path(args.source)
    files = [p for p in src.rglob("*") if p.suffix.lower() in IMG_EXTS]
    fps = {}
    for p in files:
        try:
            fps[p] = fingerprint(p)
        except Exception as e:
            print(f"跳过 {p}: {e}")
    paths = list(fps)
    print(f"共 {len(paths)} 张，开始两两对比（差异率 < 8% 视为近重复）…\n")
    # 用简单的 union-find 按近重复分组
    parent = {p: p for p in paths}
    def find(x):
        while parent[x] != x:
            parent[x] = parent[parent[x]]; x = parent[x]
        return x
    def union(a, b):
        ra, rb = find(a), find(b)
        if ra != rb: parent[rb] = ra
    pairs_diff = {}
    for i, a in enumerate(paths):
        for b in paths[i+1:]:
            d = diff_ratio(fps[a], fps[b])
            if d < 0.08:
                pairs_diff[(a, b)] = d
                union(a, b)
    groups = {}
    for p in paths:
        groups.setdefault(find(p), []).append(p)
    multi = [g for g in groups.values() if len(g) > 1]
    print(f"=== 近重复组 {len(multi)} 组（{sum(len(g) for g in multi)} 张）===")
    for g in sorted(multi, key=lambda x: -len(x)):
        print(f"\n组（{len(g)} 张）:")
        for p in sorted(g, key=str):
            rel = p.relative_to(src).as_posix()
            print(f"  {rel}")
        # 组内两两差异率
        for a, b in itertools.combinations(g, 2):
            d = pairs_diff.get((a,b), pairs_diff.get((b,a)))
            if d is not None:
                print(f"    差异 {a.name} vs {b.name}: {d:.3f}")
    print(f"\n=== 独立帧（{len(groups)-len(multi)} 张）各自保留 ===")


if __name__ == "__main__":
    main()
