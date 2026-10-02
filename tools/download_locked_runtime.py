#!/usr/bin/env python3
"""Download only runtime assets recorded in model-lock/runtimes.lock.json."""

from __future__ import annotations

import argparse
import hashlib
import http.client
import json
import os
import re
import socket
import ssl
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
LOCK_PATH = ROOT / "model-lock" / "runtimes.lock.json"
DOWNLOAD_ROOT = ROOT / ".tools" / "downloads" / "runtimes"
CHUNK_SIZE = 8 * 1024 * 1024
PROGRESS_INTERVAL = 64 * 1024 * 1024
MAX_RETRIES = 8
SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
REDIRECT_HOSTS = {
    "github.com",
    "release-assets.githubusercontent.com",
    "objects.githubusercontent.com",
}


def load_lock() -> dict:
    lock = json.loads(LOCK_PATH.read_text(encoding="utf-8"))
    if lock.get("schemaVersion") != 1 or not isinstance(lock.get("runtimes"), list):
        raise RuntimeError("运行时锁清单格式不受支持。")
    return lock


def build_opener(proxy: str | None) -> urllib.request.OpenerDirector:
    handlers: list[urllib.request.BaseHandler] = []
    if proxy:
        parsed = urllib.parse.urlparse(proxy)
        if parsed.scheme not in {"http", "https"} or not parsed.hostname or parsed.username or parsed.password:
            raise RuntimeError("代理必须是无凭证的 HTTP(S) URL。")
        handlers.append(urllib.request.ProxyHandler({"http": proxy, "https": proxy}))
    else:
        handlers.append(urllib.request.ProxyHandler())
    handlers.append(urllib.request.HTTPSHandler(context=ssl.create_default_context()))
    return urllib.request.build_opener(*handlers)


def safe_destination(name: str, runtime_id: str, version: str) -> Path:
    if not name or Path(name).name != name or name in {".", ".."}:
        raise RuntimeError(f"锁清单中含不安全的资产文件名：{name!r}")
    destination = DOWNLOAD_ROOT / runtime_id / version / name
    root = DOWNLOAD_ROOT.resolve()
    destination.parent.mkdir(parents=True, exist_ok=True)
    parent = destination.parent.resolve()
    if parent != root and root not in parent.parents:
        raise RuntimeError("运行时下载目录越过仓库内锁定缓存根目录。")
    return destination


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while block := stream.read(CHUNK_SIZE):
            digest.update(block)
    return digest.hexdigest()


def check_final_url(response: object) -> None:
    final_url = response.geturl()  # type: ignore[attr-defined]
    parsed = urllib.parse.urlparse(final_url)
    if parsed.scheme != "https" or parsed.hostname not in REDIRECT_HOSTS:
        raise RuntimeError(f"拒绝非官方 HTTPS 下载重定向目标：{final_url}")


