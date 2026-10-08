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
