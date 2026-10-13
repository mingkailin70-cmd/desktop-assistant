#!/usr/bin/env python3
"""在固定中文合成留出集上评估锁定的 Jevos-v4 OpenVINO CPU 候选。"""

from __future__ import annotations

import ctypes
import hashlib
import json
import math
import os
import secrets
import socket
import statistics
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any

from decision_benchmark_suites import get_suite


MODEL_ID = "jevos-v4-0.9b-openvino-int8-decision-eval"
SUITE_ID = "synthetic-zh-xiaok-routing-holdout-v1"
MIN_START_AVAILABLE_MIB = 8 * 1024
MIN_RUNTIME_AVAILABLE_MIB = 4 * 1024
THREADS = 2
REQUEST_TIMEOUT_SECONDS = 10
STARTUP_TIMEOUT_SECONDS = 90


class MemoryStatusEx(ctypes.Structure):
    _fields_ = [
        ("dwLength", ctypes.c_uint32),
        ("dwMemoryLoad", ctypes.c_uint32),
        ("ullTotalPhys", ctypes.c_uint64),
        ("ullAvailPhys", ctypes.c_uint64),
        ("ullTotalPageFile", ctypes.c_uint64),
        ("ullAvailPageFile", ctypes.c_uint64),
        ("ullTotalVirtual", ctypes.c_uint64),
        ("ullAvailVirtual", ctypes.c_uint64),
        ("ullAvailExtendedVirtual", ctypes.c_uint64),
    ]


