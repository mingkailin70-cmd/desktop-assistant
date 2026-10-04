# MSIX 打包与权限验收

## v0.1.37.0 桌宠 v12 角色与界面更新（2026-10-05）

固定 SDK Release 构建0警告、0错误；自包含签名 MSIX 位于 `artifacts\msix-validation\4fab65da3fee484c953bbd5e7f8e998f\XiaoK-signed-validation.msix`，大小84,637,514字节，SHA-256 `001163FDDC0E82685A332B2EC627318C00940652E1483CB387F2AFEA38688546`。SignTool验签 `Valid`，0警告、0错误；开发证书已在 `LocalMachine\TrustedPeople`，本次没有变更证书信任。安装前打包脚本未能自动匹配运行中的主窗口，未安装、未终止进程；随后按进程路径、PID和窗口标题核对唯一的安装版 Host，并投递应用注册的正常退出消息，确认进程正常退出后安装成功，没有强制结束进程。当前账户包状态 `Ok`、版本 `0.1.37.0`；从应用入口启动后进程响应正常。Open Computer Use目视确认收起桌宠 `300×372 DIP` 和展开面板 `500×650 DIP` 均使用v12银渐层角色；状态胶囊显示简短中文“待命”，完整状态可悬停查看；展开版的任务反馈和底部快捷键提示可见，无垂直滚动条。自动化截图的透明区域显示黑色，因此没有据此判定实际桌面合成已验收。未启动模型、麦克风或通知监听。

## v0.1.34.0 桌宠 v11 与登录启动状态（2026-10-05）

当前账户 `Get-AppxPackage` 返回包状态 `Ok`、版本 `0.1.34.0`；正常启动后的安装版 `XiaoK.Host` 进程响应正常。通过小K设置页读取 Windows `StartupTask` API，状态文案为“Windows 登录启动任务已启用；可以在此关闭”；未更改该设置。包入口同时列于 `Get-StartApps`，AUMID 为 `MingKaiLin.XiaoK_g0ndt6g65c8pe!App`。这证明启动任务当前已启用，不证明下一次登录时实际进入托盘；Windows 重启后的启动、托盘状态和恢复行为仍待实测。

## v0.1.33.0 桌宠 v10 视觉更新（2026-10-04）

自包含签名 MSIX 位于 artifacts/msix-validation/9fae0290c2ab4cbc979f74d643a455ce/XiaoK-signed-validation.msix，大小84,531,000字节，SHA-256 64F7C5A9D2E13D8BFB8F3803D5FA01708874843D2D3B07496A11AE6F64298850。SignTool验签Valid（0警告、0错误）；证书此前已在LocalMachine/TrustedPeople，未修改证书信任。更新后当前账户包状态Ok、版本0.1.33.0；正常应用入口启动后进程响应正常。收起桌宠、头像与欢迎横幅已使用v10透明PNG。CUA未枚举原生应用，未取得窗口截图；没有启动模型、麦克风或通知监听。
## v0.1.32.0 语音运行时更新（2026-10-04）

签名 MSIX 位于 artifacts/msix-validation/fa41ed3a1282442289ffe08246944563/XiaoK-signed-validation.msix，大小84,543,798字节，SHA-256 F75D019FF998D6FD408203F6F91CC89B25D1F42623C7591F4A88F689A8CCD3A5。SignTool验签Valid（0警告、0错误）；开发证书此前已在LocalMachine/TrustedPeople，本次没有修改信任。更新后当前账户包状态为Ok，版本0.1.32.0；安装版Host/ModelBroker的六句CPU语音回环6/6匹配，并观察到ASR两分钟闲置卸载及成功重载。该包仍包含桌宠v9角色素材；后续视觉更新单独升级包版本。没有启用麦克风或通知监听。
# 小K桌宠 v9 更新包（2026-10-04）

