# Jevos-v4 社区决策权重中文筛查

## 结论

Jevos-v4 0.9B OpenVINO INT8 社区权重能在本机 CPU 上启动，但没有通过小K决策路由筛查：固定合成中文留出集答对 21/40（52.5%），敏感升级类仅 1/8，副作用前确认类 5/8。不得把它接入生产路由、联系人身份判断、发送授权或 ToolBroker 权限路径。此次只证明候选运行时和评分链路可复现，不证明真实通知场景质量。

## 固定候选身份

- GitHub项目：[feder-cr/jev](https://github.com/feder-cr/jev)，发布`jevos-v4`；权重由[Hugging Face模型仓库](https://huggingface.co/feder-cr/jev)托管。
- 权重revision：`95575aec49b6e90a30c94fef3d59b315dd060780`。
- 格式：OpenVINO IR INT8；角色仅为有限选项决策候选。
- 主要权重文件：`openvino_model.bin`，683,213,388字节，SHA-256 `ad03ebff10d412b57898b283b37530f7a1ed121587314956b9946cdbb03c99bc`。
- 运行时：[jevos-v4 GitHub Release](https://github.com/feder-cr/jev/releases/tag/jevos-v4)，提交`f764b70be2fc7d1ac9ef0cfddc5ccc0a41db7965`带GitHub验证签名；Windows x64 ZIP SHA-256 `e70f03b008d10a35adbe8313f32238d233182c8eff5df75d9a5b3212834b271d`，发布校验清单SHA-256 `0e973f260345e1b2c9145927f23a7c2c295e07642db09de0ce363c2c8fdef3ef`，解出的`jev.exe` SHA-256 `36fa11f15b0effa8f392b9ee2ad30e126945dc0ec9357573dd2c8cabc9aaf751`。
- 模型与运行时许可均为MIT；运行包附第三方许可清单。许可审查不代表训练数据来源已完全独立核验。

权重目录在忽略的`models/`下，运行时在忽略的`.tools/`下；以上模型文件均未提交到Git。

## 评测方法

- 题集：`synthetic-zh-xiaok-routing-holdout-v1`，40条人工合成中文流程题，包含回答/起草、普通工具提案、追问、副作用前确认和敏感升级，每类8条。题目与标签由同一评测者编写，尚未独立盲审。
- 评测入口：`tools/evaluate_jevos_v4_candidate.py`；通过模型锁核对权重文件、运行包和可执行文件散列，再核对CPU服务健康端点的模型指纹。
- 评测器SHA-256：`a9ba4eed0d5f31a48e46210b5726a22ef64a8dfcc64f26f8131b8fc576cf68bb`；题集源文件`tools/decision_benchmark_suites.py` SHA-256：`a24e3835e9d97d4f12dc8deb66b522b875f184169b433b4f8ff40c5d30bdf103`；服务报告权重指纹：`6afd085ed8676530c4e4f0dfd0e532b5903f6445742979b342c30ea3b3b2af53`。
- 运行方式：本机Windows，OpenVINO上游服务、CPU两线程、服务低于普通进程优先级、随机内存API令牌、仅绑定回环地址、状态缓存关闭；运行前要求可用RAM至少8 GiB，运行中低于4 GiB会终止。
- 评测过程中不访问微信/QQ或Windows通知、不保存逐题预测、不使用独显。服务在脚本退出路径终止。
- 复跑命令：

  ```powershell
  python tools/evaluate_jevos_v4_candidate.py
  ```

## 本机结果

| 指标 | 结果 |
|---|---:|
| 正确数 / 准确率 | 21/40 / 52.5% |
| Macro-F1 | 0.5060 |
| 多分类Brier（逐题平方和后平均） | 0.6314 |
| Top-probability ECE（5箱） | 0.1029 |
| 回答/起草 | 4/8 |
| 普通工具提案 | 3/8 |
| 追问澄清 | 8/8 |
| 副作用前确认 | 5/8 |
| 敏感升级 | 1/8 |
| 单题延迟中位 / p95 | 0.4490 / 0.4672秒 |
| 服务就绪时间 | 2.696秒 |
| 峰值工作集 | 1,212.0 MiB |
| 运行前 / 后可用RAM | 11,898 / 10,730 MiB |
| GPU | 未使用 |

## 解释与后续

性能与小内存占用适合轻量研究对照，但关键安全类别明显不合格。特别是敏感升级题的1/8结果意味着不能把该权重用来判断请求是否敏感，也不能用置信度替代固定规则或用户确认。追问题8/8也不能抵消其他类别的失败。

这40题不是独立标注集，不含自然聊天通知；本结果不能代替每个客户端30条真实、可见正文的私聊通知核验，也不能证明模型在长上下文、模糊请求、对抗输入或真实工作负载上的性能。生产模型和ModelBroker配置未改变。下一步优先独立审阅中文题目/标签，再筛选来源与数据披露充分、中文证据明确且适合8 GB显存或CPU运行的社区候选；大模型或视觉权重仍须遵守RAM/显存准入线。
