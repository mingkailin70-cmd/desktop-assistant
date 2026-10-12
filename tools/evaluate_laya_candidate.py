#!/usr/bin/env python3
"""在固定合成中文意图题上评估本机锁定的 Laya 多语言候选。"""

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

from decision_benchmark_suites import INTENT_PILOT_V1, SUITES, get_suite
from evaluate_jev_style_candidate import (
    expected_calibration_error,
    percentile_nearest_rank,
)


MODEL_ID = "laya-multilingual-322m-decision-cpu-eval"


def verify_locked_model(repository: Path, model_dir: Path) -> None:
    lock = json.loads((repository / "model-lock" / "models.lock.json").read_text(encoding="utf-8"))
    model = next((item for item in lock["models"] if item.get("id") == MODEL_ID), None)
    if model is None or model.get("status") != "downloaded_and_verified":
        raise RuntimeError("Laya 候选未在模型锁清单中标记为已校验。")
    expected_directory = (repository / "models" / model["localDirectory"]).resolve(strict=True)
    if model_dir != expected_directory:
        raise RuntimeError("给定模型目录与锁定的候选 revision 不一致。")
    for item in model["files"]:
        expected = str(item.get("localVerifiedSha256", "")).lower()
        if len(expected) != 64:
            raise RuntimeError(f"缺少 {item['name']} 的锁定 SHA-256。")
        path = (model_dir / item["name"]).resolve(strict=True)
        if model_dir not in path.parents:
            raise RuntimeError("模型文件解析到 revision 目录之外。")
        digest = hashlib.sha256()
        with path.open("rb") as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(chunk)
        if digest.hexdigest() != expected:
            raise RuntimeError(f"模型文件散列不匹配：{item['name']}。")


def peak_working_set_mib() -> float | None:
    if os.name != "nt":
        return None
    import ctypes
    from ctypes import wintypes

    class MemoryCounters(ctypes.Structure):
        _fields_ = [
            ("cb", wintypes.DWORD),
            ("PageFaultCount", wintypes.DWORD),
            ("PeakWorkingSetSize", ctypes.c_size_t),
            ("WorkingSetSize", ctypes.c_size_t),
            ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
            ("QuotaPagedPoolUsage", ctypes.c_size_t),
            ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
            ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
            ("PagefileUsage", ctypes.c_size_t),
            ("PeakPagefileUsage", ctypes.c_size_t),
            ("PrivateUsage", ctypes.c_size_t),
        ]

    counters = MemoryCounters()
    counters.cb = ctypes.sizeof(counters)
    psapi = ctypes.WinDLL("Psapi.dll")
    kernel32 = ctypes.WinDLL("Kernel32.dll")
    kernel32.GetCurrentProcess.restype = wintypes.HANDLE
    psapi.GetProcessMemoryInfo.argtypes = [
        wintypes.HANDLE,
        ctypes.POINTER(MemoryCounters),
        wintypes.DWORD,
    ]
    psapi.GetProcessMemoryInfo.restype = wintypes.BOOL
    ok = psapi.GetProcessMemoryInfo(
        kernel32.GetCurrentProcess(), ctypes.byref(counters), counters.cb
    )
    if not ok:
        return None
    return round(counters.PeakWorkingSetSize / (1024 * 1024), 1)