桌宠主视觉切换到透明底 3D 银渐层幼猫 `xiaok-silver-shaded-3d-v9-wave.png`，收起态、标题头像和欢迎横幅共用同一素材。WPF 解码确认素材为 1254×1254 BGRA，角点透明且主体不透明。固定 SDK Release 解决方案构建 0 警告、0 错误；自包含 MSIX `0.1.31.0` 位于 `artifacts\msix-validation\81f8959eb03240ed85d85193b458604b\XiaoK-signed-validation.msix`，大小 84,541,805 字节，SHA-256 `FC3F389E48CB915BF30BA0F026CCAEB6FF8B9A929A754EA51722CC70AF4D6D28`。签名者 SHA-1 指纹 `B96A02547ABA84523619E11EB7788AE9850A5C60`；证书此前已在 `LocalMachine\TrustedPeople`，本次未改信任。安装前签名状态 `Valid`，SignTool 验证 0 警告、0 错误；更新器正常请求 0.1.30.0 Host 退出后将新包安装到当前账户，`Get-AppxPackage` 状态为 `Ok`。安装目录 EXE 直接启动成功，窗口标题“小K”；此路径暴露的空激活参数异常已在源码防护并随 0.1.31.0 修复。Open Computer Use 未枚举任何原生应用，因此未捕获安装版窗口截图；运行进程已确认，桌面上的最终像素效果、不同背景的透明边缘仍待人工目视复验。没有启动模型、麦克风或通知监听。

桌宠视觉 v8 更新（2026-10-04）：固定 SDK Release 解决方案构建 0 警告、0 错误；使用本机缓存依赖以 `--no-restore` 发布 win-x64 自包含程序。签名 MSIX `0.1.29.0` 为 `84,605,851` 字节，SHA-256 `CB46F23C6F0CB1CECB36B1E024FA6C09D04606757217CD40049BB15AD606262F`，SignTool 验签 0 警告、0 错误；开发证书原已位于 `LocalMachine\TrustedPeople`，本次未改证书信任。安装器请求旧 Host（PID 48732）正常退出后更新当前账户，安装包版本为 `0.1.29.0`；随后从新版 WindowsApps 目录启动 Host（PID 43224，窗口标题“小K”）。新包已包含 v8 角色资源。CUA 当前没有枚举原生应用窗口，本轮未取得安装版截图；因此启动成功已核实，v8 实际画面与桌面背景透明边缘仍待截图验收。没有启动模型、麦克风或通知监控。

桌宠视觉 v7 更新（2026-10-04）：固定 SDK Release 解决方案构建 0 警告、0 错误；无需联网还原，使用本机缓存运行时发布 win-x64 自包含程序。签名 MSIX `0.1.28.0` 为 `84,398,920` 字节，SHA-256 `DF308A372A49B9D2C20CD2975C0EB088E9C1D12D9982CB6A631F94EA8A576662`，SignTool 验签 0 错误；证书原已位于 `LocalMachine\TrustedPeople`，本次未修改信任。安装器正常请求旧 Host（PID 34052）退出，随后更新当前账户并从新版包目录启动 Host（PID 48732）。更换后的 v7 透明 PNG 已包含在资源中。后续于同日通过 Open Computer Use 捕获到该已安装窗口：收起态 `300×372`，点击展开后 `500×650`，两个视图均显示 v7 形象且展开面板主要控件在窗口范围内。透明区域在截图中显示为黑色，实际桌面合成边缘、背景变化、DPI/多显示器仍待检查；系统级快捷键也未由后台按键模拟证明。未启动模型、麦克风或通知监控。

桌宠视觉 v4 更新（2026-10-04）：固定 SDK Release 构建 0 警告、0 错误；签名 MSIX `0.1.21.0` 位于 `artifacts\msix-validation\7ec633edf9774ebe9fa030079c1027ec\XiaoK-signed-validation.msix`，SHA-256 为 `9130E73D260FE6DD304AE80311078A94099AD568372E7FCD641DA480A20E9248`，SignTool 验签通过且无警告。开发证书已在 `LocalMachine\TrustedPeople`，本次未增加证书信任。安装器请求旧进程正常退出并在等待期间确认进程退出，随后将 0.1.21.0 安装到当前账户；检查 `Get-AppxPackage` 状态为 `Ok`，并从新安装目录启动 Host。新版素材已在自包含发布程序集内确认，未启动推理或更改通知/麦克风权限。桌面实际窗口截图和透明合成仍待核验。

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

