# 模型与运行环境锁定清单

此目录只保存版本、来源、许可与校验记录，不保存模型、wheel、凭证或用户数据。

- models.lock.json 是模型来源、固定 revision、文件清单和校验状态登记表。使用 `tools/download_locked_models.py` 前后都会查询固定 revision 的上游文件元数据；缺失的文件大小和 LFS SHA256 会先从该固定版本补入锁清单，再进行断点续传。响应提前结束或临时 SSL/连接错误时最多自动重连8次；大小和 SHA-256 均通过才标为 `downloaded_and_verified`，仍不代表推理效果或资源验收通过。
- runtimes.lock.json 是系统运行时与 Python 入口依赖登记表。运行包 asset 名、实际散列及安装状态必须从官方发布页和本机文件确认。
- 开发阶段主模型目录位于仓库忽略的 `models\llm\qwen3.5-4b\<40位revision>`；正式安装目标为 `D:\XiaoK\Models\<模型类别>\<模型ID>\<revision>`。托管 llama.cpp 清单 `llama-runtime.json` 与固定运行时 `Runtime\llama-server.exe` 放在对应模型版本目录；Qwen GGUF 文件名及 revision 必须与锁清单一致。
- 运行时及依赖仍须单独获准下载；登记清单不代表资产来源已复核或本机已安装。提交前确认权重均被 Git 忽略、`git status` 不列出模型文件。
- requirements-asr.in 和 requirements-tts.in 是顶层依赖输入，分别对应独立 Python 3.12 环境。传递依赖解析为固定版本和 wheel SHA256 后，才生成对应的 requirements-*.lock.txt。
- 本机文件 SHA256 必须由本机计算；上游文件树中 LFS SHA 作为预期值。固定 revision 中没有 LFS SHA 的小型配置文件，会在 TLS 下载并核对固定 revision/字节数后以首次本机散列建立后续校验基线。
- 版本或文件变化时新增候选记录；新版本通过同一离线评测后再切换，不覆盖已验收版本。
- `blocked-license-review` 表示来源或模型许可未明确，下载脚本会拒绝处理该条目。工具/引擎代码的开源许可证不能自动替代具体权重、token 或数据文件的许可证；取得权利人明确条款并完成评估后，才能改回 `candidate`。

状态：candidate → partially_downloaded → downloaded_and_verified → locally_evaluated → accepted。许可证审查阻塞项不得进入下载状态。首版主模型、ASR、TTS 和 MiMo 9B 四组权重均已下载至仓库忽略的 `models/` 并通过校验，共35个文件、25,954,040,709字节；运行时、Python 环境和依赖包仍要分别获得确认。
