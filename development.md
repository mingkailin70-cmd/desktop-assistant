# 小K开发与仓库维护

更新：2026-09-29。GitHub远端为 mingkailin70-cmd/desktop-assistant，分支 main；本地工作目录由开发者自定。用户要求每完成一个阶段形成提交并推送；不强推、不重写已发布历史。仓库设置清单在 .github/repository-settings.md。

## 固定约束

- .NET SDK 固定为 10.0.401，global.json 禁止自动漂移；本机 SDK 放在已忽略的 .tools 目录。
- 本机工程可使用空 NuGet 源恢复，因为当前未引入 NuGet 包。第三方依赖、运行时/模型权重下载和新的联网命令须逐项获得用户确认。
- 不将模型、缓存、数据库、日志、录音、截图、通知正文、凭证和本机设置提交到 Git。
- Windows桌面交互由当前用户进程负责；模型只产出结构化提案，ToolBroker决定权限与执行。
- 每个阶段结束先看差异/编译结果和数据忽略规则，再创建阶段提交并推送到指定远端。

## 工程模块

- XiaoK.Host：桌宠、托盘、快捷键、文字输入、任务状态、取消和审批预览。
- XiaoK.Core：任务、工具、消息通知与审批契约。
- XiaoK.Tools：固定工具注册与调用协调。
- XiaoK.Adapters.Windows：应用启动、文件检索、窗口核验及消息适配器。
- XiaoK.Inference：本机回环模型客户端和排他资源租约。
- XiaoK.Voice：唤醒、VAD、ASR、TTS边界。
- XiaoK.Storage：任务、偏好和脱敏记录；开发骨架暂用JSON，首版前换SQLite。

个人运行数据放在 D:\XiaoK\Data，模型和缓存放 D 盘其他仓库外目录。Windows用户设置位于 %LOCALAPPDATA%\XiaoK\settings.json。

## 本地命令

本仓库包含 NuGet.Config 清空外部包源，用于当前无第三方包的离线还原。

    dotnet --version
    dotnet restore XiaoK.sln
    dotnet build XiaoK.sln --configuration Release
    dotnet run --project src/XiaoK.Host/XiaoK.Host.csproj

本机首次编译曾使用本地空包源完成。新加依赖前先征求逐项批准、写清许可/版本/用途，再更新 NuGet.Config 与工作流。

## GitHub维护

GitHub Actions 在 main push、PR 和手动触发时构建 Windows 解决方案。Dependabot 当前仅检查 GitHub Actions 更新；没有自动引入NuGet包。Issue/PR模板要求不给出真实消息或凭证。仓库没有选定许可证；公开可见不代表授予复用权限。

GitHub连接器报告对仓库无推送/管理权限；本机Git HTTPS fetch现已成功，阶段提交仍需用本机现有Git凭据推送。本地提交身份已配置。遇到上传失败时保留本地提交并报告，不以强推或重写远端历史规避问题。远端管理设置需由有权限的仓库所有者在GitHub页面应用。

## 当前阶段

P0通知可见性、模型/语音资源、中文任务集尚未通过；P1桌面壳与模块骨架已编译但未完成长期运行和资源验收。P2–P4仍须实现和实测。首版安装、通知许可、重启恢复、长时间运行和回滚必须在P4通过。