需进入安装时，显式加上 `-Install`。脚本先核对包内身份、签名者和本地公钥证书并验证签名，然后检查已安装进程。`0.1.7.0` 起，小K可接收只用于退出的固定注册窗口消息；安装器会核对窗口属于当前账户已安装的 Host 进程，请求应用走正常异步清理流程，并最多等待45秒。窗口或进程身份不匹配、请求失败或超时都会停止安装，不会强制结束进程，也不会修改证书信任。`0.1.7.0` 以前的运行版本不支持该请求，须从托盘菜单正常退出后再重试。关闭应用后，脚本才执行证书信任检查/必要导入和 `Add-AppxPackage`；安装不会自动启动小K。若证书已在 `Cert:\LocalMachine\TrustedPeople`，更新当前用户包不需要管理员权限；若证书尚未受信任，导入机器级信任需要管理员 PowerShell，并会影响该电脑所有用户。脚本不会把证书放入 `Trusted Root Certification Authorities`。

0.1.7.0 更新包（2026-10-04）：自包含签名包位于 `artifacts\msix-validation\122d8a1f0e14464caba2c96baf1255a3\XiaoK-signed-validation.msix`，SHA-256 `1D056C984A959FCD02C0931E1F6848EA971F85DDABF44F7218518B349F435CD0`，Authenticode 状态 `Valid`。旧版0.1.5.0仍在运行时，安装脚本按设计拒绝安装；包当前只完成生成与验签，没有安装或启动。

诊断启动记录（2026-10-03）：0.1.0.0 版本曾以 `--diagnostics-profile` 隔离配置启动并从小K界面退出；确认通知监听与模型均关闭、未请求 Windows 通知/麦克风授权、未读取常规设置。该结果不代表常规模式已启动。

安装更新（2026-10-03）：当前账户已从 `MingKaiLin.XiaoK_0.1.0.0_neutral__g0ndt6g65c8pe` 更新为 `MingKaiLin.XiaoK_0.1.1.0_neutral__g0ndt6g65c8pe`，包状态 `Ok`。SignTool 验签与 Authenticode 状态均为 `Valid`；包 SHA-256 为 `774954F84311E77812F20E56E351895163646827D57868A7548BBA2A7F8071D5`。安装脚本确认开发证书此前已在机器 `TrustedPeople` 中，本次没有新增信任项。后续首次正常启动已显示桌宠与任务面板，没有出现 .NET 错误或权限提示；程序空闲且未采集麦克风，包沙盒内未找到设置文件。唤醒词、通知监听和登录启动仍关闭；通知/麦克风授权、任务功能、多屏/DPI、登录恢复、长期运行、回滚仍待验收。

MSIX 设置与首个工具链实测（2026-10-03）：打包应用的 `Environment.SpecialFolder.LocalApplicationData` 被 Windows 重定向到包专属 `LocalCache\Local`，设置实际保存在 `%LOCALAPPDATA%\Packages\MingKaiLin.XiaoK_g0ndt6g65c8pe\LocalCache\Local\XiaoK\settings.json`；`LocalCache` 在本机是指向 `D:\WpSystem` 的目录联接。普通非打包开发运行仍使用 `%LOCALAPPDATA%\XiaoK\settings.json`，两者不会自动同步。为已安装版本配置了 VS Code `D:\VS Code\Code.exe`、项目 `D:\Desktop\learn\siri`、隔离工作区 `D:\XiaoK\Workspaces` 和本机应用白名单；唤醒词、微信/QQ通知监控均关闭，发布者 AUMID 列表为空。首次启动工具按钮因配置写在非打包目录而按白名单策略安全拒绝，移入包内设置位置并重启后，点击“打开 VS Code 项目”显示目标目录。窗口枚举仍见一个本地 `siri` 窗口和一个 `[SSH: Three]` 窗口；本地窗口句柄前后相同，所以这次只证明项目目标链路返回成功，未证明新建了额外窗口。随后在小K输入“查找文件 XiaoK.sln”，本地搜索返回 `D:\Desktop\learn\siri\XiaoK.sln`，达到5,000目录项上限后明确提示结果可能不完整；没有读取文件内容。没有查看聊天或发送消息。

