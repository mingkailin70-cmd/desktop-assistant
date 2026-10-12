#!/usr/bin/env python3
"""Run a fixed, synthetic Chinese intent-classification pilot against a local Jev-Style model."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import statistics
import sys
import time
from pathlib import Path


LABELS = ("billing", "delivery", "account", "technical", "other")
MODEL_ID = "jev-style-v3-0.8b-decision-cpu-eval"
OPTIONS = {
    "billing": "付款、扣费、退款或发票问题",
    "delivery": "物流、配送、签收或收货地址问题",
    "account": "账号、登录、密码或绑定信息问题",
    "technical": "软件、设备、安装或功能故障",
    "other": "社交、一般告知或以上都不适用",
}
QUESTION = "这条合成消息主要属于哪个主题？"
CASES = (
    ("billing", "我的会员订阅被扣了两笔相同金额，麻烦帮我核对一下。"),
    ("billing", "退款状态显示成功了，但银行卡几天都没有到账。"),
    ("billing", "我已经关闭自动续费，昨天还是被扣了一次费用。"),
    ("billing", "刚才开发票时把抬头写错了，想申请重新开具。"),
    ("delivery", "快递物流三天没有更新，能帮我确认包裹现在到哪里了吗？"),
    ("delivery", "我选的是送货上门，快递员却把包裹放到了驿站。"),
    ("delivery", "订单显示已经签收，但我家门口和物业都没有找到包裹。"),
    ("delivery", "商品还没有发出，可以把收货地址改成公司吗？"),
    ("account", "我换了新手机，登录时一直收不到账号验证码。"),
    ("account", "账号提示有异地登录，我想先退出其他设备上的会话。"),
    ("account", "忘记了登录密码，而且原来的邮箱也已经停用了。"),
    ("account", "请问在哪里可以修改账号绑定的手机号码？"),
    ("technical", "Windows客户端更新后打开一直白屏，重装也没有好。"),
    ("technical", "点击导出按钮后没有反应，程序日志里出现了错误。"),
    ("technical", "我在设置中保存了网络信息，应用仍然显示离线。"),
    ("technical", "最新版启动时提示缺少组件，按提示安装后问题还在。"),
    ("other", "周日我从上海出发，大家中午约在哪里碰面？"),
    ("other", "我把周会材料放到共享文件夹了，发你参考一下。"),
    ("other", "收到，谢谢提醒，我会照计划处理。"),
    ("other", "今晚路上可能会堵车，我大概晚半小时到家。"),
)


def percentile_nearest_rank(values: list[float], percentile: float) -> float:
    ordered = sorted(values)
    return ordered[max(0, math.ceil(percentile * len(ordered)) - 1)]


def expected_calibration_error(confidences: list[float], correct: list[bool], bins: int = 5) -> float:
    total = len(confidences)
    result = 0.0
    for index in range(bins):
        lower = index / bins
        upper = (index + 1) / bins
        members = [
            (confidence, is_correct)
            for confidence, is_correct in zip(confidences, correct, strict=True)
            if lower <= confidence < upper or (index == bins - 1 and confidence == 1.0)
        ]
        if members:
            mean_confidence = statistics.mean(row[0] for row in members)
            accuracy = statistics.mean(float(row[1]) for row in members)
            result += len(members) / total * abs(accuracy - mean_confidence)
    return result


def verify_locked_model(repository: Path, model_dir: Path) -> None:
    lock_path = repository / "model-lock" / "models.lock.json"
    lock = json.loads(lock_path.read_text(encoding="utf-8"))
    model = next((item for item in lock["models"] if item.get("id") == MODEL_ID), None)
    if model is None or model.get("status") != "downloaded_and_verified":
        raise RuntimeError("Jev-Style candidate is not marked downloaded_and_verified in the lock file.")
    expected_directory = (repository / "models" / model["localDirectory"]).resolve(strict=True)
    if model_dir != expected_directory:
        raise RuntimeError("The supplied model directory does not match the locked candidate revision.")
    for item in model["files"]:
        expected = str(item.get("localVerifiedSha256", "")).lower()
        if len(expected) != 64:
            raise RuntimeError(f"The locked SHA-256 is missing for {item['name']}.")
        path = (model_dir / item["name"]).resolve(strict=True)
        if model_dir not in path.parents:
            raise RuntimeError("A locked model file resolves outside its revision directory.")
        digest = hashlib.sha256()
        with path.open("rb") as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(chunk)
        if digest.hexdigest() != expected:
            raise RuntimeError(f"Locked SHA-256 mismatch: {item['name']}.")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model-dir", type=Path, help="本机已校验的模型目录；默认使用模型锁定路径")
    args = parser.parse_args()

    repository = Path(__file__).resolve().parents[1]
    if args.model_dir is None:
        lock = json.loads((repository / "model-lock" / "models.lock.json").read_text(encoding="utf-8"))
        locked = next((item for item in lock["models"] if item.get("id") == MODEL_ID), None)
        if locked is None or not locked.get("localDirectory"):
            raise RuntimeError("The Jev-Style candidate has no locked local directory.")
        model_dir = (repository / "models" / locked["localDirectory"]).resolve(strict=True)
    else:
        model_dir = args.model_dir.resolve(strict=True)
    verify_locked_model(repository, model_dir)
    for name in ("HF_HUB_OFFLINE", "TRANSFORMERS_OFFLINE", "HF_HUB_DISABLE_TELEMETRY"):
        os.environ[name] = "1"
    os.environ["TOKENIZERS_PARALLELISM"] = "false"
    os.environ["OMP_NUM_THREADS"] = "2"
    os.environ["MKL_NUM_THREADS"] = "2"
    sys.path.insert(0, str(model_dir))

    import torch

    torch.set_num_threads(2)
    torch.set_num_interop_threads(1)
    if torch.cuda.is_available():
        raise RuntimeError("CPU-only candidate evaluation refused a CUDA-enabled runtime.")

    from jev_style_decision import JevStyleDecision

    load_started = time.perf_counter()
    model = JevStyleDecision(model_dir=model_dir, device="cpu", dtype="float32", verify=False)
    load_seconds = time.perf_counter() - load_started

    latencies: list[float] = []
    confidences: list[float] = []
    correct: list[bool] = []
    per_label: dict[str, dict[str, int]] = {
        label: {"count": 0, "correct": 0} for label in LABELS
    }
    predictions: list[str] = []
    brier_sum = 0.0
    for expected, state in CASES:
        started = time.perf_counter()
        result = model.decide(state, QUESTION, options=OPTIONS)
        latencies.append(time.perf_counter() - started)
        probabilities = result["probabilities"]
        if result["answer"] not in LABELS:
            raise RuntimeError("Model returned an answer outside the fixed choice set.")
        if set(probabilities) != set(LABELS):
            raise RuntimeError("Model returned a different choice set than the fixed benchmark.")
        probability_sum = sum(float(probabilities[label]) for label in LABELS)
        if not math.isfinite(probability_sum) or abs(probability_sum - 1.0) > 1e-5:
            raise RuntimeError("Model returned invalid choice probabilities.")
        is_correct = result["answer"] == expected
        predictions.append(result["answer"])
        correct.append(is_correct)
        confidences.append(float(result["top_probability"]))
        per_label[expected]["count"] += 1
        per_label[expected]["correct"] += int(is_correct)
        for label in LABELS:
            target = 1.0 if expected == label else 0.0
            brier_sum += (float(probabilities[label]) - target) ** 2

    true_positive = {label: 0 for label in LABELS}
    false_positive = {label: 0 for label in LABELS}
    false_negative = {label: 0 for label in LABELS}
    # The held-out result list is deliberately reduced to aggregate counts.
    # No raw or per-message model outputs are persisted or printed.
    for (expected, _state), predicted in zip(CASES, predictions, strict=True):
        if predicted == expected:
            true_positive[expected] += 1
        else:
            false_negative[expected] += 1
            false_positive[predicted] += 1

    f1_values = []
    for label in LABELS:
        tp = true_positive[label]
        fp = false_positive[label]
        fn = false_negative[label]
        denominator = 2 * tp + fp + fn
        f1_values.append(0.0 if denominator == 0 else 2 * tp / denominator)

    summary = {
        "benchmark": "synthetic-zh-intent-pilot-v1",
        "samples": len(CASES),
        "labels": list(LABELS),
        "device": "cpu",
        "torch": torch.__version__,
        "cuda_available": torch.cuda.is_available(),
        "load_seconds": round(load_seconds, 3),
        "latency_median_seconds": round(statistics.median(latencies), 3),
        "latency_p95_nearest_rank_seconds": round(percentile_nearest_rank(latencies, 0.95), 3),
        "accuracy": round(sum(correct) / len(correct), 4),
        "macro_f1": round(statistics.mean(f1_values), 4),
        "brier_multiclass_mean_sum_squares": round(brier_sum / len(CASES), 4),
        "ece_top_probability_5_bins": round(expected_calibration_error(confidences, correct), 4),
        "accuracy_by_expected_label": {
            label: round(values["correct"] / values["count"], 4)
            for label, values in per_label.items()
        },
        "per_example_predictions_saved": False,
        "note": "Small synthetic pilot only; not a release gate or production-quality claim.",
    }
    print(json.dumps(summary, ensure_ascii=False, sort_keys=True))
    del model
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
