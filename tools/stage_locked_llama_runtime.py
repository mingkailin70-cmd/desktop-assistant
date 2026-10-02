#!/usr/bin/env python3
"""Safely stage verified llama.cpp archives beside the locked Qwen GGUF."""

from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import stat
import sys
import time
import uuid
import zipfile
from pathlib import Path, PurePosixPath


ROOT = Path(__file__).resolve().parents[1]
RUNTIME_LOCK = ROOT / "model-lock" / "runtimes.lock.json"
MODEL_LOCK = ROOT / "model-lock" / "models.lock.json"
DOWNLOADS = ROOT / ".tools" / "downloads" / "runtimes"
CHUNK_SIZE = 8 * 1024 * 1024
SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
RUNTIME_ID = "llama.cpp"
MODEL_ID = "qwen3.5-4b-q4km"


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while block := stream.read(CHUNK_SIZE):
            digest.update(block)
    return digest.hexdigest()


def is_reparse(path: Path) -> bool:
    try:
        attributes = path.lstat().st_file_attributes
    except AttributeError:
        return path.is_symlink()
    return bool(attributes & 0x400) or path.is_symlink()


def checked_archive_path(name: str) -> PurePosixPath:
    relative = PurePosixPath(name)
    if (
        not name
        or relative.is_absolute()
        or any(part in {"", ".", ".."} for part in relative.parts)
        or "\\" in name
        or ":" in name
        or "\x00" in name
    ):
        raise RuntimeError(f"压缩包含有不安全路径：{name!r}")
    return relative


def read_locks() -> tuple[dict, dict, dict, dict]:
    runtime_lock = json.loads(RUNTIME_LOCK.read_text(encoding="utf-8"))
    model_lock = json.loads(MODEL_LOCK.read_text(encoding="utf-8"))
    runtime_matches = [item for item in runtime_lock.get("runtimes", []) if item.get("id") == RUNTIME_ID]
    model_matches = [item for item in model_lock.get("models", []) if item.get("id") == MODEL_ID]
    if runtime_lock.get("schemaVersion") != 1 or len(runtime_matches) != 1 or len(model_matches) != 1:
        raise RuntimeError("运行时或模型锁清单缺少唯一且受支持的条目。")
    runtime, model = runtime_matches[0], model_matches[0]
    if runtime.get("version") != "b11259" or model.get("status") != "downloaded_and_verified":
        raise RuntimeError("llama.cpp 版本或 Qwen 固定模型状态不符合首版锁定要求。")
    return runtime_lock, model_lock, runtime, model


def verify_archive_files(runtime: dict) -> list[tuple[dict, Path]]:
    archives: list[tuple[dict, Path]] = []
    for artifact in runtime.get("artifacts", []):
        name = artifact.get("name")
        expected_size = artifact.get("sizeBytes")
        expected_hash = artifact.get("localVerifiedSha256", "").lower()
        if (
            not isinstance(name, str)
            or not isinstance(expected_size, int)
            or not SHA256_RE.fullmatch(expected_hash)
            or expected_hash != artifact.get("sha256", "").lower()
        ):
            raise RuntimeError(f"运行时压缩包尚无完整本机校验记录：{name}")
        archive = DOWNLOADS / RUNTIME_ID / runtime["version"] / name
        if not archive.is_file() or is_reparse(archive):
            raise RuntimeError(f"运行时压缩包缺失或为重解析点：{archive}")
        if archive.stat().st_size != expected_size or sha256_file(archive) != expected_hash:
            raise RuntimeError(f"运行时压缩包本机大小或 SHA-256 与锁清单不符：{name}")
        archives.append((artifact, archive))
    if not archives:
        raise RuntimeError("运行时锁清单没有资产。")
    return archives


def safe_model_directory(model: dict) -> tuple[Path, Path]:
    relative = model.get("localDirectory")
    if not isinstance(relative, str):
        raise RuntimeError("锁定的 Qwen 模型没有本机目录。")
    models_root = (ROOT / "models").resolve()
    model_root = (models_root / relative).resolve()
    if models_root not in model_root.parents:
        raise RuntimeError("锁定模型目录越出仓库 models 根目录。")
    current = models_root
    for part in Path(relative).parts:
        current = current / part
        if current.exists() and is_reparse(current):
            raise RuntimeError(f"模型路径中包含重解析点：{current}")
    model_file = next((item for item in model.get("files", []) if item.get("name", "").endswith(".gguf")), None)
    if not model_file or not SHA256_RE.fullmatch(model_file.get("localVerifiedSha256", "")):
        raise RuntimeError("Qwen 锁定 GGUF 没有本机 SHA-256 校验记录。")
    model_path = model_root / model_file["name"]
    if not model_path.is_file() or is_reparse(model_path):
        raise RuntimeError(f"Qwen GGUF 缺失或为重解析点：{model_path}")
    if model_path.stat().st_size != model_file.get("upstreamReportedSizeBytes"):
        raise RuntimeError("Qwen GGUF 文件大小与模型锁清单不符。")
    if sha256_file(model_path) != model_file["localVerifiedSha256"]:
        raise RuntimeError("Qwen GGUF SHA-256 与模型锁清单不符。")
    return model_root, model_path


