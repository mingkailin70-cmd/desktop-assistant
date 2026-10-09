# Windows 通知接入与隐私边界

更新：2026-10-09。本文记录小K通过 Windows `UserNotificationListener` 接收系统通知的实现边界，以及进入真实微信/QQ验收前必须保持的安全条件。当前账户已安装签名MSIX `0.1.83.0`。0.1.81.0设置页曾运行一次性来源诊断：当时Windows通知访问权限为允许，最多512条Toast快照中找到4条QQ显示名/AUMID候选、未找到微信候选；诊断只读应用显示名和AUMID，未读通知视觉树或正文。该候选未与用户可识别的真实来源对应，通知开关仍关闭，不能据此启用正文分析。

## 当前实现

- `XiaoK.Host` 使用 `UserNotificationListener` 读取 Windows 通知中心中新增的 Toast 通知；权限只能由用户通过设置页按钮请求。
- 通知读取在 MSIX 身份存在且用户授予系统授权后才会启动。普通目录运行、授权拒绝/撤销或进程退出时均不读取通知。
- 设置中每款应用默认关闭，并要求分别配置通知来源 AUMID allowlist。只读取系统提供的 `AppUserModelId` 并先与 allowlist 比较；未知来源不会读取正文、持久化或送入模型。设置校验支持带 `!` 的包应用 AUMID 和不带 `!` 的经典桌面应用标识，但必须从实际 Windows 通知元数据核实后填写；开始菜单显示的启动 ID 只是候选，不能替代通知来源核对。微软文档说明桌面应用可使用应用定义的 AppUserModelID，常见格式为 `Company.Product...`，无需包含包应用的分隔符 `!`（[AppUserModelIDs](https://learn.microsoft.com/en-us/windows/win32/shell/appids)、[包身份概览](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/package-identity-overview)）。
- 0.1.60.0设置页新增用户主动触发的一次性来源诊断，要求Windows通知访问权限已经授予；不会自动请求权限。它按创建时间从当前Toast快照中选出最多512条近期通知，再读取其`AppInfo.DisplayInfo.DisplayName`和`AppInfo.AppUserModelId`，按微信/WeChat/Weixin/QQ显示名过滤并在设置窗口内临时展示候选；不订阅新增事件、不访问`Notification.Visual`、不读取通知正文、不改白名单、不保存候选、不启动持续监听。应用显示名只用于帮助人工寻找样本，不能证明发布者身份；候选须与用户明确识别的真实通知交叉验证，且不应仅凭该诊断自动启用监听。
- 启动时把当时已存在的匹配通知记为基线，不将旧通知误当作新消息。新增事件按发布者和系统通知 ID 去重，并受内存容量限制。
- 新增事件先进入最多 256 个 ID 的有界队列，排队项与正在处理项统一去重，由单个消费者串行读取 Windows 通知列表；队列只保存系统 ID，不保存通知正文。持续事件超过容量时不再接收新 ID，并提示用户手动查看客户端，避免并发请求无界增长。
- 处理前检查输入桌面是否为 `Default`；在异步获取通知列表返回后重新检查通知授权，并在惰性正文读取的最后一步再次检查授权和解锁状态。读取期间发现权限撤销时立即停止监听；发现锁屏时只提示、不读取正文。通知 AUMID 同时出现在微信和 QQ 配置中时拒绝启动监听，避免应用归属歧义。异步获取结果若来自已停止或已替换的监听实例，则丢弃。
- 领域策略采用惰性正文读取器。来源不匹配、权限无效、会话锁定、会话类型未知或重复通知时，不会调用正文读取器。
- 只有会话类型确认是私聊且至少有一个有效会话 ID 或发送者标识时，策略才继续处理；两者都缺失或元数据超长时，只提示人工查看。
- 超过 24 小时的旧通知跳过自动分析；会话限速在正文读取前执行，因此被限速的通知不会先读正文再丢弃。
- 即使来源身份已匹配，只要私聊/群聊归属未知，当前实现只显示通用提示，不自动切换会话、不读正文、不运行模型。
- 源码核对确认当前还没有接入微信或 QQ 的会话解析器：`WindowsNotificationMonitor.ProcessAddedAsync` 选择来源适配器后，将 `conversationId`、`sender` 和 `isPrivateConversation` 都以 `null` 传入。因而所有持续监听事件都会按“无法确定私聊”处理，策略不会调用惰性正文读取器；仅 AUMID 命中并不意味着小K已经读取或分析通知正文。这是源码路径结论，不能替代真实客户端样本验收。
- 已确认是私聊但通知正文为空时，只提示用户手动打开客户端；不切换窗口。正文超过 20,000 字符时不交给模型，通知对象不保留正文。
- 只有策略明确确认私聊并取得可见正文时，正文才通过内存事件交给 Host；Host 复核当前开关、AUMID、会话归属和时效后，进入最多 32 项的低优先级本地分析队列。交互任务优先取得 ModelBroker 租约。停用监听、用户取消或退出会取消相应队列任务。结果只在小K界面和不含正文的托盘提示中短时呈现，不写入任务数据库或日志，也不会自动发送回复。
- 通知正文没有写入任务数据库、日志、崩溃报告或遥测。当前消息通知策略和去重状态只在进程内存中保存。

## Windows API 与软件包状态

项目将目标框架固定为 `net10.0-windows10.0.26100.0`，通过 `WindowsSdkPackageVersion` 使用 `Microsoft.Windows.SDK.NET.Ref` 10.0.26100.87。`NuGet.Config` 仅映射该获准包；这不是客户端发送接口，也不自带微信或 QQ 会话标识。

### R0：标准监听接口能提供哪些会话信息

微软 `UserNotificationListener` 文档列出的单条`UserNotification`字段是应用信息、创建时间、通知ID和`Notification`内容；文档示例从`Notification.Visual`的`ToastGeneric`绑定读取文本元素，并将首项当标题、后续项当正文。`Notification`本身公开`Visual`，而Windows Toast定义中的`launch`参数是点击通知时交给原应用用于跳转的激活上下文。参见[Notification listener](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/notification-listener)、[UserNotification](https://learn.microsoft.com/en-us/uwp/api/windows.ui.notifications.usernotification?view=winrt-28000)、[Notification.Visual](https://learn.microsoft.com/en-us/uwp/api/windows.ui.notifications.notification.visual?view=winrt-28000)和[通知内容结构](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-content)。

**基于这些公开成员清单的判断：Windows标准监听对象没有结构化`conversationId`、联系人稳定ID或`isPrivateConversation`字段，也没有文档化的`launch`读取属性。** 因此必须由微信/QQ各自的可见通知格式提供足够证据，才能尝试构造会话键并区分私聊/群聊；标题/正文的通用位置只能说明显示文本，不能自行证明私聊。此结论是从微软文档描述的API面作出的推断，不代表已经检查了本机真实客户端Toast XML，也不证明某一客户端一定不在其可见文本中放入可识别标记。

当前`WeChatNoticeAdapter`和`QQNoticeAdapter`仍需上游提供真实样本解析结果；`WindowsNotificationMonitor.ProcessAddedAsync`显式传入空`conversationId`、`sender`和私聊标记，所以不会读取正文。应先以真实微信/QQ后台通知验证是否存在稳定、可区分的可见格式；若没有，则R0消息通知闸门判定不可行，需另行评估官方接口或可验证的非前台UIA路径，不能弱化当前私聊判定或读取客户端数据库、注入客户端。

[`src/XiaoK.Host/Package.appxmanifest`](../../src/XiaoK.Host/Package.appxmanifest) 声明 `userNotificationListener` 能力，开发发布者固定为 `CN=XiaoK Local Development`。当前账户签名MSIX为`0.1.83.0`，状态`Ok`。最后一次已记录的系统授权检查发生于0.1.81.0只读来源诊断，当时通知访问权限为允许；诊断发现4条QQ候选、未发现微信候选，只显示应用名称和AUMID。该次没有读取正文、启用持续监听或改白名单。当前微信/QQ通知开关保持关闭；通知访问授权状态未在0.1.83.0重新检查。真实来源身份、可见正文格式和私聊归属均未核实；编译、签名、安装和窗口可见都不能代替系统授权或真实通知验证。安装和回滚细节见[MSIX打包说明](../开发与发布/MSIX打包说明.md)。

## 自动分析启用条件

小K只有同时具备以下证据时，才能把正文送到本地模型：

1. Windows 通知权限状态为已授予，且用户明确开启对应客户端监控。
2. 通知的 Windows 来源 AUMID 与该客户端已核实的 allowlist 完全匹配。
3. 有经真实通知样本验证的客户端专用解析器，能够可靠地区分私聊与群聊；未知格式必须返回“无法判定”。
4. 输入桌面处于解锁状态，通知是新事件且未重复。
5. 通知实际包含可见正文，长度处于上限以内。

任何条件不满足时，失败关闭：不读取正文、不送模型，并向用户说明需要手动查看或检查授权。通知文本是单条消息，不代表完整会话上下文；模型只能分析明确内容、可能意图和建议，不得补造聊天背景。

## 本机验收记录

无第三方测试依赖的安全检查程序覆盖：非允许来源、未知会话、缺少会话归属、过期通知、限速前置、锁屏、权限撤销、正文读取前的授权/解锁复核、微信/QQ来源归属冲突、已确认私聊、重复通知、无正文和超长正文，以及通知来源候选过滤、无关显示名拒绝、512项扫描和20项候选上限、设置UI只读显示与不读正文的源码边界。最新记录（2026-10-09，0.1.83.0）为固定SDK Release构建0警告、0错误；完整Windows安全套件119项通过、0项跳过，包含AppContainer OS边界检查。检查使用合成AUMID和内存字符串，不读取系统通知正文、不访问微信/QQ账号；这些模拟用例不是客户端实测证据。

以下仍待本机真实验收，不能由模拟策略测试代替：

- 固定 MSIX 发布者身份、安装包签名、系统通知授权和授权撤销恢复。
- 在当前 Windows 与客户端版本上核实微信、QQ 通知来源 AUMID、通知文字结构及真实私聊/群聊判别依据。
- 每款客户端至少 30 条后台正文可见私聊通知，核对来源、私聊归属、去重、漏报/误报和自动分析比例；账号消息测试须由用户逐项授权。
- 无正文、群聊、客户端退出/重启、锁屏、睡眠恢复和权限撤销等失败路径。

在以上条件通过之前，设置页 AUMID 字段保持用户手工核实输入，任何未知会话都不会自动分析。
