# MSIX 打包与权限验收

[`src/XiaoK.Host/Package.appxmanifest`](../../src/XiaoK.Host/Package.appxmanifest) 声明 Windows 通知监听能力和当前用户登录启动任务。设置页已使用 Windows `StartupTask` API 读取和请求任务状态；开发版仍使用当前用户注册表项。只有安装签名软件包并由用户明确授权后，才能注册通知监听器。尚未实测登录任务实际激活行为及启动时是否进入托盘。

## Host 发布模式

Host 使用仓库内的 `Properties/PublishProfiles/Windows-x64-self-contained.pubxml` 作为首版 Windows x64 发布配置：自包含发布并保留 WPF/WinRT 所需代码，不启用单文件打包或裁剪，避免目标电脑缺少 .NET 10 系统运行时而无法启动。`tools/publish_xiaok.ps1` 在固定 SDK、锁定依赖下还原并发布到仓库忽略的 `artifacts/publish/win-x64`。

该 profile 只定义 Host 的发布方式，不生成 MSIX，也不代表运行时已发布或安装。`Package.appxmanifest` 的登录启动扩展使用 `uap5:Extension` 与 `uap5:StartupTask`；微软的 [uap5 扩展规范](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-uap5-extension)和 [StartupTask 规范](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-uap5-startuptask)要求使用 UAP 5 命名空间。清单同时声明桌面全信任入口需要的 `runFullTrust` 能力。

清单发布者固定为 `CN=XiaoK Local Development`，只代表本机开发身份，不是公开发行身份。`tools/new_xiaok_dev_certificate.ps1` 在当前用户个人证书存储中创建三年期 RSA 3072 代码签名证书，私钥不可导出；只导出公钥 `.cer` 和证书指纹元数据到 Git 忽略的 `artifacts/signing`。脚本不会把证书导入受信任证书存储。首次运行会在本机创建私钥，后续签名包更新必须保留该证书；若丢失则需要重新信任新证书，并先移除旧包再安装新身份。

可用以下脚本复现自包含发布和包结构验证：

~~~powershell
# 自包含发布成功后，使用默认的 artifacts\publish\win-x64
.\tools\package_msix_validation.ps1

# 创建或复用当前用户开发证书
.\tools\new_xiaok_dev_certificate.ps1

# 用本地开发证书签名验证包
$cert = Get-Content artifacts\signing\xiaok-development-certificate.json -Raw | ConvertFrom-Json
.\tools\package_msix_validation.ps1 -CertificateThumbprint $cert.thumbprint
~~~

打包脚本只允许从 Host Release 输出或仓库内 `artifacts\publish` 读取文件；它会检查必要入口文件、拒绝重解析点，并将 manifest、图标和发布输出复制到独立的 `artifacts\msix-validation\<随机ID>` 目录。传入证书指纹后，脚本检查证书主题与 manifest 发布者一致、证书有效、具有代码签名 EKU/数字签名用途及私钥，然后调用 Windows SDK SignTool 以 SHA-256 签名。打包脚本本身不会导入证书信任、安装或启动软件包。初次信任导入前，Authenticode 链验证按预期报告根证书不受信任；取得用户授权并导入公钥后，SignTool 验证已通过，签名状态为 `Valid`。

安装前只读核对签名 MSIX、证书指纹和 SHA-256：

~~~powershell
.\tools\install_xiaok_msix.ps1 -PackagePath 'artifacts\msix-validation\<生成目录>\XiaoK-signed-validation.msix'
~~~

需进入安装时，必须在管理员 PowerShell 中显式加上 `-Install`。脚本会先核对包内身份、签名者和本地公钥证书，然后把仅含公钥的证书导入 `Cert:\LocalMachine\TrustedPeople`，验签后只为当前执行账户运行 `Add-AppxPackage`，不会自动启动小K。Windows App Installer 对自签名 MSIX 要求机器级 `TrustedPeople` 信任，因此这项授权会影响该电脑所有用户，并需管理员权限；本证书留在 `TrustedPeople` 后，Windows 会认可任何由它签名的 MSIX。不要把此证书放入 `Trusted Root Certification Authorities`。

当前状态（2026-10-03）：在用户授权后已完成机器级证书信任导入和当前账户安装。SignTool 与 Authenticode 状态均通过，包标识为 `MingKaiLin.XiaoK_0.1.0.0_neutral__g0ndt6g65c8pe`。随后仅用 `--diagnostics-profile` 隔离配置启动一次并通过小K界面退出；确认通知监听与模型均关闭、未请求 Windows 通知/麦克风授权、未读取常规设置，诊断临时目录位于 `%TEMP%\XiaoK-Diagnostics-*`。常规模式未启动；通知/麦克风授权、完整 UI、登录启动、重启恢复和真实功能仍未验收。

若需回滚，先在目标账户移除小K包；只有全机没有仍依赖该发布者的软件包时，才移除机器信任项。使用支持 `-WhatIf` 和逐步确认的卸载脚本：

~~~powershell
# 先预览会卸载的当前账户应用，不做变更
.\tools\uninstall_xiaok_msix.ps1 -WhatIf

# 确认卸载当前账户应用；脚本会提示确认
.\tools\uninstall_xiaok_msix.ps1 -Confirm

# 所有用户下均无依赖包后，在管理员 PowerShell 中单独移除机器信任
.\tools\uninstall_xiaok_msix.ps1 -RemoveTrustedCertificate -Confirm
~~~

卸载程序不会删除 `D:\XiaoK\Data` 用户数据库或模型；它们仍保留在包外，需按数据保留策略单独处理。

清单引用的三张图标已补入 `src/XiaoK.Host/Assets/`，以桌宠界面现用的紫色 K 形象制作。静态检查确认清单是格式正确的 XML，`StoreLogo.png` 为 50×50、`Square150x150Logo.png` 为 150×150、`Square44x44Logo.png` 为 44×44，且引用文件均存在；后续 MakeAppx 包语义校验也已通过。

可复现的自包含发布步骤：

~~~powershell
.\.tools\dotnet\dotnet.exe restore src\XiaoK.Host\XiaoK.Host.csproj --locked-mode --runtime win-x64
.\.tools\dotnet\dotnet.exe publish src\XiaoK.Host\XiaoK.Host.csproj --configuration Release --no-restore --runtime win-x64 --self-contained true --output artifacts\publish\win-x64 -p:PublishProfile=Windows-x64-self-contained
~~~

本机开发证书只用于当前用户测试，不可用于公开发行或分发。证书信任导入和安装会改变当前用户的证书信任与应用状态；开始这一步前应展示证书主题、SHA-256 指纹、安装包路径和回滚方式。公开发行前需采用正式签名身份，并确定应用身份、登录启动注册、升级和回滚行为。
