#!/usr/bin/env python3
"""ONNX 模型元数据隐私清洗（默认动作，所有训练/导出路径都要过这一步）。

Ultralytics 导出时会把 `data.yaml` 路径写进 metadata_props["description"]，
本机训练就会留下  C:\\Users\\<用户名>\\...  这种绝对路径，还带本地时间戳。
模型权重本身不含任何训练图片，但元数据是明文字符串，会随模型一起发布。
本模块做两件事：

  * scrub(): 把元数据收敛到白名单（结构/类别所需），description 换中性文案，
    删除 date 等本地痕迹；权重张量不动，推理结果逐位不变。
  * audit(): 扫描整个 ONNX 二进制里的可打印字符串，发现本机路径/用户名即报出。

独立使用：
    python training/scrub_onnx.py <model.onnx>            # 清洗
    python training/scrub_onnx.py <model.onnx> --check     # 只审计不修改
train_yolo.py 导出后自动调用 scrub + audit；verify_model.py 结构校验时自动 audit。
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

NEUTRAL_DESCRIPTION = "Ultralytics YOLO model trained on the eFootball 6-class dataset"

# 运行时/排查真正需要的元数据；其它一律不保留（防止以后版本再塞入路径或环境信息）
KEEP_KEYS = (
    "author", "version", "license", "docs", "task", "head", "stride",
    "imgsz", "batch", "names", "channels", "end2end", "args",
)

# 本机痕迹特征：Windows 盘符路径、常见用户目录、用户名、项目路径、临时 yaml 名
# 只放通用模式，不写具体用户名/用户目录名（检测规则本身也不该携带个人信息）
LOCAL_MARKERS = (
    ":\\Users", ":/Users", "/home/", "/Users/", "\\AppData\\",
    "WorkBuddy", "efootball-bot", "data_train.yaml", "training\\out", "training/out",
    "clipboard-images", "Local\\Temp", "Local/Temp",
)


def audit(path: str | Path) -> list[str]:
    """返回模型二进制里所有含本机痕迹的可打印字符串（去重、按字典序）。"""
    data = Path(path).read_bytes()
    strings = [s.decode("ascii", "replace") for s in re.findall(rb"[ -~]{8,}", data)]
    hits = {s for s in strings if any(m in s for m in LOCAL_MARKERS)}
    return sorted(hits)


def scrub(path: str | Path, description: str = NEUTRAL_DESCRIPTION) -> list[str]:
    """清洗元数据并返回清洗后仍残留的本地痕迹（正常应为空列表）。"""
    try:
        import onnx
    except ImportError as ex:
        raise SystemExit(f"缺少 onnx（{ex}）。请执行 python -m pip install onnx") from ex

    p = Path(path)
    model = onnx.load(str(p))
    props = {
        item.key: item.value.decode("utf-8", "replace") if isinstance(item.value, bytes) else str(item.value)
        for item in model.metadata_props
    }

    cleaned: list[tuple[str, str]] = [("description", description)]
    cleaned += [(k, props[k]) for k in KEEP_KEYS if k in props]
    # args 里若含本机路径也一并中性化（Ultralytics 某些版本会写 Windows 或 POSIX data 路径）
    def _scrub_args(val: str) -> str:
        val = re.sub(r"[A-Za-z]:[\\\\/][^'\" ,}]+", "<path>", val)
        return re.sub(r"/(?:home|Users|content|tmp|var|opt|root)/[^'\" ,}]+", "<path>", val)

    cleaned = [
        (k, _scrub_args(v)) if k == "args" else (k, v)
        for k, v in cleaned
    ]

    del model.metadata_props[:]
    for key, value in cleaned:
        item = model.metadata_props.add()
        item.key = key
        item.value = value
    if model.doc_string:
        model.doc_string = ""
    onnx.save(model, str(p))
    return audit(p)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("model")
    ap.add_argument("--check", action="store_true", help="只审计，不修改文件")
    args = ap.parse_args()

    p = Path(args.model)
    if not p.is_file():
        raise SystemExit(f"模型不存在：{p}")
    before = audit(p)
    if args.check:
        print(f"[AUDIT] {p.name}: {len(before)} 处本地痕迹")
        for s in before:
            print("  ", repr(s[:220]))
        raise SystemExit(1 if before else 0)

    if not before:
        print(f"[OK] {p.name} 本来就干净，无需清洗")
        return
    print(f"[..] {p.name} 发现 {len(before)} 处本地痕迹，开始清洗")
    for s in before:
        print("  ", repr(s[:220]))
    after = scrub(p)
    if after:
        print("[ERROR] 清洗后仍有残留：", file=sys.stderr)
        for s in after:
            print("  ", repr(s[:220]), file=sys.stderr)
        raise SystemExit(1)
    print("[OK] 清洗完成，元数据只剩白名单字段；权重未改动")


if __name__ == "__main__":
    main()
