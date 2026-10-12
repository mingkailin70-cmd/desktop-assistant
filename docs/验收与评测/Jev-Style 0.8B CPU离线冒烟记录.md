# Jev-Style 0.8B CPU 离线冒烟记录

日期：2026-10-12  
用途：确认固定的社区决策模型权重、Python实现和CPU环境可以在本机离线协同运行。此记录不是模型质量验收或生产准入结论。

## 固定对象

- 项目：[lawrence3699/jev-style](https://github.com/lawrence3699/jev-style)
- 模型：[Jev-Style-0.8B-Decision-v3](https://huggingface.co/chaoliangUNSW/Jev-Style-0.8B-Decision-v3)
- 仓库revision：`b023d1f9c7858fbf01504577a3bfc349ea5c7385`
- 模型SHA-256：`0f8c861605dcdfb356a63e056baa2106e81042e8981e8d0d26fe50b34599541e`
- 源码`jev_style_decision.py` SHA-256：`b93c2345732be8ef5ec4274b77ded564e421ccb91967a207de4f18254438ce42`
- 权重、代码、配置和清单共12个文件，逐文件SHA-256与`model-lock/models.lock.json`相符，状态为`downloaded_and_verified`。
- 环境目录：`D:\XiaoK\JevStyle\.venv`，与语音环境分离；依赖冻结快照保存在`D:\XiaoK\JevStyle\requirements-resolved-20261012.txt`，均位于仓库外。
- Python 3.12.15、PyTorch 2.14.0+cpu、Transformers 5.17.0、Tokenizers 0.23.2、NumPy 2.5.3；`torch.cuda.is_available()`为`False`。

## 单条合成输入

合成状态：`您的订单被重复扣费。请帮我核对，并告诉我下一步怎么办。`  
问题：`这条通知的主要意图是什么？`  
固定选项：付款/扣费、物流配送、技术故障、其他/无法判断。

输入不含真实通知、联系人信息、账号数据或用户聊天正文；运行设置`HF_HUB_OFFLINE=1`和`TRANSFORMERS_OFFLINE=1`，权重从本地固定目录加载。

## 观测结果

| 指标 | 结果 |
| --- | --- |
| 输出选项 | `billing` |
| 选项概率 | billing 0.9713；delivery 0.0121；technical 0.0040；other 0.0127 |
| 输入长度 | 125 tokens |
| 首次加载 | 约10.0秒 |
| 单条推理 | 1.36秒 |
| 推理设备 | CPU；没有调用GPU |
| 运行时告警 | 缺少可选`causal-conv1d`和`flash-linear-attention`优化核，已退回参考PyTorch实现 |
| 进程内存抽样 | 权重加载期间一次观察到工作集约3,280 MiB；这不是峰值测量 |

测试前可用系统内存约11.2 GiB；结束并释放进程后可用内存约10.99 GiB。GPU数值和系统内存会随其他程序变化，单次采样不代表常驻资源占用。

## 结论与边界

通过了“权重可载入、离线CPU路径可运行、固定选项返回概率”的单条冒烟。样本量为1，不能外推为中文分类准确率、概率校准或通知处理效果；还未测macro-F1、Brier/ECE、拒答与升级率、长输入、连续负载和真实通知。模型输出仍只能作为观察建议，不参与来源身份、联系人身份、正文读取、ToolBroker权限或外发确认。权重卡标Apache-2.0，但训练数据披露了研究用途或许可不清的数据；在单独完成许可复核前，不分发、不打包、不作商业部署。
