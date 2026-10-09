# 小K本地桌面助手

小K是面向单台 Windows 电脑的桌面宠物与本地助手。首版目标覆盖六类任务：打开应用、查找文件、在隔离工作区完成编程任务、分析可见的私聊通知、起草回复，以及在用户检查收件人、正文和附件并确认后发送。

模型推理仅在本机运行，不会把提示或任务交给 Codex，也不会静默回退到云端模型。微信和 QQ 的消息收发仍依赖各自正常的网络连接。

## 当前工程状态

状态快照：2026-10-09。当前账户已安装签名MSIX 0.1.83.0，状态Ok；包139,305,762字节，SHA-256 1C5725AC58712E1D0117F6C1456CBDD5AFB59798EFE63E9FDEC648893745A554，SignTool验签0警告/0错误，开发证书信任未变。安装目录含PdfPig 0.1.16程序集和许可说明；隐藏诊断启动窗口不可见、前台句柄启动/运行/退出不变、正常退出码0。安装版PDF工作进程通过合成单页PDF文本提取；窗口句柄0，退出后Host进程归零。Release构建0警告/0错误、安全套件119项（0跳过）、桌面体验76项通过。没有启动模型、麦克风或通知；摘要模型质量、时延、普通用户界面和对抗性PDF峰值资源仍未验收。

- WPF Host 提供可缩放银渐层桌宠、托盘、快捷键、文字输入、任务中心、逐任务取消、停麦和非模态待办入口。登录启动、重启恢复、DPI/多屏及持续运行还未完整验收。
- ToolBroker 当前源码包含白名单应用/窗口、范围文件搜索和内容位置搜索、本机文档摘要、只读扩展名分类、限定目录内复制、同目录重命名、同卷搜索范围内移动、单文件回收站操作、ZIP归档及HTTPS公网网页读取/下载。0.1.83.0支持配置搜索根内64 KiB文本/源码、受限DOCX，以及最多8 MiB/100页/64 KiB提取文字的PDF可选择文本摘要。PDF解析运行在低优先级子进程中，通过管道传递内容、不落盘，并由Windows Job Object限制512 MiB进程内存和20秒运行时间；扫描件不做OCR。正文按不可信内容短时交给本机模型。摘要真实模型质量仍未实测；真实用户文件副作用、消息发送和通用桌面操作仍待安装版验收。批量整理、登录态浏览器和通用网页操作尚未接入。
- 编程代理限于已选择项目的隔离工作区。现有固定评测尚无模型通过完整发布门槛，不把局部题目分数当成可用编码能力。
- 本地 Qwen/llama.cpp 与 ASR/TTS 环境已部署到 D 盘；模型质量、长期资源、真实麦克风/扬声器及设备行为仍待验收。当前可用显存低于模型启动门槛，本阶段未加载模型。推理没有云端回退。
- Windows 通知监听默认关闭，当前没有完成微信/QQ来源身份与私聊分类实测；真实消息发送适配器尚未接入。权限范围仍只允许 QQ 联系人 K、微信联系人 L，逐条外发必须检查最终内容并确认。
- 固定 SDK 10.0.401。最近Windows安全套件119项通过、0项跳过，桌面体验专项76项通过；各阶段具体结果见[开发进度与待办](docs/产品与进度/开发进度与待办.md)。这些合成检查不替代六场景、普通安装界面和长期运行验收。
- VPet Core稳定包已接入正式Host，已验证隐藏启动和若干离屏渲染路径；真实桌面透明合成、动画素材、可见交互窗口和长期资源仍需验收。
- 模型、用户数据库、缓存、通知正文、录音、截图、凭证和本机设置不得提交。源码状态、阶段证据和剩余验收见[开发进度与待办](docs/产品与进度/开发进度与待办.md)。
## 构建

需要 Windows 和本机固定版 .NET SDK 10.0.401。Host 的 Windows SDK targeting pack 固定为 `Microsoft.Windows.SDK.NET.Ref` 10.0.26100.87；网页读取锁定 Playwright .NET 1.63.0，PDF文本读取锁定 PdfPig 0.1.16及精确传递依赖，NuGet 源映射只允许明确列出的包 ID。

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

可将 [设置样例](src/XiaoK.Host/settings.example.json) 复制到用户设置路径后按需调整。默认模型端点为 `http://127.0.0.1:8080/`，程序只接受回环地址。Qwen + llama.cpp 托管链路已通过合成请求；独立 CPU/GPU 微基准确认 GPU 配置卸载33/33层，生成均速约95.34 token/s、采样显存占用峰值4,378 MiB。另有一次 ModelBroker 托管生命周期复验：首请求冷启动6.28秒、热请求10样本 p50/p95 为2.82/3.51秒、取消约0.51秒、空闲262秒后卸载并可重新加载，退出后进程回收；运行峰值显存占用4,678 MiB。短样本不能证明长期稳定、压力/OOM恢复或模型质量。本机已安装并运行 0.1.14.0；完整安全套件92项通过、0项跳过。P0模型任务成功率、真实语音、通知、外发和六类场景发布门槛仍未通过。

## 文档导航

项目文档按产品与进度、架构与接口、开发与发布、使用说明、仓库管理分类，入口见[文档索引](docs/README.md)。整体架构说明见[整体架构与项目结构](docs/架构与接口/整体架构与项目结构.md)，模型权重、D盘环境和回滚方案见[模型权重与环境部署方案](docs/开发与发布/模型权重与环境部署方案.md)。协作前先读根目录的 [AI 协作者说明](AGENTS.md)、[贡献指南](CONTRIBUTING.md) 和 [安全政策](SECURITY.md)。

每个完成的里程碑应在 `main` 上形成聚焦提交。当前开发仓库中的 `models/` 只供本机下载与验证，Git 必须保持忽略这些文件；个人数据仍保存在仓库之外。

仓库目前没有许可证文件。公开可见不代表授予代码复用许可；只有仓库所有者选定许可证后，才会添加相应授权。
