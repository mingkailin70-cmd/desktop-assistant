# Windows x64 自包含发布首测

日期：2026-10-03。此记录证明固定 SDK 可生成含 .NET 运行时的 Windows x64 Host 并通过 MSIX 结构检查；不代表签名安装、应用运行或首版门槛通过。

后续进度（2026-10-03）：清单 Publisher 已固定为 `CN=XiaoK Local Development`。新增本机开发证书脚本后，SignTool 已成功签名 82,564,395 字节自包含 MSIX，MakeAppx 解包检查成功。`Get-AuthenticodeSignature` 和 `SignTool verify /pa` 因证书链尚未受信任而返回根证书不受信任；这是尚未导入公钥证书的预期状态。签名证书未导入受信任存储，包仍未安装或启动，首次安装、授权、登录启动、应用功能、重启恢复和回滚仍待实机验收。

## 可复现命令

在仓库根目录使用 Windows PowerShell 5.1：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\publish_xiaok.ps1
```

脚本固定调用仓库内 .NET SDK 10.0.401。它以 locked mode 还原 Host 的 `win-x64` 目标，按 `Windows-x64-self-contained.pubxml` 进行 Release 发布，核验运行时配置和必要 WPF/.NET 文件，然后调用固定 Windows SDK 的 MakeAppx 打包器。

## 本机结果

- `NuGet.config` 使用精确包 ID 映射，仅允许固定 Windows SDK 引用与本项目所需的 win-x64 .NET Core、Windows Desktop、ASP.NET Core runtime packs 和 host pack 从 nuget.org 还原。
- `src/XiaoK.Host/packages.lock.json` 现包含 `net10.0-windows10.0.26100/win-x64` 锁定目标；本次没有升级第三方包版本。
- 自包含发布目录：`artifacts/publish/win-x64`；脚本确认 `XiaoK.Host.exe`、WPF 框架程序集、`hostfxr.dll`、`hostpolicy.dll`、`coreclr.dll` 和 `System.Private.CoreLib.dll` 存在。
- runtime config 声明 `Microsoft.NETCore.App 10.0.12` 和 `Microsoft.WindowsDesktop.App 10.0.12` 为随包运行时。
- Windows PowerShell 5.1 调用 MakeAppx 成功；验证布局共492个文件，生成未签名包82,544,978字节。

## 未验证范围

原始验证包没有签名、安装或启动；后续签名包尚未导入本机信任或安装。没有验证 Windows 通知/麦克风授权、登录启动、升级/卸载、重启恢复、模型/语音部署与回滚。自包含只解决随包 .NET 运行时，不包含模型权重、Python 语音环境、数据库或用户数据迁移。
