## v0.1.68.0 R1任务中心取消状态保留绑定（2026-10-09）

任务中心点击“取消”时，原代码直接给绑定到`CanCancel`的按钮属性赋值，可能移除WPF绑定。现在通过`SetCurrentValue`临时禁用，并按当前任务状态恢复有效值，保留原绑定；桌面交互专项加入源代码回归保护。固定SDK 10.0.401 Release解决方案构建0警告、0错误；Windows安全套件115项通过、0项跳过；桌面交互专项63项通过。

自包含签名MSIX位于`artifacts\msix-validation\39c8e9283b5e4d35a293d1f71a26b824\XiaoK-signed-validation.msix`，124,643,983字节，SHA-256 `D3104E1BB98D944B77D9AE5A53756C916C654F7A9E25EC90EF661F1493474C85`。SignTool验签0警告、0错误；签名证书指纹`B96A02547ABA84523619E11EB7788AE9850A5C60`原已受信任，本阶段未改变信任。当前账户安装`MingKaiLin.XiaoK_0.1.68.0_neutral__g0ndt6g65c8pe`，状态`Ok`。

隔离诊断配置下进程PID 11740的“小K”窗口不可见。启动前、启动后与正常退出后的前台句柄均为`0x10DC6`；向该PID唯一标题为“小K”的窗口投递应用注册的正常关闭消息后，Host以退出码0结束，当前无Host进程。没有打开普通界面、运行真实任务或启用模型、麦克风与通知；任务中心普通UI和真实队列操作仍待用户会话验收。

## v0.1.67.0 R1任务中心刷新按稳定身份保留选中项（2026-10-09）

任务中心刷新不再用可读标题识别选中行。历史条目新增内部稳定键，普通任务使用完整GUID，隔离编程任务使用完整任务ID；列表更新后按稳定键恢复选择，原记录已消失时恢复滚动位置。桌面交互专项62项和Windows安全套件115项通过，Release构建0警告、0错误。

自包含签名MSIX位于`artifacts\msix-validation\3a9e887de6b1453c82bb0d899156650d\XiaoK-signed-validation.msix`，124,643,804字节，SHA-256 `730C5BF40E9D47B299EEFC5BD5C3BD9E82715CC96B848B94C3594228D21A26B4`。SignTool验签0警告、0错误；签名证书指纹`B96A02547ABA84523619E11EB7788AE9850A5C60`已受信任，本阶段未改证书信任。当前账户已安装`MingKaiLin.XiaoK_0.1.67.0_neutral__g0ndt6g65c8pe`，状态`Ok`。

隔离诊断隐藏启动后，窗口不可见；启动前、启动后和正常退出后的前台句柄均为`69062`。应用注册正常退出消息使Host以退出码0结束，当前无Host进程。未打开普通界面、执行真实任务或启用模型、麦克风和通知；普通任务中心UI和真实队列流程仍待用户会话验收。

## v0.1.66.0 R1审批待办按任务ID关联（2026-10-09）

复核0.1.65.0实现时发现它只检查“是否存在任意确认/代码审阅”，因此一个任务等待确认会让任务历史里的所有运行任务都显示为等待审批。源码提交`06cd816`将任务ID从Host路由传过ToolBroker、回收站确认与代码审阅，再由当前进程内审批箱按任务ID精确查询；消息预览、排队任务和终态不会被误标。任务ID只保存在短时内存待办中，不进入SQLite，也不用于跨进程恢复。

