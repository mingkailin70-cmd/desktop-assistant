# Windows 通知接入与隐私边界

更新：2026-10-09。本文记录小K通过 Windows `UserNotificationListener` 接收系统通知的实现边界，以及进入真实微信/QQ验收前必须保持的安全条件。当前账户已安装签名MSIX `0.1.92.0`，状态`Ok`；包SHA-256为`86E9AB22CEAC74653D45433552D69460AE274926BBE7B5ECD095AF3557FB3B91`。新增无窗口命令行来源诊断已在安装包身份下实测：Windows通知访问状态为Allowed，最多512条快照中检查37条，仅显示4条`QQ`/AUMID `QQ`候选，未发现微信候选；候选与本机开始菜单QQ注册项`AppID=QQ`和运行路径`D:\QQ\QQ.exe`相符。诊断只访问显示名、AUMID、创建时间，不读`Notification.Visual`或正文，不请求授权、不监听、不保存结果，宿主随后退出。安装设置仍是两端监控关闭、白名单为空；Host进程为0。0.1.90.0正文读取器二次门控仍在：会话解析器未接入时，监视器不会把真实读取器交给适配器，因此不会读取正文。没有开始持续监听或发送消息；真实会话分类、稳定联系人ID和私聊格式仍待验收。

## 当前实现

