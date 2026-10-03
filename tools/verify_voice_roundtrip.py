#!/usr/bin/env python3
"""Verify locked local TTS -> ASR with CPU or CUDA TTS, no microphone or saved audio."""

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
GPU_MINIMUM_INITIAL_FREE_MIB = 4_524
GPU_MINIMUM_RUNTIME_FREE_MIB = 1_024


def load_locked_models(
    model_root: pathlib.Path | None = None,
    environment_root: pathlib.Path | None = None,
) -> dict[str, tuple[pathlib.Path, pathlib.Path]]:
    model_root = (model_root or ROOT / "models").resolve(strict=True)
    environment_root = (environment_root or ROOT / ".tools" / "venvs").resolve(strict=True)
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
        model_dir = (model_root / item["localDirectory"]).resolve(strict=True)
        if not model_dir.is_relative_to(model_root):
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
        python = environment_root / environment / "Scripts" / "python.exe"
        if not python.is_file():
            raise RuntimeError(f"Locked Python environment missing: {python}")
        result[model_id] = (model_dir, python)
    if not WORKER.is_file():
        raise RuntimeError("Voice worker is missing")
    return result


def process_environment(python: pathlib.Path, allow_cuda: bool = False) -> dict[str, str]:
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
        "CUDA_VISIBLE_DEVICES": "0" if allow_cuda else "-1",
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


class NvidiaGpuMemorySampler:
    def __init__(self) -> None:
        system_root = pathlib.Path(os.environ.get("SystemRoot", r"C:\Windows"))
        self._nvidia_smi = system_root / "System32" / "nvidia-smi.exe"
        self._taskkill = system_root / "System32" / "taskkill.exe"
        self._lock = threading.Lock()
        self._stop = threading.Event()
        self._owned_pid: int | None = None
        self._breach: str | None = None
        self._result: dict[str, int] | None = None
        self._initial_free_mib, self._total_mib = self._read_gpu()
        if self._has_competing_model_process():
            raise RuntimeError("GPU evaluation refused because llama-server or llama-bench is already running")
        if self._initial_free_mib < GPU_MINIMUM_INITIAL_FREE_MIB:
            raise RuntimeError(
                f"GPU evaluation refused: initial free memory {self._initial_free_mib} MiB is below "
                f"the required {GPU_MINIMUM_INITIAL_FREE_MIB} MiB"
            )
        self._minimum_free_mib = self._initial_free_mib
        self._thread = threading.Thread(target=self._sample_loop, daemon=True)
        self._thread.start()

    def _read_gpu(self) -> tuple[int, int]:
        if not self._nvidia_smi.is_file():
            raise RuntimeError("nvidia-smi is missing; GPU evaluation cannot enforce its VRAM guard")
        result = subprocess.run(
            [str(self._nvidia_smi), "--query-gpu=memory.free,memory.total", "--format=csv,noheader,nounits"],
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=5, check=False,
        )
        if result.returncode != 0:
            raise RuntimeError("nvidia-smi failed; GPU evaluation is blocked: " + result.stderr.strip()[:300])
        rows = [line.strip() for line in result.stdout.splitlines() if line.strip()]
        if len(rows) != 1:
            raise RuntimeError("GPU evaluation requires exactly one readable NVIDIA GPU")
        fields = [item.strip() for item in rows[0].split(",")]
        if len(fields) != 2:
            raise RuntimeError("nvidia-smi returned an invalid memory snapshot")
        try:
            free_mib, total_mib = (int(item) for item in fields)
        except ValueError as error:
            raise RuntimeError("nvidia-smi returned nonnumeric memory values") from error
        if free_mib <= 0 or total_mib <= 0 or free_mib > total_mib:
            raise RuntimeError("nvidia-smi returned an impossible memory snapshot")
        return free_mib, total_mib

    def _has_competing_model_process(self) -> bool:
        if not self._taskkill.is_file():
            raise RuntimeError("taskkill is missing; GPU evaluation cannot verify model-process exclusion")
        tasklist = self._taskkill.with_name("tasklist.exe")
        if not tasklist.is_file():
            raise RuntimeError("tasklist is missing; GPU evaluation cannot verify model-process exclusion")
        for image in ("llama-server.exe", "llama-bench.exe"):
            result = subprocess.run(
                [str(tasklist), "/FI", f"IMAGENAME eq {image}", "/FO", "CSV", "/NH"],
                capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=5, check=False,
            )
            if result.returncode != 0:
                raise RuntimeError("tasklist failed; GPU evaluation cannot verify model-process exclusion")
            if any(line.lstrip().startswith('"') for line in result.stdout.splitlines()):
                return True
        return False

    def set_owned_pid(self, process_id: int | None) -> None:
        with self._lock:
            self._owned_pid = process_id
            should_terminate = process_id is not None and self._breach is not None
        if should_terminate:
            self._terminate_owned_tree(process_id)

    def _terminate_owned_tree(self, process_id: int | None) -> None:
        if process_id is None:
            return
        subprocess.run(
            [str(self._taskkill), "/PID", str(process_id), "/T", "/F"],
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=10, check=False,
        )

    def _sample_loop(self) -> None:
        while not self._stop.wait(0.5):
            try:
                free_mib, _ = self._read_gpu()
                with self._lock:
                    self._minimum_free_mib = min(self._minimum_free_mib, free_mib)
                    if free_mib < GPU_MINIMUM_RUNTIME_FREE_MIB and self._breach is None:
                        self._breach = f"GPU free memory fell below {GPU_MINIMUM_RUNTIME_FREE_MIB} MiB"
                        process_id = self._owned_pid
                    else:
                        process_id = None
                if process_id is not None:
                    self._terminate_owned_tree(process_id)
                if self._breach is not None:
                    return
            except Exception as error:
                with self._lock:
                    if self._breach is None:
                        self._breach = "GPU memory monitoring failed: " + str(error)[:300]
                    process_id = self._owned_pid
                self._terminate_owned_tree(process_id)
                return

    def ensure_safe(self) -> None:
        with self._lock:
            breach = self._breach
        if breach is not None:
            raise RuntimeError(breach)

    def close(self) -> dict[str, int]:
        if self._result is not None:
            return self._result
        self._stop.set()
        self._thread.join(timeout=3)
        try:
            after_free_mib, _ = self._read_gpu()
            with self._lock:
                self._minimum_free_mib = min(self._minimum_free_mib, after_free_mib)
                breach = self._breach
            if breach is not None:
                raise RuntimeError(breach)
        except Exception:
            if self._breach is None:
                raise
            after_free_mib = -1
        self._result = {
            "total_mib": self._total_mib,
            "initial_free_mib": self._initial_free_mib,
            "minimum_free_mib": self._minimum_free_mib,
            "after_worker_free_mib": after_free_mib,
        }
        return self._result


