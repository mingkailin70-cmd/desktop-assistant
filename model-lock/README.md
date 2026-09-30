# 模型与运行环境锁定清单

此目录只保存版本、来源、许可与校验记录，不保存模型、wheel、凭证或用户数据。

- models.lock.json 是模型候选登记表。未下载的文件保持 candidate 状态；上游 revision、准确文件清单和预期散列需在准备阶段再次核验。
- runtimes.lock.json 是系统运行时与 Python 入口依赖登记表。运行包 asset 名、实际散列及安装状态必须从官方发布页和本机文件确认。
- 托管 llama.cpp 使用配置的仓库外模型目录中的 `llama-runtime.json`（默认 `D:\XiaoK\Models`）；仅登记清单不代表官方资产来源已复核。清单必须和仓库锁中的版本、资产 SHA、模型 revision 及本机 SHA 对应；运行时固定放在 `Runtime\llama-server.exe`，Qwen GGUF 固定放在模型目录顶层。
- 托管 llama.cpp 使用仓库外 `D:\XiaoK\Models\llama-runtime.json`；仅登记清单不代表官方资产来源已复核。该清单必须和仓库锁中的版本、资产 SHA、模型 revision 及本机 SHA 对应，且运行时固定放在 `Runtime\llama-server.exe`，Qwen GGUF 固定放在模型目录顶层。
- requirements-asr.in 和 requirements-tts.in 是顶层依赖输入，分别对应独立 Python 3.12 环境。传递依赖解析为固定版本和 wheel SHA256 后，才生成对应的 requirements-*.lock.txt。
- 本机文件 SHA256 必须由本机计算；上游网页给出的 SHA 只能记作预期值，不能标记为本机已验证。
- 版本或文件变化时新增候选记录；新版本通过同一离线评测后再切换，不覆盖已验收版本。

状态：candidate → approved_for_download → downloaded_and_verified → locally_evaluated → accepted。提交代码不代表批准下载。
