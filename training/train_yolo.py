#!/usr/bin/env python3
"""训练 6 类 eFootball YOLO 并导出 ONNX（产出候选模型，不自动覆盖正式模型）。

前置：Python 3.10+ 且已安装 ultralytics（会带 torch / onnx）：
    python -m pip install ultralytics
本机若没有 GPU，建议把 training/dataset.zip 传到云端（Colab / 云 GPU）跑，CPU 跑 100 epoch 很慢。

标准流程（仓库根目录）：
    python training/prepare_dataset.py --pack      # 校验 + 并入负样本 + 打包
    python training/train_yolo.py --epochs 100     # 训练 + 导出
    python training/verify_model.py --frames training/negatives/images   # 校验导出 + 看误检分数
    # 确认没问题后（显式执行，默认绝不覆盖）：
    python training/train_yolo.py --promote

约束（改错会让运行时整套界面判定失效）：
  * 类别顺序固定为 models/yolo/classes.txt 的 6 行，data.yaml / label.txt 必须一致
    （prepare_dataset.py 会强制校验）。共享层 MatchBrain 硬编码了 "main_menu" 等类名。
  * 导出 ONNX 的输入尺寸由 YoloDetector 从元数据自动读取（动态维回退 640）；
    输出兼容 [1,N,6]（端到端 NMS-free）与 [1,4+nc,N]（常规预测头）两种。
  * 替换正式模型前必须跑全量测试：多个 *RealFrameTests 直接加载 yolo26n.onnx 断言结果，
    换模型后可能需要重新校准 Yolo.ScoreThreshold / MatchBrain.MinScore(0.45f)。
"""

from __future__ import annotations

import argparse
import re
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TRAINING = ROOT / "training"
DATA_YAML = TRAINING / "dataset" / "data.yaml"
CLASSES_FILE = ROOT / "src" / "EfootballBot" / "models" / "yolo" / "classes.txt"
OFFICIAL_MODEL = ROOT / "src" / "EfootballBot" / "models" / "yolo" / "yolo26n.onnx"
OUT_DIR = TRAINING / "out"
CANDIDATE = OUT_DIR / "yolo26n_candidate.onnx"


def fail(msg: str) -> None:
    print(f"[ERROR] {msg}", file=sys.stderr)
    raise SystemExit(1)


def class_order() -> list[str]:
    classes = [x.strip() for x in CLASSES_FILE.read_text(encoding="utf-8-sig").splitlines() if x.strip()]
    yaml_names: dict[int, str] = {}
    for line in DATA_YAML.read_text(encoding="utf-8-sig").splitlines():
        m = re.match(r"^\s*(\d+)\s*:\s*(\S+)\s*$", line)
        if m:
            yaml_names[int(m.group(1))] = m.group(2)
    yaml_order = [yaml_names[i] for i in sorted(yaml_names)]
    if yaml_order != classes:
        fail(f"data.yaml 与 classes.txt 类别顺序不一致：{yaml_order} vs {classes}")
    if len(classes) != 6:
        fail(f"类别数应为 6，实读 {len(classes)}；运行时 C# 侧按 6 类硬编码了类名")
    return classes


def promote() -> None:
    if not CANDIDATE.is_file():
        fail(f"没有候选模型 {CANDIDATE}，请先完成训练与导出")
    if OFFICIAL_MODEL.is_file():
        backup = OUT_DIR / "yolo26n_backup.onnx"
        shutil.copy2(OFFICIAL_MODEL, backup)
        print(f"[OK] 旧模型已备份到 {backup}")
    shutil.copy2(CANDIDATE, OFFICIAL_MODEL)
    print(f"[OK] 已替换 {OFFICIAL_MODEL}")
    print("      下一步必须执行：全量测试 + 实机验证（见 verify_model.py 输出与 README 说明）")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--weights", default="yolo26n.pt",
                    help="预训练权重；Ultralytics 首次会自动下载。也可用上次的 runs/.../best.pt 续训")
    ap.add_argument("--epochs", type=int, default=100)
    ap.add_argument("--imgsz", type=int, default=640)
    ap.add_argument("--batch", type=int, default=16)
    ap.add_argument("--device", default="", help="留空自动；CPU 写 cpu；NVIDIA 写 0")
    ap.add_argument("--project", default=str(TRAINING / "runs"))
    ap.add_argument("--name", default="efootball6")
    ap.add_argument("--promote", action="store_true",
                    help="仅把已导出的候选模型覆盖到正式路径（默认绝不覆盖）")
    args = ap.parse_args()

    classes = class_order()
    print(f"[OK] 类别顺序：{' / '.join(classes)}")

    if args.promote:
        promote()
        return

    try:
        from ultralytics import YOLO
    except ImportError:
        fail("缺少 ultralytics。请先执行：python -m pip install ultralytics\n"
             "（本机若无 GPU，建议把 training/dataset.zip 传到云端训练）")

    if not DATA_YAML.is_file():
        fail(f"缺少 {DATA_YAML}，先跑 prepare_dataset.py")
    for subset in ("train", "val"):
        imgs = list((TRAINING / "dataset" / "images" / subset).glob("*.*"))
        if not imgs:
            fail(f"dataset/images/{subset} 为空")

    OUT_DIR.mkdir(parents=True, exist_ok=True)
    model = YOLO(args.weights)
    train_kwargs = dict(
        data=str(DATA_YAML),
        epochs=args.epochs,
        imgsz=args.imgsz,
        batch=args.batch,
        project=args.project,
        name=args.name,
        exist_ok=True,
    )
    if args.device:
        train_kwargs["device"] = args.device
    print(f"[..] 开始训练：{train_kwargs}")
    model.train(**train_kwargs)

    print("[..] 导出 ONNX（端到端 NMS-free 输出 [1,N,6]，YoloDetector 原生兼容）")
    exported = model.export(format="onnx", imgsz=args.imgsz, dynamic=False, simplify=False)
    src = Path(exported)
    if not src.is_file():
        fail(f"导出未产出文件：{src}")
    shutil.copy2(src, CANDIDATE)
    print(f"[OK] 候选模型：{CANDIDATE}（{CANDIDATE.stat().st_size / 1024 / 1024:.1f} MB）")
    print("[OK] 正式模型未被改动。接下来：")
    print("     python training/verify_model.py --frames training/negatives/images")
    print("     dotnet test tests/EfootballBot.Tests/EfootballBot.Tests.csproj")
    print("     确认后再执行：python training/train_yolo.py --promote")


if __name__ == "__main__":
    main()
