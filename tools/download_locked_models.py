#!/usr/bin/env python3
"""按 model-lock 中固定 revision 下载权重，并在落盘前后校验来源和 SHA-256。"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import stat
import sys
import urllib.parse
import urllib.error
import urllib.request
from pathlib import Path, PurePosixPath


CHUNK_SIZE = 8 * 1024 * 1024
PROGRESS_INTERVAL = 128 * 1024 * 1024
FILE_ATTRIBUTE_REPARSE_POINT = 0x400
SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
REVISION_RE = re.compile(r"^[0-9a-f]{40}$")


def request_opener(proxy: str | None) -> urllib.request.OpenerDirector:
    if proxy:
        return urllib.request.build_opener(
            urllib.request.ProxyHandler({"http": proxy, "https": proxy})
        )
    return urllib.request.build_opener()


def reject_reparse(path: Path) -> None:
    if not path.exists():
        return
    info = path.lstat()
    if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & FILE_ATTRIBUTE_REPARSE_POINT:
        raise RuntimeError(f"拒绝沿重解析点读写模型目录：{path}")


def safe_target(models_root: Path, local_directory: str, file_name: str) -> Path:
    relative = PurePosixPath(local_directory)
    file_relative = PurePosixPath(file_name)
    if relative.is_absolute() or file_relative.is_absolute():
        raise RuntimeError("模型锁中的本地路径必须是相对路径。")
    if any(part in ("", ".", "..") for part in (*relative.parts, *file_relative.parts)):
        raise RuntimeError("模型锁中的路径包含不安全目录段。")

    destination = models_root.joinpath(*relative.parts, *file_relative.parts)
    resolved = destination.resolve(strict=False)
    try:
        resolved.relative_to(models_root.resolve())
    except ValueError as error:
        raise RuntimeError("模型目标路径越出仓库 models 目录。") from error

    current = models_root
    reject_reparse(current)
    for part in (*relative.parts, *file_relative.parts[:-1]):
        current = current / part
        reject_reparse(current)
    return destination


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while block := stream.read(CHUNK_SIZE):
            digest.update(block)
    return digest.hexdigest()


def load_lock(lock_path: Path) -> dict:
    return json.loads(lock_path.read_text(encoding="utf-8"))


def save_lock(lock_path: Path, data: dict) -> None:
    temporary = lock_path.with_suffix(lock_path.suffix + ".tmp")
    temporary.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    os.replace(temporary, lock_path)


def fetch_json(opener: urllib.request.OpenerDirector, url: str) -> object:
    request = urllib.request.Request(url, headers={"User-Agent": "XiaoK-model-lock-downloader/1.0"})
    with opener.open(request, timeout=60) as response:
        return json.load(response)


def verify_upstream_files(opener: urllib.request.OpenerDirector, model: dict) -> dict[str, dict]:
    source = model.get("source", "")
    parsed = urllib.parse.urlparse(source)
    revision = model.get("revision", "")
    if parsed.scheme != "https" or parsed.netloc != "huggingface.co" or not REVISION_RE.fullmatch(revision):
        raise RuntimeError(f"{model.get('id')}: 来源必须是 Hugging Face HTTPS 固定 revision。")
    repo = parsed.path.strip("/")
    if not repo or ".." in PurePosixPath(repo).parts:
        raise RuntimeError(f"{model.get('id')}: 来源仓库路径无效。")

    api_url = f"https://huggingface.co/api/models/{repo}/tree/{revision}?recursive=true"
    upstream = fetch_json(opener, api_url)
    if not isinstance(upstream, list):
        raise RuntimeError(f"{model.get('id')}: 上游文件清单格式无效。")
    upstream_by_path = {item.get("path"): item for item in upstream if isinstance(item, dict)}
    result: dict[str, dict] = {}
    for locked_file in model.get("files", []):
        name = locked_file.get("name", "")
        if not name or name.startswith("/") or ".." in PurePosixPath(name).parts:
            raise RuntimeError(f"{model.get('id')}: 锁文件名无效：{name}")
        entry = upstream_by_path.get(name)
        if not entry or entry.get("type") != "file":
            raise RuntimeError(f"{model.get('id')}: 固定 revision 中缺少文件：{name}")
        expected_size = locked_file.get("upstreamReportedSizeBytes")
        if not isinstance(expected_size, int) or entry.get("size") != expected_size:
            raise RuntimeError(f"{model.get('id')}: 上游文件大小与锁清单不符：{name}")
        upstream_hash = ((entry.get("lfs") or {}).get("oid") or "").removeprefix("sha256:").lower()
        expected_hash = locked_file.get("expectedUpstreamSha256")
        if upstream_hash and expected_hash and upstream_hash != expected_hash.lower():
            raise RuntimeError(f"{model.get('id')}: 上游 SHA-256 与锁清单不符：{name}")
        if upstream_hash and not expected_hash:
            locked_file["expectedUpstreamSha256"] = upstream_hash
        if locked_file.get("expectedUpstreamSha256") and not SHA256_RE.fullmatch(
            locked_file["expectedUpstreamSha256"].lower()
        ):
            raise RuntimeError(f"{model.get('id')}: SHA-256 格式无效：{name}")
        result[name] = entry
    return result


def download_one(
    opener: urllib.request.OpenerDirector,
    model: dict,
    locked_file: dict,
    destination: Path,
) -> str:
    size = locked_file["upstreamReportedSizeBytes"]
    expected_hash = (locked_file.get("expectedUpstreamSha256") or "").lower()
    local_hash = (locked_file.get("localVerifiedSha256") or "").lower()

    destination.parent.mkdir(parents=True, exist_ok=True)
    reject_reparse(destination.parent)
    partial = destination.with_name(destination.name + ".partial")
    reject_reparse(destination)
    reject_reparse(partial)

    if destination.exists():
        if destination.stat().st_size == size:
            actual = sha256_file(destination)
            if (expected_hash and actual == expected_hash) or (local_hash and actual == local_hash):
                locked_file["localVerifiedSha256"] = actual
                return actual
        raise RuntimeError(f"目标文件已存在但无法用锁清单验证；为避免覆盖已保存数据而停止：{destination}")

    offset = partial.stat().st_size if partial.exists() else 0
    if offset > size:
        raise RuntimeError(f"部分下载文件大于锁定大小；请检查后删除：{partial}")
    if offset == size:
        actual_hash = sha256_file(partial)
        if expected_hash and actual_hash != expected_hash:
            raise RuntimeError(f"完整部分文件 SHA-256 不匹配：{locked_file['name']}；文件保留供检查。")
        if not expected_hash:
            locked_file["expectedUpstreamSha256"] = actual_hash
        locked_file["localVerifiedSha256"] = actual_hash
        os.replace(partial, destination)
        return actual_hash

    repo = urllib.parse.urlparse(model["source"]).path.strip("/")
    revision = model["revision"]
    remote_name = urllib.parse.quote(locked_file["name"], safe="/")
    url = f"https://huggingface.co/{repo}/resolve/{revision}/{remote_name}?download=true"
    headers = {"User-Agent": "XiaoK-model-lock-downloader/1.0"}
    if offset:
        headers["Range"] = f"bytes={offset}-"
    request = urllib.request.Request(url, headers=headers)

    with opener.open(request, timeout=120) as response:
        status = getattr(response, "status", response.getcode())
        if offset and status == 206:
            content_range = response.headers.get("Content-Range", "")
            if not content_range.startswith(f"bytes {offset}-"):
                raise RuntimeError(f"上游续传范围与本地部分文件不一致：{content_range}")
            mode = "ab"
        elif status == 200:
            offset = 0
            mode = "wb"
        else:
            raise RuntimeError(f"上游返回意外 HTTP 状态：{status}")

        written = offset
        next_report = ((written // PROGRESS_INTERVAL) + 1) * PROGRESS_INTERVAL
        with partial.open(mode) as output:
            while block := response.read(CHUNK_SIZE):
                output.write(block)
                written += len(block)
                if written >= next_report:
                    print(f"  {locked_file['name']}: {written:,}/{size:,} bytes", flush=True)
                    next_report = written + PROGRESS_INTERVAL

    actual_size = partial.stat().st_size
    if actual_size != size:
        raise RuntimeError(f"下載大小錯誤：{locked_file['name']}，{actual_size} != {size}；部分文件保留以便續傳。")

    actual_hash = sha256_file(partial)
    if expected_hash and actual_hash != expected_hash:
        raise RuntimeError(f"SHA-256 不匹配：{locked_file['name']}，本機為 {actual_hash}；部分文件保留供檢查。")
    if not expected_hash:
        # 小型配置檔由已驗證 TLS 的固定 revision 取得，首次下載散列作為後續鎖定基線。
        locked_file["expectedUpstreamSha256"] = actual_hash
    locked_file["localVerifiedSha256"] = actual_hash
    os.replace(partial, destination)
    return actual_hash


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", required=True, help="model-lock/models.lock.json 中的模型 ID")
    parser.add_argument("--proxy", help="可選 HTTPS/HTTP 代理，例如 http://127.0.0.1:7897")
    args = parser.parse_args()

    repository = Path(__file__).resolve().parents[1]
    models_root = repository / "models"
    lock_path = repository / "model-lock" / "models.lock.json"
    lock = load_lock(lock_path)
    model = next((item for item in lock.get("models", []) if item.get("id") == args.model), None)
    if model is None:
        raise RuntimeError(f"模型锁中没有此 ID：{args.model}")
    if model.get("status") not in ("candidate", "partially_downloaded", "downloaded_and_verified"):
        raise RuntimeError(f"模型状态不允许下载：{model.get('status')}")
    local_directory = model.get("localDirectory")
    if not local_directory:
        raise RuntimeError(f"{args.model}: 锁清单缺少 localDirectory。")

    models_root.mkdir(parents=True, exist_ok=True)
    reject_reparse(models_root)
    opener = request_opener(args.proxy)
    verify_upstream_files(opener, model)
    save_lock(lock_path, lock)

    total = sum(item["upstreamReportedSizeBytes"] for item in model.get("files", []))
    print(f"模型：{args.model}，revision：{model['revision']}")
    print(f"目标：{models_root / local_directory}")
    print(f"锁定文件合计：{total:,} bytes")

    for locked_file in model.get("files", []):
        target = safe_target(models_root, local_directory, locked_file["name"])
        print(f"取得并校验：{locked_file['name']}", flush=True)
        digest = download_one(opener, model, locked_file, target)
        complete_now = all(item.get("localVerifiedSha256") for item in model.get("files", []))
        model["status"] = "downloaded_and_verified" if complete_now else "partially_downloaded"
        if any(item.get("localVerifiedSha256") for item in model.get("files", [])):
            lock["status"] = "p0-models-partially-downloaded"
        required = [item for item in lock.get("models", []) if item.get("requiredForP0")]
        if required and all(item.get("status") == "downloaded_and_verified" for item in required):
            lock["status"] = "p0-models-downloaded-and-verified-not-evaluated"
        save_lock(lock_path, lock)
        print(f"  SHA-256 {digest}", flush=True)

    print(f"完成：{models_root / local_directory}")
    print("下載僅表示散列已驗證；模型效果、推理環境和硬件資源尚未驗收。")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, RuntimeError, urllib.error.URLError) as error:
        print(f"下載失敗：{error}", file=sys.stderr)
        raise SystemExit(1)
