#!/usr/bin/env python3
"""Verify locked local TTS -> ASR on CPU without microphone or saved audio."""

from __future__ import annotations

import argparse
import base64
import ctypes
import hashlib
import io
import json
import math
import os
import pathlib
import queue
import statistics
import subprocess
import sys
import threading
import time
import unicodedata
import wave
from ctypes import wintypes


ROOT = pathlib.Path(__file__).resolve().parents[1]
WORKER = ROOT / "src" / "XiaoK.Voice" / "voice_worker.py"
LOCK_PATH = ROOT / "model-lock" / "models.lock.json"
EXPECTED = {
    "qwen3-asr-0.6b": ("5eb144179a02acc5e5ba31e748d22b0cf3e303b0", "asr"),
    "qwen3-tts-12hz-0.6b-customvoice": (
        "85e237c12c027371202489a0ec509ded67b5e4b5",
        "tts",
    ),
}
PHRASES = (
    "你好，小K，请打开本地项目。",
    "我先检查文件内容，再告诉你结果。",
    "任务已经取消，我没有执行后续操作。",
    "下午三点请提醒我保存文档。",
    "发送前请再次核对联系人、正文和附件。",
    "操作结果无法确认时，不要自动重试。",
)
MAX_PROTOCOL_LINE = 20 * 1024 * 1024


def load_locked_models() -> dict[str, tuple[pathlib.Path, pathlib.Path]]:
    lock = json.loads(LOCK_PATH.read_text(encoding="utf-8"))
    models = {item["id"]: item for item in lock["models"]}
    result = {}
    for model_id, (revision, environment) in EXPECTED.items():
        item = models[model_id]
        if (
            item.get("revision") != revision
            or item.get("license") != "apache-2.0"
            or item.get("status") != "downloaded_and_verified"
            or item.get("requiredForP0") is not True
        ):
            raise RuntimeError(f"Model lock mismatch: {model_id}")
        model_dir = (ROOT / "models" / item["localDirectory"]).resolve(strict=True)
        if not model_dir.is_relative_to((ROOT / "models").resolve(strict=True)):
            raise RuntimeError(f"Model path escapes models/: {model_id}")
        for file in item["files"]:
            expected_hash = file["expectedUpstreamSha256"].lower()
            if file["localVerifiedSha256"].lower() != expected_hash:
                raise RuntimeError(f"Model lock hash mismatch: {model_id}/{file['name']}")
            path = (model_dir / file["name"]).resolve(strict=True)
            if not path.is_relative_to(model_dir) or path.stat().st_size != file["upstreamReportedSizeBytes"]:
                raise RuntimeError(f"Model file path/size mismatch: {model_id}/{file['name']}")
            digest = hashlib.sha256()
            with path.open("rb") as source:
                for block in iter(lambda: source.read(4 * 1024 * 1024), b""):
                    digest.update(block)
            if digest.hexdigest() != expected_hash:
                raise RuntimeError(f"Model file SHA-256 mismatch: {model_id}/{file['name']}")
        python = ROOT / ".tools" / "venvs" / environment / "Scripts" / "python.exe"
        if not python.is_file():
            raise RuntimeError(f"Locked Python environment missing: {environment}")
        result[model_id] = (model_dir, python)
    if not WORKER.is_file():
        raise RuntimeError("Voice worker is missing")
    return result


