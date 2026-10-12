#!/usr/bin/env python3
"""在本机回环接口上评估锁定的 H2O 决策模型合成中文题集。"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import statistics
import sys
import time
import urllib.request
from pathlib import Path

from decision_benchmark_suites import INTENT_PILOT_V1, SUITES, get_suite

MODEL_ID = "h2o-lightning-4b-native-q8-decision-eval"
MODEL_FILE = "h2o-lightning-4b-native-Q8_0.gguf"
DEFAULT_URL = "http://127.0.0.1:9931"


def local_auth_headers() -> dict[str, str]:
    headers = {"Accept": "application/json"}
    api_key = os.environ.get("XIAOK_H2O_EVAL_API_KEY", "")
    if api_key:
        headers["Authorization"] = f"Bearer {api_key}"
    return headers


def percentile_nearest_rank(values: list[float], percentile: float) -> float:
    ordered = sorted(values)
    return ordered[max(0, math.ceil(percentile * len(ordered)) - 1)]


def expected_calibration_error(
    confidences: list[float], correct: list[bool], bins: int = 5
) -> float:
    total = len(confidences)
    result = 0.0
    for index in range(bins):
        lower = index / bins
        upper = (index + 1) / bins
        members = [
            (confidence, is_correct)
            for confidence, is_correct in zip(confidences, correct, strict=True)
            if lower <= confidence < upper
            or (index == bins - 1 and confidence == 1.0)
        ]
        if members:
            mean_confidence = statistics.mean(row[0] for row in members)
            accuracy = statistics.mean(float(row[1]) for row in members)
            result += len(members) / total * abs(accuracy - mean_confidence)
    return result


def read_locked_model(repository: Path) -> tuple[Path, str]:
    lock_path = repository / "model-lock" / "models.lock.json"
    lock = json.loads(lock_path.read_text(encoding="utf-8"))
    model = next((item for item in lock["models"] if item.get("id") == MODEL_ID), None)
    if model is None or model.get("status") != "downloaded_and_verified":
        raise RuntimeError("H2O候选未在模型锁中标为downloaded_and_verified。")
    model_dir = (repository / "models" / model["localDirectory"]).resolve(strict=True)
    model_file = (model_dir / MODEL_FILE).resolve(strict=True)
    if model_dir not in model_file.parents:
        raise RuntimeError("锁定模型路径解析到了版本目录之外。")

    expected = next(
        (item.get("localVerifiedSha256", "") for item in model["files"] if item.get("name") == MODEL_FILE),
        "",
    ).lower()
    if len(expected) != 64:
        raise RuntimeError("模型锁缺少H2O候选的本机SHA-256。")

    digest = hashlib.sha256()
    with model_file.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    if digest.hexdigest() != expected:
        raise RuntimeError("H2O候选SHA-256与模型锁不符。")
    return model_file, expected


def get_json(url: str, timeout: float = 5.0) -> dict:
    request = urllib.request.Request(url, headers=local_auth_headers())
    with urllib.request.urlopen(request, timeout=timeout) as response:
        if response.status != 200:
            raise RuntimeError(f"本机推理服务返回HTTP {response.status}。")
        return json.load(response)


def post_json(url: str, body: dict, timeout: float = 180.0) -> dict:
    payload = json.dumps(body, ensure_ascii=False).encode("utf-8")
    headers = local_auth_headers()
    headers["Content-Type"] = "application/json"
    request = urllib.request.Request(
        url,
        data=payload,
        headers=headers,
        method="POST",
    )
    with urllib.request.urlopen(request, timeout=timeout) as response:
        if response.status != 200:
            raise RuntimeError(f"本机推理服务返回HTTP {response.status}。")
        return json.load(response)


def main() -> int:
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--suite",
        choices=tuple(SUITES),
        default=INTENT_PILOT_V1.suite_id,
        help="固定合成题集ID",
    )
    args = parser.parse_args()
    suite = get_suite(args.suite)
    repository = Path(__file__).resolve().parents[1]
    model_file, model_sha256 = read_locked_model(repository)

    # 不接受自定义主机、协议或端口，避免将合成集发送到外部服务。
    base_url = DEFAULT_URL
    get_json(f"{base_url}/health")
    model_response = get_json(f"{base_url}/v1/models")
    data = model_response.get("data")
    if not isinstance(data, list) or len(data) != 1 or not isinstance(data[0], dict):
        raise RuntimeError("本机服务没有返回唯一的已加载模型。")
    model_info = data[0]
    model_name = str(model_info.get("id", ""))
    if MODEL_FILE.lower() not in model_name.lower():
        raise RuntimeError("127.0.0.1:9931加载的不是模型锁指定的H2O权重。")
    modalities = model_info.get("architecture", {}).get("output_modalities", [])
    if "decisions" not in modalities:
        raise RuntimeError("加载模型未声明原生decisions输出，拒绝开始评估。")

    labels = suite.labels
    latencies: list[float] = []
    confidences: list[float] = []
    correct: list[bool] = []
    predictions: list[str] = []
    per_label: dict[str, dict[str, int]] = {
        label: {"count": 0, "correct": 0} for label in labels
    }
    brier_sum = 0.0

    for index, (expected, state) in enumerate(suite.cases, start=1):
        body = {
            "state": state,
            "questions": {
                "route": {
                    "type": "choice",
                    "instructions": suite.question,
                    "criteria": suite.options,
                }
            },
        }
        started = time.perf_counter()
        response = post_json(f"{base_url}/v1/systemone", body)
        latencies.append(time.perf_counter() - started)
        answer = response.get("answers", {}).get("route", {})
        prediction = answer.get("choice")
        probabilities = answer.get("probabilities")
        if prediction not in labels or not isinstance(probabilities, dict):
            raise RuntimeError("H2O返回的标签不在固定候选集合中。")
        if set(probabilities) != set(labels):
            raise RuntimeError("H2O返回的选项与固定题集不一致。")
        values = [float(probabilities[label]) for label in labels]
        if not all(math.isfinite(value) and 0.0 <= value <= 1.0 for value in values):
            raise RuntimeError("H2O返回了无效概率。")
        probability_sum = sum(values)
        if abs(probability_sum - 1.0) > 1e-5:
            raise RuntimeError("H2O返回概率之和不为1。")

        is_correct = prediction == expected
        predictions.append(prediction)
        correct.append(is_correct)
        confidences.append(float(answer.get("confidence", max(values))))
        per_label[expected]["count"] += 1
        per_label[expected]["correct"] += int(is_correct)
        for label, probability in zip(labels, values, strict=True):
            target = 1.0 if label == expected else 0.0
            brier_sum += (probability - target) ** 2
        if index % 10 == 0 or index == len(suite.cases):
            print(f"已完成 {index}/{len(suite.cases)} 条合成题", file=sys.stderr, flush=True)

    true_positive = {label: 0 for label in labels}
    false_positive = {label: 0 for label in labels}
    false_negative = {label: 0 for label in labels}
    for (expected, _state), prediction in zip(suite.cases, predictions, strict=True):
        if prediction == expected:
            true_positive[expected] += 1
        else:
            false_negative[expected] += 1
            false_positive[prediction] += 1

    f1_values = []
    for label in labels:
        tp = true_positive[label]
        fp = false_positive[label]
        fn = false_negative[label]
        denominator = 2 * tp + fp + fn
        f1_values.append(0.0 if denominator == 0 else 2 * tp / denominator)

    summary = {
        "benchmark": suite.suite_id,
        "model_id": MODEL_ID,
        "model_file_sha256": model_sha256,
        "runtime_model_id": model_name,
        "runtime_output_modalities": modalities,
        "samples": len(suite.cases),
        "device": "cpu (server must be launched with --n-gpu-layers 0)",
        "accuracy": round(sum(correct) / len(correct), 4),
        "macro_f1": round(statistics.mean(f1_values), 4),
        "brier_multiclass_mean_sum_squares": round(brier_sum / len(suite.cases), 4),
        "ece_top_probability_5_bins": round(
            expected_calibration_error(confidences, correct), 4
        ),
        "accuracy_by_expected_label": {
            label: round(values["correct"] / values["count"], 4)
            for label, values in per_label.items()
        },
        "latency_median_seconds": round(statistics.median(latencies), 3),
        "latency_p95_nearest_rank_seconds": round(
            percentile_nearest_rank(latencies, 0.95), 3
        ),
        "per_example_predictions_saved": False,
        "note": suite.note,
    }
    print(json.dumps(summary, ensure_ascii=False, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
