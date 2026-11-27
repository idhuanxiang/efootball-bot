#!/usr/bin/env python3
"""训练 6 类 eFootball YOLO 并导出 ONNX（产出候选模型，不自动覆盖正式模型）。

前置：Python 3.10+ 且已安装 ultralytics（会带 torch / onnx）：
    python -m pip install ultralytics
本机若没有 GPU，建议把 training/dataset.zip 传到云端（Colab / 云 GPU）跑，CPU 跑 100 epoch 很慢。

标准流程（仓库根目录）：
    python training/prepare_dataset.py --pack      # 校验 + 并入负样本 + 打包
    # Colab / 云 GPU：直接打开 training/cloud_train.ipynb，上传 dataset.zip 后运行全部单元格
    python training/train_yolo.py --epochs 100     # 本地/云主机命令行训练 + 导出
    python training/verify_model.py --frames training/negatives/images   # 校验导出 + 看误检分数
    # 确认没问题后（显式执行，默认绝不覆盖）：
    python training/train_yolo.py --promote

体积默认（v8.0.15）：默认导出 **FP16**（half=True），约 4.7MB 而不是 FP32 的 9.4MB。
  旧的自训练模型本来就是 FP16；本机 FP32/FP16 对比实测 13 帧最大差 0.001，
  输出格式与 C# / sidecar 完全兼容。CPU-only 环境 half 导出失败会自动回退 FP32，
  也可用 --fp32 显式指定。Colab notebook 同步。

隐私默认（每次导出都自动执行，不需要额外参数）：
  * 关闭 Ultralytics 匿名遥测（settings sync=False），本机训练不外发版本/环境/超参事件。
  * 导出后立即用 scrub_onnx.py 清洗 ONNX 元数据：Ultralytics 会把 data.yaml 的
    绝对路径和本地时间写进 description/date，明文随模型发布；清洗只动元数据，
    权重与推理数值逐位不变（实测 13 帧分数一致）。
  * --promote 上架前再审计一次，含本机痕迹直接拒绝；verify_model.py 结构校验也会审计。

云端 vs 本地一致性：
  * 决定效果的是数据/权重/超参/版本，不是训练地点。同一个导出的 .onnx 拿回本地，
    运行时读到的是完全相同的权重，推理效果与"在哪训的"无关。
  * 但"两次独立训练"不会逐位相同（GPU 非确定性、AMP/TF32、数据增强随机顺序）。
    要尽量对齐：两边用同一份 dataset.zip、同一个 weights 文件、同一 ultralytics 版本，
    并显式固定 --epochs/--imgsz/--batch/--seed；需要严格复现再加 --deterministic。
  * 别用 Ultralytics HUB 的一键托管训练代替本脚本：它会套自己的默认超参/权重，
    结果可能与本地明显不同。
  * 最容易出差别的是**导出**：不同 ultralytics 版本可能导出 [1,N,6]（端到端 NMS-free）
    而不是现网的 [1,10,8400]（常规预测头）。C# 两种都兼容，但置信度分布会变，
    所以导出后必须跑 verify_model.py 重新量基线帧，必要时校准 MinScore。

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
    # 上架前再审计一次，避免手工替换/旧候选把带本机路径的元数据带进发布包
    from scrub_onnx import audit
    leftovers = audit(CANDIDATE)
    if leftovers:
        fail(f"候选模型含本机痕迹，拒绝上架（先跑 python training/scrub_onnx.py {CANDIDATE}）："
             f"{leftovers[:3]}")
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
    ap.add_argument("--freeze", type=int, default=0,
                    help="冻结前 N 层骨干网络（如 10 用于基于 best.pt 的快速增量微调；0=全量微调）")
    ap.add_argument("--device", default="", help="留空自动；CPU 写 cpu；NVIDIA 写 0")
    ap.add_argument("--workers", type=int, default=0,
                    help="dataloader 进程数；Windows 上默认 0，多进程常触发 bootstrapping 报错")
    ap.add_argument("--seed", type=int, default=0,
                    help="随机种子；云端与本地填同一个值，数据增强/打乱顺序才对齐")
    ap.add_argument("--deterministic", action="store_true",
                    help="强制 torch 确定性算法（更可复现但更慢，个别 GPU 算子可能不支持；默认关）")
    ap.add_argument("--project", default=str(TRAINING / "runs"))
    ap.add_argument("--name", default="efootball6")
    ap.add_argument("--fp32", action="store_true",
                    help="导出 FP32（默认 FP16：体积约一半 4.7MB vs 9.4MB，推理分数几乎逐位一致）")
    ap.add_argument("--promote", action="store_true",
                    help="仅把已导出的候选模型覆盖到正式路径（默认绝不覆盖）")
    args = ap.parse_args()

    classes = class_order()
    print(f"[OK] 类别顺序：{' / '.join(classes)}")

    if args.promote:
        promote()
        return

    try:
        from ultralytics import YOLO, settings as yolo_settings
    except ImportError:
        fail("缺少 ultralytics。请先执行：python -m pip install ultralytics\n"
             "（本机若无 GPU，建议把 training/dataset.zip 传到云端训练）")
    # 默认关闭 Ultralytics 匿名遥测（sync）：本机训练不向外发版本/环境/超参事件。
    # 只改本机 settings.json，不影响训练结果；用户显式改回 true 才会重新开启。
    try:
        if yolo_settings.get("sync", False):
            yolo_settings.update({"sync": False})
            print("[OK] 已关闭 Ultralytics 匿名遥测（sync=False）")
    except Exception as ex:
        print(f"[WARN] 关闭遥测失败（不影响训练）：{ex}", file=sys.stderr)

    if not DATA_YAML.is_file():
        fail(f"缺少 {DATA_YAML}，先跑 prepare_dataset.py")
    for subset in ("train", "val"):
        imgs = list((TRAINING / "dataset" / "images" / subset).glob("*.*"))
        if not imgs:
            fail(f"dataset/images/{subset} 为空")

    OUT_DIR.mkdir(parents=True, exist_ok=True)
    # Ultralytics 对 `path: .` 的解析随版本变化（8.4.216 实测把相对路径落到了仓库根，
    # 报 "images/val not found"）。训练时用一份带绝对路径的临时 yaml，
    # 原始 dataset/data.yaml 保持 `path: .` 不变（dataset.zip 的 HUB 上传格式不能动）。
    train_yaml = OUT_DIR / "data_train.yaml"
    rewritten = []
    for line in DATA_YAML.read_text(encoding="utf-8-sig").splitlines():
        if re.match(r"^\s*path\s*:", line):
            rewritten.append(f"path: {DATA_YAML.parent.as_posix()}")
        else:
            rewritten.append(line)
    train_yaml.write_text("\n".join(rewritten) + "\n", encoding="utf-8")
    print(f"[OK] 训练用 data.yaml：{train_yaml}（path={DATA_YAML.parent.as_posix()}）")

    model = YOLO(args.weights)
    train_kwargs = dict(
        data=str(train_yaml),
        epochs=args.epochs,
        imgsz=args.imgsz,
        batch=args.batch,
        workers=args.workers,         # Windows 默认 0，避免多进程 dataloader 报错
        seed=args.seed,               # 云端/本地一致性：固定数据增强与打乱顺序
        deterministic=args.deterministic,
        project=args.project,
        name=args.name,
        exist_ok=True,
        # 游戏 UI 按钮永不水平/垂直镜像或旋转：关闭 fliplr/flipud/degrees 防止把左下「B 返回」翻到右下污染 btn_confirm
        fliplr=0.0,
        flipud=0.0,
        degrees=0.0,
    )
    if args.freeze > 0:
        train_kwargs["freeze"] = args.freeze
    if args.device:
        train_kwargs["device"] = args.device
    print(f"[..] 开始训练：{train_kwargs}")
    model.train(**train_kwargs)

    best_pt = Path(args.project) / args.name / "weights" / "best.pt"
    if best_pt.is_file():
        shutil.copy2(best_pt, OUT_DIR / "yolo26n_best.pt")
        print(f"[OK] 已保存最优权重：{OUT_DIR / 'yolo26n_best.pt'}（可用于下次 --weights 增量续训）")

    print("[..] 导出 ONNX（640×640 静态形状；默认 FP16，体积约 FP32 的一半）")
    export_kw = dict(format="onnx", imgsz=args.imgsz, dynamic=False, simplify=False)
    if not args.fp32:
        export_kw["half"] = True
    try:
        exported = model.export(**export_kw)
    except Exception as ex:
        if "half" not in export_kw:
            raise
        # 个别 CPU-only 环境不支持 half 导出：回退 FP32，保证流程能跑完
        print(f"[WARN] FP16 导出失败，回退 FP32：{ex}", file=sys.stderr)
        export_kw.pop("half")
        exported = model.export(**export_kw)
    src = Path(exported)
    if not src.is_file():
        fail(f"导出未产出文件：{src}")
    shutil.copy2(src, CANDIDATE)
    # 默认隐私清洗：Ultralytics 会把 data.yaml 的绝对路径 + 本地时间写进 ONNX 元数据，
    # 明文随模型发布。权重不含训练图片，但这条字符串会泄露本机用户名/目录结构。
    # 清洗只动元数据，权重与推理数值逐位不变（实测 13 帧分数一致）。
    from scrub_onnx import audit, scrub
    leftovers = scrub(CANDIDATE)
    if leftovers:
        fail(f"导出模型清洗后仍残留本机痕迹：{leftovers[:3]}")
    print(f"[OK] 元数据已清洗（无本机路径/时间戳）；audit 残留 {len(audit(CANDIDATE))} 处")
    print(f"[OK] 候选模型：{CANDIDATE}（{CANDIDATE.stat().st_size / 1024 / 1024:.1f} MB）")
    print("[OK] 正式模型未被改动。接下来：")
    print("     python training/verify_model.py --frames training/negatives/images")
    print("     dotnet test tests/EfootballBot.Tests/EfootballBot.Tests.csproj")
    print("     确认后再执行：python training/train_yolo.py --promote")


if __name__ == "__main__":
    main()
