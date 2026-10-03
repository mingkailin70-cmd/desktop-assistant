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

需进入安装时，显式加上 `-Install`。脚本会先核对包内身份、签名者和本地公钥证书，再运行 SignTool 验签，最后只为当前执行账户运行 `Add-AppxPackage`，不会自动启动小K。若证书已在 `Cert:\LocalMachine\TrustedPeople`，更新当前账户包不需要管理员权限；若证书尚未受信任，导入机器级信任需要管理员 PowerShell，并会影响该电脑所有用户。脚本不会把证书放入 `Trusted Root Certification Authorities`。

诊断启动记录（2026-10-03）：0.1.0.0 版本曾以 `--diagnostics-profile` 隔离配置启动并从小K界面退出；确认通知监听与模型均关闭、未请求 Windows 通知/麦克风授权、未读取常规设置。该结果不代表常规模式已启动。

安装更新（2026-10-03）：当前账户已从 `MingKaiLin.XiaoK_0.1.0.0_neutral__g0ndt6g65c8pe` 更新为 `MingKaiLin.XiaoK_0.1.1.0_neutral__g0ndt6g65c8pe`，包状态 `Ok`。SignTool 验签与 Authenticode 状态均为 `Valid`；包 SHA-256 为 `774954F84311E77812F20E56E351895163646827D57868A7548BBA2A7F8071D5`。安装脚本确认开发证书此前已在机器 `TrustedPeople` 中，本次没有新增信任项。后续首次正常启动已显示桌宠与任务面板，没有出现 .NET 错误或权限提示；程序空闲且未采集麦克风，包沙盒内未找到设置文件。唤醒词、通知监听和登录启动仍关闭；通知/麦克风授权、任务功能、多屏/DPI、登录恢复、长期运行、回滚仍待验收。

MSIX 设置与首个工具链实测（2026-10-03）：打包应用的 `Environment.SpecialFolder.LocalApplicationData` 被 Windows 重定向到包专属 `LocalCache\Local`，设置实际保存在 `%LOCALAPPDATA%\Packages\MingKaiLin.XiaoK_g0ndt6g65c8pe\LocalCache\Local\XiaoK\settings.json`；`LocalCache` 在本机是指向 `D:\WpSystem` 的目录联接。普通非打包开发运行仍使用 `%LOCALAPPDATA%\XiaoK\settings.json`，两者不会自动同步。为已安装版本配置了 VS Code `D:\VS Code\Code.exe`、项目 `D:\Desktop\learn\siri`、隔离工作区 `D:\XiaoK\Workspaces` 和本机应用白名单；唤醒词、微信/QQ通知监控均关闭，发布者 AUMID 列表为空。首次启动工具按钮因配置写在非打包目录而按白名单策略安全拒绝，移入包内设置位置并重启后，点击“打开 VS Code 项目”显示目标目录。窗口枚举仍见一个本地 `siri` 窗口和一个 `[SSH: Three]` 窗口；本地窗口句柄前后相同，所以这次只证明项目目标链路返回成功，未证明新建了额外窗口。随后在小K输入“查找文件 XiaoK.sln”，本地搜索返回 `D:\Desktop\learn\siri\XiaoK.sln`，达到5,000目录项上限后明确提示结果可能不完整；没有读取文件内容。没有查看聊天或发送消息。

VS Code 新窗口核验修正（2026-10-03）：复核后发现旧启动验收只能证明配置目标被接受，不能把启动前已存在的 VS Code 窗口当作本次启动结果。源码现先记录匹配项目的可见窗口句柄，只有检测到新增句柄才报告成功；超时返回 `APP_LAUNCH_OUTCOME_UNCERTAIN`，不自动重试。安装并启动 `0.1.2.0` 后再次点击“打开 VS Code 项目”，既有本地窗口 `68050` 和 SSH 窗口 `1575294` 均保留，但未出现新窗口；小K如实显示结果不确定。此次验证确认旧窗口不会再造成成功误报，独立窗口启动本身仍未通过。包 SHA-256：`3B40B279877ADDA91E038F03F750B15C3BB8FACF37B5613970406D60BAD3E5AB`；签名有效，开发证书此前已在 `LocalMachine\TrustedPeople`，本次未改动证书信任。未查看微信/QQ聊天或发送消息。

直接进程启动复测（2026-10-03）：将白名单 EXE 的 `UseShellExecute` 设为 `false` 后重新发布、签名并安装 `0.1.3.0`；签名有效，包 SHA-256 为 `FD2A4E1CF72F93B499762B4DD5F9A533AF778884D247EFB32A72F221529F17C5`，本次没有改动证书信任。小K仍显示 `APP_LAUNCH_OUTCOME_UNCERTAIN`；复测前后只枚举到原本地 VS Code 窗口 `68050` 与 `[SSH: Three]` 窗口 `1575294`，没有创建新的匹配窗口。因此独立项目窗口启动仍失败，不把安全检查通过当成功验收，也不自动重试这次结果不确定的操作。

已有项目窗口验收（2026-10-03）：`app.launch.v1` 检测到唯一的本地 VS Code 项目窗口时，改为激活并核验该窗口在前台，不重复启动；多个目标窗口时失败关闭。`0.1.4.0` 安装版实测将 `68050` 本地 `siri` 项目窗口切到前台，Windows 窗口列表仍保留单独的 `[SSH: Three]` 窗口 `1575294`；资源管理器和辅助功能树均显示项目根目录 `D:\Desktop\learn\siri`。本路径验收通过；未匹配窗口时的 `--new-window` 创建路径仍待本机验证。包 SHA-256：`501ADE8FABA512565EAB3A0C2AF5519D68E94D2062ED208DE0B1536911741622`；签名有效，未改动证书信任。

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