def process_environment(python: pathlib.Path) -> dict[str, str]:
    windows = pathlib.Path(os.environ.get("SystemRoot", r"C:\Windows"))
    profile = pathlib.Path(os.environ["USERPROFILE"])
    local = pathlib.Path(os.environ["LOCALAPPDATA"])
    appdata = pathlib.Path(os.environ.get("APPDATA", str(profile / "AppData" / "Roaming")))
    temporary = local / "Temp" / "XiaoK" / "VoiceTemp"
    cache = local / "XiaoK" / "Cache"
    (temporary).mkdir(parents=True, exist_ok=True)
    (cache / "huggingface").mkdir(parents=True, exist_ok=True)
    (cache / "torch").mkdir(parents=True, exist_ok=True)
    return {
        "SystemRoot": str(windows),
        "WINDIR": str(windows),
        "PATH": f"{python.parent};{windows / 'System32'}",
        "TEMP": str(temporary),
        "TMP": str(temporary),
        "USERPROFILE": str(profile),
        "LOCALAPPDATA": str(local),
        "APPDATA": str(appdata),
        "PYTHONNOUSERSITE": "1",
        "PYTHONUTF8": "1",
        "PYTHONIOENCODING": "utf-8",
        "HF_HUB_OFFLINE": "1",
        "TRANSFORMERS_OFFLINE": "1",
        "HF_HUB_DISABLE_TELEMETRY": "1",
        "TOKENIZERS_PARALLELISM": "false",
        "CUDA_VISIBLE_DEVICES": "-1",
        "HF_HOME": str(cache / "huggingface"),
        "TORCH_HOME": str(cache / "torch"),
        "OMP_NUM_THREADS": "4",
        "MKL_NUM_THREADS": "4",
        "OPENBLAS_NUM_THREADS": "4",
    }


def read_line(stream, timeout_seconds: int) -> str:
    result: queue.Queue[tuple[bool, str | BaseException]] = queue.Queue(maxsize=1)

    def read() -> None:
        try:
            result.put((True, stream.readline()))
        except BaseException as error:  # surface pipe errors in the caller
            result.put((False, error))

    threading.Thread(target=read, daemon=True).start()
    try:
        success, value = result.get(timeout=timeout_seconds)
    except queue.Empty as error:
        raise TimeoutError("Voice worker response timed out") from error
    if not success:
        raise RuntimeError(f"Voice worker pipe failed: {value}")
    line = value
    if not line or len(line) > MAX_PROTOCOL_LINE:
        raise RuntimeError("Voice worker returned an empty or oversized protocol line")
    return line


class MemoryStatusEx(ctypes.Structure):
    _fields_ = [
        ("cb", wintypes.DWORD),
        ("dwMemoryLoad", wintypes.DWORD),
        ("ullTotalPhys", ctypes.c_ulonglong),
        ("ullAvailPhys", ctypes.c_ulonglong),
        ("ullTotalPageFile", ctypes.c_ulonglong),
        ("ullAvailPageFile", ctypes.c_ulonglong),
        ("ullTotalVirtual", ctypes.c_ulonglong),
        ("ullAvailVirtual", ctypes.c_ulonglong),
        ("ullAvailExtendedVirtual", ctypes.c_ulonglong),
    ]


class FileTime(ctypes.Structure):
    _fields_ = [("low", wintypes.DWORD), ("high", wintypes.DWORD)]