VS Code 新窗口核验修正（2026-10-03）：复核后发现旧启动验收只能证明配置目标被接受，不能把启动前已存在的 VS Code 窗口当作本次启动结果。源码现先记录匹配项目的可见窗口句柄，只有检测到新增句柄才报告成功；超时返回 `APP_LAUNCH_OUTCOME_UNCERTAIN`，不自动重试。安装并启动 `0.1.2.0` 后再次点击“打开 VS Code 项目”，既有本地窗口 `68050` 和 SSH 窗口 `1575294` 均保留，但未出现新窗口；小K如实显示结果不确定。此次验证确认旧窗口不会再造成成功误报，独立窗口启动本身仍未通过。包 SHA-256：`3B40B279877ADDA91E038F03F750B15C3BB8FACF37B5613970406D60BAD3E5AB`；签名有效，开发证书此前已在 `LocalMachine\TrustedPeople`，本次未改动证书信任。未查看微信/QQ聊天或发送消息。

直接进程启动复测（2026-10-03）：将白名单 EXE 的 `UseShellExecute` 设为 `false` 后重新发布、签名并安装 `0.1.3.0`；签名有效，包 SHA-256 为 `FD2A4E1CF72F93B499762B4DD5F9A533AF778884D247EFB32A72F221529F17C5`，本次没有改动证书信任。小K仍显示 `APP_LAUNCH_OUTCOME_UNCERTAIN`；复测前后只枚举到原本地 VS Code 窗口 `68050` 与 `[SSH: Three]` 窗口 `1575294`，没有创建新的匹配窗口。因此独立项目窗口启动仍失败，不把安全检查通过当成功验收，也不自动重试这次结果不确定的操作。

已有项目窗口验收（2026-10-03）：`app.launch.v1` 检测到唯一的本地 VS Code 项目窗口时，改为激活并核验该窗口在前台，不重复启动；多个目标窗口时失败关闭。`0.1.4.0` 安装版实测将 `68050` 本地 `siri` 项目窗口切到前台，Windows 窗口列表仍保留单独的 `[SSH: Three]` 窗口 `1575294`；资源管理器和辅助功能树均显示项目根目录 `D:\Desktop\learn\siri`。本路径验收通过；未匹配窗口时的 `--new-window` 创建路径仍待本机验证。包 SHA-256：`501ADE8FABA512565EAB3A0C2AF5519D68E94D2062ED208DE0B1536911741622`；签名有效，未改动证书信任。

托管模型目录设置与 0.1.5.0 更新（2026-10-03）：设置校验允许开发阶段模型目录位于 Git 仓库忽略的 `models/` 子树，同时仍拒绝仓库其他目录、UNC 和磁盘根目录；固定 SDK Release 构建 0 警告/0 错误，安全检查 84 项通过、0 跳过。`0.1.5.0` 自包含 MSIX SHA-256：`42B17097C8434ADF02D7402C7E118883297B2A607521DE76B138429FED586800`，签名有效，更新后包状态 `Ok`；本次没有添加或修改证书信任。安装版设置将模型目录指向仓库锁定的 Qwen3.5-4B revision，保留原有其他配置，监控和唤醒词仍关闭，并在更新前保存设置备份。重启后的 Host 已加载托管模型配置并显示首次推理按需启动；本次未在安装版发起推理，故实际权重加载、显存和卸载结果未验收。

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
