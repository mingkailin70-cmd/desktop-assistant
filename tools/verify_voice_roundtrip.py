#!/usr/bin/env python3
"""Verify locked local TTS -> ASR on CPU without microphone or saved audio."""

from __future__ import annotations

import argparse
import base64
import hashlib
import io
import json
import os
import pathlib
import queue
import subprocess
import sys
import threading
import time
import unicodedata
import wave


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
PHRASE = "你好，小K。请打开本地项目。"
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


def run_worker(task: str, model_id: str, request: dict, models) -> tuple[dict, float, float]:
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
        inference_started = time.perf_counter()
        process.stdin.write(json.dumps(request, ensure_ascii=False, separators=(",", ":")) + "\n")
        process.stdin.flush()
        response = json.loads(read_line(process.stdout, 360))
        inference_seconds = time.perf_counter() - inference_started
        if response.get("ok") is not True:
            raise RuntimeError(f"{task} inference failed: {response}")
        return response, load_seconds, inference_seconds
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
    tts, tts_load, tts_inference = run_worker(
        "tts",
        "qwen3-tts-12hz-0.6b-customvoice",
        {"command": "synthesize", "text": PHRASE},
        models,
    )
    audio = base64.b64decode(tts["wavBase64"], validate=True)
    with wave.open(io.BytesIO(audio), "rb") as wav:
        sample_rate = wav.getframerate()
        channels = wav.getnchannels()
        duration = wav.getnframes() / sample_rate
        if sample_rate not in range(8000, 96001) or channels not in (1, 2) or not 0 < duration <= 60:
            raise RuntimeError("TTS returned an invalid WAV")
    asr, asr_load, asr_inference = run_worker(
        "asr",
        "qwen3-asr-0.6b",
        {
            "command": "transcribe",
            "language": "Chinese",
            "wavBase64": base64.b64encode(audio).decode("ascii"),
        },
        models,
    )
    transcript = asr.get("text", "")
    exact_match = normalize(transcript) == normalize(PHRASE)
    summary = {
        "result": "PASS" if not args.strict_transcript or exact_match else "TRANSCRIPT_MISMATCH",
        "mode": "CPU-only, offline, synthetic TTS-to-ASR, no microphone, no audio persisted",
        "model_revisions": {key: EXPECTED[key][0] for key in EXPECTED},
        "tts_load_seconds": round(tts_load, 2),
        "tts_inference_seconds": round(tts_inference, 2),
        "tts_sample_rate": sample_rate,
        "tts_channels": channels,
        "tts_duration_seconds": round(duration, 2),
        "tts_audio_bytes_in_memory": len(audio),
        "asr_load_seconds": round(asr_load, 2),
        "asr_inference_seconds": round(asr_inference, 2),
        "asr_language": asr.get("language", ""),
        "asr_text_unicode_escaped": transcript.encode("unicode_escape").decode("ascii"),
        "normalized_transcript_match": exact_match,
    }
    print(json.dumps(summary, ensure_ascii=True, separators=(",", ":")), flush=True)
    return 0 if summary["result"] == "PASS" else 2


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(json.dumps({"result": "FAIL", "error": str(error)}, ensure_ascii=True), flush=True)
        raise SystemExit(1)
