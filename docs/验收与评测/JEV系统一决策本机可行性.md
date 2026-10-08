# JEV System 1 决策路径本机可行性核对

核对日期：2026-10-09。用途：给 R0 模型路线作技术准入判断；不是模型质量评测，也没有加载新模型。

## 上游模型形态

截至本次核对，AutoTrust 的 `JEV-9B` 将普通生成 System 2 和结构化决策 System 1 分成两条路径。System 1 接受 `noul`、`choice`、`score` 三类问题，输出选项概率；官方推荐 vLLM，用 `jev-decision` LoRA 和专用24槽决策头。模型卡称 System 2 主干与 Qwen3.5-9B 相同，并列出约17.9 GB BF16 主干、约40.1M参数的 System 1 LoRA、FP32决策头和逐类型校准温度。卡内报告的速度是在 B200 上测得，不代表本机表现。[固定 revision 的 JEV 模型卡](https://huggingface.co/autotrust/JEV-9B/tree/b63f651ce8ed64481d3f5e73ecdb05f740042f01)

模型卡还包含视觉决策演示；那条路径通过 `vl/serve.sh` 配置视觉主干和 System 1 适配器，并不是当前小K已具备的通用桌面控制能力。上游说明直接选择八种机械臂指令并未成功，演示使用连续的二元视觉判断。[JEV 模型卡视觉说明](https://huggingface.co/autotrust/JEV-9B/tree/b63f651ce8ed64481d3f5e73ecdb05f740042f01)

## 本机已有资产与运行条件

- `model-lock/models.lock.json` 锁定的是社区转换的 `JEV-9B.Q4_K_M.gguf`，revision `95be0d83b95147aa9d95ab3f462a2a727276d9a3`，大小5,629,110,048字节、SHA-256 `e1bef4864401bac66ab3ea39d159aa6ac922b14fd3ffd739e9dc2605d82f5ca9`。此文件用于文本生成/编码候选评测；清单中没有官方 `adapter_vllm`、决策头、校准文件或视觉资产。
- 现有 JEV GGUF 在旧版中文 CodeAgent 固定管线测得2/11，已因剩余题无法达到80%门槛而停止。该分数是生成质量结果，不是 System 1 决策头的成绩。详细记录见[MiMo与Qwen编码任务初筛记录](MiMo与Qwen编码任务初筛记录.md)。
- 本机 GPU 为 RTX 5060 Laptop、总显存8,151 MiB；本次资源快照为已用1,678 MiB、可用6,222 MiB。17.9 GB BF16 主干不可能完整驻留该显卡。CPU卸载或量化方案理论上可另行探索，但目前没有本机延迟、内存余量和稳定性证据。该快照只代表一个时点。
- 固定运行时为 llama.cpp b11259。它支持常规 GGUF LoRA，但不能据此认定 JEV 的专用头能直接加载。JEV 将决策头封装到 `lm_head` LoRA；b11259 自带转换脚本会对映射不到主干的 `lm_head` 报错，或在转换映射中忽略它。[JEV 的 vLLM 适配器说明](https://huggingface.co/autotrust/JEV-9B/tree/b63f651ce8ed64481d3f5e73ecdb05f740042f01)；[llama.cpp b11259 LoRA 转换器](https://github.com/ggml-org/llama.cpp/blob/b11259/convert_lora_to_gguf.py)。本次没有下载该官方适配器或运行转换，结论来自已锁定的本机资产、模型卡和固定版本源码。

## R0 准入结论

现有资产和运行时**不能直接提供 JEV System 1**。不将当前 JEV GGUF 标记为决策模型，不更改生产模型，也不为本机8 GB显卡套用 B200 跑分。小K 对确定、固定的权限和动作选择继续使用本地确定性规则；Qwen3.5-4B 仍是中文生成候选，编码发布门槛仍由独立的完整40题评测决定。

若要重开 JEV System 1 子项目，先在隔离原型中实现或验证决策头加载，包括 `lm_head` LoRA 的忠实转换/执行、官方参考输出对照、概率校准/选项顺序稳健性、拒绝边界、GPU至少预留1 GiB、CPU/RAM与延迟。没有这些结果前不接入 ModelBroker、不启动视觉路径。该子项可以在消息身份及30条通知门槛等待用户自然通知时独立排期；它不改变 R0 消息闸门未通过的状态。

本次核对只访问公开模型卡、固定 llama.cpp 源码和仓库锁清单；未读取聊天/Toast内容，未联系联系人，未下载新权重，未启动模型服务，也未更改运行配置。

## 2026-10-09 补充：JEV-27B 与 Qwen3.8

AutoTrust 现有 `JEV-27B` 以 `Qwen3.8-27B` 为底座，公开卡提供 System 2 文本生成和 System 1 类型化决策；System 1 可通过其 `serve_decide.py` 在 vLLM 服务上暴露 `POST /v1/decide`。这是相较 JEV-9B 专用头转换问题更清晰的服务接口，但仍要求部署整套 JEV-27B/vLLM，不是能直接喂给现有 llama.cpp b11259 的小型决策头，也不会替代本地 ToolBroker 的权限判断。[JEV-27B 官方模型卡](https://huggingface.co/autotrust/JEV-27B)

该卡报告权重约52 GB、KV cache约65 KB/token；256K上下文还需约17 GB KV cache。它远超本机 RTX 5060 Laptop 8 GB，权重本身也大于32 GB系统内存；没有适用于当前设备的量化、CPU卸载延迟和校准保真数据。Qwen 官方清单列出的 Qwen3.8 开放权重为27B和2.4T-A95B；后者同样不属于本机候选。[Qwen 官方仓库](https://github.com/QwenLM/Qwen3.8)

**准入决定：不下载 JEV-27B/Qwen3.8，不接入生产 ModelBroker。** 如以后要研究 JEV 的 System 1，须另立离线实验：先评估能保持官方决策头、温度校准和参考输出的量化/CPU方案，再以小型脱敏选项集检验置信度与拒答；任何决策结果仍只能是建议，不能扩大权限或直接执行副作用。现有 JEV-9B Q4 代码候选2/11、System 1 未接入的结论不变。

## 2026-10-09 补充：MiMo V2.6 公开成绩与本机结果

MiMo 官方模型卡报告其 9B SFT checkpoint 在 SWE Verified `avg@3` 为61.1、SWE-Pro `avg@3` 为44.6，并报告 Terminal Bench 2.1 为37.1；卡片标明这些数值来自 MiMo 技术报告，且部分任务集为内部评测。[MiMo V2.6 Distill 9B 官方模型卡](https://huggingface.co/XiaomiMiMo/MiMo-V2.6-Distill-Qwen-9B)

这些成绩值得把 MiMo 保留为近期候选，但不覆盖小K的本机证据：当前固定中文任务管线此前只得到 MiMo Q8_0 3/12、JEV-9B 2/11；各自因剩余题数已无法达到80%门槛而停止。任务集、提示、量化、工具约束及通过标准不同，不能把公开榜单和本机比例合并。故 MiMo 仍只作离线候选，不改变当前运行权重或 P0 编程闸门状态。

以上补充只核对公开模型卡和现有仓库评测记录；没有下载权重、启动推理、改动部署或生产模型配置。
