# 本机 D 盘运行环境部署核验

日期：2026-10-04

本记录证明模型与 ASR/TTS 运行环境已部署到本机正式路径，并记录一轮 CPU 合成语音往返结果。它不代表六类场景、真人语音、模型质量或首版发布门槛通过。

## 部署目录

| 用途 | 正式路径 | 说明 |
|---|---|---|
| 本地模型与推理运行时 | `D:\XiaoK\Models` | Qwen3.5-4B Q4_K_M、Qwen3-ASR-0.6B、Qwen3-TTS Base/CustomVoice、llama.cpp b11259 |
| 语音 Python 环境 | `D:\XiaoK\Voice` | CPython 3.12.15 x64；`asr`、`tts` 为独立虚拟环境 |
| 部署前源副本 | `D:\XiaoK\InstallStaging\Models`、`D:\XiaoK\InstallStaging\Voice` | 暂时保留用于恢复；不会由 Host 自动加载 |

模型根目录现有87个文件，总计10,348,191,088字节。部署前检查了31个锁定权重文件（9,602,558,901字节）、55个运行时文件和1个 `llama-runtime.json`。源与目标逐项比较相对路径、大小和 SHA-256，全部一致；`llama-server.exe` 散列与模型内运行时配置相符。暂存源与目标均未发现重解析点。模型根目录只部署 Qwen 与语音模型；MiMo、JEV 等质量评测候选不纳入常驻模型目录。

## 语音环境核验

运行 `tools/setup_voice_environments.ps1 -EnvironmentRoot D:\XiaoK\Voice -OfflineOnly`，没有下载依赖或访问网络。脚本核验固定 CPython 归档、uv 和 wheel 哈希，在目标路径新建两个独立环境。

- ASR：CPython 3.12.15 x64；`uv pip check` 检查97个包并通过；离线导入 `torch`、`torchaudio`、`qwen_asr` 通过。
- TTS：CPython 3.12.15 x64；`uv pip check` 检查91个包并通过；离线导入 `torch`、`torchaudio`、`qwen_tts`、`onnxruntime` 通过。
- 运行环境固定 Torch 2.14.1+cu132、TorchAudio 2.11.0+cpu。导入检查没有加载语音权重、调用 CUDA 设备或访问麦克风。
- 安装日志出现 SoX 未安装、flash-attn 未安装的可选组件提示，以及 nagisa 的无效转义序列 `SyntaxWarning`；脚本最终退出码为0，依赖检查及导入均通过。

## 正式路径合成语音往返

命令：

```powershell
& 'D:\XiaoK\Voice\asr\Scripts\python.exe' -u 'tools\verify_voice_roundtrip.py' --strict-transcript --tts-device cpu --model-root 'D:\XiaoK\Models' --environment-root 'D:\XiaoK\Voice'
```

结果为 `PASS`。Qwen3-TTS CustomVoice 合成六条合成句子，再由 Qwen3-ASR 识别；按既定去空格、去标点规则严格匹配6/6。TTS p50/p95 为11.92/15.48秒，模型加载10.51秒；ASR p50/p95 为1.07/2.60秒，加载10.52秒。音频只在内存中流转，未写入文件。脚本使用 CPU TTS 和 CPU ASR，无麦克风、无 GPU、无云端回退。

整机可用内存由测试前16,173 MiB降至最低11,053 MiB；CPU平均/峰值17.5%/31.8%，207个整机采样点。这些 CPU/RAM 数值包含其他进程，不能单独归因于小K。GPU字段为脚本未采样；`nvidia-smi` 测试期间约为1,571 MiB已用、6,329 MiB空闲、0%利用率。测试结束后语音 Python 工作进程数为0，可用RAM回升到16,182 MiB。

## 安装版 Host 状态（部署时的 0.1.14.0 快照）

已安装的 MSIX 为0.1.14.0。用户设置的 `ModelRoot` 为 `D:\XiaoK\Models`；`VoiceEnvironmentRoot` 字段为空时，设置加载逻辑会应用默认值 `D:\XiaoK\Voice`。部署完成后通过桌宠菜单选择“退出小K”进行优雅退出，再重新启动。Host 页面显示托管 llama.cpp 将在首次推理时按需启动、空闲4分钟后卸载，说明安装实例发现了本地模型配置。本次没有通过 Host 发起模型请求；LLM权重没有加载，模型回复、显存准入实际请求和卸载路径仍待验证。

## 尚待验收

- 补验显存不足拒绝、Host 内取消与恢复、精确空闲卸载时延及压力下进程回收。
- 通过 Host UI 验证 ASR/TTS 工作进程端到端调用，以及麦克风、扬声器、权限拒绝、停麦和取消路径。
- 评估真人语料、噪声、中文与中英混说、主观音质和长时稳定性。
- 完成登录启动、锁屏/休眠、重启恢复、长时运行和正式回滚演练。
- 微信/QQ通知、消息适配、六类任务、中文编码任务集与所有 P0/P4 发布门槛仍未通过。

## 后续更新：0.1.19.0 安装版本地问答（2026-10-04）

本节更新部署时的 Host 状态。使用已安装的 XiaoK.Host 0.1.19.0（进程 PID 48512），对正式 D 盘模型目录发起一次无工具合成问答。提示为“请只回复‘本地问答正常’，不要调用工具，也不要操作电脑”；界面返回完全匹配的“本地问答正常”，任务状态已结束。没有提交真实聊天内容、启动通知监听或使用麦克风。

运行进程 PID 47412 的映像路径及命令行均指向 D:\XiaoK\Models 下锁定的 Qwen3.5-4B Q4_K_M 与 llama.cpp b11259，端点为回环地址。模型驻留时的一次 GPU 快照：8151 MiB 总显存、4702 MiB 已用、3198 MiB 可用、1% 利用率、45°C；llama-server 工作集约3212 MiB。该快照不是峰值采样。之后复核 PID 已退出；GPU 快照回到1618 MiB已用、6282 MiB可用。Host 界面显示空闲4分钟后卸载；进程曾在08:35:46仍存在，并在08:36:39快照中消失。没有精确记录推理租约释放时刻，因此不能用这两次快照证明精确卸载时延。

此结果只通过安装 Host 的单次合成问答、正常加载与一次进程回收观察；不证明回答质量、稳定时延、准入拒绝/取消、压力与 OOM 恢复、长时稳定或首版发布门槛。完整模型与场景验收仍待完成。