def run_worker_batch(
    task: str, model_id: str, requests: list[dict], models, device: str = "cpu",
    gpu_sampler: NvidiaGpuMemorySampler | None = None,
) -> tuple[list[dict], float, list[float]]:
    model_dir, python = models[model_id]
    errors: list[str] = []
    process = subprocess.Popen(
        [str(python), "-u", str(WORKER), "--task", task, "--model-dir", str(model_dir), "--device", device],
        cwd=ROOT,
        env=process_environment(python, allow_cuda=device == "cuda"),
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        encoding="utf-8",
        errors="replace",
        bufsize=1,
    )
    if gpu_sampler is not None:
        gpu_sampler.set_owned_pid(process.pid)
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
            if gpu_sampler is not None:
                gpu_sampler.ensure_safe()
            if response.get("ok") is not True:
                raise RuntimeError(f"{task} inference failed: {response}")
            responses.append(response)
        return responses, load_seconds, inference_seconds
    except Exception as error:
        raise RuntimeError(f"{error}; worker stderr tail={errors[-20:]!r}") from error
    finally:
        if gpu_sampler is not None:
            gpu_sampler.set_owned_pid(None)
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
    parser.add_argument("--tts-device", choices=("cpu", "cuda"), default="cpu", help="evaluation-only TTS device; ASR remains CPU")
    parser.add_argument("--model-root", type=pathlib.Path, default=ROOT / "models", help="model root containing the locked llm/ and speech/ trees")
    parser.add_argument("--environment-root", type=pathlib.Path, default=ROOT / ".tools" / "venvs", help="root containing the locked asr/ and tts/ Python environments")
    args = parser.parse_args()
    if os.name != "nt":
        raise RuntimeError("This check targets the locked Windows speech environments")
    models = load_locked_models(args.model_root, args.environment_root)
    gpu_sampler = NvidiaGpuMemorySampler() if args.tts_device == "cuda" else None
    system_resources = SystemResourceSampler()
    try:
        tts_responses, tts_load, tts_inferences = run_worker_batch(
            "tts",
            "qwen3-tts-12hz-0.6b-customvoice",
            [{"command": "synthesize", "text": phrase} for phrase in PHRASES],
            models,
            device=args.tts_device,
            gpu_sampler=gpu_sampler,
        )
        gpu_summary = gpu_sampler.close() if gpu_sampler is not None else None
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
            "mode": f"offline, {args.tts_device}-TTS/CPU-ASR, six synthetic samples, no microphone, no audio persisted",
            "resource_scope": "system-wide CPU/RAM and whole-GPU VRAM; system readings include unrelated background processes",
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
            "transcript_match_by_sample": {
                f"S{index:02d}": matched for index, matched in enumerate(matches, start=1)
            },
            "sample_count": len(PHRASES),
            "system_total_physical_mib": resource_summary["total_physical_mib"],
            "system_available_before_mib": resource_summary["initial_available_mib"],
            "system_available_minimum_mib": resource_summary["minimum_available_mib"],
            "system_cpu_average_percent": resource_summary["cpu_average_percent"],
            "system_cpu_peak_percent": resource_summary["cpu_peak_percent"],
            "system_cpu_sample_count": resource_summary["cpu_sample_count"],
            "gpu_memory": gpu_summary,
        }
    finally:
        if gpu_sampler is not None:
            gpu_sampler.close()
        system_resources.close()
    print(json.dumps(summary, ensure_ascii=True, separators=(",", ":")), flush=True)
    return 0 if summary["result"] == "PASS" else 2


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(json.dumps({"result": "FAIL", "error": str(error)}, ensure_ascii=True), flush=True)
        raise SystemExit(1)
