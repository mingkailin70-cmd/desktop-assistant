#!/usr/bin/env python3
"""在本机启动受限的 H2O 候选服务、核对访问边界并运行合成筛查。"""

from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import os
import secrets
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path

from decision_benchmark_suites import SUITES
from evaluate_h2o_decision_candidate import MODEL_FILE, MODEL_ID, read_locked_model

UNTRUSTED_ORIGIN = "https://untrusted.invalid"


def fail(message: str) -> None:
    raise RuntimeError(message)


def locked_runtime(repository: Path) -> tuple[Path, dict[str, object]]:
    lock = json.loads((repository / "model-lock" / "models.lock.json").read_text(encoding="utf-8"))
    model = next((item for item in lock["models"] if item.get("id") == MODEL_ID), None)
    runtime = model.get("secureLocalEvaluationRuntime") if isinstance(model, dict) else None
    if not isinstance(runtime, dict) or runtime.get("status") != "built-and-verified-local-only":
        fail("模型锁中没有已核验的受限本机评测运行时。")

    runtime_dir = (repository / runtime["localDirectory"]).resolve(strict=True)
    expected_files = runtime.get("sha256ByFile")
    if not isinstance(expected_files, dict) or "llama-server.exe" not in expected_files:
        fail("模型锁缺少受限运行时文件散列。")

    for name, expected in expected_files.items():
        candidate = (runtime_dir / name).resolve(strict=True)
        if runtime_dir not in candidate.parents:
            fail("运行时文件解析到了锁定目录之外。")
        digest = hashlib.sha256(candidate.read_bytes()).hexdigest()
        if digest.lower() != str(expected).lower():
            fail(f"运行时文件散列不符：{name}")

    server = runtime_dir / "llama-server.exe"
    return server, runtime


def choose_available_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
        probe.bind(("127.0.0.1", 0))
        return int(probe.getsockname()[1])


def assert_memory_budget() -> None:
    class MemoryStatus(ctypes.Structure):
        _fields_ = [
            ("dwLength", ctypes.c_ulong),
            ("dwMemoryLoad", ctypes.c_ulong),
            ("ullTotalPhys", ctypes.c_ulonglong),
            ("ullAvailPhys", ctypes.c_ulonglong),
            ("ullTotalPageFile", ctypes.c_ulonglong),
            ("ullAvailPageFile", ctypes.c_ulonglong),
            ("ullTotalVirtual", ctypes.c_ulonglong),
            ("ullAvailVirtual", ctypes.c_ulonglong),
            ("ullAvailExtendedVirtual", ctypes.c_ulonglong),
        ]

    status = MemoryStatus()
    status.dwLength = ctypes.sizeof(status)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    if not kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):
        raise ctypes.WinError(ctypes.get_last_error())
    available_mib = status.ullAvailPhys // (1024 * 1024)
    if available_mib < 9 * 1024:
        fail(f"可用RAM仅 {available_mib} MiB，低于H2O本机评测9 GiB启动线。")


def request(url: str, api_key: str | None = None, method: str = "GET", headers: dict[str, str] | None = None):
    request_headers = {"Accept": "application/json"}
    if api_key is not None:
        request_headers["Authorization"] = f"Bearer {api_key}"
    if headers:
        request_headers.update(headers)
    req = urllib.request.Request(url, headers=request_headers, method=method)
    return urllib.request.urlopen(req, timeout=3)


def set_below_normal_priority(process: subprocess.Popen[bytes]) -> None:
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenProcess.argtypes = [ctypes.c_uint32, ctypes.c_int, ctypes.c_uint32]
    kernel32.OpenProcess.restype = ctypes.c_void_p
    kernel32.SetPriorityClass.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
    kernel32.SetPriorityClass.restype = ctypes.c_int
    kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
    kernel32.CloseHandle.restype = ctypes.c_int

    handle = kernel32.OpenProcess(0x0200 | 0x0400, False, process.pid)
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        if not kernel32.SetPriorityClass(handle, 0x00004000):
            raise ctypes.WinError(ctypes.get_last_error())
    finally:
        kernel32.CloseHandle(handle)


def wait_until_ready(process: subprocess.Popen[bytes], api_key: str, base_url: str) -> None:
    deadline = time.monotonic() + 90
    last_error: Exception | None = None
    while time.monotonic() < deadline:
        if process.poll() is not None:
            fail(f"受限 H2O 服务启动失败，退出码 {process.returncode}。")
        try:
            with request(f"{base_url}/health", api_key):
                return
        except (OSError, urllib.error.URLError, urllib.error.HTTPError) as error:
            last_error = error
            time.sleep(1)
    fail(f"等待受限 H2O 服务就绪超时：{type(last_error).__name__ if last_error else 'timeout'}")


