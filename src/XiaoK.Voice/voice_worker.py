"""小K本地语音子进程。只接收有界 WAV/文本 JSON 行，不使用网络或麦克风。"""

import argparse
import base64
import binascii
import io
import json
import sys
import traceback

PROTOCOL_OUT = sys.stdout
sys.stdout = sys.stderr  # 第三方库的诊断输出不能污染标准输出协议。

MAX_REQUEST_LINE = 20 * 1024 * 1024
MAX_AUDIO_BYTES = 12 * 1024 * 1024
MAX_AUDIO_SECONDS = 60
MAX_TEXT_LENGTH = 1000


def emit(value):
    PROTOCOL_OUT.write(json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n")
    PROTOCOL_OUT.flush()


GPU_MODEL_MINIMUM_FREE_MIB = 4524


def load_model(task, model_dir, device):
    import torch

    if device == "cuda":
        if task != "tts":
            raise ValueError("GPU evaluation is enabled only for the fixed TTS candidate")
        if not torch.cuda.is_available():
            raise RuntimeError("CUDA_UNAVAILABLE")
        free_bytes, _ = torch.cuda.mem_get_info(0)
        if free_bytes < GPU_MODEL_MINIMUM_FREE_MIB * 1024 * 1024:
            raise RuntimeError("GPU_ADMISSION_REJECTED")
        gpu_dtype = torch.bfloat16 if torch.cuda.is_bf16_supported() else torch.float16
        device_map = "cuda:0"
    else:
        gpu_dtype = torch.float32
        device_map = "cpu"

    if task == "asr":
        from qwen_asr import Qwen3ASRModel

        return Qwen3ASRModel.from_pretrained(
            model_dir,
            torch_dtype=gpu_dtype,
            device_map=device_map,
            local_files_only=True,
            trust_remote_code=False,
            low_cpu_mem_usage=True,
            max_new_tokens=256,
        )

    from qwen_tts import Qwen3TTSModel

    return Qwen3TTSModel.from_pretrained(
        model_dir,
        torch_dtype=gpu_dtype,
        device_map=device_map,
        local_files_only=True,
        trust_remote_code=False,
        low_cpu_mem_usage=True,
    )


def transcribe(model, request):
    import numpy as np
    import soundfile as sf
    from qwen_asr.inference.qwen3_asr import MAX_ASR_INPUT_SECONDS

    encoded = request.get("wavBase64")
    if not isinstance(encoded, str) or len(encoded) > ((MAX_AUDIO_BYTES + 2) // 3) * 4:
        raise ValueError("INVALID_AUDIO")
    try:
        wav_bytes = base64.b64decode(encoded, validate=True)
    except (binascii.Error, ValueError):
        raise ValueError("INVALID_AUDIO") from None
    if not wav_bytes or len(wav_bytes) > MAX_AUDIO_BYTES:
        raise ValueError("INVALID_AUDIO")

    try:
        info = sf.info(io.BytesIO(wav_bytes))
    except Exception:
        raise ValueError("INVALID_WAV") from None
    if info.format != "WAV" or info.channels not in (1, 2) or info.samplerate < 8000 or info.samplerate > 96000:
        raise ValueError("INVALID_WAV")
    if info.frames <= 0 or info.frames > info.samplerate * MAX_AUDIO_SECONDS:
        raise ValueError("AUDIO_TOO_LONG")
    try:
        audio, sample_rate = sf.read(io.BytesIO(wav_bytes), dtype="float32", always_2d=False)
    except Exception:
        raise ValueError("INVALID_WAV") from None
    if audio.ndim == 2:
        audio = np.mean(audio, axis=1, dtype=np.float32)
    if audio.ndim != 1 or sample_rate < 8000 or sample_rate > 96000:
        raise ValueError("INVALID_WAV")
    duration = len(audio) / float(sample_rate)
    if duration <= 0 or duration > min(MAX_AUDIO_SECONDS, MAX_ASR_INPUT_SECONDS):
        raise ValueError("AUDIO_TOO_LONG")
    if not np.isfinite(audio).all():
        raise ValueError("INVALID_WAV")

    language = request.get("language", "Auto")
    if language not in ("Auto", "Chinese"):
        raise ValueError("UNSUPPORTED_LANGUAGE")
    results = model.transcribe(audio=(audio, int(sample_rate)), language=None if language == "Auto" else "Chinese")
    if not isinstance(results, list) or len(results) != 1:
        raise RuntimeError("INVALID_MODEL_RESPONSE")
    item = results[0]
    text = str(getattr(item, "text", ""))[:4000]
    detected_language = str(getattr(item, "language", ""))[:64]
    return {"ok": True, "text": text, "language": detected_language}


def synthesize(model, request):
    import numpy as np
    import soundfile as sf

    text = request.get("text")
    if not isinstance(text, str) or not text.strip() or len(text) > MAX_TEXT_LENGTH:
        raise ValueError("INVALID_TEXT")
    wavs, sample_rate = model.generate_custom_voice(
        text=text,
        speaker="Vivian",
        language="Chinese",
        instruct="用自然、清晰的普通话播报。",
        non_streaming_mode=True,
    )
    if not isinstance(wavs, list) or len(wavs) != 1 or sample_rate < 8000 or sample_rate > 96000:
        raise RuntimeError("INVALID_MODEL_RESPONSE")
    audio = np.asarray(wavs[0], dtype=np.float32).reshape(-1)
    if len(audio) == 0 or not np.isfinite(audio).all() or len(audio) / float(sample_rate) > MAX_AUDIO_SECONDS:
        raise RuntimeError("INVALID_MODEL_AUDIO")
    stream = io.BytesIO()
    sf.write(stream, audio, int(sample_rate), format="WAV", subtype="PCM_16")
    wav_bytes = stream.getvalue()
    if len(wav_bytes) > MAX_AUDIO_BYTES:
        raise ValueError("AUDIO_TOO_LARGE")
    return {"ok": True, "sampleRate": int(sample_rate), "wavBase64": base64.b64encode(wav_bytes).decode("ascii")}


def main():
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--task", choices=("asr", "tts"), required=True)
    parser.add_argument("--model-dir", required=True)
    parser.add_argument("--device", choices=("cpu", "cuda"), default="cpu")
    args = parser.parse_args()

    try:
        model = load_model(args.task, args.model_dir, args.device)
    except Exception:
        traceback.print_exc(file=sys.stderr)
        emit({"type": "ready", "ok": False, "code": "MODEL_LOAD_FAILED"})
        return 2

    emit({"type": "ready", "ok": True})
    for line in sys.stdin.buffer:
        if len(line) > MAX_REQUEST_LINE:
            emit({"ok": False, "code": "REQUEST_TOO_LARGE"})
            return 3
        if not line.strip():
            continue
        try:
            request = json.loads(line.decode("utf-8"))
            if not isinstance(request, dict):
                raise ValueError("INVALID_REQUEST")
            if args.task == "asr" and request.get("command") == "transcribe":
                response = transcribe(model, request)
            elif args.task == "tts" and request.get("command") == "synthesize":
                response = synthesize(model, request)
            else:
                response = {"ok": False, "code": "UNKNOWN_COMMAND"}
        except ValueError as exc:
            response = {"ok": False, "code": str(exc)[:64]}
        except Exception:
            traceback.print_exc(file=sys.stderr)
            response = {"ok": False, "code": "INFERENCE_FAILED"}
        emit(response)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
