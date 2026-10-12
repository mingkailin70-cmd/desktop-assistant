#!/usr/bin/env python3
"""评估本机 Jev-Style 候选在固定合成中文决策题集上的表现。"""

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

MODEL_ID = "jev-style-v3-0.8b-decision-cpu-eval"


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
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model-dir", type=Path, help="本机已校验的模型目录；默认使用模型锁定路径")
    parser.add_argument("--suite", choices=tuple(SUITES), default=INTENT_PILOT_V1.suite_id)
    args = parser.parse_args()
    suite = get_suite(args.suite)
    labels = suite.labels

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
    per_label: dict[str, dict[str, int]] = {label: {"count": 0, "correct": 0} for label in labels}
    predictions: list[str] = []
    brier_sum = 0.0
    for expected, state in suite.cases:
        started = time.perf_counter()
        result = model.decide(state, suite.question, options=suite.options)
        latencies.append(time.perf_counter() - started)
        probabilities = result["probabilities"]
        if result["answer"] not in labels:
            raise RuntimeError("Model returned an answer outside the fixed choice set.")
        if set(probabilities) != set(labels):
            raise RuntimeError("Model returned a different choice set than the fixed benchmark.")
        probability_sum = sum(float(probabilities[label]) for label in labels)
        if not math.isfinite(probability_sum) or abs(probability_sum - 1.0) > 1e-5:
            raise RuntimeError("Model returned invalid choice probabilities.")
        is_correct = result["answer"] == expected
        predictions.append(result["answer"])
        correct.append(is_correct)
        confidences.append(float(result["top_probability"]))
        per_label[expected]["count"] += 1
        per_label[expected]["correct"] += int(is_correct)
        for label in labels:
            target = 1.0 if expected == label else 0.0
            brier_sum += (float(probabilities[label]) - target) ** 2

    true_positive = {label: 0 for label in labels}
    false_positive = {label: 0 for label in labels}
    false_negative = {label: 0 for label in labels}
    # The held-out result list is deliberately reduced to aggregate counts.
    # No raw or per-message model outputs are persisted or printed.
    for (expected, _state), predicted in zip(suite.cases, predictions, strict=True):
        if predicted == expected:
            true_positive[expected] += 1
        else:
            false_negative[expected] += 1
            false_positive[predicted] += 1

    f1_values = []
    for label in labels:
        tp = true_positive[label]
        fp = false_positive[label]
        fn = false_negative[label]
        denominator = 2 * tp + fp + fn
        f1_values.append(0.0 if denominator == 0 else 2 * tp / denominator)

    summary = {
        "benchmark": suite.suite_id,
        "samples": len(suite.cases),
        "labels": list(labels),
        "device": "cpu",
        "torch": torch.__version__,
        "cuda_available": torch.cuda.is_available(),
        "load_seconds": round(load_seconds, 3),
        "latency_median_seconds": round(statistics.median(latencies), 3),
        "latency_p95_nearest_rank_seconds": round(percentile_nearest_rank(latencies, 0.95), 3),
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
    del model
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