class ProcessMemoryCountersEx(ctypes.Structure):
    _fields_ = [
        ("cb", ctypes.c_uint32),
        ("PageFaultCount", ctypes.c_uint32),
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


def available_memory_mib() -> int:
    status = MemoryStatusEx()
    status.dwLength = ctypes.sizeof(status)
    if not ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):
        raise ctypes.WinError(ctypes.get_last_error())
    return int(status.ullAvailPhys // (1024 * 1024))


def peak_working_set_mib(pid: int) -> float | None:
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    psapi = ctypes.WinDLL("psapi", use_last_error=True)
    kernel32.OpenProcess.argtypes = [ctypes.c_uint32, ctypes.c_int, ctypes.c_uint32]
    kernel32.OpenProcess.restype = ctypes.c_void_p
    kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
    psapi.GetProcessMemoryInfo.argtypes = [
        ctypes.c_void_p,
        ctypes.POINTER(ProcessMemoryCountersEx),
        ctypes.c_uint32,
    ]
    psapi.GetProcessMemoryInfo.restype = ctypes.c_int
    handle = kernel32.OpenProcess(0x1000, 0, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
    if not handle:
        return None
    try:
        counters = ProcessMemoryCountersEx()
        counters.cb = ctypes.sizeof(counters)
        if not psapi.GetProcessMemoryInfo(handle, ctypes.byref(counters), counters.cb):
            return None
        return counters.PeakWorkingSetSize / (1024 * 1024)
    finally:
        kernel32.CloseHandle(handle)


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(4 * 1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def verify_candidate(repository: Path) -> tuple[dict[str, Any], Path, Path]:
    lock = json.loads((repository / "model-lock" / "models.lock.json").read_text(encoding="utf-8"))
    model = next((item for item in lock["models"] if item.get("id") == MODEL_ID), None)
    if model is None or model.get("status") not in ("downloaded_and_verified", "locally_evaluated"):
        raise RuntimeError("Jevos-v4 OpenVINO model is not locked as downloaded and verified.")
    model_dir = (repository / "models" / model["localDirectory"] / "model").resolve(strict=True)
    if repository.resolve() not in model_dir.parents:
        raise RuntimeError("The locked model directory escapes the repository.")
    for item in model["files"]:
        expected = str(item.get("localVerifiedSha256", "")).lower()
        path = (repository / "models" / model["localDirectory"] / item["name"]).resolve(strict=True)
        if model_dir.parent not in path.parents or not expected or sha256_file(path) != expected:
            raise RuntimeError(f"Locked model file verification failed: {item['name']}.")

    runtime = model.get("runtime") or {}
    runtime_archive = (repository / runtime.get("localDirectory", "") / runtime.get("archive", "")).resolve(strict=True)
    runtime_sha = str(runtime.get("archiveSha256", "")).lower()
    if runtime.get("status") not in ("downloaded_and_verified", "locally_evaluated"):
        raise RuntimeError("The locked JEVOS Windows runtime is not downloaded and verified.")
    if repository.resolve() not in runtime_archive.parents or not runtime_sha or sha256_file(runtime_archive) != runtime_sha:
        raise RuntimeError("The JEVOS runtime archive does not match its locked SHA-256.")
    executable = (repository / runtime["localDirectory"] / "jev" / "jev.exe").resolve(strict=True)
    if repository.resolve() not in executable.parents:
        raise RuntimeError("The locked runtime executable escapes the repository.")
    executable_sha = str(runtime.get("executableSha256", "")).lower()
    if not runtime.get("signatureVerified") or not executable_sha or sha256_file(executable) != executable_sha:
        raise RuntimeError("The locked runtime signature metadata or executable SHA-256 verification is missing.")
    return model, model_dir, executable


def get_json(url: str, timeout: float, headers: dict[str, str] | None = None) -> dict[str, Any]:
    request = urllib.request.Request(url, headers=headers or {"Accept": "application/json"})
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return json.load(response)


def post_json(url: str, payload: dict[str, Any], api_key: str) -> dict[str, Any]:
    data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    request = urllib.request.Request(
        url,
        data=data,
        headers={"Content-Type": "application/json", "Authorization": f"Bearer {api_key}"},
        method="POST",
    )
    with urllib.request.urlopen(request, timeout=REQUEST_TIMEOUT_SECONDS) as response:
        return json.load(response)


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


def main() -> int:
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")
    if os.name != "nt":
        raise RuntimeError("This candidate evaluator is restricted to its locked Windows runtime.")
    available_start = available_memory_mib()
    if available_start < MIN_START_AVAILABLE_MIB:
        raise RuntimeError(f"Available RAM is {available_start} MiB; the start floor is {MIN_START_AVAILABLE_MIB} MiB.")

    repository = Path(__file__).resolve().parents[1]
    model, model_dir, executable = verify_candidate(repository)
    suite = get_suite(SUITE_ID)
    labels = suite.labels
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]

    api_key = secrets.token_urlsafe(36)
    environment = os.environ.copy()
    environment["JEV_API_KEY"] = api_key
    environment["OMP_NUM_THREADS"] = str(THREADS)
    command = [
        str(executable), "serve", "--host", "127.0.0.1", "--port", str(port),
        "--model-dir", str(model_dir), "--threads", str(THREADS), "--ctx", "8192",
        "--warmup", "384", "--batch-tokens", "0", "--state-cache", "0", "--state-cache-tokens", "0",
    ]
    child_flags = getattr(subprocess, "BELOW_NORMAL_PRIORITY_CLASS", 0) | getattr(subprocess, "CREATE_NO_WINDOW", 0)
    server = subprocess.Popen(command, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, env=environment,
                              creationflags=child_flags)
    latencies: list[float] = []
    correct: list[bool] = []
    confidences: list[float] = []
    per_label: dict[str, dict[str, int]] = {label: {"count": 0, "correct": 0} for label in labels}
    confusion: dict[str, dict[str, int]] = {label: {pred: 0 for pred in labels} for label in labels}
    brier_sum = 0.0
    api = f"http://127.0.0.1:{port}"
    health: dict[str, Any] | None = None
    started = time.perf_counter()
    ready_seconds: float | None = None
    process_peak_mib: float | None = None
    try:
        deadline = time.monotonic() + STARTUP_TIMEOUT_SECONDS
        while time.monotonic() < deadline:
            if server.poll() is not None:
                detail = (server.stderr.read() if server.stderr else b"").decode("utf-8", "replace")[-1200:]
                raise RuntimeError(f"JEVOS exited before readiness: {detail}")
            try:
                health = get_json(api + "/health", timeout=2)
                ready_seconds = time.perf_counter() - started
                break
            except (OSError, urllib.error.URLError, json.JSONDecodeError):
                time.sleep(0.15)
        if health is None:
            raise RuntimeError("JEVOS CPU service readiness timed out.")
        files = {item["name"].removeprefix("model/"): item["localVerifiedSha256"] for item in model["files"]}
        health_files = health.get("model_files")
        if not isinstance(health_files, dict) or any(health_files.get(name) != files.get(name) for name in
                                                     ("model.json", "openvino_model.bin", "openvino_model.xml", "tokenizer.gguf")):
            raise RuntimeError("JEVOS health model fingerprint does not match the lock file.")

        for expected, state in suite.cases:
            if available_memory_mib() < MIN_RUNTIME_AVAILABLE_MIB:
                raise RuntimeError("Available RAM dropped below the 4 GiB runtime floor; stopping the evaluation.")
            payload = {
                "model": "jevos-v4",
                "state": state,
                "questions": {"route": {"type": "choice", "instructions": suite.question, "criteria": suite.options}},
            }
            request_started = time.perf_counter()
            result = post_json(api + "/v1/systemone", payload, api_key)
            latencies.append(time.perf_counter() - request_started)
            answer = result["answers"]["route"]
            prediction = answer["choice"]
            probabilities = {label: float(value) for label, value in answer["probabilities"].items()}
            if prediction not in labels or set(probabilities) != set(labels):
                raise RuntimeError("Candidate returned an answer outside the locked choice set.")
            if any(not math.isfinite(value) or value < 0 or value > 1 for value in probabilities.values()):
                raise RuntimeError("Candidate returned an invalid probability.")
            if abs(sum(probabilities.values()) - 1.0) > 1e-5:
                raise RuntimeError("Candidate probabilities do not sum to one.")
            hit = prediction == expected
            correct.append(hit)
            confidences.append(max(probabilities.values()))
            per_label[expected]["count"] += 1
            per_label[expected]["correct"] += int(hit)
            confusion[expected][prediction] += 1
            brier_sum += sum((probabilities[label] - (1.0 if label == expected else 0.0)) ** 2 for label in labels)
            observed_peak = peak_working_set_mib(server.pid)
            if observed_peak is not None:
                process_peak_mib = max(process_peak_mib or 0.0, observed_peak)

        f1_values: list[float] = []
        for label in labels:
            true_positive = confusion[label][label]
            false_positive = sum(confusion[other][label] for other in labels if other != label)
            false_negative = sum(confusion[label][other] for other in labels if other != label)
            precision = true_positive / (true_positive + false_positive) if true_positive + false_positive else 0.0
            recall = true_positive / (true_positive + false_negative) if true_positive + false_negative else 0.0
            f1_values.append(2 * precision * recall / (precision + recall) if precision + recall else 0.0)

        count = len(suite.cases)
        summary = {
            "suite": SUITE_ID,
            "suite_source_sha256": sha256_file(repository / "tools" / "decision_benchmark_suites.py"),
            "model": MODEL_ID,
            "weight_revision": model["revision"],
            "weight_sha256": model["files"][1]["localVerifiedSha256"],
            "health_fingerprint": health.get("fingerprint"),
            "samples": count,
            "correct": sum(correct),
            "accuracy": round(sum(correct) / count, 4),
            "macro_f1": round(statistics.mean(f1_values), 4),
            "brier_multiclass_mean_sum_squares": round(brier_sum / count, 4),
            "ece_top_probability_5_bins": round(expected_calibration_error(confidences, correct), 4),
            "per_category": {label: f"{value['correct']}/{value['count']}" for label, value in per_label.items()},
            "latency_seconds_median": round(statistics.median(latencies), 4),
            "latency_seconds_p95_nearest_rank": round(percentile_nearest_rank(latencies, 0.95), 4),
            "load_ready_seconds": round(ready_seconds or 0.0, 3),
            "cpu_threads": THREADS,
            "available_ram_start_mib": available_start,
            "available_ram_end_mib": available_memory_mib(),
            "process_peak_working_set_mib": round(process_peak_mib, 1) if process_peak_mib is not None else None,
            "gpu_used": False,
            "per_case_outputs_saved": False,
            "evaluator_sha256": sha256_file(Path(__file__).resolve()),
        }
        print(json.dumps(summary, ensure_ascii=False))
        return 0
    finally:
        if server.poll() is None:
            server.terminate()
            try:
                server.wait(timeout=5)
            except subprocess.TimeoutExpired:
                server.kill()
                server.wait(timeout=5)
        if server.stderr:
            server.stderr.close()


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, RuntimeError, urllib.error.URLError) as error:
        print(f"evaluation failed: {error}", file=sys.stderr)
        raise SystemExit(1)