class SystemResourceSampler:
    def __init__(self) -> None:
        self._kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        self._kernel32.GlobalMemoryStatusEx.argtypes = (ctypes.POINTER(MemoryStatusEx),)
        self._kernel32.GlobalMemoryStatusEx.restype = wintypes.BOOL
        self._kernel32.GetSystemTimes.argtypes = (
            ctypes.POINTER(FileTime), ctypes.POINTER(FileTime), ctypes.POINTER(FileTime)
        )
        self._kernel32.GetSystemTimes.restype = wintypes.BOOL
        self._initial_available_mib = 0
        self._minimum_available_mib = 0
        self._total_physical_mib = 0
        self._cpu_samples: list[float] = []
        self._previous_times: tuple[int, int, int] | None = None
        self._result: dict[str, float | int] | None = None
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._sample_loop, daemon=True)
        self._read()
        self._thread.start()

    def _read(self) -> None:
        memory = MemoryStatusEx()
        memory.cb = ctypes.sizeof(memory)
        if not self._kernel32.GlobalMemoryStatusEx(ctypes.byref(memory)):
            raise ctypes.WinError(ctypes.get_last_error())
        available_mib = int(memory.ullAvailPhys / (1024 * 1024))
        total_mib = int(memory.ullTotalPhys / (1024 * 1024))
        if self._total_physical_mib == 0:
            self._initial_available_mib = available_mib
            self._minimum_available_mib = available_mib
            self._total_physical_mib = total_mib
        else:
            self._minimum_available_mib = min(self._minimum_available_mib, available_mib)

        idle = FileTime()
        kernel = FileTime()
        user = FileTime()
        if not self._kernel32.GetSystemTimes(ctypes.byref(idle), ctypes.byref(kernel), ctypes.byref(user)):
            raise ctypes.WinError(ctypes.get_last_error())
        current = (self._file_time_value(idle), self._file_time_value(kernel), self._file_time_value(user))
        if self._previous_times is not None:
            previous_idle, previous_kernel, previous_user = self._previous_times
            idle_delta = current[0] - previous_idle
            total_delta = (current[1] - previous_kernel) + (current[2] - previous_user)
            if total_delta > 0:
                self._cpu_samples.append(max(0.0, min(100.0, 100.0 * (total_delta - idle_delta) / total_delta)))
        self._previous_times = current

    @staticmethod
    def _file_time_value(value: FileTime) -> int:
        return (int(value.high) << 32) | int(value.low)

    def _sample_loop(self) -> None:
        while not self._stop.wait(0.5):
            try:
                self._read()
            except OSError:
                return

    def close(self) -> dict[str, float | int]:
        if self._result is not None:
            return self._result
        self._stop.set()
        self._thread.join(timeout=2)
        try:
            self._read()
        except OSError:
            pass
        self._result = {
            "total_physical_mib": self._total_physical_mib,
            "initial_available_mib": self._initial_available_mib,
            "minimum_available_mib": self._minimum_available_mib,
            "cpu_average_percent": round(statistics.mean(self._cpu_samples), 1) if self._cpu_samples else 0.0,
            "cpu_peak_percent": round(max(self._cpu_samples), 1) if self._cpu_samples else 0.0,
            "cpu_sample_count": len(self._cpu_samples),
        }
        return self._result


def run_worker_batch(
    task: str, model_id: str, requests: list[dict], models
) -> tuple[list[dict], float, list[float]]:
    model_dir, python = models[model_id]
    errors: list[str] = []
    process = subprocess.Popen(
        [str(python), "-u", str(WORKER), "--task", task, "--model-dir", str(model_dir)],
        cwd=ROOT,
        env=process_environment(python),
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        encoding="utf-8",
        errors="replace",
        bufsize=1,
    )
    def drain_stderr() -> None:
        for line in process.stderr:
            errors.append(line.rstrip()[:500])
            if len(errors) > 64:
                del errors[:16]

    threading.Thread(target=drain_stderr, daemon=True).start()
    started = time.perf_counter()
    try:
        ready = json.loads(read_line(process.stdout, 180))
        load_seconds = time.perf_counter() - started
        if ready.get("type") != "ready" or ready.get("ok") is not True:
            raise RuntimeError(f"{task} model failed to load: {ready}")
        responses: list[dict] = []
        inference_seconds: list[float] = []
        for request in requests:
            inference_started = time.perf_counter()
            process.stdin.write(json.dumps(request, ensure_ascii=False, separators=(",", ":")) + "\n")
            process.stdin.flush()
            response = json.loads(read_line(process.stdout, 360))
            inference_seconds.append(time.perf_counter() - inference_started)
            if response.get("ok") is not True:
                raise RuntimeError(f"{task} inference failed: {response}")
            responses.append(response)
        return responses, load_seconds, inference_seconds
    except Exception as error:
        raise RuntimeError(f"{error}; worker stderr tail={errors[-20:]!r}") from error
    finally:
        if process.stdin and not process.stdin.closed:
            process.stdin.close()
        try:
            process.wait(timeout=20)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=10)
        for pipe in (process.stdout, process.stderr):
            if pipe:
                pipe.close()


