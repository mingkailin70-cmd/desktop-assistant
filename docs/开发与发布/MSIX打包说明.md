# MSIX 打包与权限验收

[`src/XiaoK.Host/Package.appxmanifest`](../../src/XiaoK.Host/Package.appxmanifest) 声明 Windows 通知监听能力和当前用户登录启动任务。设置页已使用 Windows `StartupTask` API 读取和请求任务状态；开发版仍使用当前用户注册表项。它只证明应用层的注册接口已接线：WPF 项目尚未构建为签名 MSIX，未配置签名证书，也未请求或取得 Windows 通知访问权限。尚未实测 Windows 登录任务实际激活行为及启动时是否进入托盘。只有安装已签名的软件包并由用户明确授权后，才能注册通知监听器。

## Host 发布模式

Host 使用仓库内的 `Properties/PublishProfiles/Windows-x64-self-contained.pubxml` 作为首版 Windows x64 发布配置：自包含发布并保留 WPF/WinRT 所需代码，不启用单文件打包或裁剪，避免目标电脑缺少 .NET 10 系统运行时而无法启动。正式发布前应先在锁定模式下还原 win-x64 运行时包，再用该 profile 发布到仓库忽略的 `artifacts/publish/win-x64`；运行时包还原可能访问 NuGet，须取得用户确认并更新锁文件后才能执行。

该 profile 只定义 Host 的发布方式，不生成 MSIX，也不代表运行时已发布或安装。MSIX 构建仍需补齐有效的包发布者身份、匹配的签名证书、打包工具链和资产文件；目前清单中的 `TODO-LOCAL-SIGNING-CERTIFICATE` 是占位符，禁止用于安装或通知权限验收。

预期的发布步骤（依赖包获准并锁定后执行）：

~~~powershell
.\.tools\dotnet\dotnet.exe restore src\XiaoK.Host\XiaoK.Host.csproj --locked-mode --runtime win-x64
.\.tools\dotnet\dotnet.exe publish src\XiaoK.Host\XiaoK.Host.csproj --configuration Release --no-restore --runtime win-x64 --self-contained true --output artifacts\publish\win-x64 -p:PublishProfile=Windows-x64-self-contained
~~~

在 P4 发布前必须确定软件包发布者、图标、处理器架构、应用身份、登录启动注册、升级和回滚行为。不得安装此草稿清单，也不得使用未签名身份绕过 Windows 授权。