def verify_security_boundary(api_key: str, base_url: str, expected_origin: str) -> None:
    try:
        with request(f"{base_url}/v1/models") as response:
            status = response.status
    except urllib.error.HTTPError as error:
        status = error.code
    if status not in (401, 403):
        fail(f"未认证的 /v1/models 请求返回 HTTP {status}，服务没有按预期要求密钥。")

    headers = {
        "Origin": UNTRUSTED_ORIGIN,
        "Access-Control-Request-Method": "POST",
        "Access-Control-Request-Headers": "Authorization,Content-Type",
    }
    with request(f"{base_url}/v1/systemone", method="OPTIONS", headers=headers) as response:
        response_origin = response.headers.get("Access-Control-Allow-Origin", "")
        allow_credentials = response.headers.get("Access-Control-Allow-Credentials", "").lower()
    if response_origin != expected_origin or allow_credentials == "true":
        fail(f"CORS 未按锁定源限制（返回 Allow-Origin={response_origin!r}）。")

    with request(f"{base_url}/v1/models", api_key) as response:
        models = json.load(response).get("data")
    if not isinstance(models, list) or len(models) != 1:
        fail("受限服务没有加载唯一锁定模型。")
    info = models[0]
    if MODEL_FILE.lower() not in str(info.get("id", "")).lower():
        fail("受限服务加载的模型与模型锁不符。")
    modalities = info.get("architecture", {}).get("output_modalities", [])
    if "decisions" not in modalities:
        fail("锁定模型没有声明原生 decisions 输出。")


def remove_temp(path: Path | None) -> None:
    if path is None:
        return
    try:
        path.unlink(missing_ok=True)
    except OSError:
        pass


def main() -> int:
    if os.name != "nt":
        fail("此运行入口只支持 Windows 本机评测。")

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--suite", choices=tuple(SUITES), default="synthetic-zh-xiaok-routing-v1")
    args = parser.parse_args()

    repository = Path(__file__).resolve().parents[1]
    model_path, _ = read_locked_model(repository)
    server, _ = locked_runtime(repository)
    assert_memory_budget()
    port = choose_available_port()
    base_url = f"http://127.0.0.1:{port}"
    allowed_origin = base_url

    api_key = secrets.token_urlsafe(36)
    key_file_path: Path | None = None
    stdout_path: Path | None = None
    stderr_path: Path | None = None
    process: subprocess.Popen[bytes] | None = None
    stdout_stream = None
    stderr_stream = None

    try:
        with tempfile.NamedTemporaryFile(
            mode="w", encoding="ascii", newline="\n", prefix="xiaok-h2o-", suffix=".keys", delete=False
        ) as key_file:
            key_file.write(api_key + "\n")
            key_file_path = Path(key_file.name)

        stdout_handle, stdout_name = tempfile.mkstemp(prefix="xiaok-h2o-", suffix=".stdout.log")
        stderr_handle, stderr_name = tempfile.mkstemp(prefix="xiaok-h2o-", suffix=".stderr.log")
        os.close(stdout_handle)
        os.close(stderr_handle)
        stdout_path = Path(stdout_name)
        stderr_path = Path(stderr_name)
        stdout_stream = stdout_path.open("wb")
        stderr_stream = stderr_path.open("wb")

        command = [
            str(server), "--model", str(model_path), "--host", "127.0.0.1", "--port", str(port),
            "--ctx-size", "2048", "--n-gpu-layers", "0", "--threads", "4", "--threads-batch", "4",
            "--parallel", "1", "--no-webui", "--api-key-file", str(key_file_path),
            "--cors-origins", allowed_origin, "--no-cors-credentials", "--cors-headers",
            "Authorization,Content-Type",
        ]
        process = subprocess.Popen(
            command,
            cwd=server.parent,
            stdin=subprocess.DEVNULL,
            stdout=stdout_stream,
            stderr=stderr_stream,
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
        set_below_normal_priority(process)
        wait_until_ready(process, api_key, base_url)

        # llama-server reads its key list at startup; remove the short-lived file as soon as ready.
        remove_temp(key_file_path)
        key_file_path = None
        verify_security_boundary(api_key, base_url, allowed_origin)
        print(f"安全边界核验通过：模型API无密钥请求被拒绝，未信任Origin不获CORS读取权限；健康探针公开且仅绑定127.0.0.1:{port}。", flush=True)

        environment = os.environ.copy()
        environment["XIAOK_H2O_EVAL_API_KEY"] = api_key
        environment["XIAOK_H2O_EVAL_PORT"] = str(port)
        evaluator = repository / "tools" / "evaluate_h2o_decision_candidate.py"
        completed = subprocess.run(
            [sys.executable, "-B", str(evaluator), "--suite", args.suite],
            cwd=repository,
            env=environment,
            check=False,
        )
        return completed.returncode
    finally:
        if process is not None and process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
        if stdout_stream is not None:
            stdout_stream.close()
        if stderr_stream is not None:
            stderr_stream.close()
        remove_temp(key_file_path)
        remove_temp(stdout_path)
        remove_temp(stderr_path)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError, ValueError) as error:
        print(f"错误：{error}", file=sys.stderr)
        raise SystemExit(2)