def normalize(text: str) -> str:
    return "".join(
        char.casefold()
        for char in text
        if not char.isspace() and not unicodedata.category(char).startswith("P")
    )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--strict-transcript", action="store_true", help="return failure unless normalized ASR text matches the synthetic prompt")
    args = parser.parse_args()
    if os.name != "nt":
        raise RuntimeError("This check targets the locked Windows speech environments")
    models = load_locked_models()
    system_resources = SystemResourceSampler()
    try:
        tts_responses, tts_load, tts_inferences = run_worker_batch(
            "tts",
            "qwen3-tts-12hz-0.6b-customvoice",
            [{"command": "synthesize", "text": phrase} for phrase in PHRASES],
            models,
        )
        audio_samples = []
        audio_metadata = []
        for response in tts_responses:
            audio = base64.b64decode(response["wavBase64"], validate=True)
            with wave.open(io.BytesIO(audio), "rb") as wav:
                sample_rate = wav.getframerate()
                channels = wav.getnchannels()
                duration = wav.getnframes() / sample_rate
                if sample_rate not in range(8000, 96001) or channels not in (1, 2) or not 0 < duration <= 60:
                    raise RuntimeError("TTS returned an invalid WAV")
            audio_samples.append(audio)
            audio_metadata.append({"sample_rate": sample_rate, "channels": channels, "duration_seconds": duration, "bytes": len(audio)})
        asr_responses, asr_load, asr_inferences = run_worker_batch(
            "asr",
            "qwen3-asr-0.6b",
            [
                {
                    "command": "transcribe",
                    "language": "Chinese",
                    "wavBase64": base64.b64encode(audio).decode("ascii"),
                }
                for audio in audio_samples
            ],
            models,
        )
        matches = [normalize(response.get("text", "")) == normalize(phrase)
            for response, phrase in zip(asr_responses, PHRASES, strict=True)]
        audio_samples.clear()
        resource_summary = system_resources.close()
        tts_p50 = statistics.median(tts_inferences)
        asr_p50 = statistics.median(asr_inferences)
        tts_p95 = sorted(tts_inferences)[math.ceil(0.95 * len(tts_inferences)) - 1]
        asr_p95 = sorted(asr_inferences)[math.ceil(0.95 * len(asr_inferences)) - 1]
        summary = {
            "result": "PASS" if not args.strict_transcript or all(matches) else "TRANSCRIPT_MISMATCH",
            "mode": "CPU-only, offline, six synthetic TTS-to-ASR samples, no microphone, no audio persisted",
            "resource_scope": "system-wide CPU and available RAM; includes unrelated background processes",
            "model_revisions": {key: EXPECTED[key][0] for key in EXPECTED},
            "tts_load_seconds": round(tts_load, 2),
            "tts_inference_seconds": [round(value, 2) for value in tts_inferences],
            "tts_inference_p50_seconds": round(tts_p50, 2),
            "tts_inference_p95_nearest_rank_seconds": round(tts_p95, 2),
            "audio_samples": [{**item, "duration_seconds": round(item["duration_seconds"], 2)} for item in audio_metadata],
            "asr_load_seconds": round(asr_load, 2),
            "asr_inference_seconds": [round(value, 2) for value in asr_inferences],
            "asr_inference_p50_seconds": round(asr_p50, 2),
            "asr_inference_p95_nearest_rank_seconds": round(asr_p95, 2),
            "normalized_transcript_matches": sum(matches),
            "sample_count": len(PHRASES),
            "system_total_physical_mib": resource_summary["total_physical_mib"],
            "system_available_before_mib": resource_summary["initial_available_mib"],
            "system_available_minimum_mib": resource_summary["minimum_available_mib"],
            "system_cpu_average_percent": resource_summary["cpu_average_percent"],
            "system_cpu_peak_percent": resource_summary["cpu_peak_percent"],
            "system_cpu_sample_count": resource_summary["cpu_sample_count"],
        }
    finally:
        system_resources.close()
    print(json.dumps(summary, ensure_ascii=True, separators=(",", ":")), flush=True)
    return 0 if summary["result"] == "PASS" else 2


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(json.dumps({"result": "FAIL", "error": str(error)}, ensure_ascii=True), flush=True)
        raise SystemExit(1)
