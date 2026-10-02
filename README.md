# 小K本地桌面助手

小K是面向单台 Windows 电脑的桌面宠物与本地助手。首版目标覆盖六类任务：打开应用、查找文件、在隔离工作区完成编程任务、分析可见的私聊通知、起草回复，以及在用户检查收件人、正文和附件并确认后发送。

模型推理仅在本机运行，不会把提示或任务交给 Codex，也不会静默回退到云端模型。微信和 QQ 的消息收发仍依赖各自正常的网络连接。

## 当前工程状态

仓库处于首轮工程实施阶段，尚未达到发布状态：

- `global.json` 固定 .NET SDK 版本为 10.0.401。
- WPF Host 默认显示紧凑桌宠；单击打开任务面板，桌宠与托盘菜单提供设置、取消和停麦入口，`Ctrl+Shift+K` 可唤起任务面板。展开卡片含“查找文件”“分析消息”“起草回复”快捷入口，点击只填入命令前缀，用户补充内容并点运行后才开始任务。桌宠位置以物理像素保存在本机设置，使用 PerMonitorV2 并约束在当前显示器工作区；多屏/DPI与实际交互尚未验收。
- 固定工具包含允许列表内的应用启动、窗口切换、有限范围文件名搜索和隔离工作区编程代理。窗口切换会核验前台 HWND；VS Code 目标须匹配配置的小K项目目录。隔离代理支持只读检索、补丁生成、差异审阅，以及用户明确选择后的固定 .NET 还原/测试配方；真实项目行为与本地模型成功率仍待评测。
- 本地推理客户端只接受回环 HTTP 地址。
- Windows `UserNotificationListener` 与微信/QQ 发布者 AUMID 白名单代码已接入。签名开发版 MSIX 已在当前账户安装，但尚未启动或请求通知授权；真实通知身份和私聊/群聊分类未验收。会话类型未知时策略拒绝读取正文或自动分析。
- Host 已加入用户点击后录音、停止后本地转写并由用户检查文本、显式点击播报等控件；ASR/TTS 已完成一条合成往返。真实音频设备、Windows 权限提示和端到端延迟仍待验收。浏览器自动化、微信/QQ 发送适配器及附件发送尚未实现。
- 任务状态、联系人回复风格偏好和有限审批审计已使用 Windows 系统 `winsqlite3.dll` 写入 SQLite。任务表只保存规范类别、状态、时间和受限错误码；偏好表只保存本地联系人名称、固定风格 ID、用户确认来源和更新时间；审计表只保存固定动作 ID、固定结果、随机记录 ID 和时间，不含预览正文、附件、项目路径，也不具备重放能力。SQLite v1/v2/v3→v4 迁移前备份并经过合成数据检查。设置页已提供只读审批历史、v4 数据库备份/恢复和本地历史清理；清理会保留迁移标记、移除设置文件中的旧联系人偏好副本，并清除数据目录内可识别的小K备份。隐私清理和恢复流程尚未用本机真实数据验收。
- 桌面窗口和托盘已有“最近任务”入口，可查看脱敏任务状态和隔离编程工作区路径；不会自动续跑任务。AppContainer ACL/身份的 Host 强杀后恢复已通过模拟 Host 探针，任务续跑和断电恢复仍待验收。
- 开发阶段模型权重位于仓库忽略目录 `models/`，正式部署再迁移到 `D:\XiaoK\Models`。首版主模型、ASR、TTS Base/CustomVoice、MiMo 9B 和固定 llama.cpp b11259 运行时资产均已下载并按锁清单校验；文件均不得提交。个人数据、消息内容、录音和凭证保存在仓库外。

P0 仍需本机证据：每款客户端至少 30 条正文可见的私聊通知、模型与语音延迟和显存测量，以及中文编程任务评测。六类场景的正常、失败和取消路径全部验收前，不应把应用当作日常可用产品。

