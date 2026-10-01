# MSIX 打包与权限验收

[`src/XiaoK.Host/Package.appxmanifest`](../../src/XiaoK.Host/Package.appxmanifest) 声明 Windows 通知监听能力和当前用户登录启动任务。设置页已使用 Windows `StartupTask` API 读取和请求任务状态；开发版仍使用当前用户注册表项。它只证明应用层的注册接口已接线：WPF 项目尚未构建为签名 MSIX，未配置签名证书，也未请求或取得 Windows 通知访问权限。尚未实测 Windows 登录任务实际激活行为及启动时是否进入托盘。只有安装已签名的软件包并由用户明确授权后，才能注册通知监听器。

## Host 发布模式

Host 使用仓库内的 `Properties/PublishProfiles/Windows-x64-self-contained.pubxml` 作为首版 Windows x64 发布配置：自包含发布并保留 WPF/WinRT 所需代码，不启用单文件打包或裁剪，避免目标电脑缺少 .NET 10 系统运行时而无法启动。正式发布前应先在锁定模式下还原 win-x64 运行时包，再用该 profile 发布到仓库忽略的 `artifacts/publish/win-x64`；运行时包还原可能访问 NuGet，须取得用户确认并更新锁文件后才能执行。

该 profile 只定义 Host 的发布方式，不生成 MSIX，也不代表运行时已发布或安装。`Package.appxmanifest` 的登录启动扩展使用 `uap5:Extension` 与 `uap5:StartupTask`；微软的 [uap5 扩展规范](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-uap5-extension)和 [StartupTask 规范](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-uap5-startuptask)要求使用 UAP 5 命名空间。清单同时声明桌面全信任入口需要的 `runFullTrust` 能力。

本机 Windows SDK `MakeAppx.exe` 已通过清单语义和包内容校验，生成了一个未签名的验证包。该次校验使用现有 framework-dependent Release 输出；验证包只证明当前 manifest、应用入口文件和资产能组成包，不证明自包含运行时已发布，也不证明包能安装或运行。清单中的 `TODO-LOCAL-SIGNING-CERTIFICATE` 仍是占位符；正式安装仍需由用户确定发布者并提供匹配证书，之后单独验签、安装和请求通知授权。

可用以下脚本复现不签名、不安装的包结构验证：

~~~powershell
# 自包含发布成功后，使用默认的 artifacts\publish\win-x64
.\tools\package_msix_validation.ps1

# 仅检查当前 framework-dependent Release 构建时，显式指定输出目录
.\tools\package_msix_validation.ps1 -PublishDirectory 'src\XiaoK.Host\bin\Release\net10.0-windows10.0.26100.0'
~~~

脚本只允许从 Host Release 输出或仓库内 `artifacts\publish` 读取文件；它会检查必要入口文件、拒绝重解析点，并将 manifest、图标和发布输出复制到独立的 `artifacts\msix-validation\<随机ID>` 目录。MakeAppx 成功只代表包结构和 manifest 语义检查通过，不执行签名、安装或启动。

清单引用的三张图标已补入 `src/XiaoK.Host/Assets/`，以桌宠界面现用的紫色 K 形象制作。静态检查确认清单是格式正确的 XML，`StoreLogo.png` 为 50×50、`Square150x150Logo.png` 为 150×150、`Square44x44Logo.png` 为 44×44，且引用文件均存在；后续 MakeAppx 包语义校验也已通过。

预期的发布步骤（依赖包获准并锁定后执行）：

~~~powershell
.\.tools\dotnet\dotnet.exe restore src\XiaoK.Host\XiaoK.Host.csproj --locked-mode --runtime win-x64
.\.tools\dotnet\dotnet.exe publish src\XiaoK.Host\XiaoK.Host.csproj --configuration Release --no-restore --runtime win-x64 --self-contained true --output artifacts\publish\win-x64 -p:PublishProfile=Windows-x64-self-contained
~~~

在 P4 发布前必须确定软件包发布者、图标、处理器架构、应用身份、登录启动注册、升级和回滚行为。不得安装此草稿清单，也不得使用未签名身份绕过 Windows 授权。
