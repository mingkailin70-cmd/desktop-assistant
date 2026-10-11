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

- 项目：[microsoft/winappCli](https://github.com/microsoft/WinAppCli)，本次核对的公开稳定发布为 `v0.7.1`，仓库许可证为 MIT；官方项目仍标记为 Public Preview，处于实验与积极开发阶段。
- 可借鉴处：官方 UI 自动化文档区分 UIA 模式调用和系统级输入。`inspect`、`search`、`get-value`、`wait-for`、`set-value`、`invoke` 等使用 UIA pattern；`send-keys`、鼠标手势等需要交互桌面且目标窗口前台。UIA 只读命令可以并发；会接触物理桌面的命令使用轮次仲裁。官方还展示以 `--on sandbox` 将应用和 UI 操作放进 Windows Sandbox。
- 与小K的关系：未来可用于**离线验收宿主**，先核对控件树、AutomationId、值读回和失败状态；桌面交互测试优先尝试 Sandbox。产品运行时仍只能经固定 `ToolBroker` 动作和 `InteractionCoordinator`，不能把 CLI 的任意 `ui` 命令直接开放给模型。
- 当前决定：不安装、不增加 NuGet 依赖，也不启用 Windows Sandbox 系统功能。CLI仍是Public Preview，Sandbox功能是否适合当前Windows版本、硬件和账户策略需要单独检查；未确认隔离前，任何实际输入测试都不得指向用户桌面。UIA pattern 方法也必须逐应用核实是否保持焦点和窗口状态。
- 引入前检查：锁定正式发布版本与二进制散列，审阅命令Schema及其输入注入/截图/窗口激活副作用，核对依赖许可证和遥测；先在小K自有合成窗口中验证无焦点变化，再考虑Sandbox中的安装版检查。

## Microsoft Agent Framework：检查点与人工交接思路

- 项目：[microsoft/agent-framework](https://github.com/microsoft/agent-framework)；官方 [.NET 检查点和人工交接示例](https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/03-workflows/Checkpoint/CheckpointWithHumanInTheLoop/Program.cs)演示在工作流步骤边界保存检查点、请求外部输入后再恢复。
- 可借鉴处：长任务在安全工具边界持久化进度，等待用户审批后恢复状态；恢复时重新核验任务会话、目标和授权，不重放不确定的副作用。
- 当前决定：只作为架构参考。小K继续使用本机 `ModelBroker`、确定性 `ToolBroker` 与 SQLite/隔离任务状态；不引入会把小K任务送往云模型的配置，不让通用代理框架覆盖固定权限规则。
- 引入前检查：确认稳定版和许可证；审查序列化状态中的个人数据、恢复边界、插件/工具调用链和传递依赖，并用本机固定模型验证离线行为。

## 后续调研规则

每次补充候选时，记录项目官方仓库、固定版本、许可证、可复用模块、资源成本、隐私/网络路径、与小K现有接口的差异以及验证结果。优先采用可独立替换的小组件或测试工具；只有本机试验通过且没有削弱 `ToolBroker` 权限边界时，才进入正式实现。候选宣传、跑分和演示均不算小K的本机验收证据。
