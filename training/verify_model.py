#!/usr/bin/env python3
"""校验导出的 ONNX，并量化「周活动页被误检成 main_menu」是否压下去。

依赖：python -m pip install onnxruntime numpy pillow

用法：
    python training/verify_model.py
    python training/verify_model.py --frames training/negatives/images
    python training/verify_model.py --frames training/negatives/images --compare \
        src/EfootballBot/models/yolo/yolo26n.onnx

输出里 main_menu 一列是关键：负样本帧上该分数应低于 0.45（共享层 MatchBrain.MinScore），
否则运行时仍会把周活动卡片页判成主菜单。若只是略高于 0.45，优先调
Yolo.ScoreThreshold / MatchBrain.MinScore，不必急着再训一轮。

预处理与 C# YoloDetector 对齐：等比例缩放 + 114 灰边 letterbox、RGB、/255、NCHW。
仅用于离线评估，不参与产品运行时。
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CLASSES_FILE = ROOT / "src" / "EfootballBot" / "models" / "yolo" / "classes.txt"
CANDIDATE = ROOT / "training" / "out" / "yolo26n_candidate.onnx"
OFFICIAL = ROOT / "src" / "EfootballBot" / "models" / "yolo" / "yolo26n.onnx"
DETECTOR_THRESHOLD = 0.25   # AppConfig.Yolo.ScoreThreshold
CLASSIFY_THRESHOLD = 0.45   # MatchBrain.MinScore（共享层硬编码常量）
IMAGE_EXTS = {".jpg", ".jpeg", ".png", ".webp", ".bmp"}


def fail(msg: str) -> None:
    print(f"[ERROR] {msg}", file=sys.stderr)
    raise SystemExit(1)


def imports():
    try:
        import numpy as np
        import onnxruntime as ort
        from PIL import Image
        return np, ort, Image
    except ImportError as ex:
        fail(f"缺少依赖（{ex}）。请执行：python -m pip install onnxruntime numpy pillow")


def inspect(session, np, classes: list[str]) -> tuple[int, int, str]:
    inp = session.get_inputs()[0]
    out = session.get_outputs()[0]
    shape = [d if isinstance(d, int) else -1 for d in inp.shape]
    if len(shape) != 4 or shape[1] != 3:
        fail(f"输入应为 NCHW 且 3 通道，实读 {inp.shape}")
    h, w = shape[2], shape[3]
    if h <= 0 or w <= 0:
        fail(f"输入尺寸是动态维（{inp.shape}），C# 侧会回退 640；建议导出时固定 imgsz")
    oshape = [d if isinstance(d, int) else -1 for d in out.shape]
    if len(oshape) != 3:
        fail(f"输出应为 3 维，实读 {out.shape}")
    nc = len(classes)
    if oshape[2] == 6 or oshape[1] == 6:
        kind = "端到端 NMS-free [1,N,6]"
    elif oshape[1] == 4 + nc or oshape[2] == 4 + nc:
        kind = f"常规预测头 [1,{4 + nc},N]（{nc} 类）"
    else:
        fail(f"输出形状 {out.shape} 与 {nc} 类不匹配；C# YoloDetector 无法解析")
    print(f"[OK] 输入 {inp.name} {inp.shape} / 输出 {out.name} {out.shape} / {kind}")
    return w, h, kind


def letterbox(img, np, Image, w: int, h: int):
    src_w, src_h = img.size
    scale = min(w / src_w, h / src_h)
    nw, nh = round(src_w * scale), round(src_h * scale)
    resized = img.resize((nw, nh), Image.Resampling.BILINEAR)
    canvas = Image.new("RGB", (w, h), (114, 114, 114))
    canvas.paste(resized, ((w - nw) // 2, (h - nh) // 2))
    arr = np.asarray(canvas, dtype=np.float32) / 255.0
    return arr.transpose(2, 0, 1)[None, ...]


def max_class_score(raw, np, cls: int, nc: int, kind: str) -> tuple[float, int]:
    arr = np.asarray(raw)
    if arr.ndim == 3 and arr.shape[0] == 1:
        arr = arr[0]
    if "NMS-free" in kind:
        # [N,6] 或转置成 [6,N]
        if arr.shape[0] == 6 and arr.shape[1] != 6:
            arr = arr.T
        rows = arr  # [N,6]: cx,cy,w,h,score,cls
        cls_col, score_col = 5, 4
    else:
        # [4+nc,N] 或 [N,4+nc]
        if arr.shape[0] == 4 + nc and arr.shape[1] != 4 + nc:
            arr = arr.T
        rows = arr
        cls_col, score_col = None, 4 + cls
    if rows.ndim != 2:
        return 0.0, 0
    if cls_col is not None:
        hit = rows[(np.round(rows[:, cls_col]).astype(int) == cls) & (rows[:, score_col] >= DETECTOR_THRESHOLD)]
        return (float(hit[:, score_col].max()) if len(hit) else 0.0), len(hit)
    hit = rows[rows[:, score_col] >= DETECTOR_THRESHOLD]
    return (float(hit[:, score_col].max()) if len(hit) else 0.0), len(hit)


def evaluate(model: Path, frames: list[Path], np, ort, Image, classes: list[str]) -> None:
    if not model.is_file():
        fail(f"模型不存在：{model}")
    session = ort.InferenceSession(str(model), providers=["CPUExecutionProvider"])
    w, h, kind = inspect(session, np, classes)
    cls = classes.index("main_menu")
    print(f"\n=== {model.name}（main_menu id={cls}）===")
    # 判定列是中性描述：这组帧可能是负样本（期望"不触发"），
    # 也可能是真主菜单正样本（期望"触发"），别把正样本的高分写成"误判"。
    print(f"{'帧':<40} {'main_menu 最高分':>16} {'命中数':>8}  判定")
    for p in frames:
        with Image.open(p) as im:
            x = letterbox(im.convert("RGB"), np, Image, w, h)
        raw = session.run(None, {session.get_inputs()[0].name: x})[0]
        score, n = max_class_score(raw, np, cls, len(classes), kind)
        verdict = "触发分类" if score >= CLASSIFY_THRESHOLD else "不触发"
        print(f"{p.name:<40} {score:>16.3f} {n:>8}  {verdict}")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", default=str(CANDIDATE), help="待校验的 ONNX")
    ap.add_argument("--compare", default="", help="同时评估另一个模型（一般是现网正式模型）")
    ap.add_argument("--frames", default="", help="要评估的帧目录；不传则只做结构校验")
    args = ap.parse_args()

    np, ort, Image = imports()
    classes = [x.strip() for x in CLASSES_FILE.read_text(encoding="utf-8-sig").splitlines() if x.strip()]
    if "main_menu" not in classes:
        fail("classes.txt 里没有 main_menu")
    frames: list[Path] = []
    if args.frames:
        d = Path(args.frames)
        if not d.is_absolute():
            d = ROOT / d
        frames = sorted(p for p in d.iterdir() if p.suffix.lower() in IMAGE_EXTS)
        if not frames:
            fail(f"{d} 里没有图片")

    model = Path(args.model)
    if not model.is_absolute():
        model = ROOT / model
    if not frames:
        session = ort.InferenceSession(str(model), providers=["CPUExecutionProvider"])
        inspect(session, np, classes)
        print("[OK] 结构校验通过（未指定 --frames，跳过误检评估）")
        return
    if args.compare:
        other = Path(args.compare)
        if not other.is_absolute():
            other = ROOT / other
        evaluate(other, frames, np, ort, Image, classes)
    evaluate(model, frames, np, ort, Image, classes)
    print(f"\n判定线：检测器 {DETECTOR_THRESHOLD} / 共享分类 MinScore {CLASSIFY_THRESHOLD}。"
          "\n负样本帧（周活动页/比赛页）应「不触发」，真主菜单帧应「触发」；"
          "两边都要验，只压误报不够。若边界帧不稳，考虑提高阈值或再补同类帧重训。")


if __name__ == "__main__":
    main()