## 构建

需要 Windows 和本机固定版 .NET SDK 10.0.401。Host 的 Windows SDK targeting pack 固定为 `Microsoft.Windows.SDK.NET.Ref` 10.0.26100.87；本机已缓存，NuGet 源映射仅允许这个包。应用目前没有其他 NuGet 依赖。

```powershell
$dotnet = if (Test-Path .\.tools\dotnet\dotnet.exe) { Resolve-Path .\.tools\dotnet\dotnet.exe } else { 'dotnet' }
& $dotnet --version
& $dotnet restore XiaoK.sln
& $dotnet build XiaoK.sln --configuration Release --no-restore
& $dotnet exec tests\XiaoK.Tools.SafetyChecks\bin\Release\net10.0\XiaoK.Tools.SafetyChecks.dll
& $dotnet run --project src\XiaoK.Host\XiaoK.Host.csproj --configuration Release --no-build --no-restore
```

`.tools\dotnet` 是本仓库开发机上的 SDK 副本，已被 Git 忽略；其他电脑可使用已安装的 10.0.401 SDK。首次还原若需下载固定 Windows SDK 引用包，NuGet 配置会将来源限制到该包；其他依赖不会自动获得来源映射。

## 本机数据与设置

- 用户设置：`%LOCALAPPDATA%\XiaoK\settings.json`
- 任务状态：`D:\XiaoK\Data\tasks.sqlite3`；如存在旧版 `tasks.json`，迁移后原文件保留作恢复副本。
- 联系人回复风格：同一 SQLite 数据库的 `contact_reply_styles` 表；旧设置中的偏好仅在首次迁移时导入，迁移标记防止用户删除后再次导入。迁移本身不改写 `settings.json`，设置页不再把新的偏好写回该文件。
- 开发阶段主模型目录：`D:\Desktop\learn\siri\models\llm\qwen3.5-4b\f9f88ac3e234be915e23811a6d28ea287bdb927e`（已由 Git 忽略）；正式安装目标模型、缓存和评测目录：`D:\XiaoK\Models`、`D:\XiaoK\Cache`、`D:\XiaoK\Evaluations`

可将 [设置样例](src/XiaoK.Host/settings.example.json) 复制到用户设置路径后按需调整。默认模型端点为 `http://127.0.0.1:8080/`，程序只接受回环地址。Qwen + llama.cpp 托管链路已通过合成请求；独立 CPU/GPU 微基准确认 GPU 配置卸载33/33层，生成均速约95.34 token/s、采样显存占用峰值4,378 MiB。另有一次 ModelBroker 托管生命周期复验：首请求冷启动6.28秒、热请求10样本 p50/p95 为2.82/3.51秒、取消约0.51秒、空闲262秒后卸载并可重新加载，退出后进程回收；运行峰值显存占用4,678 MiB。短样本不能证明长期稳定、压力/OOM恢复或模型质量。签名开发版 MSIX 已安装但尚未启动；P0 与首版发布门槛仍未通过。

## 文档导航

项目文档按产品与进度、架构与接口、开发与发布、使用说明、仓库管理分类，入口见[文档索引](docs/README.md)。整体架构说明见[整体架构与项目结构](docs/架构与接口/整体架构与项目结构.md)，模型权重、D盘环境和回滚方案见[模型权重与环境部署方案](docs/开发与发布/模型权重与环境部署方案.md)。协作前先读根目录的 [AI 协作者说明](AGENTS.md)、[贡献指南](CONTRIBUTING.md) 和 [安全政策](SECURITY.md)。

每个完成的里程碑应在 `main` 上形成聚焦提交。当前开发仓库中的 `models/` 只供本机下载与验证，Git 必须保持忽略这些文件；个人数据仍保存在仓库之外。

仓库目前没有许可证文件。公开可见不代表授予代码复用许可；只有仓库所有者选定许可证后，才会添加相应授权。
