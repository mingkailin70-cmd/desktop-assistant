# Laya 多语言决策候选 20 题中文试筛

日期：2026-10-13（Asia/Shanghai）
性质：人工编写的合成短消息小规模筛选；不是发布门槛、真实聊天评估或生产准入。

## 固定对象与复现

- 模型仓库：[`convaiinnovations/laya`](https://huggingface.co/convaiinnovations/laya)，固定 revision `7b928d828b7b0e022f929d9bd2e44165aa270148`，Apache-2.0。
- 多语言决策权重：`multilingual/model.safetensors`，643,835,514 字节，SHA-256 `9d628fd971b700382ac6f65920a86f149777b2e748e0c955fb3b19695aa8f204`。模型、分词器和代码共 8 个锁定文件，均有 SHA-256，存放在 Git 忽略的 `models/` 下。
- 独立环境：Python 3.12.15，Laya 0.4.6，PyTorch 2.14.0+cpu，Transformers 5.17.0；位于 `D:\XiaoK\Laya\.venv`。依赖输入和带哈希锁文件为 [`requirements.in`](../../tools/XiaoK.DecisionBenchmark/requirements.in) 与 [`requirements.lock.txt`](../../tools/XiaoK.DecisionBenchmark/requirements.lock.txt)。
- 评测器：[`evaluate_laya_candidate.py`](../../tools/evaluate_laya_candidate.py)，SHA-256 `9C97DD96955C96FFF8BF9DF6457CBE20935066953F8B44F65018248704F7C4F1`。执行前核验模型锁内全部文件散列；设置 Hugging Face/Transformers 离线模式，限制两个 CPU 线程，并拒绝 CUDA。
- 复现命令：`D:\XiaoK\Laya\.venv\Scripts\python.exe -B tools\evaluate_laya_candidate.py`。
- 输入与题目和 Jev-Style 试筛完全相同：20 条短合成中文消息、五类各 4 条。使用 Laya 类型化 `choice` 输出及其原生选项概率，不保存逐题预测。

## 结果

| 指标 | 结果 |
| --- | ---: |
| 样本数 | 20 |
| 准确率 | 19/20（0.95） |
| 各类准确率 | 付款/扣费 4/4；物流 4/4；账号 4/4；技术 4/4；其他 3/4 |
| Macro-F1 | 0.9492 |
| 多类 Brier | 0.0922 |
| 5-bin top-probability ECE | 0.0474 |
| 单题 CPU 推理中位数 / p95 | 0.124 秒 / 0.132 秒 |
| 模型加载时间 | 26.744 秒 |
| 峰值进程工作集 | 2,243.6 MiB |
| 执行设备 | CPU；PyTorch `2.14.0+cpu`，CUDA 不可用 |
| 启动前资源快照 | 可用 RAM 12.38 GiB；RTX 空闲显存 5,531 MiB，GPU 利用率 4% |
| 运行后显存 | 空闲显存约 5,546 MiB；没有使用 GPU |
| 逐题预测落盘 | 否 |

当日已有 Jev-Style v3 的同题集结果是 20/20，Macro-F1 1.00，Brier 0.0093，ECE 0.0562，CPU 推理中位数/p95 为 1.620/1.865 秒，加载 8.376 秒，进程工作集观察值约 3,280 MiB。两者都只是 20 条清晰短题的小试：Laya 推理明显更快、工作集较小，但总体少答对 1 题，唯一错误落在“其他”类；Jev 的校准分数也更好。由于样本极少，ECE 的差异不能视作稳定优势。

## 结论与边界

Laya 值得保留为**轻量 CPU 候选**，不能据此替换现有候选或接入生产路由。下一轮先扩展只含合成内容的中文题集，增加边界模糊、需要追问、拒绝/升级、只读工具提案和对抗性格式；同时报告分层错误，不保存逐条模型输出。只有在更大、独立、人工复核的题集达到预设门槛后，才考虑影子运行。

这次评估没有真实微信/QQ通知或聊天内容，没有测试联系人身份、工具权限、发送、长上下文、多轮对话、桌面任务、长期运行或模型故障恢复。Laya 概率只作观察性候选建议；不得决定通知归属、联系人身份、权限或外发授权。模型仍不进入 Host 生产链路。
