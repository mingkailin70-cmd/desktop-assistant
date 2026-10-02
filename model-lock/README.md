# 模型与运行环境锁定清单

此目录只保存版本、来源、许可与校验记录，不保存模型、wheel、凭证或用户数据。

- models.lock.json 是模型来源、固定 revision、文件清单和校验状态登记表。使用 `tools/download_locked_models.py` 前后都会查询固定 revision 的上游文件元数据；缺失的文件大小和 LFS SHA256 会先从该固定版本补入锁清单，再进行断点续传。响应提前结束或临时 SSL/连接错误时最多自动重连8次；大小和 SHA-256 均通过才标为 `downloaded_and_verified`，仍不代表推理效果或资源验收通过。
- runtimes.lock.json 是系统运行时与 Python 入口依赖登记表。使用 `tools/download_locked_runtime.py --runtime llama.cpp --proxy http://127.0.0.1:7897` 可下载经固定 SHA-256 锁定的官方 ZIP；`tools/stage_locked_llama_runtime.py` 会复核压缩包与 Qwen GGUF 后安全解压到对应模型版本目录。用户已接受 CUDA 本机使用许可。2026-10-02 使用托管 `LlamaCppModelRuntime`、`ModelBroker` 和回环 `LocalInferenceClient` 完成两次合成聊天请求；权重载入并返回指定短句。验证用 `llama-runtime.json` 已删除。实际 CUDA 层卸载、峰值显存、性能和进程退出时延仍未验收。
- [本地模型推理首测记录](../docs/验收与评测/本地模型推理首测记录.md) 保存两次合成请求的本机证据与未完成的 GPU/生命周期测量项。
- 开发阶段主模型目录位于仓库忽略的 `models\llm\qwen3.5-4b\<40位revision>`；正式安装目标为 `D:\XiaoK\Models\<模型类别>\<模型ID>\<revision>`。托管 llama.cpp 清单 `llama-runtime.json` 与固定运行时 `Runtime\llama-server.exe` 放在对应模型版本目录；Qwen GGUF 文件名及 revision 必须与锁清单一致。
- llama.cpp 固定运行时已获准下载并校验；ASR/TTS Python 环境、wheel 和其他依赖仍须逐项确认。下载或暂存状态不代表模型推理验收通过。提交前确认权重和运行时均被 Git 忽略、`git status` 不列出大型二进制文件。
- requirements-asr.in 和 requirements-tts.in 是顶层依赖输入，分别对应独立 Python 3.12 环境。本机目前只有 Python 3.14.6；Qwen 官方包声明支持到 Python 3.13，因此候选固定为项目内 CPython 3.12.15（Astral 构建 20261001）与 uv 0.12.20。候选依赖为 PyTorch 2.14.1 CUDA 13.2 与 TorchAudio 2.11.0；Qwen兼容性、传递依赖和 wheel SHA256 经批准解析后，才生成对应的 requirements-*.lock.txt。
- 本机文件 SHA256 必须由本机计算；上游文件树中 LFS SHA 作为预期值。固定 revision 中没有 LFS SHA 的小型配置文件，会在 TLS 下载并核对固定 revision/字节数后以首次本机散列建立后续校验基线。
- 版本或文件变化时新增候选记录；新版本通过同一离线评测后再切换，不覆盖已验收版本。
- `blocked-license-review` 表示来源或模型许可未明确，下载脚本会拒绝处理该条目。工具/引擎代码的开源许可证不能自动替代具体权重、token 或数据文件的许可证；取得权利人明确条款并完成评估后，才能改回 `candidate`。

状态：candidate → partially_downloaded → downloaded_and_verified → locally_evaluated → accepted。许可证审查阻塞项不得进入下载状态。首版主模型、ASR、TTS 和 MiMo 9B 四组权重均已下载至仓库忽略的 `models/` 并通过校验，共35个文件、25,954,040,709字节。llama.cpp b11259 CUDA 13.4 两个官方 ZIP 已下载、校验并解压为55个本机文件；两次合成推理冒烟通过，但没有测得 CUDA 实际卸载、显存峰值和标准性能，尚不能标为 accepted。CUDA 本机使用许可已由用户确认；不分发运行包。ASR/TTS 环境和依赖未部署。