- `XiaoK.Host` 使用 `UserNotificationListener` 读取 Windows 通知中心中新增的 Toast 通知；权限只能由用户通过设置页按钮请求。
- 通知读取在 MSIX 身份存在且用户授予系统授权后才会启动。普通目录运行、授权拒绝/撤销或进程退出时均不读取通知。
- 设置中每款应用默认关闭，并要求分别配置通知来源 AUMID allowlist。只读取系统提供的 `AppUserModelId` 并先与 allowlist 比较；未知来源不会读取正文、持久化或送入模型。设置校验支持带 `!` 的包应用 AUMID 和不带 `!` 的经典桌面应用标识，但必须从实际 Windows 通知元数据核实后填写；开始菜单显示的启动 ID 只是候选，不能替代通知来源核对。微软文档说明桌面应用可使用应用定义的 AppUserModelID，常见格式为 `Company.Product...`，无需包含包应用的分隔符 `!`（[AppUserModelIDs](https://learn.microsoft.com/en-us/windows/win32/shell/appids)、[包身份概览](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/package-identity-overview)）。
- 0.1.60.0设置页新增用户主动触发的一次性来源诊断，要求Windows通知访问权限已经授予；不会自动请求权限。它按创建时间从当前Toast快照中选出最多512条近期通知，再读取其`AppInfo.DisplayInfo.DisplayName`、`AppInfo.AppUserModelId`和`CreationTime`，按微信/WeChat/Weixin/QQ显示名过滤并在设置窗口内临时展示候选、数量和最近出现时间（本地时区）；时间只帮助用户与自己识别的自然通知对照。不订阅新增事件、不访问`Notification.Visual`、不读取通知正文、不改白名单、不保存候选、不启动持续监听。应用显示名只用于帮助人工寻找样本，不能证明发布者身份；候选须与用户明确识别的真实通知交叉验证，且不应仅凭该诊断自动启用监听。
- 0.1.92.0新增`--notification-source-diagnostic`安装包内无窗口入口，供受控诊断流程后台读取相同元数据并立即退出，不启动桌宠、语音、模型、任务中心或持续监听。没有MSIX身份、通知授权未处于Allowed或读取期间授权被撤销时，模式失败关闭且不请求授权；stdout只返回状态与过滤后的候选摘要，不保存文件。当前QQ发布者AUMID `QQ`已与本机开始菜单注册项和`D:\QQ\QQ.exe`路径相符；该来源确认只识别发布者，不提供联系人/会话身份，不足以区分私聊与群聊。
- 启动时把当时已存在的匹配通知记为基线，不将旧通知误当作新消息。新增事件按发布者和系统通知 ID 去重，并受内存容量限制。
- 新增事件先进入最多 256 个 ID 的有界队列，排队项与正在处理项统一去重，由单个消费者串行读取 Windows 通知列表；队列只保存系统 ID，不保存通知正文。持续事件超过容量时不再接收新 ID，并提示用户手动查看客户端，避免并发请求无界增长。
- 处理前检查输入桌面是否为 `Default`；在异步获取通知列表返回后重新检查通知授权，并在惰性正文读取的最后一步再次检查授权和解锁状态。读取期间发现权限撤销时立即停止监听；发现锁屏时只提示、不读取正文。通知 AUMID 同时出现在微信和 QQ 配置中时拒绝启动监听，避免应用归属歧义。异步获取结果若来自已停止或已替换的监听实例，则丢弃。
- 领域策略采用惰性正文读取器；新增`MessageNoticePolicy.GateBodyReader`作为适配器边界的第二道门。来源不匹配、权限无效、会话锁定、会话类型未知或重复通知时，不会调用正文读取器；未确认私聊或没有有效会话ID/发送者标识时，适配器收到的只是返回`null`的空读取器，而不会持有实际Toast对象的读取委托。
- 只有会话类型确认是私聊且至少有一个有效会话 ID 或发送者标识时，策略才继续处理；两者都缺失或元数据超长时，只提示人工查看。
- 超过 24 小时的旧通知跳过自动分析；会话限速在正文读取前执行，因此被限速的通知不会先读正文再丢弃。
- 即使来源身份已匹配，只要私聊/群聊归属未知，当前实现只显示通用提示，不自动切换会话、不读正文、不运行模型。
- 源码核对确认当前还没有接入微信或 QQ 的会话解析器：`WindowsNotificationMonitor.ProcessAddedAsync` 选择来源适配器后，将 `conversationId`、`sender` 和 `isPrivateConversation` 都以 `null` 传入，并由`GateBodyReader`替换为无操作读取器。因而所有持续监听事件都会按“无法确定私聊”处理，适配器不持有能读取真实通知正文的委托；仅 AUMID 命中并不意味着小K已经读取或分析通知正文。这是源码路径结论，不能替代真实客户端样本验收。
- 已确认是私聊但通知正文为空时，只提示用户手动打开客户端；不切换窗口。正文超过 20,000 字符时不交给模型，通知对象不保留正文。
- 只有策略明确确认私聊并取得可见正文时，正文才通过内存事件交给 Host；Host 复核当前开关、AUMID、会话归属和时效后，进入最多 32 项的低优先级本地分析队列。交互任务优先取得 ModelBroker 租约。停用监听、用户取消或退出会取消相应队列任务。结果只在小K界面和不含正文的托盘提示中短时呈现，不写入任务数据库或日志，也不会自动发送回复。
- 通知正文没有写入任务数据库、日志、崩溃报告或遥测。当前消息通知策略和去重状态只在进程内存中保存。

### R0：非前台 UI Automation 可行性初探（2026-10-09）

为评估不抢前台的客户端自动化路径，对当前运行的 QQ 9.9.30.48762 和微信 4.1.15.13 进程做了只读 UI Automation Control View 与 Raw View 结构快照。探查只读取元素的控件类型、类名、AutomationId元数据和原生窗口句柄；没有读取元素名称、Value/Text内容、通知正文或聊天记录，没有点击、输入、移动窗口或改变焦点。Control View最多遍历250个元素、5层；Raw View最多遍历1200个元素、10层。

| 客户端 | 当前观测到的结构元素 | 初步判断 |
| --- | --- | --- |
| QQ 9.9.30.48762 | Control View有4个元素：2个 `Window / Chrome_WidgetWin_1`、2个 `Pane / Intermediate D3D Window` | Control View只呈现渲染表面；Raw View结果见下表 |
| 微信 4.1.15.13 | Control View有3个元素：1个 `Window / Qt51514QWindowIcon`、2个 `Pane`（`Qt51514QWindowIcon`、`MMUIRenderSubWindowHW`） | Control View只呈现Qt/渲染表面；Raw View结果见下表 |

Raw View的只读结构结果：

| 客户端 | Raw View元素概况 | 可用性边界 |
| --- | --- | --- |
| QQ 9.9.30.48762 | 完整遍历到630个元素：51个`Button`、1个`Edit`、2个`Document`、93个`Text`、400个`Custom`及其他容器；观测到按钮的`InvokePattern`、编辑框的`ValuePattern`和`InvokePattern`、文档的`ValuePattern`；上述按钮/编辑框/文档没有提供可用的`AutomationId` | 存在一定的程序化控件表面，但没有读取名称或值，无法确认哪个按钮是联系人、会话、消息输入或发送，也无法判断是否能在后台稳定操作 |
| 微信 4.1.15.13 | 完整遍历到8个元素：4个`Button`、2个`Pane`、1个`TitleBar`、1个`Window`；4个按钮支持`InvokePattern`并有AutomationId；未观察到`Edit`或`Document` | 按钮用途未核实；没有观察到可用于确认会话、读取消息或编辑待发正文的语义控件 |

这是当前窗口状态的一次快照：未读取窗口标题，因此不能证明命中的顶层窗口就是聊天主窗；也没有确认控件在不同窗口状态和客户端版本中的稳定性。微软说明，自定义控件若没有 UIA provider，可能只向 UI Automation 暴露有限的窗口句柄信息；这与Control View观察到的渲染表面相符。[UI Automation Providers Overview](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-providersoverview)

Raw View确实暴露了一些按钮、文本及编辑/文档模式，但探查没有读取控件名称或内容，没有调用`ValuePattern.SetValue`、`Invoke`或其他动作，也没有验证目标联系人、会话类别、消息内容和发送结果。因此不能把控件数量或模式支持当作后台消息能力通过；也不能仅凭窗口坐标或模糊控件顺序构造发送适配器。后续若继续探索，必须先设计只读映射/合成目标核验，再由真实客户端样本验证；任何真实发送仍需针对最终内容单条确认。

QQ官方机器人开放平台不是个人QQ账号的等价发送通道：官方消息文档列出单聊使用机器人侧的用户 `openid`，被动回复需引用收到的事件或消息；同一文档说明主动推送自2025年4月21日起不再提供。该API即使具备开发者凭据，也会以机器人身份发送，不能据此宣称小K能够在用户个人QQ客户端中任意向好友K主动发送。[腾讯QQ机器人发送消息文档](https://github.com/tencent-connect/bot-docs/blob/main/docs/develop/api-v2/server-inter/message/send-receive/send.md)

所以目前既没有已验证的会话UIA解析器，也没有可替代用户个人QQ身份的官方主动发送路由。上述探查没有解决通知私聊归属问题，也没有读取真实通知样本、启用监听或发送消息。后续仍须在用户能够识别来源的自然通知上完成 AUMID 与私聊格式验证；若客户端 UIA 仍只暴露自绘表面，应报告真实限制，不得转用数据库、进程注入、协议逆向或抢前台坐标点击。

## Windows API 与软件包状态

项目将目标框架固定为 `net10.0-windows10.0.26100.0`，通过 `WindowsSdkPackageVersion` 使用 `Microsoft.Windows.SDK.NET.Ref` 10.0.26100.87。`NuGet.Config` 仅映射该获准包；这不是客户端发送接口，也不自带微信或 QQ 会话标识。

### R0：标准监听接口能提供哪些会话信息

微软 `UserNotificationListener` 文档列出的单条`UserNotification`字段是应用信息、创建时间、通知ID和`Notification`内容；文档示例从`Notification.Visual`的`ToastGeneric`绑定读取文本元素，并将首项当标题、后续项当正文。`Notification`本身公开`Visual`，而Windows Toast定义中的`launch`参数是点击通知时交给原应用用于跳转的激活上下文。参见[Notification listener](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/notification-listener)、[UserNotification](https://learn.microsoft.com/en-us/uwp/api/windows.ui.notifications.usernotification?view=winrt-26100)、[UserNotification.Notification](https://learn.microsoft.com/en-us/uwp/api/windows.ui.notifications.usernotification.notification?view=winrt-26100)、[Notification.Visual](https://learn.microsoft.com/en-us/uwp/api/windows.ui.notifications.notification.visual?view=winrt-26100)和[通知内容结构](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-content)。

**基于这些公开成员清单的判断：Windows标准监听对象没有结构化`conversationId`、联系人稳定ID或`isPrivateConversation`字段，也没有文档化的`launch`读取属性。** 因此必须由微信/QQ各自的可见通知格式提供足够证据，才能尝试构造会话键并区分私聊/群聊；标题/正文的通用位置只能说明显示文本，不能自行证明私聊。此结论是从微软文档描述的API面作出的推断，不代表已经检查了本机真实客户端Toast XML，也不证明某一客户端一定不在其可见文本中放入可识别标记。

当前`WeChatNoticeAdapter`和`QQNoticeAdapter`仍需上游提供真实样本解析结果；`WindowsNotificationMonitor.ProcessAddedAsync`显式传入空`conversationId`、`sender`和私聊标记，所以不会读取正文。应先以真实微信/QQ后台通知验证是否存在稳定、可区分的可见格式；若没有，则R0消息通知闸门判定不可行，需另行评估官方接口或可验证的非前台UIA路径，不能弱化当前私聊判定或读取客户端数据库、注入客户端。

[`src/XiaoK.Host/Package.appxmanifest`](../../src/XiaoK.Host/Package.appxmanifest) 声明 `userNotificationListener` 能力，开发发布者固定为 `CN=XiaoK Local Development`。当前账户签名MSIX为`0.1.92.0`，状态`Ok`；包SHA-256为`86E9AB22CEAC74653D45433552D69460AE274926BBE7B5ECD095AF3557FB3B91`。0.1.92.0无窗口只读诊断实测系统通知访问为Allowed，检查37条现存Toast元数据，发现4条QQ/AUMID `QQ`候选、未发现微信候选；QQ来源与本机开始菜单登记及`D:\QQ\QQ.exe`进程路径相符。设置仍为微信/QQ监控关闭、AUMID白名单为空。诊断不读通知正文、不启用持续监听、不改白名单；Host随后退出。QQ来源发布者已得到本机佐证，但联系人/会话身份、私聊属性和可见正文格式尚未核验；编译、签名、安装或发布者匹配都不能替代这些真实通知验证。安装和回滚细节见[MSIX打包说明](../开发与发布/MSIX打包说明.md)。

## 自动分析启用条件

小K只有同时具备以下证据时，才能把正文送到本地模型：

1. Windows 通知权限状态为已授予，且用户明确开启对应客户端监控。
2. 通知的 Windows 来源 AUMID 与该客户端已核实的 allowlist 完全匹配。
3. 有经真实通知样本验证的客户端专用解析器，能够可靠地区分私聊与群聊；未知格式必须返回“无法判定”。
4. 输入桌面处于解锁状态，通知是新事件且未重复。
5. 通知实际包含可见正文，长度处于上限以内。

任何条件不满足时，失败关闭：不读取正文、不送模型，并向用户说明需要手动查看或检查授权。通知文本是单条消息，不代表完整会话上下文；模型只能分析明确内容、可能意图和建议，不得补造聊天背景。

## 本机验收记录

无第三方测试依赖的安全检查程序覆盖：非允许来源、未知会话、缺少会话归属、过期通知、限速前置、锁屏、权限撤销、正文读取前的授权/解锁复核、微信/QQ来源归属冲突、已确认私聊、重复通知、无正文和超长正文，以及通知来源候选过滤、无关显示名拒绝、512项扫描和20项候选上限、候选最近创建时间聚合、设置UI只读显示与正文读取器二次门控。最新源码复核（2026-10-09）使用固定SDK 10.0.401离线Release构建0警告、0错误；完整Windows安全套件121项通过、0项跳过，包含AppContainer OS边界检查。检查使用合成AUMID、合成时间和内存字符串，不读取系统通知正文、不访问微信/QQ账号；这些模拟用例不是客户端实测证据。0.1.92.0无窗口诊断已实测QQ发布者来源与本机登记相符，但没有读取正文或验证联系人/私聊信息。0.1.89.0和0.1.90.0的功能证据分别是候选展示改进和正文读取器门控，不能替代通知正文形态与私聊身份的真实验证。

以下仍待本机真实验收，不能由模拟策略测试代替：

- 固定 MSIX 发布者身份、安装包签名、系统通知授权和授权撤销恢复。
- 在当前 Windows 与客户端版本上核实微信、QQ 通知来源 AUMID、通知文字结构及真实私聊/群聊判别依据。
- 每款客户端至少 30 条后台正文可见私聊通知，核对来源、私聊归属、去重、漏报/误报和自动分析比例；账号消息测试须由用户逐项授权。
- 无正文、群聊、客户端退出/重启、锁屏、睡眠恢复和权限撤销等失败路径。

在以上条件通过之前，设置页 AUMID 字段保持用户手工核实输入，任何未知会话都不会自动分析。