def main() -> int:
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--suite", choices=tuple(SUITES), default=INTENT_PILOT_V1.suite_id)
    args = parser.parse_args()
    suite = get_suite(args.suite)
    labels = suite.labels

    repository = Path(__file__).resolve().parents[1]
    lock = json.loads((repository / "model-lock" / "models.lock.json").read_text(encoding="utf-8"))
    locked = next((item for item in lock["models"] if item.get("id") == MODEL_ID), None)
    if locked is None or not locked.get("localDirectory"):
        raise RuntimeError("模型锁清单中没有 Laya 候选的本机目录。")
    model_dir = (repository / "models" / locked["localDirectory"]).resolve(strict=True)
    verify_locked_model(repository, model_dir)

    for name in ("HF_HUB_OFFLINE", "TRANSFORMERS_OFFLINE", "HF_HUB_DISABLE_TELEMETRY"):
        os.environ[name] = "1"
    os.environ["TOKENIZERS_PARALLELISM"] = "false"
    os.environ["OMP_NUM_THREADS"] = "2"
    os.environ["MKL_NUM_THREADS"] = "2"

    import torch

    torch.set_num_threads(2)
    torch.set_num_interop_threads(1)
    if torch.cuda.is_available():
        raise RuntimeError("拒绝将 CPU-only 候选评测切换到 CUDA。")

    import laya

    load_started = time.perf_counter()
    agent = laya.load(str(model_dir), subfolder="multilingual", device="cpu", backend="eager")
    load_seconds = time.perf_counter() - load_started

    questions = {
        "intent": {
            "type": "choice",
            "instructions": suite.question,
            "criteria": suite.options,
        }
    }
    latencies: list[float] = []
    confidences: list[float] = []
    correct: list[bool] = []
    predictions: list[str] = []
    per_label = {label: {"count": 0, "correct": 0} for label in labels}
    brier_sum = 0.0

    for expected, state in suite.cases:
        started = time.perf_counter()
        result = agent.system_one(state, questions, lang="zh")
        latencies.append(time.perf_counter() - started)
        answer = result.get("answers", {}).get("intent", {})
        prediction = answer.get("choice")
        probabilities = answer.get("probabilities")
        if prediction not in labels:
            raise RuntimeError("模型返回了固定选项之外的标签。")
        if not isinstance(probabilities, dict) or set(probabilities) != set(labels):
            raise RuntimeError("模型没有返回完整的固定选项概率。")
        probabilities = {label: float(probabilities[label]) for label in labels}
        probability_sum = sum(probabilities.values())
        if (
            not math.isfinite(probability_sum)
            or abs(probability_sum - 1.0) > 0.002
            or any(not math.isfinite(value) or value < 0 or value > 1 for value in probabilities.values())
        ):
            raise RuntimeError("模型返回了无效的选项概率。")

        is_correct = prediction == expected
        predictions.append(prediction)
        correct.append(is_correct)
        confidences.append(max(probabilities.values()))
        per_label[expected]["count"] += 1
        per_label[expected]["correct"] += int(is_correct)
        for label in labels:
            target = 1.0 if expected == label else 0.0
            brier_sum += (probabilities[label] - target) ** 2

    true_positive = {label: 0 for label in labels}
    false_positive = {label: 0 for label in labels}
    false_negative = {label: 0 for label in labels}
    for (expected, _state), predicted in zip(suite.cases, predictions, strict=True):
        if predicted == expected:
            true_positive[expected] += 1
        else:
            false_negative[expected] += 1
            false_positive[predicted] += 1

    f1_values = []
    for label in labels:
        tp, fp, fn = true_positive[label], false_positive[label], false_negative[label]
        denominator = 2 * tp + fp + fn
        f1_values.append(0.0 if denominator == 0 else 2 * tp / denominator)

    summary = {
        "benchmark": suite.suite_id,
        "model": MODEL_ID,
        "samples": len(suite.cases),
        "labels": list(labels),
        "device": "cpu",
        "torch": torch.__version__,
        "cuda_available": torch.cuda.is_available(),
        "load_seconds": round(load_seconds, 3),
        "latency_median_seconds": round(statistics.median(latencies), 3),
        "latency_p95_nearest_rank_seconds": round(percentile_nearest_rank(latencies, 0.95), 3),
        "peak_working_set_mib": peak_working_set_mib(),
        "accuracy": round(sum(correct) / len(correct), 4),
        "macro_f1": round(statistics.mean(f1_values), 4),
        "brier_multiclass_mean_sum_squares": round(brier_sum / len(suite.cases), 4),
        "ece_top_probability_5_bins": round(expected_calibration_error(confidences, correct), 4),
        "accuracy_by_expected_label": {
            label: round(values["correct"] / values["count"], 4)
            for label, values in per_label.items()
        },
        "per_example_predictions_saved": False,
        "note": suite.note,
    }
    print(json.dumps(summary, ensure_ascii=False, sort_keys=True))
    del agent
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
