# 模型与运行环境锁定清单

此目录只保存版本、来源、许可与校验记录，不保存模型、wheel、凭证或用户数据。

- models.lock.json 是模型来源、固定 revision、文件清单和校验状态登记表。使用 `tools/download_locked_models.py` 前后都会查询固定 revision 的上游文件元数据；缺失的文件大小和 LFS SHA256 会先从该固定版本补入锁清单，再进行断点续传。响应提前结束或临时 SSL/连接错误时最多自动重连8次；大小和 SHA-256 均通过才标为 `downloaded_and_verified`，仍不代表推理效果或资源验收通过。
- runtimes.lock.json 是系统运行时与 Python 入口依赖登记表。使用 `tools/download_locked_runtime.py --runtime llama.cpp --proxy http://127.0.0.1:7897` 可下载经固定 SHA-256 锁定的官方 ZIP；`tools/stage_locked_llama_runtime.py` 会复核压缩包与 Qwen GGUF 后安全解压到对应模型版本目录。用户已接受 CUDA 本机使用许可。2026-10-02 使用托管 `LlamaCppModelRuntime`、`ModelBroker` 和回环 `LocalInferenceClient` 完成两次合成聊天请求；权重载入并返回指定短句。验证用 `llama-runtime.json` 已删除。实际 CUDA 层卸载、峰值显存、性能和进程退出时延仍未验收。
- [本地模型推理首测记录](../docs/验收与评测/本地模型推理首测记录.md) 保存两次合成请求的本机证据与未完成的 GPU/生命周期测量项。
- 开发阶段主模型目录位于仓库忽略的 `models\llm\qwen3.5-4b\<40位revision>`；正式安装目标为 `D:\XiaoK\Models\<模型类别>\<模型ID>\<revision>`。托管 llama.cpp 清单 `llama-runtime.json` 与固定运行时 `Runtime\llama-server.exe` 放在对应模型版本目录；Qwen GGUF 文件名及 revision 必须与锁清单一致。
- llama.cpp 固定运行时已下载并校验；语音 Python 运行时与依赖已安装到仓库忽略的 `.tools/`，完整哈希锁文件纳入 Git。可用 `powershell -ExecutionPolicy Bypass -File tools/setup_voice_environments.ps1` 重建/同步两个语音环境；网络需要代理时传入 `-Proxy http://代理地址:端口`。脚本会核验 CPython、uv 和 CUDA PyTorch wheel 的大小与 SHA-256，依照锁文件同步依赖并做离线导入检查，不更改系统 Python、PATH 或注册表。环境检查通过不代表 ASR/TTS 权重推理、CUDA 实际调用、显存、语音质量或延迟验收通过。
- requirements-asr.in 和 requirements-tts.in 是顶层依赖输入，分别对应独立 Python 3.12 环境。项目内 CPython 固定为 3.12.15（Astral 构建 20261001），uv 固定为 0.12.20；ASR/TTS 已分别锁定全部传递依赖版本和 wheel SHA-256。PyTorch 固定为 2.14.1 CUDA 13.2，TorchAudio 固定为 2.11.0。`uv pip check` 和两个环境的离线模块导入均通过；未加载语音权重、调用 CUDA 设备或访问麦克风。
- TTS 导入时会提示没有 SoX 和 flash-attn；当前锁定的 Qwen3-TTS 12Hz 权重不走可选 25Hz tokenizer 的 SoX 命令路径，缺少 flash-attn 时包会选用其 PyTorch 实现。此提示不代表已做推理速度或音频质量验收。
- 本机文件 SHA256 必须由本机计算；上游文件树中 LFS SHA 作为预期值。固定 revision 中没有 LFS SHA 的小型配置文件，会在 TLS 下载并核对固定 revision/字节数后以首次本机散列建立后续校验基线。
- 版本或文件变化时新增候选记录；新版本通过同一离线评测后再切换，不覆盖已验收版本。
- `blocked-license-review` 表示来源或模型许可未明确，下载脚本会拒绝处理该条目。工具/引擎代码的开源许可证不能自动替代具体权重、token 或数据文件的许可证；取得权利人明确条款并完成评估后，才能改回 `candidate`。

状态：candidate → partially_downloaded → downloaded_and_verified → locally_evaluated → accepted。许可证审查阻塞项不得进入下载状态。首版主模型、ASR、TTS 和 MiMo 9B 四组权重均已下载至仓库忽略的 `models/` 并通过校验，共35个文件、25,954,040,709字节。llama.cpp b11259 CUDA 13.4 两个官方 ZIP 已下载、校验并解压为55个本机文件；两次合成推理冒烟通过，但没有测得 CUDA 实际卸载、显存峰值和标准性能，尚不能标为 accepted。CUDA 本机使用许可已由用户确认；不分发运行包。语音环境现已安装和哈希锁定，依赖一致性与离线导入检查通过；ASR/TTS 权重推理、显卡/麦克风访问与语音质量仍待独立验收。
