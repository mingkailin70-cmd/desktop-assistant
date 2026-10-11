# GitHub开源项目借鉴记录

本清单记录经过初步核对、可能帮助小K的公开项目。列入清单不等于采用；每次实际引入代码、依赖或素材前，仍要固定提交/版本，复查许可证、维护状态、传递依赖、联网行为和本项目安全边界。

## Netwright：.NET UIA动作、稳定定位与结果差异

- 项目：[ricfanin/netwright](https://github.com/ricfanin/netwright)，仓库说明为MIT，面向.NET桌面应用；文档列出WPF、WinForms和WinUI支持，提供MCP与测试库。仓库README称当前为2.0重写版，但本次没有固定发行包或提交SHA，因此不把它当作已审计的产品依赖。
- 可借鉴处：把一次操作拆为“定位目标→调用UI Automation Pattern→等待界面稳定→返回前后状态变化”；用AutomationId/控件类型组合定位，以RuntimeId处理同一轮会话内的目标引用，并在快照中限制无关布局节点、列表项和行数。操作能力按UIA Pattern与物理输入分层；键盘、右键、双击或没有Pattern的控件明确走前台路径。
- 与小K的关系：Netwright针对有可访问性Provider的桌面应用，无法保证每个第三方程序暴露完整控件树；它也提供启动/关闭应用、截图和前台键鼠等宽权限工具。其README说明被它启动的应用可以通过随应用运行的伴随组件执行部分后台动作。这与小K只能操作用户当前安装应用、必须固定目标并避免任意注入的边界不同。
- 当前决定：不引用其MCP工具面、不安装外部服务器、不注入伴随组件。后续可在小K自有合成窗先借鉴Pattern白名单、目标新鲜度、settle和结果差异的结构，再逐个验证第三方App的实际Provider与焦点变化。
- 来源：[仓库README](https://github.com/ricfanin/netwright/blob/main/README.md)、[工具契约](https://github.com/ricfanin/netwright/blob/main/docs/TOOLS_REFERENCE.md)、[架构说明](https://github.com/ricfanin/netwright/blob/main/docs/ARCHITECTURE.md)、[MIT许可证](https://github.com/ricfanin/netwright/blob/main/LICENSE)。文档和基准数字来自项目维护者；小K尚未复现，不能当作本机实测。

## WinProbe：基于FlaUI的WPF/UIA自动化候选

- 项目：[wosoolsoft/WinProbe](https://github.com/wosoolsoft/WinProbe)，仓库标注MIT、.NET 10和FlaUI/UIA3，README声称提供44个MCP工具、后台控件操作与录制/回放。
- 可借鉴处：Core/WPF/Recording/MCP分层，和FlaUI `Invoke`、`ValuePattern`、`SelectionItem`结合的动作模型；目标可观测状态与固定错误结果值得参考。
- 限制与决定：本次GitHub页面仅显示1颗Star，项目自述的“后台安全”和工具覆盖尚无独立证据；公开工具面还包括键盘、截图、窗口生命周期、录制和回放。当前只作低置信度设计参考，不安装、不接MCP、不复用键盘/`PostMessage`/截图实现。需审阅源码、逐API核实第三方App副作用，并在合成窗口独立复现后再重新评估。
- 来源：[仓库README与架构概述](https://github.com/wosoolsoft/WinProbe)、[架构说明](https://github.com/wosoolsoft/WinProbe/blob/main/docs/architecture.md)、[安全说明](https://github.com/wosoolsoft/WinProbe/blob/main/SECURITY.md)、[MIT许可证](https://github.com/wosoolsoft/WinProbe/blob/main/LICENSE)。功能与许可证为仓库自述；没有作为小K验收结果。

## FlaUI：Windows UIA 验收参考

- 项目：[FlaUI/FlaUI](https://github.com/FlaUI/FlaUI)，官方仓库说明它基于 Windows UI Automation，覆盖 WPF、Win32、WinForms 和 Store 应用；官方发布页列有 v5.0.0。
- 可借鉴处：将 UI Automation 用于**测试宿主和隔离夹具**，验证窗口/控件是否可发现、辅助功能名称是否稳定、状态变化是否符合预期。
- 当前决定：不将 FlaUI 放入小K正式运行依赖，也不让产品运行时通过 UIA 注入鼠标/键盘或抢前台。需要实机 UIA 验收时，先对小K自有测试窗口做只读探测；操作真实窗口、读取聊天内容、保存截图都必须另行受控。
- 引入前检查：核对所选 FlaUI 版本支持的目标框架和许可证；固定 NuGet 版本/散列，并确保验收运行不驱动用户正在使用的应用。

## Microsoft winapp CLI：后台 UIA 与隔离验收候选

- 项目：[microsoft/winappCli](https://github.com/microsoft/WinAppCli)，本次核对公开稳定发布 `v0.7.1`（2026-10-01，提交`e3cd0b94f3e7b776ac51ba0564f4908094e979b5`）；仓库为MIT，但官方仍标记Public Preview。该版本文档固定在[UI Automation v0.7.1](https://github.com/microsoft/winappCli/blob/v0.7.1/docs/ui-automation.md)。不能把可变的`main`文档或未发布功能自动当作此稳定版本能力。
- 可借鉴处：它把桌面任务按并发冲突分三档：`inspect/search/get-value/wait-for`等只读查询可以并发；`set-value`与UIA滚动不注入物理输入、不取得桌面焦点，但因会改变目标应用状态而在自动化工作流间串行；`invoke`、截图、焦点、键鼠/触控注入等按桌面独占轮次执行。截图可能恢复最小化窗口或把目标拉到前台；Sandbox启动/重连也可能短暂切换焦点。该分类比“是否调用UIA API”更能描述干扰风险。工具还要求唯一/消歧后的控件选择，对模糊选择失败关闭，并在控件变化后重新发现目标。
- 与小K的关系：小K应借鉴三档交互能力、目标唯一性、过期目标重定位和独立结果核验；ToolBroker仍是唯一动作入口。winapp CLI的桌面仲裁只覆盖共同使用该CLI的进程，文档明确直接调用UI Automation NuGet库不在其仲裁保证内；因此小K若直接用FlaUI或Windows UIA API，必须由小K自己的协调器串行仲裁，不能因“后台UIA”就宣称不会打断用户。后台值写入虽不抢焦点，仍可能和用户同时编辑同一个应用冲突。
- 当前决定：不安装CLI、不增加其NuGet依赖、不启用Windows Sandbox，也未对真实应用发送输入。它是公共预览，官方README提醒`main`与稳定发布行为可能不同；其宽泛工具面和命令协调不直接接入小K。小K现已在Core实现并由ToolBroker持有自己的仲裁租约：只读UIA查询按目标共享、目标状态变更按目标独占并要求活动探针、物理桌面操作按全局独占串行，后台不能取得桌面租约。Windows前台占用探针已接入Host，只比对固定应用ID对应的可执行映像路径。现新增显式用户请求`app.ui.snapshot.v1`：只映射本机已登记应用和唯一可见窗口，进程身份由Host与工作进程双重核验；查询在5秒限时的单次子进程内执行，结果只返回有限语义控件属性并留在当前任务内存。小K自有合成窗经该隔离子进程读取后AutomationId/Name/启用状态正确，前台HWND不变、控件未触发。没有读取真实第三方应用，也没有新增UIA依赖。当前安全检查122项、桌面体验检查86项通过，Release解决方案构建0警告/0错误。真实应用Provider、MSIX下的子进程运行、锁屏和用户并发切换仍待验收；本机合成窗通过不证明第三方应用都能无扰读取。下一步继续对照固定应用配置和实际客户端Provider验证，绝不自动启动监控或读取真实聊天内容。
- 来源：[v0.7.1 UI Automation 文档](https://github.com/microsoft/winappCli/blob/v0.7.1/docs/ui-automation.md)、[v0.7.1 Release](https://github.com/microsoft/winappCli/releases/tag/v0.7.1)、[MIT许可证](https://github.com/microsoft/winappCli/blob/v0.7.1/LICENSE)、[官方README的Public Preview说明](https://github.com/microsoft/winappCli/blob/main/README.md)、Microsoft Learn [`AutomationElement.FromHandle`](https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.automationelement.fromhandle?view=windowsdesktop-10.0)与[WPF UI Automation Peer](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/controls/ui-automation-of-a-wpf-custom-control)。文档中的“不取焦点”属于CLI对其特定命令路径的描述，不代表小K直接调用同类UIA pattern已通过本机无干扰验收。

## Microsoft PowerToys：稳定AutomationId的UI测试实践

- 项目：[microsoft/PowerToys](https://github.com/microsoft/PowerToys)，本次固定核对`main`提交`ccdd9b19ca980e9d383a42c235fa313a595ac712`的公开UI测试迁移指南与测试模式文档；该项目以专用UI测试框架覆盖多个Windows模块。当前测试框架经winapp CLI驱动UI Automation，并要求测试拥有独立的应用生命周期；旧WinAppDriver流程另需退出目标进程及启用Developer Mode。说明UI自动化测试可能控制或重启目标应用，不能直接在用户工作的实例上照搬。
- 可借鉴处：优先使用`AutomationProperties.AutomationId`和语义控件，不依赖屏幕坐标；AutomationId是产品可访问性接口，应保持稳定并按真实控件命名。测试夹具使用单独实例，并在操作后断言状态，而不是只看命令是否返回。
- 当前决定：只采纳稳定目标和合成夹具的设计原则，不安装PowerToys测试框架、不驱动正在运行的PowerToys，也不将其CI交互测试当成后台安全证据。小K本轮隔离UIA探针正是用离屏合成窗口验证AutomationId、Name和IsEnabled，前台句柄不变、按钮动作计数为0。
- 来源：[固定提交的PowerToys UI测试指南](https://github.com/microsoft/PowerToys/blob/ccdd9b19ca980e9d383a42c235fa313a595ac712/doc/devdocs/development/ui-tests.md)、[固定提交的UI测试模式与陷阱](https://github.com/microsoft/PowerToys/blob/ccdd9b19ca980e9d383a42c235fa313a595ac712/.github/skills/ui-tests-migration/references/patterns-and-pitfalls.md)。以上描述来自仓库文档；没有复用其测试运行器或代码。

## Microsoft Agent Framework：检查点与人工交接思路

- 项目：[microsoft/agent-framework](https://github.com/microsoft/agent-framework)；官方 [.NET 检查点和人工交接示例](https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/03-workflows/Checkpoint/CheckpointWithHumanInTheLoop/Program.cs)演示在工作流步骤边界保存检查点、请求外部输入后再恢复。
- 可借鉴处：长任务在安全工具边界持久化进度，等待用户审批后恢复状态；恢复时重新核验任务会话、目标和授权，不重放不确定的副作用。
- 当前决定：只作为架构参考。小K继续使用本机 `ModelBroker`、确定性 `ToolBroker` 与 SQLite/隔离任务状态；不引入会把小K任务送往云模型的配置，不让通用代理框架覆盖固定权限规则。
- 引入前检查：确认稳定版和许可证；审查序列化状态中的个人数据、恢复边界、插件/工具调用链和传递依赖，并用本机固定模型验证离线行为。

## 微信与 QQ 通知监听候选：只借鉴来源适配边界

- [BSOD-MEMZ/QQListener](https://github.com/BSOD-MEMZ/QQListener) 标注 MIT，README 描述 WinSDK Toast 与 UIA 两种采集模式。其配置指南要求填写 QQ 号和“聊天信息保存文件夹”，但本次没有审计实现代码，不能据 README 断言它具体如何读取数据。因为这与小K禁止读取客户端数据库/聊天记录的边界存在明显疑点，暂不安装、运行或复用代码；如未来要复核，先逐文件审计通知模式的真实数据路径。
- [CueBeiXing/wechat-notify](https://github.com/CueBeiXing/wechat-notify) README 描述通过 UI Automation 读取聊天列表的 `ChatSessionCell`、群聊发送者和消息内容，并建议 Qt 微信无控件树时启用系统可访问性设置，某些情形需要管理员改注册表。该路径依赖实时聊天窗口控件，不等同于后台 Toast；也与本机既有 UIA 快照（微信仅8个结构元素、未见 Edit/Document）不吻合。仓库页面仅显示1次提交、0 Star；不安装、不读取真实聊天树、不改系统辅助功能注册表，也不将它作为通知解析证据。
- [RavelloH/WinToastRelay](https://github.com/RavelloH/WinToastRelay) 标注 MIT，README 使用 `UserNotificationListener`、启动时建立通知基线、按新增通知 ID 处理，并采用应用白名单。这个通用监听生命周期与小K现有实现相符，可作代码审查清单；它面向把通知转发至 webhook/第三方服务，违反小K推理及通知正文留在本机的要求，因此不接入其转发、凭据、持久队列或网络代码，也没有引入该项目依赖。
- 官方 [Windows 通知监听文档](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/notification-listener)将 `UserNotification`描述为来源应用、时间、ID和通知对象；通用 Toast 文本通过 `ToastGeneric`绑定读取。文档没有定义微信/QQ的会话ID、联系人稳定ID或私聊标记。客户端格式判断仍须以本机自然到达的真实通知为证，不能由第三方项目的功能宣称替代。

## UIInspect.MCP：UIA目标绑定与能力分层的低置信度参考

- 项目：[ChrisPulman/UIInspect.MCP](https://github.com/ChrisPulman/UIInspect.MCP/tree/810e67c2f96b44d8857ffea586e4d55b0113a35c)，本次固定检查提交 `810e67c2f96b44d8857ffea586e4d55b0113a35c`。仓库标注MIT，README描述为.NET 10、FlaUI/UIA3的MCP stdio服务，含有限控件树、目标引用、能力分层和确认/速率限制等设计。项目较新且社区使用量低，本次只检查仓库说明、文件清单和许可证，没有逐行审计实现、安装或本机复现。
- 可借鉴处：将操作目标绑定到精确进程/会话及受限控件引用；将只读能力和可写能力分开；限制树读取规模并在审计中隐藏敏感值。这些只能转化为小K内部 `ToolBroker` 的设计检查点。
- 当前决定：不安装、不接MCP，也不复用其自动化执行实现。仓库包含启动时自动安装Codex技能的逻辑，并提供 `--unrestricted` 运行选项以关闭同意/租约/速率限制；两者都不适合小K的无Codex连接器和固定授权边界。必须独立核对底层UIA调用的焦点、输入和第三方应用副作用，不能仅凭README的“后台”或“安全”描述认定不打扰。
- 来源：[固定提交仓库](https://github.com/ChrisPulman/UIInspect.MCP/tree/810e67c2f96b44d8857ffea586e4d55b0113a35c)、[该提交的MIT许可证](https://github.com/ChrisPulman/UIInspect.MCP/blob/810e67c2f96b44d8857ffea586e4d55b0113a35c/LICENSE)。以上项目特性来自维护者材料；未由小K独立验证。

## Gitleaks：隔离编程代理凭证识别规则参考

- 项目：[gitleaks/gitleaks](https://github.com/gitleaks/gitleaks)，本次参考固定发布 `v8.30.1`，官方配置包含大量按凭证提供方区分的规则；仓库采用MIT许可证。
- 可借鉴处：将凭证识别拆成有限时长、可测试的具体格式规则；已吸收少数高辨识度格式（例如AWS访问标识、GitHub/GitLab令牌、Google API Key、JWT和连接字符串）的思路，并对专用私钥块采取整文件拒绝进入模型上下文。
- 当前决定：不集成其二进制、完整规则库或扫描命令。小K只需要在代码上下文发送前做本地脱敏，不能把扫描结果或原始匹配内容写入日志；本地规则集远小于Gitleaks，因此只能作为纵深防御，不得宣称能发现所有密钥。规则需按脱敏合成样本和允许的模板占位符持续回归。
- 来源：[v8.30.1默认规则](https://github.com/gitleaks/gitleaks/blob/v8.30.1/config/gitleaks.toml)、[v8.30.1发布页](https://github.com/gitleaks/gitleaks/releases/tag/v8.30.1)、[MIT许可证](https://github.com/gitleaks/gitleaks/blob/v8.30.1/LICENSE)。只参考规则类别，未复制其完整配置或代码。

本轮只阅读公开仓库页面和官方文档，没有克隆、安装、运行这些候选，也没有读取本机通知正文、聊天窗口或真实项目凭证。可借鉴项限于独立来源适配、通知ID基线/队列、显式白名单、UIA能力分层和凭证格式测试；第三方采集、数据存储、网络转发、MCP运行面和前台自动化均不复用。

## 后续调研规则

每次补充候选时，记录项目官方仓库、固定版本、许可证、可复用模块、资源成本、隐私/网络路径、与小K现有接口的差异以及验证结果。优先采用可独立替换的小组件或测试工具；只有本机试验通过且没有削弱 `ToolBroker` 权限边界时，才进入正式实现。候选宣传、跑分和演示均不算小K的本机验收证据。
