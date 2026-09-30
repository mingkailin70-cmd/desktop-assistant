# 小K本地桌面助手

小K是面向单台 Windows 电脑的桌面宠物与本地助手。首版目标覆盖六类任务：打开应用、查找文件、在隔离工作区完成编程任务、分析可见的私聊通知、起草回复，以及在用户检查收件人、正文和附件并确认后发送。

模型推理仅在本机运行，不会把提示或任务交给 Codex，也不会静默回退到云端模型。微信和 QQ 的消息收发仍依赖各自正常的网络连接。

## 当前工程状态

仓库处于首轮工程实施阶段，尚未达到发布状态：

- `global.json` 固定 .NET SDK 版本为 10.0.401。
- WPF 桌面壳、托盘菜单、`Ctrl+Shift+K` 快捷键、取消和停麦入口可以编译。
- 固定工具目前包含允许列表内的应用启动、有限范围文件名查找和隔离工作区编程代理；编程代理生成待审阅差异，用户另行批准后可运行固定的 .NET 还原/测试配方。当前没有 Windows 文件系统沙箱，项目测试代码仍可能越出工作区产生副作用。
- 本地推理客户端只接受回环 HTTP 地址。
- Windows `UserNotificationListener` 已接入，但需具有 MSIX 身份并取得用户授权才会启动。当前发布者 AUMID 和私聊/群聊分类尚未实测；会话类型未知时不会读取正文或自动分析。
- 录音、本地模型进程管理、浏览器操作和微信/QQ实际发送尚未实现。
- 任务状态、联系人回复风格偏好和有限审批审计已使用 Windows 系统 `winsqlite3.dll` 写入 SQLite。任务表只保存规范类别、状态、时间和受限错误码；偏好表只保存本地联系人名称、固定风格 ID、用户确认来源和更新时间；审计表只保存固定动作 ID、固定结果、随机记录 ID 和时间，不含预览正文、附件、项目路径，也不具备重放能力。旧任务 JSON 与设置 JSON 保留作恢复来源；SQLite v1→v3 迁移前备份并经过合成数据检查。设置页已提供只读审批历史、v3 数据库备份和恢复；恢复前会另存活动数据库保护副本，且不会重放旧任务。隐私清理和本机真实数据验收仍待完成。
- 桌面窗口和托盘已有“最近任务”入口，可查看脱敏任务状态和隔离编程工作区路径；不会自动续跑任务，实际崩溃恢复仍待验收。
- 模型权重、运行时包、私聊正文、个人数据和本机凭证不得存入仓库。

P0 仍需本机证据：每款客户端至少 30 条正文可见的私聊通知、模型与语音延迟和显存测量，以及中文编程任务评测。六类场景的正常、失败和取消路径全部验收前，不应把应用当作日常可用产品。

## 构建

需要 Windows 和本机固定版 .NET SDK 10.0.401。Host 的 Windows SDK targeting pack 固定为 `Microsoft.Windows.SDK.NET.Ref` 10.0.26100.87；本机已缓存，NuGet 源映射仅允许这个包。应用目前没有其他 NuGet 依赖。

```powershell
$dotnet = if (Test-Path .\.tools\dotnet\dotnet.exe) { Resolve-Path .\.tools\dotnet\dotnet.exe } else { 'dotnet' }
& $dotnet --version
& $dotnet restore XiaoK.sln
& $dotnet build XiaoK.sln --configuration Release --no-restore
& $dotnet run --project tests\XiaoK.Tools.SafetyChecks\XiaoK.Tools.SafetyChecks.csproj --configuration Release --no-build --no-restore
& $dotnet run --project src\XiaoK.Host\XiaoK.Host.csproj --configuration Release --no-build --no-restore
```

`.tools\dotnet` 是本仓库开发机上的 SDK 副本，已被 Git 忽略；其他电脑可使用已安装的 10.0.401 SDK。首次还原若需下载固定 Windows SDK 引用包，NuGet 配置会将来源限制到该包；其他依赖不会自动获得来源映射。

## 本机数据与设置

- 用户设置：`%LOCALAPPDATA%\XiaoK\settings.json`
- 任务状态：`D:\XiaoK\Data\tasks.sqlite3`；如存在旧版 `tasks.json`，迁移后原文件保留作恢复副本。
- 联系人回复风格：同一 SQLite 数据库的 `contact_reply_styles` 表；旧设置中的偏好仅在首次迁移时导入，迁移标记防止用户删除后再次导入。迁移本身不改写 `settings.json`，设置页不再把新的偏好写回该文件。
- 模型、缓存和评测数据目标目录：`D:\XiaoK\Models`、`D:\XiaoK\Cache`、`D:\XiaoK\Evaluations`

可将 [设置样例](src/XiaoK.Host/settings.example.json) 复制到用户设置路径后按需调整。默认模型端点为 `http://127.0.0.1:8080/`，程序只接受回环地址。模型下载和第三方运行时安装不属于仓库内容。

## 文档导航

项目文档按产品与进度、架构与接口、开发与发布、使用说明、仓库管理分类，入口见[文档索引](docs/README.md)。整体架构说明见[整体架构与项目结构](docs/架构与接口/整体架构与项目结构.md)，模型权重、D盘环境和回滚方案见[模型权重与环境部署方案](docs/开发与发布/模型权重与环境部署方案.md)。协作前先读根目录的 [AI 协作者说明](AGENTS.md)、[贡献指南](CONTRIBUTING.md) 和 [安全政策](SECURITY.md)。

每个完成的里程碑应在 `main` 上形成聚焦提交。模型文件和个人数据须保存在 Git 仓库之外。

仓库目前没有许可证文件。公开可见不代表授予代码复用许可；只有仓库所有者选定许可证后，才会添加相应授权。