def download_asset(
    opener: urllib.request.OpenerDirector,
    runtime: dict,
    asset: dict,
) -> tuple[Path, str]:
    name = asset.get("name")
    expected_size = asset.get("sizeBytes")
    expected_hash = asset.get("sha256", "").lower()
    if not isinstance(name, str) or not isinstance(expected_size, int) or expected_size <= 0:
        raise RuntimeError("锁清单中的运行时资产名称或字节数无效。")
    if not SHA256_RE.fullmatch(expected_hash):
        raise RuntimeError(f"锁清单中的运行时资产 SHA-256 无效：{name}")

    runtime_id = runtime.get("id")
    version = runtime.get("version")
    source = urllib.parse.urlparse(runtime.get("source", ""))
    if source.scheme != "https" or source.hostname != "github.com":
        raise RuntimeError("运行时来源必须是固定 GitHub 官方 release URL。")
    release_path = source.path.rstrip("/")
    if not release_path.endswith(f"/releases/tag/{version}"):
        raise RuntimeError("运行时版本与固定 release 标签不一致。")
    repository = release_path.removesuffix(f"/releases/tag/{version}")
    url = f"https://github.com{repository}/releases/download/{urllib.parse.quote(version)}/{urllib.parse.quote(name)}"

    destination = safe_destination(name, runtime_id, version)
    partial = destination.with_name(destination.name + ".partial")
    if destination.exists():
        actual_size = destination.stat().st_size
        actual_hash = sha256_file(destination)
        if actual_size == expected_size and actual_hash == expected_hash:
            return destination, actual_hash
        raise RuntimeError(f"已存在的运行时资产不符合锁定大小或 SHA-256，保留原文件并停止：{destination}")

    for attempt in range(MAX_RETRIES + 1):
        offset = partial.stat().st_size if partial.exists() else 0
        if offset > expected_size:
            raise RuntimeError(f"断点文件大于锁定资产大小，请检查后处理：{partial}")
        if offset == expected_size:
            break

        headers = {"User-Agent": "XiaoK-locked-runtime-downloader/1.0", "Accept": "application/octet-stream"}
        if offset:
            headers["Range"] = f"bytes={offset}-"
        request = urllib.request.Request(url, headers=headers)

        try:
            with opener.open(request, timeout=120) as response:
                check_final_url(response)
                status = getattr(response, "status", response.getcode())
                if offset and status == 206:
                    content_range = response.headers.get("Content-Range", "")
                    if not content_range.startswith(f"bytes {offset}-"):
                        raise RuntimeError(f"上游续传范围和本地断点不一致：{content_range}")
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
                            print(f"  {name}: {written:,}/{expected_size:,} bytes", flush=True)
                            next_report = written + PROGRESS_INTERVAL
        except urllib.error.HTTPError as error:
            if error.code not in {408, 429, 500, 502, 503, 504} or attempt == MAX_RETRIES:
                raise RuntimeError(f"运行时资产下载失败，HTTP {error.code}：{name}") from error
            print(f"  HTTP {error.code}，保留断点后重试 {attempt + 1}/{MAX_RETRIES}：{name}", flush=True)
            time.sleep(min(attempt + 1, 5))
            continue
        except (urllib.error.URLError, http.client.HTTPException, ssl.SSLError, socket.timeout, TimeoutError, ConnectionError) as error:
            if attempt == MAX_RETRIES:
                raise RuntimeError(f"网络连续中断，断点已保留：{name}；{error}") from error
            print(f"  网络中断，保留断点并重连 {attempt + 1}/{MAX_RETRIES}：{name}", flush=True)
            time.sleep(min(attempt + 1, 5))
            continue

        actual_size = partial.stat().st_size
        if actual_size > expected_size:
            raise RuntimeError(f"下载字节数超过锁定大小：{name}，{actual_size} > {expected_size}")
        if actual_size < expected_size:
            if attempt == MAX_RETRIES:
                raise RuntimeError(f"上游响应连续提前结束；断点保留：{name}，{actual_size}/{expected_size} bytes")
            print(f"  响应提前结束，保留断点 {actual_size:,}/{expected_size:,} bytes；重连 {attempt + 1}/{MAX_RETRIES}", flush=True)
            time.sleep(min(attempt + 1, 5))
            continue
        break

    actual_size = partial.stat().st_size
    if actual_size != expected_size:
        raise RuntimeError(f"下载大小不符：{name}，{actual_size} != {expected_size}")
    actual_hash = sha256_file(partial)
    if actual_hash != expected_hash:
        raise RuntimeError(f"SHA-256 不符；断点文件保留以供检查：{name}，实际 {actual_hash}")
    os.replace(partial, destination)
    return destination, actual_hash


def save_lock(lock: dict) -> None:
    temporary = LOCK_PATH.with_name(LOCK_PATH.name + ".tmp")
    temporary.write_text(json.dumps(lock, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    os.replace(temporary, LOCK_PATH)


def main() -> int:
    parser = argparse.ArgumentParser(description="按 runtimes.lock.json 下载固定官方运行时并核验 SHA-256。")
    parser.add_argument("--runtime", required=True, help="固定运行时 ID，例如 llama.cpp")
    parser.add_argument("--proxy", help="可选 HTTPS/HTTP 代理 URL，例如 http://127.0.0.1:7897")
    args = parser.parse_args()

    lock = load_lock()
    matches = [item for item in lock["runtimes"] if item.get("id") == args.runtime]
    if len(matches) != 1:
        raise RuntimeError(f"运行时锁清单中没有唯一匹配的 ID：{args.runtime}")
    runtime = matches[0]
    artifacts = runtime.get("artifacts")
    if not isinstance(artifacts, list) or not artifacts:
        raise RuntimeError("锁清单没有可下载的固定运行时资产。")

    opener = build_opener(args.proxy)
    for asset in artifacts:
        path, actual_hash = download_asset(opener, runtime, asset)
        asset["localVerifiedSha256"] = actual_hash
        asset["localPath"] = path.relative_to(ROOT).as_posix()
        print(f"已验证：{asset['name']} ({asset['sizeBytes']:,} bytes, SHA-256 {actual_hash})")

    runtime["status"] = "downloaded_and_verified"
    runtime["notes"] = (
        f"{runtime['version']} 固定资产均已下载至仓库忽略的缓存目录并通过本机 SHA-256 校验；"
        "尚未解压运行或执行模型推理。"
    )
    lock["lastLocalVerificationDate"] = time.strftime("%Y-%m-%d")
    save_lock(lock)
    print(f"清单已更新：{LOCK_PATH.relative_to(ROOT)}；运行时未启动。")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, RuntimeError, urllib.error.URLError) as error:
        print(f"下载失败：{error}", file=__import__("sys").stderr)
        raise SystemExit(1)
