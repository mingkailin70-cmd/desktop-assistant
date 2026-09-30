# MSIX 打包与权限验收

[`src/XiaoK.Host/Package.appxmanifest`](../../src/XiaoK.Host/Package.appxmanifest) 声明 Windows 通知监听能力和当前用户登录启动任务。设置页已使用 Windows `StartupTask` API 读取和请求任务状态；开发版仍使用当前用户注册表项。它只证明应用层的注册接口已接线：WPF 项目尚未构建为签名 MSIX，未配置签名证书，也未请求或取得 Windows 通知访问权限。尚未实测 Windows 登录任务实际激活行为及启动时是否进入托盘。只有安装已签名的软件包并由用户明确授权后，才能注册通知监听器。

在 P4 发布前必须确定软件包发布者、图标、处理器架构、应用身份、登录启动注册、升级和回滚行为。不得安装此草稿清单，也不得使用未签名身份绕过 Windows 授权。