def extract_verified_archives(archives: list[tuple[dict, Path]], staging: Path) -> list[dict]:
    staged: dict[str, dict] = {}
    for _, archive in archives:
        with zipfile.ZipFile(archive) as package:
            for info in package.infolist():
                relative = checked_archive_path(info.filename)
                if info.is_dir():
                    continue
                mode = info.external_attr >> 16
                if stat.S_ISLNK(mode):
                    raise RuntimeError(f"压缩包含符号链接：{info.filename}")
                name = relative.as_posix()
                digest = hashlib.sha256()
                destination = staging.joinpath(*relative.parts)
                resolved = destination.resolve()
                if staging.resolve() not in resolved.parents:
                    raise RuntimeError(f"压缩包路径越出暂存目录：{info.filename}")
                destination.parent.mkdir(parents=True, exist_ok=True)

                with package.open(info, "r") as source:
                    if name in staged:
                        prior = staging / name
                        with prior.open("rb") as existing:
                            for block in iter(lambda: existing.read(CHUNK_SIZE), b""):
                                digest.update(block)
                        if prior.stat().st_size != info.file_size:
                            raise RuntimeError(f"不同压缩包存在冲突文件：{name}")
                        # The archive is hash-pinned; compare its duplicate payload too.
                        duplicate_digest = hashlib.sha256()
                        while block := source.read(CHUNK_SIZE):
                            duplicate_digest.update(block)
                        if duplicate_digest.hexdigest() != digest.hexdigest():
                            raise RuntimeError(f"不同压缩包中同名文件内容不一致：{name}")
                        continue

                    with destination.open("xb") as output:
                        while block := source.read(CHUNK_SIZE):
                            output.write(block)
                            digest.update(block)
                staged[name] = {
                    "name": name,
                    "sizeBytes": destination.stat().st_size,
                    "sha256": digest.hexdigest(),
                }
    if "llama-server.exe" not in staged:
        raise RuntimeError("固定运行时压缩包内没有 llama-server.exe。")
    return [staged[name] for name in sorted(staged)]


def save_lock(lock: dict) -> None:
    temporary = RUNTIME_LOCK.with_name(RUNTIME_LOCK.name + ".tmp")
    temporary.write_text(json.dumps(lock, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    os.replace(temporary, RUNTIME_LOCK)


def main() -> int:
    runtime_lock, _, runtime, model = read_locks()
    archives = verify_archive_files(runtime)
    model_root, _ = safe_model_directory(model)
    final_directory = model_root / "Runtime"
    if final_directory.exists():
        if is_reparse(final_directory) or not final_directory.is_dir():
            raise RuntimeError(f"运行时目标目录不是普通目录：{final_directory}")
        expected = runtime.get("stagedFiles")
        if not isinstance(expected, list) or not expected:
            raise RuntimeError(f"运行时目标目录已经存在，但没有可验证清单；拒绝覆盖：{final_directory}")
        for entry in expected:
            item = final_directory / entry["name"]
            if not item.is_file() or is_reparse(item) or item.stat().st_size != entry["sizeBytes"]:
                raise RuntimeError(f"已暂存运行时文件缺失、大小错误或为重解析点：{item}")
            if sha256_file(item) != entry["sha256"]:
                raise RuntimeError(f"已暂存运行时文件 SHA-256 不匹配：{item}")
        print(f"固定运行时已经暂存并复核：{final_directory}")
        return 0

    staging = model_root / f".Runtime.staging-{uuid.uuid4().hex}"
    staging.mkdir()
    try:
        staged_files = extract_verified_archives(archives, staging)
        os.replace(staging, final_directory)
    except Exception:
        shutil.rmtree(staging, ignore_errors=True)
        raise

    runtime["status"] = "staged_not_executed"
    runtime["stagedDirectory"] = final_directory.relative_to(ROOT).as_posix()
    runtime["stagedFiles"] = staged_files
    runtime["notes"] = (
        "固定资产已下载、校验并解压至锁定模型目录；尚未生成启用清单、启动运行时或执行模型推理。"
    )
    runtime_lock["lastLocalVerificationDate"] = time.strftime("%Y-%m-%d")
    save_lock(runtime_lock)
    print(f"已安全暂存 {len(staged_files)} 个运行时文件至：{final_directory}")
    print("llama-runtime.json 未创建；运行时未启动。")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, RuntimeError, zipfile.BadZipFile) as error:
        print(f"暂存失败：{error}", file=sys.stderr)
        raise SystemExit(1)