固定SDK 10.0.401 Release解决方案构建0警告、0错误；Windows安全套件115项通过、0项跳过；桌面体验专项61项通过。并行任务合成检查证明任务A的确认只把A投影为等待审批，任务B仍运行；代码审阅和回收站确认均验证传入的任务ID。源码提交`06cd816`已推送至`origin/main`，GitHub Actions [Build #308](https://github.com/mingkailin70-cmd/desktop-assistant/actions/runs/37806510359)成功。

自包含签名MSIX位于`artifacts\msix-validation\b3beefe7571246aaaec8f5cc436f77dc\XiaoK-signed-validation.msix`，124,643,701字节，SHA-256 `7029C1DE699B13CA59A6561B09999D04950436FB2C55CEA1E5C1DFA00B20D90D`。SignTool验签0警告、0错误；签名者`CN=XiaoK Local Development`，证书指纹`B96A02547ABA84523619E11EB7788AE9850A5C60`已受信任，本阶段未改证书信任。当前账户已安装`MingKaiLin.XiaoK_0.1.66.0_neutral__g0ndt6g65c8pe`，状态`Ok`。

隔离诊断模式隐藏启动后，小K窗口不可见、前台焦点保持不变；通过应用注册的正常退出消息关闭，退出码0，当前无Host进程。没有打开普通界面、运行真实任务或启用模型、麦克风、通知。R1普通UI与真实任务流程仍待验收。

## v0.1.65.0 R1任务历史审批状态投影初版（2026-10-08，后由0.1.66.0修正）

本版首次加入审批状态读取投影，但使用全局“是否存在任意确认/代码审阅待办”标志处理全部运行历史项；并行任务期间可能把没有待办的任务误显示为`AwaitingApproval`。该关联缺陷已在0.1.66.0修正。投影不改SQLite、不持久化审批，也不在重启后恢复或重放动作。

固定SDK Release构建0警告、0错误；Windows安全套件115项通过、0项跳过；桌面体验专项61项通过。代码提交`5657a48`已推送，GitHub Actions Build #305取消；版本提交`aef59a3`已推送，Build #306成功。

签名MSIX位于`artifacts\msix-validation\1a24f2938ab64ff4a87c11f4ee9e7969\XiaoK-signed-validation.msix`，124,642,986字节，SHA-256 `DC0606233451FB6E9FDFEC9E5BD66251D276277783E3F873D67E21C1FB094187`。SignTool验签0警告、0错误；签名者`CN=XiaoK Local Development`，证书指纹`B96A02547ABA84523619E11EB7788AE9850A5C60`已受信任，安装没有改变证书信任。当前账户已安装`MingKaiLin.XiaoK_0.1.65.0_neutral__g0ndt6g65c8pe`，状态`Ok`。

安装版只在`--diagnostics-profile --background`模式下隐藏启动；窗口“小K”不可见，应用注册关闭消息使进程以退出码0结束。没有打开普通界面、运行用户任务或启用模型、麦克风和通知；R1普通UI与任务流程仍待验收。隔离配置仅访问独立Temp目录，没有读取常规用户数据。

## v0.1.64.0 运行时任务类别持久化修复（2026-10-08）
本版修复SQLite任务存储类别白名单落后于`AssistantRuntime`路由的问题。此前存储只接受8种旧类别，而运行时已经路由18种任务；不受支持的新类别会在首次排队状态保存时失败，任务无法进入执行队列。Core新增统一固定`TaskCategoryCatalog`供Host显示和SQLite校验/读取共同使用。安全回归逐一验证18类写入、读取和备份均成功，仍拒绝未知类别；任务摘要/结果正文继续丢弃。没有运行真实任务或触碰真实文件。

固定SDK 10.0.401 Release解决方案构建0警告、0错误；Windows安全套件115项通过、0项跳过；桌面体验专项61项通过。修复提交`ae51d32`已推送，GitHub Actions [Build #301](https://github.com/mingkailin70-cmd/desktop-assistant/actions/runs/37798191625)成功；版本提交`de3783c`已推送，GitHub Actions [Build #302](https://github.com/mingkailin70-cmd/desktop-assistant/actions/runs/37798471086)成功。

自包含签名MSIX位于`artifacts\msix-validation\da53d9cc526c4716b35e5752a545dc25\XiaoK-signed-validation.msix`，124,642,458字节，SHA-256 `E4F698A8FF824F06F27D5F9528ED142DC8B83B35A637E7632167DC547099BDA2`。SignTool验签0警告、0错误；证书已在`LocalMachine\TrustedPeople`，本次没有修改信任。当前账户已安装`MingKaiLin.XiaoK_0.1.64.0_neutral__g0ndt6g65c8pe`，状态`Ok`。安装脚本本身没有启动Host；随后从安装目录以隔离诊断配置隐藏启动，窗口句柄均不可见，未启用模型、麦克风、通知或任务，并经应用注册的正常退出消息关闭。当前无`XiaoK.Host`进程。真实任务路由、普通用户界面和真实文件操作仍待安装版验收。诊断配置留在系统Temp下的独立目录，未访问常规用户数据。

## v0.1.63.0 R3单文件回收站与SQLite v5更新（2026-10-08）

本版包含固定范围的单文件回收站工具、审批后身份复核、审批后提交前取消处理，以及SQLite v4→v5审计迁移；设置页备份恢复提示同步为v5。固定SDK 10.0.401 Release构建0警告、0错误；完整Windows安全套件115项通过、0项跳过，桌面交互专项61项通过。拒绝、审批前/后取消和范围限制均使用仓库外临时合成文件验证，没有调用真实回收站。

自包含签名MSIX位于`artifacts\msix-validation\7e1c6d6f09684ee6ae12db66927cd904\XiaoK-signed-validation.msix`，124,643,072字节，SHA-256 `EC26919A3F9C8A1B350BBA23C7B78E78A0E42456FF82774C602BFF98E419F191`。SignTool验签0警告、0错误；签名证书原已在`LocalMachine\TrustedPeople`，本版未改动信任。当前账户已安装`MingKaiLin.XiaoK_0.1.63.0_neutral__g0ndt6g65c8pe`，状态`Ok`。安装后没有启动Host或普通小K界面；没有触碰真实文件。Shell使用路径式接口，最终身份复核与调用之间存在短暂并发替换窗口，真实文件操作和安装版用户流程仍待验收。

## v0.1.60.0 R0通知来源诊断辅助入口（2026-10-08）

本版设置页新增由用户主动点击的一次性通知来源诊断。仅当MSIX已有Windows通知访问权限时，按创建时间从当前Toast快照中选取最多512条近期通知，只读取其应用显示名和AUMID，并展示显示名含微信/WeChat/Weixin/QQ的候选；不会自动请求授权、监听新增事件、读取正文、改动白名单或保存结果。本轮没有打开普通设置页或读取任何系统通知，真实来源身份与私聊归属仍未验证。

固定SDK 10.0.401 Release解决方案构建0警告、0错误；完整Windows安全套件114项通过、0项跳过，桌面交互专项61项通过。自包含签名MSIX位于`artifacts\msix-validation\0f6f7ca55b2f4448a1e78b558ae2996a\XiaoK-signed-validation.msix`，124,635,047字节，SHA-256 `9200FD9269D53E760D0D51224E2C0CE60C8449A316E9C37A33BC89743F953695`。SignTool验签0警告、0错误；签名者`CN=XiaoK Local Development`，指纹`B96A02547ABA84523619E11EB7788AE9850A5C60`；LocalMachine信任未更改。当前账户安装包`MingKaiLin.XiaoK_0.1.60.0_neutral__g0ndt6g65c8pe`状态`Ok`。

安装后仅使用`--diagnostics-profile --background`隔离启动。窗口枚举按PID确认主窗口“小K”隐藏，投递注册正常关闭消息后Host退出码0；启动/退出前台句柄均为`69062`。没有打开普通设置UI、请求通知权限、访问Toast或运行用户任务。该诊断不能证明新来源按钮的普通UI行为，也不能证明消息通知能力。

## v0.1.59.0 R1副作用路由不确定结果保护（2026-10-08）

本版在固定Core策略中登记可能改变系统或用户工作区状态的任务类别。进入应用启动/窗口切换、文件复制/改名/移动/压缩、公网下载或隔离编程路由后，若Host收到未处理异常、取消或超时且无法确认适配器结果，将任务状态记录为`OutcomeUncertain`，在任务中心提示先检查目标状态，不自动重试。只读搜索/静态网页读取及路由开始前的取消不受该回退影响。`message.send.v1`目前只显示预览、没有外发适配器；接入真实发送器时必须加入此策略并独立验收。

固定SDK 10.0.401 Release解决方案构建0警告、0错误；完整Windows安全套件113项通过、0项跳过，桌面交互专项61项通过。自包含签名MSIX位于`artifacts\msix-validation\a9b79224566d4ef6a0cde6617bae600f\XiaoK-signed-validation.msix`，124,630,936字节，SHA-256 `8C8B143729E3FFB8887A2A3627230E483123AEC806C86DA6328CE9D84F9A1B44`。SignTool验签0警告、0错误；签名者`CN=XiaoK Local Development`，指纹`B96A02547ABA84523619E11EB7788AE9850A5C60`。证书已在`LocalMachine\TrustedPeople`，本轮未改信任。当前账户安装包`MingKaiLin.XiaoK_0.1.59.0_neutral__g0ndt6g65c8pe`状态`Ok`，本轮未启动正常用户界面。

安装版只以`--diagnostics-profile --background`隔离启动。隐藏窗口无法经`Process.MainWindowHandle`访问，随后按PID枚举窗口、核验安装路径后向小K窗口投递注册的正常关闭消息；Host退出，启动/退出前台句柄均为`329536`。诊断模式拒绝普通桌面/文件/模型任务，因此这里只核验包身份、后台启动和正常退出，不代表产品UI或任务路由通过。源码提交`848d9ba`的GitHub Actions [Build #293](https://github.com/mingkailin70-cmd/desktop-assistant/actions/runs/37782793815)成功。

## v0.1.58.0 R1后台路由不打扰修正（2026-10-08）

本版包含源码提交`4360de8`：找文件、聊天分析/起草、发送预览、代码检索和隔离编程任务均从Host后台任务队列调用`ExecuteBackgroundAsync`。固定交互策略只将应用启动和窗口切换列为前台；任务审批和代码审阅仍以非模态任务待办呈现。没有接入真实微信/QQ发送适配器，本阶段未发送消息。

固定SDK 10.0.401自包含发布与MSIX结构验证成功，包内含.NET 10.0.12和Windows Desktop 10.0.12，共614个载荷文件。签名MSIX位于`artifacts\msix-validation\f88ef1fee13f4a439aa3f024b9de0c7b\XiaoK-signed-validation.msix`，124,630,340字节，SHA-256 `464170AB1512857A53D4DFBCEFD0718102D7EDDFD9380224CBF51D82276D1B82`。SignTool验签成功，0警告、0错误；签名者`CN=XiaoK Local Development`，指纹`B96A02547ABA84523619E11EB7788AE9850A5C60`。证书已在`LocalMachine\TrustedPeople`，本次未更改信任。MSIX已安装至当前账户，包`MingKaiLin.XiaoK_0.1.58.0_neutral__g0ndt6g65c8pe`状态`Ok`，安装目录`C:\Program Files\WindowsApps\MingKaiLin.XiaoK_0.1.58.0_neutral__g0ndt6g65c8pe`。随后以`--diagnostics-profile --background`从安装目录启动Host（PID 36056），窗口标题“小K”与PID匹配且保持隐藏。启动前、启动后、运行期间和退出后前台句柄均为`329536`；投递注册的正常退出消息后进程退出码0。诊断模式拒绝桌面操作、文件访问和模型推理；本次没有启动普通用户界面，因此消息预览UI与真实任务路由仍待验收。

Release解决方案构建0警告、0错误，后台交互专项58项通过，完整Windows安全套件113项通过、0项跳过。源码提交`4360de8`已推送；GitHub Actions [Build #287](https://github.com/mingkailin70-cmd/desktop-assistant/actions/runs/37777121163)成功。诊断退出后再次核对当前账户仍安装0.1.58.0、状态`Ok`，且当前无`XiaoK.Host`进程。

## v0.1.57.0 R1审批重启恢复修复（2026-10-08）

本版包含`d4de23e`：重启后，内存中已经失效的普通审批任务不再显示成可继续处理的`AwaitingApproval`，而映射为`OutcomeUncertain` / `APPROVAL_NOT_RESTORED`并明确提示不会自动继续或重放。隔离代码任务的旧`awaiting_approval`同样标记为中断待核对；当前进程中的审批流程不受影响。安全套件113项通过、0项跳过；Release解决方案构建0警告、0错误。待审批内容并未持久化，因此本修复不恢复审批按钮或原批准动作。

固定SDK 10.0.401将Host发布为win-x64自包含应用，含.NET 10.0.12与Windows Desktop 10.0.12；MSIX结构检查通过，614个载荷文件。签名包位于`artifacts\msix-validation\ba236fdb4ad04605bb16784fe8422a92\XiaoK-signed-validation.msix`，124,630,357字节，SHA-256 `01859C4B16A166D69695C61F190FDBD6717C759D53225E79310293DC004A14C2`。SignTool验签通过，0警告、0错误；签名者`CN=XiaoK Local Development`，指纹`B96A02547ABA84523619E11EB7788AE9850A5C60`。证书已在`LocalMachine\TrustedPeople`，本次未改信任。包已安装至当前账户，`MingKaiLin.XiaoK_0.1.57.0_neutral__g0ndt6g65c8pe`状态`Ok`，安装目录`C:\Program Files\WindowsApps\MingKaiLin.XiaoK_0.1.57.0_neutral__g0ndt6g65c8pe`；安装后确认Host未运行，本轮未启动该包。

修复提交`d4de23e`与版本提交`4073b49`均已推送至`origin/main`。GitHub Actions [Build #285](https://github.com/mingkailin70-cmd/desktop-assistant/actions/runs/37775736968)成功；此前对修复提交单独创建的Build #284被版本提交触发的新运行取消。当前包的Host启动、任务中心实机显示和重启恢复仍待验收。

## v0.1.56.0 R3搜索命令路由安装包（2026-10-08）

本版包含阶段提交`29b402e`中的Core固定提案工厂：用户搜索命令通过同一工厂生成`file.search.content.v1`提案，Host与安全检查共用构造规则。固定SDK Release解决方案构建0警告、0错误，定向内容搜索检查通过，完整Windows安全套件113项通过、0项跳过。版本号提交`1f0dedf`已推送至`origin/main`；GitHub Actions [Build #281](https://github.com/mingkailin70-cmd/desktop-assistant/actions/runs/37772765036)和[Build #282](https://github.com/mingkailin70-cmd/desktop-assistant/actions/runs/37773908609)均成功。

使用固定SDK发布为win-x64自包含Host，包内含.NET 10.0.12与Windows Desktop 10.0.12，共614个载荷文件；MSIX结构检查通过。签名包位于`artifacts\msix-validation\ff2026ccc9f34dc1bd12f541c4903fcb\XiaoK-signed-validation.msix`，124,630,093字节，SHA-256为`0FEEF23F06BC452EFA22457CD8406557333DB6DF9BAE199BA51E9132AFEFBB1D`。签名者为`CN=XiaoK Local Development`，证书指纹`B96A02547ABA84523619E11EB7788AE9850A5C60`；SignTool验签通过，0警告、0错误。证书已在`LocalMachine\TrustedPeople`，本次未改变信任设置。签名MSIX已安装到当前账户，包`MingKaiLin.XiaoK_0.1.56.0_neutral__g0ndt6g65c8pe`状态为`Ok`，安装目录为`C:\Program Files\WindowsApps\MingKaiLin.XiaoK_0.1.56.0_neutral__g0ndt6g65c8pe`。

安装后只读确认当前没有`XiaoK.Host`进程。本轮未启动0.1.56.0，故安装成功不等于启动、Host命令路由、搜索界面或真实用户目录验收通过；这些项目仍待后续在不打断桌面工作的条件下核验。没有打开任务面板、读取真实目录、启动模型/麦克风/通知或访问微信/QQ。

## v0.1.55.0 R3本机文本内容搜索（2026-10-08）

固定SDK Release解决方案构建0警告、0错误；完整Windows安全套件113项通过、0项跳过，桌宠体验专项60项通过。定向合成检查覆盖路径范围、敏感扩展名、二进制与坏编码拒绝、文件大小和总量上限、结果不含正文、固定工具调用及取消。自包含签名MSIX位于`artifacts\msix-validation\c89c870a0b4047d28a9eebf0400884b6\XiaoK-signed-validation.msix`，124,631,554字节，SHA-256 `1C1F0A11CB46B5B7554160CE3120E25DD150F97CAC220A887A2A274304C15596`；签名者`CN=XiaoK Local Development`，指纹`B96A02547ABA84523619E11EB7788AE9850A5C60`。证书原已受信任，本次未改变信任。当前账户安装包`MingKaiLin.XiaoK_0.1.55.0_neutral__g0ndt6g65c8pe`状态`Ok`。阶段提交`8b481e7`已推送至`origin/main`，GitHub Actions [Build #276](https://github.com/mingkailin70-cmd/desktop-assistant/actions/runs/37767506879)成功。之后从安装目录以`--diagnostics-profile --background`启动Host，确认隐藏窗口、前台焦点句柄未变，并按注册消息正常退出。没有使用常规用户配置、打开任务面板或搜索真实目录；隔离诊断配置留存在系统临时目录，未接触用户数据。
补充适配器核验（2026-10-08）：已安装目录中的`XiaoK.Adapters.Windows.dll`和`XiaoK.Core.dll`分别与仓库中身份/版本匹配的MSIX解包副本散列相同（SHA-256分别为`5F8FB71B0789AF0A547515847D2FCF049FC972097E50C9A32D40E7BD4AC24DD3`和`57109137A3C647510C4A3B2A30CC5EBA7B0F0FD460B85E360C043B1DA0C2B048`）。WindowsApps拒绝诊断进程直接从受保护目录加载程序集，所以在散列一致的仓库副本上运行合成验证；UTF-8/UTF-16结果均只包含路径和行号，正文不泄露。此验证不覆盖安装版Host命令路由、界面或真实用户目录。

## v0.1.54.0 R1安装与桌面窗口首轮核验（2026-10-08）

用户授权正常退出旧版后，安装器经PID与窗口标题核对，向旧Host PID 51624请求正常退出并等待其自行结束；未强杀进程。安装前再次确认签名验证包SHA-256为`E8D51DB3217C5F7B7AA968FD3578FA6C7A9EC5EE4D634D049F22147A4A03FA3D`、Authenticode签名有效，证书已在`LocalMachine\TrustedPeople`；本次没有改变证书信任。安装到当前账户成功，包`MingKaiLin.XiaoK_0.1.54.0_neutral__g0ndt6g65c8pe`状态为`Ok`。从包清单读取应用ID后启动Host PID 50428。Computer Use确认银渐层桌宠收起态210×261 DIP、展开面板500×650 DIP；任务中心树列出30项既有历史并显示“目标范围、执行模式、下一步”。采集截图只显示Host主窗口、未单独显示任务中心弹窗；透明区域在截图中呈黑色，因此不把任务中心视觉布局或真实桌面透明合成记为通过。没有运行新任务、模型、麦克风、通知或消息操作。R1真实排队、前台焦点、多屏/DPI、睡眠/退出恢复及完整任务中心弹窗截图仍待验收。

## v0.1.54.0 R1任务中心目标范围与下一步指引验证包（2026-10-08）

任务卡新增隐私安全的目标范围、执行模式和逐状态下一步建议。签名MSIX位于 artifacts\\msix-validation\\4745c8b3568c44c18a520a1bf35b0bba\\XiaoK-signed-validation.msix，124,622,479字节，SHA-256 E8D51DB3217C5F7B7AA968FD3578FA6C7A9EC5EE4D634D049F22147A4A03FA3D；签名状态Valid，签名者CN=XiaoK Local Development，证书指纹B96A02547ABA84523619E11EB7788AE9850A5C60。安装脚本预检和安装前均确认签名有效、证书已受信任；用户授权正常退出旧Host后，0.1.54.0已成功安装并启动，详见本节上方记录。固定SDK Release构建0警告/0错误、Windows安全套件112项通过/0项跳过、桌宠体验专项60项通过。阶段提交`8633092`已推送至`origin/main`；GitHub Actions [Build #271](https://github.com/mingkailin70-cmd/desktop-assistant/actions/runs/37758311408)成功。

## v0.1.53.0 R1排队任务原子取消源码验证包（2026-10-08）

排队任务取消与工作线程启动现在由原子准入门仲裁，取消先赢时立即显示取消状态、持久化并由队列跳过；工作线程先赢时仅请求协作式取消。固定SDK Release解决方案构建0警告/0错误；完整Windows安全套件111项通过、0项跳过；桌宠/后台交互专项60项通过。签名验证包位于 `artifacts\msix-validation\aa6fc339f0a643f0880b0e61c17fe6f6\XiaoK-signed-validation.msix`，大小124,620,060字节，SHA-256 `F5915FDDC4C746497DDBF2435A5C057A5A7BE43472A1E549C4EAF71C1E45B59B`；SignTool验签0警告、0错误，证书信任未改变。阶段提交 `5899ca5` 已推送至 `origin/main`，GitHub Actions [Build #267](https://github.com/mingkailin70-cmd/desktop-assistant/actions/runs/37753983268) 成功；记录该CI结果的文档提交 `1b5c99e` 的 [Build #268](https://github.com/mingkailin70-cmd/desktop-assistant/actions/runs/37754231732) 也成功。

首次尝试更新安装版时，签名预检成功且本机证书已受信任，但旧Host PID 29988没有可核验的“小K”窗口句柄，安装脚本以Win32错误1168安全停止；当时没有强制结束进程、改变证书信任或安装状态。用户随后从系统托盘正常退出旧版，安装脚本成功将签名MSIX 0.1.53.0安装到当前账户；包状态Ok，安装目录为 C:\Program Files\WindowsApps\MingKaiLin.XiaoK_0.1.53.0_neutral__g0ndt6g65c8pe。随后只读观察到Host PID 51624、窗口标题“小K”、MainWindowHandle=198644。GitHub Actions [Build #269](https://github.com/mingkailin70-cmd/desktop-assistant/actions/runs/37755153526)对提交033dc11852b665714d9bbb56003d839ef6ba42f4成功。该句柄证明窗口已创建，不是截图或交互验收；此前Computer Use操作被Escape中断，桌面像素、透明合成、焦点、任务中心真实操作及恢复均未验证。该时点未观察到Host子进程或llama/Python/ASR/TTS命名进程，不代表模型推理验收通过。证书信任保持原样。
# MSIX 打包与权限验收

## v0.1.50.0 R3后台单文件移动源码验证包（2026-10-08）

新增 `file.move.v1`，仅支持设置搜索范围内同卷、单个普通文件移动到已存在目标目录，不覆盖同名项目。固定SDK Release解决方案构建0警告、0错误；完整Windows安全套件108项通过、0项跳过；桌宠体验专项57项通过；定向移动检查覆盖成功、范围外源/目标、冲突、预取消和目录联接源/目标。签名验证包位于 `artifacts\msix-validation\56765cbe671f48c29b8ded7f6afe7e51\XiaoK-signed-validation.msix`，大小124,604,980字节，SHA-256 `AEA718597F902AF4478F0D2297DE851BFB974E21A36E0F9C24B6CBD1C60DDCA6`；manifest版本0.1.50.0。SignTool验签成功，0警告、0错误，签名者`CN=XiaoK Local Development`，证书SHA-1指纹`B96A02547ABA84523619E11EB7788AE9850A5C60`。打包未改变证书信任。当前账户仍有旧版 MSIX `0.1.40.0` Host/PID `29988` 运行，故新包未安装或启动；提交 `cb04cd6` 已推送至 `origin/main`，GitHub Actions [Build #260](https://github.com/mingkailin70-cmd/desktop-assistant/actions/runs/37741958707) 成功。安装版与真实桌面并行工作验收仍待进行。批量整理、跨卷操作和文件占用压力情境不在本切片范围内。

## v0.1.49.0 R3只读文件分类源码验证包（2026-10-08）

新增 `file.classify.preview.v1` 只读扩展名统计。固定SDK Release解决方案构建0警告、0错误；文件分类定向检查通过；完整Windows安全套件106项通过、0项跳过；桌宠体验专项57项通过。签名验证包位于 `artifacts\msix-validation\a0c19f696e1441da9292cdcce9756e03\XiaoK-signed-validation.msix`，大小124,601,655字节，SHA-256 `B805D81276E34CB694E3EC5C861EC8ED3D711C6E65FB0AB1E81777032CCA9FAA`；SignTool验签成功，0警告、0错误，签名者 `CN=XiaoK Local Development`，证书SHA-1指纹 `B96A02547ABA84523619E11EB7788AE9850A5C60`。提交 `5e76f57` 已推送至 `origin/main`，GitHub Actions [Build #258](https://github.com/mingkailin70-cmd/desktop-assistant/actions/runs/37738758800) 成功。打包和签名未改变证书信任，未安装或启动新包。核对时当前账户仍运行 MSIX `0.1.40.0`/PID `29988`，因此保留现状以避免打断。此工具只分类预览，不整理、复制或移动文件。

## v0.1.48.0 R3静态公开网页读取源码验证包（2026-10-08）

固定SDK Release构建0警告、0错误；网页策略/ToolBroker定向检查通过，独立无头Edge静态夹具确认脚本不运行且回环子请求被阻止；完整Windows安全套件105项通过、0项跳过。自包含包只带`win32_x64` Playwright Node驱动，使用系统Edge Stable。签名MSIX位于 `artifacts\msix-validation\c34a70958bce482aba5df188d7e25392\XiaoK-signed-validation.msix`，大小124,595,332字节，SHA-256 `0622412BE9B86EC764E4B9A6CF24F38A730B1FBF0EC4A2DF9BA645287B70018F`；SignTool验签0警告、0错误，签名者`CN=XiaoK Local Development`，证书SHA-1指纹`B96A02547ABA84523619E11EB7788AE9850A5C60`。未安装或启动该包，证书信任未改变。本机首次公网DNS查询返回`HostNotFound`，随后同一读取器成功获取`example.com`静态正文156字符；该单一站点样本不代表完整网站验收。当前账户仍运行旧包，安装版窗口身份未能唯一核实。GitHub Actions [Build #256](https://github.com/mingkailin70-cmd/desktop-assistant/actions/runs/37736436369) 对提交 `e65cad1` 执行成功。

## v0.1.47.0 R3后台同目录重命名源码验证包（2026-10-08）

源码接入固定工具 `file.rename.v1` 与确定性中文命令；仅在配置搜索根内对普通单文件同目录改名，不移动或覆盖。Windows句柄式操作固定不替换目标，并在完成后核验文件身份与元数据。固定SDK Release解决方案构建0警告、0错误；Windows安全套件104项通过、0项跳过；桌宠/后台交互专项57项通过。自包含 win-x64 发布使用已锁定依赖完成。签名验证包位于 `artifacts\msix-validation\c820923aa06745f5baa033edc1b94b24\XiaoK-signed-validation.msix`，大小84,741,457字节，SHA-256 `605AF553DA9B56B0069BEC63AFA839A6FFA1A2DA05D32265DEFABE97EDDBF594`。SignTool验签成功，0警告、0错误；签名者 `CN=XiaoK Local Development`，证书SHA-1指纹 `B96A02547ABA84523619E11EB7788AE9850A5C60`。本次未改变证书信任。当前账户仍运行0.1.40.0且无可核验窗口标题，因此未安装或启动新包。GitHub Actions [Build #254](https://github.com/mingkailin70-cmd/desktop-assistant/actions/runs/37732167053) 对提交 `491506a` 执行成功。

## v0.1.46.0 R3后台单文件复制源码验证包（2026-10-08）

固定 SDK Release解决方案构建0警告、0错误；完整 Windows安全套件103项通过、0项跳过，桌宠/后台交互专项56项通过，其中包括后台文件复制正常、冲突、越界、大小上限、重解析点导出目录和散列核验。win-x64锁定还原未下载新依赖，自包含发布成功。签名验证包位于 `artifacts\msix-validation\5975c748ac254bb49d41c29b8166c628\XiaoK-signed-validation.msix`，大小84,737,333字节，SHA-256 `060DF6D0ED809F76170CCC77EA0E533C88FE5E574CE70B3C1EAB825E25064156`；签名状态`Valid`，签名者为`CN=XiaoK Local Development`，证书SHA-1指纹`B96A02547ABA84523619E11EB7788AE9850A5C60`。安装预检确认信任证书已存在，本次未变更证书信任、未安装或启动软件包。当前账户仍是旧安装版运行状态；由于没有可核验的唯一小K窗口，本次不关闭进程，因此没有安装新版。实际安装版复制交互、焦点干扰、DPI和重启恢复仍待验收。GitHub Actions结果待提交推送后补记。

## v0.1.45.0 R1任务输入保留修复验证包（2026-10-08）

固定 SDK Release解决方案构建0警告、0错误；Windows安全回归101项通过、0项跳过，桌宠体验专项55项通过。win-x64锁定还原成功并完成自包含发布。签名验证包位于 `artifacts\msix-validation\f4774fd2e6634738aa9136d5074c5e04\XiaoK-signed-validation.msix`，大小84,730,869字节，SHA-256 `B180539E483B23B36952C4E9E599E6FAB9E30D763D5799304B79F9F95881DF6E`；签名状态`Valid`，签名者为`CN=XiaoK Local Development`。打包未导入/修改证书信任、未安装或启动软件包。只读检查发现账户仍安装并运行`0.1.40.0` Host，但没有可核验窗口标题；因此没有向旧版发退出请求，也没有强制终止，避免中断当前会话。新增输入保护确保队列拒绝或满载时保留请求，并防止异步回调清除用户后来输入的文字。GitHub Actions Build #251 对提交 `6621d3e` 执行成功：[运行记录](https://github.com/mingkailin70-cmd/desktop-assistant/actions/runs/37727424370)。原生窗口验收仍未进行。

## v0.1.44.0 R1任务中心源码验证包（2026-10-08）

固定 SDK Release 解决方案构建0警告、0错误；Windows 安全回归100项通过、0项跳过，桌宠体验专项55项通过。win-x64锁定还原成功并完成自包含发布。签名验证包位于 `artifacts\msix-validation\9ea1c0a9b62247819546d61ee94e72fb\XiaoK-signed-validation.msix`，大小84,730,754字节，SHA-256 `FDFFD38934A968B7BD1F4AE428C3457B8403CC73BA8A001C6FFF62FAAC20EFE1`；签名状态`Valid`，签名者为`CN=XiaoK Local Development`。打包未导入/修改证书信任、未安装或启动软件包，当前账户仍运行旧安装版本。GitHub Actions Build #249 对提交 `aad5748` 执行成功：[运行记录](https://github.com/mingkailin70-cmd/desktop-assistant/actions/runs/37726574586)。任务中心的真实窗口布局、焦点影响、取消操作及关闭/重启恢复仍待安装版验收；本验证不代表R1完成。

## v0.1.40.0 桌宠 v15 银渐层形象更新（2026-10-05）

固定 SDK Release 解决方案构建通过，0 警告、0 错误，使用 `--no-restore`；win-x64 自包含 Host 发布也使用已还原依赖。签名 MSIX 位于 `artifacts\msix-validation\6bb29ae8718143a99178c41adadf3c9e\XiaoK-signed-validation.msix`，大小 84,706,308 字节，SHA-256 `33EE5AC41370C2DFBDB4B5A5656EFDC6F80A0681169CBB0ACC1D01E6A095A124`。签名状态 `Valid`，SignTool 验证 0 警告、0 错误；开发证书已在 `LocalMachine\TrustedPeople`，本次没有变更证书信任。安装器请求旧版 PID 28464 正常退出，之后将 `0.1.40.0` 安装到当前账户，包状态 `Ok`；诊断模式 Host PID 9264 正在运行。诊断模式不加载真实设置、不连接模型、不启用通知监听或麦克风。当前会话没有可用的原生窗口截图，因此 v15 的像素布局、透明合成和实际桌面交互仍待目视验收。

## v0.1.39.0 桌宠 v14 银渐层形象与暖白鼠尾草绿界面（2026-10-05）

固定 SDK Release 解决方案构建0警告、0错误；win-x64 自包含发布使用锁定依赖完成。签名 MSIX 位于 `artifacts\msix-validation\9cb81183d3044f67ba101470a80f421b\XiaoK-signed-validation.msix`，大小84,507,214字节，SHA-256 `C441AE564A6750B6A0ACEB51ACDD0DDADF0D6415DA088605A7F3B455DAAD6BF7`。Authenticode状态`Valid`，SignTool验证0警告、0错误；开发证书此前已位于 `LocalMachine\TrustedPeople`，本次没有改动信任。更新器确认旧版 PID 29460 正常退出，随后将 `0.1.39.0` 安装到当前账户，包状态`Ok`。安装后以 `--diagnostics-profile` 启动，新版 PID 28464 响应正常；该模式不加载真实设置、不连接模型、不启用通知监听或麦克风。桌面窗口工具未提供原生窗口截图，因此角色画面、DPI和透明合成尚未目视验收。旧版 `0.1.38.0` 包保留在本机忽略目录，可供回滚参考。

## v0.1.38.0 桌宠 v13 银渐层形象更新（2026-10-05）

固定 SDK 构建 `XiaoK.sln` 成功，0 警告、0 错误；win-x64 自包含发布使用已有锁定依赖且未联网还原。签名 MSIX 位于 `artifacts\msix-validation\3d212636db3246cb960290ece093d933\XiaoK-signed-validation.msix`，大小84,403,173字节，SHA-256 `54977A9C844727F60FFDF8F6EFB75F1935D0B522D534D913B41A228817D791FB`。SignTool验签有效，0警告、0错误；开发证书此前已在 `LocalMachine\TrustedPeople`，本次未改动信任。更新器按进程路径、PID和窗口标题确认并让旧版 PID 40964 正常退出后完成当前账户升级；当前包状态 `Ok`、版本 `0.1.38.0`。随后从已安装目录以 `--diagnostics-profile` 启动，PID 29460 响应正常；诊断模式不加载真实设置、不访问用户目录、不启动模型、通知监听或麦克风。Computer Use 未枚举原生窗口，未取得截图，因此安装与启动已核验，v13 的桌面像素布局和透明合成尚未目视验收。旧版 `0.1.37.0` MSIX 保留在本机忽略目录，可供回滚参考。

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
