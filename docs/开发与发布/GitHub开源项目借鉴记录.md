# GitHub开源项目借鉴记录

## 2026-10-12 sherpa-onnx KWS许可复核与候选锁定

- 项目：[k2-fsa/sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx)，推理引擎采用Apache-2.0；本次候选为`sherpa-onnx-kws-zipformer-zh-en-3M-2025-12-20`，不是引擎代码本身。
- 许可证证据：维护者在[许可澄清讨论#3802](https://github.com/k2-fsa/sherpa-onnx/issues/3802)于2026-10-08回复已更新ModelScope模型仓库许可证；固定ModelScope revision `afc4a534ef85f2db4f84f05ed78eaac385b3387e`的模型卡README前置信息标为Apache License 2.0。模型卡没有说明训练数据来源，本项目仅在本机评估，暂不再分发权重。
- 固定的官方GitHub release资产为`sherpa-onnx-kws-zipformer-zh-en-3M-2025-12-20.tar.bz2`，大小32,885,699字节；GitHub Releases API返回SHA-256 `68447f4fbc67e70eee3a93961f36e81e98f47aef73ce7e7ca00885c6cd3616a6`，下载后本机散列匹配。解包成员均位于单一预期根目录，未发现绝对路径、盘符或`..`成员；24个文件逐项核验大小和SHA-256，锁文件登记为`sherpa-kws-zh-en`。
- 官方KWS文档列出中英自定义关键词生成流程；chunk-16使用约320ms块延迟，文档提醒chunk-8的延迟更低但通常精度较低，并给出INT8编码器/连接器与FP32解码器的组合。后续须按自定义唤醒短语评测，不能把上游示例或许可状态当成识别质量证据。
- 当前只下载并锁定隔离候选；未安装运行时、未加载KWS模型、未访问麦克风，也没有启用桌宠唤醒。CPU占用、识别质量、硬停麦与设备生命周期均待实机验收。
- 来源：[KWS预训练模型官方文档](https://github.com/k2-fsa/sherpa/blob/master/docs/source/onnx/kws/pretrained_models/index.rst)、[模型仓库](https://modelscope.cn/models/pkufool/icefall-kws-zipformer-zh-en-3M-2025-12-20)、[许可澄清讨论](https://github.com/k2-fsa/sherpa-onnx/issues/3802)。本轮只集成锁定的模型资产，不复制第三方代码。

本清单记录经过初步核对、可能帮助小K的公开项目。列入清单不等于采用；每次实际引入代码、依赖或素材前，仍要固定提交/版本，复查许可证、维护状态、传递依赖、联网行为和本项目安全边界。

## 2026-10-12 微软 UFO² Windows 桌面代理架构

- 项目：[microsoft/UFO](https://github.com/microsoft/UFO)，本次检查固定在`main`提交`a795552d976c4c019d7c2f778a0effb5cef7de6b`；仓库许可证为MIT。仓库当前README将UFO²称为稳定的单Windows设备路线，并介绍UIA、Win32、WinCOM、本机应用API和GUI动作混合方案，以及HostAgent协调器、按应用隔离的AppAgent、状态机和MCP动作服务器。
- 可借鉴到小K：对Excel/Word等具备稳定本机接口的应用，优先实现小范围、类型化的应用适配器；跨应用计划由Host编排，具体执行者按目标应用隔离；每一步返回可观察结果，再由ToolBroker独立核验。该方向与小K既有的固定工具注册、后台入口及确定性策略相容，不能绕开ToolBroker。
- 不直接采用的原因：UFO²的`select_application_window`明确把目标窗口设为前台并聚焦；其点击、输入、滚轮和截图命令也不能视为无打扰。UFO² README把Picture-in-Picture虚拟桌面列为“coming soon/开发中”，因此当前不能用它证明在用户继续工作时可并行自动化。项目安装路线依赖Python，默认方案还展示可配置云模型；这与小K .NET/WPF、主机本地推理和最小依赖要求不同。
- 结论：只采纳“原生应用API优先、执行器按应用隔离、状态机、动作回执和逐步核验”的设计线索。真实后台输入仍须针对每个应用独立证明不会改变焦点、鼠标、键盘或剪贴板；无法证明时应报告该动作当前不支持后台执行。未克隆、安装、运行或复制UFO代码，也未增加依赖。
- 来源：[UFO²项目说明](https://github.com/microsoft/UFO/blob/a795552d976c4c019d7c2f778a0effb5cef7de6b/ufo/README.md)、[UFO²架构说明](https://github.com/microsoft/UFO/blob/a795552d976c4c019d7c2f778a0effb5cef7de6b/documents/docs/ufo2/overview.md)、[HostUIExecutor前台聚焦行为](https://github.com/microsoft/UFO/blob/a795552d976c4c019d7c2f778a0effb5cef7de6b/documents/docs/mcp/servers/host_ui_executor.md)、[MIT许可证](https://github.com/microsoft/UFO/blob/a795552d976c4c019d7c2f778a0effb5cef7de6b/LICENSE)。

## 2026-10-12 桌宠缩放、DPI与三维渲染对照

| 项目与固定快照 | 公开资料核对 | 小K的取舍 |
| --- | --- | --- |
| [SixOfFive/DesktopPet](https://github.com/SixOfFive/DesktopPet/tree/4eeb333e1866f52163af1ea2f9c02d499141ddce)，`main`提交`4eeb333e1866f52163af1ea2f9c02d499141ddce` | README描述WinForms托盘与窗口、Silk.NET隐藏OpenGL上下文、离屏帧缓冲及`UpdateLayeredWindow`透明桌宠；角色可用GLB动画，贴图可替换。README当时列44个提交；Kenney角色素材注明CC0。该仓库的项目代码许可本轮未确认，素材许可不代表代码许可。 | 可作为“3D场景离屏渲染后合成透明窗口”的研究样例；不复制代码或素材，也不替换小K现有WPF/VPet Host。渲染循环、Win32分层窗口与WPF内容桥接会新增一套技术栈，尚无证据表明它比现有路线更省GPU或更稳定。 |
| [woooooooooolf/deskpet3d](https://github.com/woooooooooolf/deskpet3d/tree/503361c788b83b836d47ac7c0027423c2eb43fe8)，`main`提交`503361c788b83b836d47ac7c0027423c2eb43fe8` | README说明其为C99、raylib 6.0、cJSON与Win32项目，支持透明窗口和GLB/glTF动作配置；LICENSE文件为GPL-3.0。README还声明由Codex生成实现和工程文档。 | 只参考模型/动作资源清单与可配置状态的组织方式。GPL-3.0与本项目依赖边界需要另行评估，且整套C渲染宿主不适合直接塞进WPF Host；不接入、不复制代码。 |
| [ejwhf/DesktopPet-cc](https://github.com/ejwhf/DesktopPet-cc/tree/d3e77210e88ca355ad32d29977bd8237ef9ab6bb)，唯一提交`d3e77210e88ca355ad32d29977bd8237ef9ab6bb` | README描述.NET 8/WPF透明桌宠、40%–160%大小、托盘设置、配置备份和姿态清单；页面只显示1个提交，GitHub未检测到代码许可证。 | 缩放和配置持久化与小K已有`PetSizingPolicy`、外层Host窗口设置及`PetWindowPositionStore`重叠；不移植其实现。小K仍需真实桌面会话验收不同尺寸、重启、多屏与DPI。 |
| [SorrowGod/desktop-pai-zong](https://github.com/SorrowGod/desktop-pai-zong/tree/99e8710c831c7548c1efc12749bd7f56b79c85be)，`main`提交`99e8710c831c7548c1efc12749bd7f56b79c85be` | README描述.NET 8/WPF、Per-Monitor V2、多屏/负坐标、按透明像素划分鼠标命中区和不激活的桌宠窗口；页面列16个提交。仓库元数据未检测到代码许可证；README还介绍远程兼容API设置。 | 仅把DPI、鼠标命中区域和窗口不激活列为小K实机验收检查点。不复用代码；远程模型路线不符合小K只在本机推理的要求。 |

**本轮选择：** 小K已有固定.NET 10/WPF Host、VPet Core适配器、自制银渐层姿态PNG、40%–160%手动缩放和缩放/位置保存实现。上述项目没有提供可以不改架构直接嵌入、许可清晰且实机证明适合小K的三维组件；因此保持当前Host，继续使用自有3D渲染风格PNG驱动姿态，并把连续3D建模/动画作为独立原型候选，而不是替换助手核心。代码中缩放存在不等于界面验收通过；真实窗口合成、40/70/100/160%、重启、多屏/DPI、位置恢复、睡眠恢复和资源占用仍未验收。此次只读各项目公开README/许可证和GitHub提交元数据；没有克隆、安装、运行、复制代码或改动小K运行时依赖。

## 2026-10-12 复核 Trope CUA 与 Agent Desktop Harness

- [tropeai/trope-cua](https://github.com/tropeai/trope-cua)（MIT）针对 Windows/macOS 桌面代理。其[安全约定](https://github.com/tropeai/trope-cua/blob/main/SAFETY.md)区分工具成功与后台安全：只有动作回执同时证明 `background_safe=true`、`cursor_moved=false`、`foreground_changed=false`，才把动作视为无打扰；[已知限制](https://github.com/tropeai/trope-cua/blob/main/docs/limits.md)说明部分 UIA 提供器、Chromium、游戏/画布界面仍会激活窗口或只能走隔离会话。可借鉴为小K未来的真实动作回执与“安全路由不可用即停止”准则；源码实现和实机表现未审计，本次未集成。
- [xuyw1997/agent-desktop-harness](https://github.com/xuyw1997/agent-desktop-harness)（Apache-2.0，仓库自述仍为 Alpha）强调把桌面探索转为可读的 Robot Framework 流程，并在执行前验证脚本、输入和断言；这可作为未来重复流程及失败证据的参考。其安全说明明确 Python 执行环境是完整信任环境，不是沙箱；桌面权限范围也比小K首版宽。本次只审阅README和安全文件，不运行、不集成。
- 两者都未证明能在微信/QQ登录会话中安全后台发送消息。小K继续遵循自己的精确联系人白名单、发送前逐条确认、结果不确定不重试；同一登录桌面中的后台安全仍须按应用逐项实机验证，不把上游项目自述当成本机证据。

## 2026-10-12 采用微软 Roslyn 语法树

- 上游：[dotnet/roslyn](https://github.com/dotnet/roslyn)，MIT；锁定NuGet包`Microsoft.CodeAnalysis.CSharp 5.9.0`及传递依赖，源提交`35d9211b841e7613c1d2f8f5af6d628ace696c4c`。微软说明语法树按C#语法生成结构节点，声明可从语法类型读取；Roslyn包页面列有精确版本与MIT许可。
- 小K用法：把已脱敏C#快照解析为本地语法树，只抽取命名空间、类型、方法和局部函数声明名。每文件解析不超过128 Ki字符，每任务累计不超过2 Mi字符；每文件最多5个摘要符号，清单最多12,000字符，模型仍最多选4条精确路径。
- 不执行或编译源码，不使用Roslyn语义模型，不扩大文件访问、写入或命令权限。整包增加约5.6 MB。没有复制Roslyn实现代码；发布包包含MIT告知，NuGet来源映射和锁文件固定版本及内容散列。
- 效果仍待用完整40题任务集评估；0.1.113.0 Release构建通过并已签名安装，不代表代码任务质量门槛通过。
- 来源：[Roslyn C#语法树指南](https://github.com/dotnet/roslyn/blob/35d9211b841e7613c1d2f8f5af6d628ace696c4c/docs/wiki/Getting-Started-C%23-Syntax-Analysis.md)、[NuGet 5.9.0包与依赖](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/5.9.0)、[固定源码许可证](https://github.com/dotnet/roslyn/blob/35d9211b841e7613c1d2f8f5af6d628ace696c4c/License.txt)。

## 2026-10-12 复核 Ghost 与 Rynne 桌面代理

- [NORTHTEKDevs/ghost](https://github.com/NORTHTEKDevs/ghost)的README声称它默认后台控制，且自己启动的应用可放在隐藏桌面；同时公开了剪贴板、Shell、全局键鼠等广泛工具。README也承认`SendInput`不能在非输入桌面工作，部分Chromium UIA调用会激活窗口，焦点归还只能缩短干扰而不是绝对保证。只把“隔离运行由小K启动的应用实例”作为未来研究方向，不接入Ghost服务器或复制其代码；用户已经打开、带账号状态的客户端不能据此认定可安全后台操控。
- [KremlevLev/Rynne](https://github.com/KremlevLev/Rynne)的README展示后台计划检查点、进程监控和文件回滚思路，也列出Groq/OpenRouter/Gemini并行代理、终端、MCP与持久化浏览器等能力。本次只读README，未审查其源代码和许可证；不引入项目，也不将其云模型/宽工具架构作为小K方案。其“逐步核验并保存检查点”可继续作为小K任务中心路线参考。
- 两个项目自述功能均未由小K实测；候选宣传不作本机能力证据。

## 2026-10-12 GitHub参考：Aider 仓库地图

- 项目：[Aider-AI/aider](https://github.com/Aider-AI/aider)，Apache-2.0。官方仓库地图文档说明，它把仓库文件及关键类、函数和签名压缩到有限上下文，并用依赖图排序挑选适合当前 token 预算的内容。
- 小K采用的子集：编程代理给模型选择文件前，从已脱敏的 C# 快照中本地提取命名空间、类型和方法声明名；按用户任务词与路径/符号的重合度优先分配摘要，每个文件最多给5个符号，整个清单仍严格不超过12,000字符。模型仍只可从清单精确选择最多4个路径，工具权限、写入审批和隔离工作区不变。
- 小K没有引入Aider代码、Tree-sitter或依赖图；摘要由限时正则提取，属于轻量导航线索，不等于完整语法分析。该变化尚未用40题编码集复测，因此不能声称提高了任务通过率。
- 来源：[官方仓库地图说明](https://github.com/Aider-AI/aider/blob/main/aider/website/docs/repomap.md)、[许可证](https://github.com/Aider-AI/aider/blob/main/LICENSE)。只阅读公开文档，没有克隆、运行或复制代码。

## 2026-10-12 GitHub评估：µagent

- 项目：[majiang213/mu-agent](https://github.com/majiang213/mu-agent)，ISC许可。README描述其面向本地7B/8B模型，采用按状态限制可用工具和编辑检查点等方法；页面当时显示1个Star、0个Fork，按低采用度参考处理。
- 不集成其CLI：项目同时提供Shell命令、Git及可选联网研究等宽能力，不符合小K唯一ToolBroker入口、固定工具白名单和无云端推理边界。分状态约束与检查点理念已由小K自己的阶段式模型调用、固定ToolBroker和隔离补丁审阅覆盖，没有复制实现。
- 来源：[官方仓库](https://github.com/majiang213/mu-agent)、[README安全边界](https://github.com/majiang213/mu-agent#additional-harness-level-guardrails)、[ISC许可证](https://github.com/majiang213/mu-agent/blob/main/LICENSE)。只读公开资料，没有克隆、运行或集成。

## 2026-10-12 GitHub参考：Microsoft Playwright MCP

- 项目：[microsoft/playwright-mcp](https://github.com/microsoft/playwright-mcp)，微软维护的Playwright浏览器自动化MCP服务；仓库显示Apache-2.0许可证。README说明它用结构化可访问性快照定位网页对象，不依赖截图坐标，并明确提示MCP服务本身不是安全边界。
- 小K采用的设计思路：继续使用现有锁定的Microsoft.Playwright程序集，以用户明确命令、当前快照令牌、ARIA角色与精确名称绑定单次动作；新增只读解析href、快照后结构指纹校验和受限同源导航，让模型/网页内容仍无权自行发起动作，页面变化时停止而不猜测目标。
- 小K没有安装或集成Playwright MCP、Node服务或其工具集合；此项目能力面包含更广泛的点击、拖拽、下载和其他浏览器操作，不符合小K固定ToolBroker、匿名网络和逐工具授权边界。小K独立实现的同源检查、重定向阻断、DNS公网验证、无Cookie GET和会话过期策略仍由本项目负责。
- 来源：[官方仓库与许可证入口](https://github.com/microsoft/playwright-mcp)、[README中的可访问性快照说明及安全边界声明](https://github.com/microsoft/playwright-mcp#playwright-mcp)。只阅读公开资料，没有克隆、运行或复制其代码。
- 落地状态：R3.3功能提交`ae655d4`及快照指纹修复`2ce8ef4`已推送；GitHub Actions Build #486与#488成功。0.1.111.0签名MSIX已在当前账户安装，实际浏览器专项、安装版交互和真实网站验收仍未完成。

## 2026-10-12 GitHub采用记录：微软 Open XML SDK

- 项目：[dotnet/Open-XML-SDK](https://github.com/dotnet/Open-XML-SDK)，官方最新稳定发行版为 `v3.5.1`，固定上游提交 `3139fdfd27414548a41555f7848d5728f6e71a42`；NuGet 包为 `DocumentFormat.OpenXml 3.5.1`，MIT 许可。
- 参考方式：采用其强类型 SpreadsheetML 包结构来创建 `WorkbookPart`、`WorksheetPart`、`SheetData` 和工作表，并用SDK校验生成的Open XML。版本、传递依赖和内容散列锁定在Windows适配器的 `packages.lock.json`；发布包包含MIT第三方告知。
- 小K实现范围：只创建一个名为“数据”的无宏工作表；所有单元格按内联文本写入，保留前导零，不生成公式；最多32列、501行（含表头）、16,000字符/64 KiB。工作簿先在内存生成和验证，再走固定Exports目录、临时文件、不覆盖发布及SHA-256核验流程。不启动Excel，不借用第三方MCP或自动化工具。
- 来源：[v3.5.1发行页](https://github.com/dotnet/Open-XML-SDK/releases/tag/v3.5.1)、[固定版本许可证](https://github.com/dotnet/Open-XML-SDK/blob/v3.5.1/LICENSE)、[微软创建工作簿文档](https://learn.microsoft.com/en-us/office/open-xml/spreadsheet/how-to-create-a-spreadsheet-document-by-providing-a-file-name)。SDK生成方式来自微软文档；验收结果只依据小K自己的自动化安全检查，不能据此声称Excel界面已实机打开验收。

### 同一SDK扩展为纯文本Word文档生成

- 项目仍为微软维护的[dotnet/Open-XML-SDK](https://github.com/dotnet/Open-XML-SDK)，沿用已固定的3.5.1 MIT包，不增加依赖。
- 采用`WordprocessingDocument`与强类型WordprocessingML的Document、Body、Paragraph、Run和Text结构。小K在内存生成最小普通`.docx`，正文按行形成段落，校验包结构，再使用既有安全发布流水线。
- 不读取模板，不接受`.docm`，不添加宏、图像、外部关系、嵌入包或资源；不启动Word，也不操作桌面输入。
- 上游参考：[Open-XML-SDK WordprocessingDocument API](https://github.com/dotnet/Open-XML-SDK/blob/v3.5.1/src/DocumentFormat.OpenXml/Packaging/WordprocessingDocument.cs)、[OfficeDev创建Word文档并添加正文](https://github.com/OfficeDev/open-xml-docs/blob/main/docs/word/how-to-open-and-add-text-to-a-word-processing-document.md)。检查来源时未克隆或执行第三方项目；具体安全性由小K自身合成检查证明。

### 同一SDK扩展为纯文本PowerPoint生成

- 沿用已经锁定并用于XLSX/DOCX的`DocumentFormat.OpenXml 3.5.1`（MIT），没有新增依赖或复制第三方代码。
- 采用PresentationML的演示文稿部件、母版、空白版式、主题和幻灯片结构，使用固定形状写入标题及要点；整份文档先在内存生成并由`OpenXmlValidator`验证，再走小K既有的固定导出目录安全发布与散列核验流程。
- 小K只接受用户明确给出的纯文本内容，不加载模板，不生成图片/图表/宏/附件/嵌入内容或外部关系；不启动PowerPoint、不操作桌面输入。
- 上游参考：[微软创建演示文稿文档说明](https://learn.microsoft.com/en-us/office/open-xml/presentation/how-to-create-a-presentation-document-by-providing-a-file-name)、[Open-XML-SDK PowerPoint示例](https://github.com/dotnet/Open-XML-SDK/blob/v3.5.1/samples/common/PowerPointUtils.cs)。上游资料用于核对Open XML部件组织；功能安全和结构验收由小K自身检查负责，不能据此声称PowerPoint界面已实际打开验收。

## 2026-10-12 GitHub继续筛选：UIA结果反馈与隔离桌面测试

本轮只阅读公开README、许可证页和微软项目文档，没有克隆、下载、运行或集成候选。

| 项目 | 可借鉴点 | 与小K的差距及决定 |
| --- | --- | --- |
| [ricfanin/netwright](https://github.com/ricfanin/netwright) | 仓库标注MIT，面向WPF、WinForms和WinUI；README描述了按控件执行UIA Pattern、等待目标可用/可见并等界面稳定、返回前后变化差异，以及把验证流程导出成回归测试。 | 同一README也说明键盘按键和右键会短暂取得焦点，并公开了窗口、截图、剪贴板、文件、系统等较宽工具面。只参考动作后的可观测结果、等待状态与测试导出；不接入其MCP工具，不对真实第三方窗口调用按键或扩展权限。 |
| [Guojiz/FastCUA](https://github.com/Guojiz/FastCUA) | 仓库标注MIT；README主张UIA优先、语义不足时用截图/网格回退，并提供本地暂停/审批控制。 | README把项目标为实验性，并说明默认配置是Full access；一个常驻服务共享真实鼠标指针，支持键盘/鼠标输入，且本地操作历史可含截图。和小K“不能抢用户输入、只允许固定目标和最小工具”的要求冲突，不安装或集成；单独暂停入口的想法已由小K托盘控制覆盖。 |
| [Microsoft winappCli：UI Automation](https://github.com/microsoft/winappCli/blob/main/docs/ui-automation.md) 与 [Windows Sandbox执行说明](https://github.com/microsoft/winappCli/blob/main/docs/sandbox-execution.md) | 微软文档提供明确的Sandbox执行目标，可将自有测试应用部署到独立来宾环境并执行UIA检查；这可作为以后隔离WPF工具回归的备选环境。 | Sandbox准备/冷连接或重连可能短暂取得焦点；建置、还原和编译仍在宿主机，沙箱内应用也无法替代真实桌面客户端。只考虑用于隔离测试，不作为小K运行时或微信/QQ真实操作证据；启用Windows功能和重启前需单独确认是否符合本机条件。 |

**本轮结论：** 小K后续可在ToolBroker允许的窄动作里统一返回“目标身份、动作结果、发生变化的字段、核验状态”，并在合成WPF窗口验证。UIA无法唯一定位、目标变化或前后状态不明时应停止并说明；不能自动降级为真实鼠标键盘、截图或剪贴板动作。Sandbox若进入开发环境选择，再先核对Windows版本、虚拟化和宿主机工作影响。

## 2026-10-12 GitHub持续筛选：桌面动作与宠物复用

以下是继续筛查得到的候选；本次只核对公开README、仓库页面和许可证线索，没有克隆、运行或加入依赖。

| 项目 | 可借鉴内容 | 评估与决定 |
| --- | --- | --- |
| [mkusaka/pbw](https://github.com/mkusaka/pbw) | Windows原生.NET桌面自动化，公开命令支持`auto`、`background`和`foreground`派发；仓库标注MIT。 | 支持目标为交互式Windows桌面。后台派发是项目提供的能力选项，不足以证明所有应用/provider不抢焦点或不与用户同时编辑安全；项目当前体量与独立复现证据也有限。仅作为后续测试矩阵和派发契约参考，不直接接入。 |
| [opensymph/open-computer-use](https://github.com/opensymph/open-computer-use) | 本地MCP服务器；Windows说明采用UI Automation与Win32，公开`background-first`操作路线，并提供显式环境变量门控前台输入/全局指针回退；仓库标注MIT。 | 具有较宽的桌面读写工具面和平台后端。环境变量门控可借鉴为显式能力开关，但第三方宣称的后台行为须逐应用验证；不作为ToolBroker替代、不安装MCP、不启用键鼠/全局指针回退。 |
| [LorisYounger/VPet](https://github.com/LorisYounger/VPet) | 可将VPet Core嵌入WPF应用，提供桌宠动画和控制接口。 | 小K已有VPet Core集成路线，继续只复用核心能力和小K自有姿态资源。上游README另列动画素材、署名/链接及商业使用限制；不得将上游素材视为可任意再分发资源，打包内容须逐项核对许可。 |

当前实现继续保留小K自己的ToolBroker桌面仲裁、目标校验与验证式工具契约；后台能力必须由小K实测证明，不因候选项目的README表述而自动放宽“不抢鼠标、键盘、焦点或剪贴板”边界。

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

## 2026-10-12 GitHub 桌面自动化候选复核

这次复核补充了几个可借鉴的方向。仓库页面和README是候选线索，不代表小K已复现其功能；没有克隆或运行候选。

| 项目 | 可借鉴点 | 与小K的差距及决定 |
| --- | --- | --- |
| [FlaUI/FlaUI](https://github.com/FlaUI/FlaUI) | 维护时间较长的.NET UIA2/UIA3封装，可用于独立UI测试宿主和测试夹具。仓库自述以MIT许可发布。 | 官方定位是Windows UI自动化测试库；封装不会自动获得“不改变焦点或不干扰用户”的保证。小K已有Windows UIA基础调用，暂不为包装层增加生产依赖；若后续测试需要更广的Provider兼容，再固定NuGet版本和散列评估。 |
| [wosoolsoft/WinProbe](https://github.com/wosoolsoft/WinProbe) | README提出分层Core/WPF/Recording/MCP、验证式动作和录制回放，可供测试结构参考。 | README声称44个MCP工具和后台操作，但当前仓库页面显示1颗Star，不能据此判定成熟或安全；不接入其广泛执行工具。 |
| [ibraheem-mustafa-dev/windowsagent](https://github.com/ibraheem-mustafa-dev/windowsagent) | UIA可访问性树可作为语义观察路线；README还展示了动作后独立验证。 | 项目同时包含输入动作、视觉回退、HTTP服务、进程启动和Shell工具；授权模型、后台焦点保证及第三方Provider副作用未按小K要求审计，不接入。 |
| [xuyw1997/agent-desktop-harness](https://github.com/xuyw1997/agent-desktop-harness) | 先把探索固化为可读工作流、执行前验证、固定步骤和失败证据，可借鉴于小K的离线评测及未来通用任务流程。 | 项目定位为可见桌面自动化/RPA，使用截图和原生输入；GitHub显示6颗Star，仓库说明仍处于0.x阶段，演示成绩为维护者自述。不能用于小K后台并发操作。 |
| [lessenings-prog/OpenGUI](https://github.com/lessenings-prog/OpenGUI) | 命名管道拆分客户端与Windows常驻服务，动作前后状态核对、崩溃恢复日志可作为IPC与诊断设计线索。 | README明确含`SendInput`键鼠注入、自动登录启动和运行日志；会影响用户当前桌面，且不能作为无干扰后台执行实现。 |

**本次结论：** 这些项目没有提供可直接复用且已证明适合小K的“任意应用后台操作而不干扰用户”的现成模块。下一步优先扩展小K自己的固定动作接口：先用合成窗口/隔离测试验证目标、能力、取消、超时与结果核验；每款真实应用单独验证Provider和并发风险。需要全局鼠标键盘、截图或前台焦点的能力继续明确标为前台操作，不能包装成后台能力。以上GitHub仓库均未固定到提交，后续若考虑复制代码或依赖，先核对精确提交、许可证、维护状态、传递依赖和数据/网络路径。

## FlaUI：Windows UIA 验收参考

- 项目：[FlaUI/FlaUI](https://github.com/FlaUI/FlaUI)，官方仓库说明它基于 Windows UI Automation，覆盖 WPF、Win32、WinForms 和 Store 应用；官方发布页列有 v5.0.0。
- 可借鉴处：将 UI Automation 用于**测试宿主和隔离夹具**，验证窗口/控件是否可发现、辅助功能名称是否稳定、状态变化是否符合预期。
- 当前决定：不将 FlaUI 放入小K正式运行依赖，也不让产品运行时通过 UIA 注入鼠标/键盘或抢前台。需要实机 UIA 验收时，先对小K自有测试窗口做只读探测；操作真实窗口、读取聊天内容、保存截图都必须另行受控。
- 引入前检查：核对所选 FlaUI 版本支持的目标框架和许可证；固定 NuGet 版本/散列，并确保验收运行不驱动用户正在使用的应用。

## Microsoft winapp CLI：后台 UIA 与隔离验收候选

- 项目：[microsoft/winappCli](https://github.com/microsoft/WinAppCli)，本次核对公开稳定发布 `v0.7.1`（2026-10-01，提交`e3cd0b94f3e7b776ac51ba0564f4908094e979b5`）；仓库为MIT，但官方仍标记Public Preview。该版本文档固定在[UI Automation v0.7.1](https://github.com/microsoft/winappCli/blob/v0.7.1/docs/ui-automation.md)。不能把可变的`main`文档或未发布功能自动当作此稳定版本能力。
- 可借鉴处：它把桌面任务按并发冲突分三档：`inspect/search/get-value/wait-for`等只读查询可以并发；`set-value`与UIA滚动不注入物理输入、不取得桌面焦点，但因会改变目标应用状态而在自动化工作流间串行；`invoke`、截图、焦点、键鼠/触控注入等按桌面独占轮次执行。截图可能恢复最小化窗口或把目标拉到前台；Sandbox启动/重连也可能短暂切换焦点。该分类比“是否调用UIA API”更能描述干扰风险。工具还要求唯一/消歧后的控件选择，对模糊选择失败关闭，并在控件变化后重新发现目标。
- 与小K的关系：小K应借鉴三档交互能力、目标唯一性、过期目标重定位和独立结果核验；ToolBroker仍是唯一动作入口。winapp CLI的桌面仲裁只覆盖共同使用该CLI的进程，文档明确直接调用UI Automation NuGet库不在其仲裁保证内；因此小K若直接用FlaUI或Windows UIA API，必须由小K自己的协调器串行仲裁，不能因“后台UIA”就宣称不会打断用户。后台值写入虽不抢焦点，仍可能和用户同时编辑同一个应用冲突。
- 当前决定：不安装CLI、不增加其NuGet依赖、不启用Windows Sandbox，也未对真实应用发送输入。它是公共预览，官方README提醒`main`与稳定发布行为可能不同；其宽泛工具面和命令协调不直接接入小K。小K现已在Core实现并由ToolBroker持有自己的仲裁租约：只读UIA查询按目标共享、目标状态变更按目标独占并要求活动探针、物理桌面操作按全局独占串行，后台不能取得桌面租约。Windows前台占用探针已接入Host，只比对固定应用ID对应的可执行映像路径。现新增显式用户请求`app.ui.snapshot.v1`：只映射本机已登记应用和唯一可见窗口，进程身份由Host与工作进程双重核验；查询在5秒限时的单次子进程内执行，Windows Job Object限制进程内存256 MiB并在所有者句柄关闭时终止；结果只返回有限语义控件属性并留在当前任务内存。小K自有合成窗经该隔离子进程读取后AutomationId/Name/启用状态正确，前台HWND不变、控件未触发；无响应子进程故障注入确认取消、5秒超时和所有者进程强制退出时工作进程均退出。没有读取真实第三方应用，也没有新增UIA依赖。当前安全检查122项、桌面体验检查86项通过，Release解决方案构建0警告/0错误。故障注入未模拟真实UIA Provider挂起；真实应用Provider、MSIX下的子进程运行、锁屏和用户并发切换仍待验收。本机合成窗通过不证明第三方应用都能无扰读取。下一步继续对照固定应用配置和实际客户端Provider验证，绝不自动启动监控或读取真实聊天内容。
- 来源：[v0.7.1 UI Automation 文档](https://github.com/microsoft/winappCli/blob/v0.7.1/docs/ui-automation.md)、[v0.7.1 Release](https://github.com/microsoft/winappCli/releases/tag/v0.7.1)、[MIT许可证](https://github.com/microsoft/winappCli/blob/v0.7.1/LICENSE)、[官方README的Public Preview说明](https://github.com/microsoft/winappCli/blob/main/README.md)、Microsoft Learn [`AutomationElement.FromHandle`](https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.automationelement.fromhandle?view=windowsdesktop-10.0)与[WPF UI Automation Peer](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/controls/ui-automation-of-a-wpf-custom-control)。文档中的“不取焦点”属于CLI对其特定命令路径的描述，不代表小K直接调用同类UIA pattern已通过本机无干扰验收。

### 2026-10-12：唯一控件状态等待的只读实现

当前可变`main`文档说明唯一`AutomationId`可用于稳定目标定位，`wait-for`可按属性/值条件等待；UIA只读查询和`wait-for`是只读命令。小K据此增加独立工具`app.ui.wait-enabled.v1`：只允许用户显式提供已登记应用与AutomationId，最多等待2.5秒，目标须在有限遍历内唯一且当前启用；歧义、树上限、目标变化和超时均失败关闭。它只读`IsEnabled`，没有复用该CLI、NuGet库、截图或写操作；离屏合成窗口验证了延迟启用成功、持续禁用超时和重复AutomationId拒绝，前台HWND保持不变。此参考仅支持“查询后置条件”的设计，不证明第三方Provider或任何后台写操作安全。

来源：[winapp CLI 当前 UI Automation 文档](https://github.com/microsoft/winappCli/blob/main/docs/ui-automation.md)。官方文档展示了用属性/值条件等待控件状态的方式；此页面指向可变`main`，本次只借鉴只读断言概念，不引入CLI或依赖。

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

## 2026-10-12 UFO²、Ghost与VPet Core复核

本次继续从GitHub官方仓库核对桌面Agent与桌宠实现。这里只记录公开文档中的设计线索；没有克隆、安装或运行第三方代码，也没有将其宣传描述视为本机实测。

| 项目 | 可借鉴点 | 与小K的差距及决定 |
| --- | --- | --- |
| [Microsoft UFO²/UFO³](https://github.com/microsoft/UFO) | UFO²把Windows主协调与应用专属执行器分层；UIA、Win32/WinCOM和应用接口可按应用选择。UFO³又增加跨设备DAG调度。官方仓库标注MIT。 | 小K仍是单机本地应用，现有`ToolBroker`负责唯一副作用入口；不引入Python多Agent框架或跨设备调度。UFO文档的示例配置涉及云API密钥，工具面还包含截图、文件和应用操作；只参考应用适配器分层及运行状态编排，所有动作继续服从小K权限策略和本地推理要求。 |
| [NORTHTEKDevs/Ghost](https://github.com/NORTHTEKDevs/ghost) | README提出锁定后台焦点策略、目标窗口锚定、动作后读回验证，并提供本机自检命令。可继续验证“无法后台执行就明确失败”和每步独立核验的设计。仓库标注MIT。 | README还描述shell、浏览器DevTools、剪贴板、全局输入及MCP工具；文中无干扰率和背景操作效果是项目维护者自述，尚非小K或独立实测。宽泛权限面不符合直接集成条件；不接入其服务或代码，只把焦点保持/结果核验纳入本机合成用例候选。 |
| [LorisYounger/VPet](https://github.com/LorisYounger/VPet) | 官方README说明其`VPet-Simulator.Core`可嵌入WPF，并分出图形核心、动画加载与显示组件；这与当前小K已接入的VPet Core相符。 | 仓库代码标示Apache-2.0，但默认动画和图片有独立授权要求：非商用分发须注明来源并给出仓库链接；商用还涉及首次及可访问页面告知、不可出售动画，且README要求联系作者。小K使用自有猫咪姿态图，不复制默认动画或Zip图库；发版前核验实际包内容及第三方素材清单。 |

**结论：** 当前新增的可执行设计原则是：为每一步声明目标和允许能力，固定并核验目标，再执行并读回结果；任何能力若可能改变前台焦点、全局输入或桌宠素材授权未核清，就不把它标为“后台安全”。未新增依赖或修改权限边界。

来源：[UFO²架构说明](https://github.com/microsoft/UFO/blob/main/documents/docs/ufo2/overview.md)、[UFO官方快速开始](https://github.com/microsoft/UFO/blob/main/documents/docs/getting_started/quick_start_ufo2.md)、[UFO许可证](https://github.com/microsoft/UFO/blob/main/LICENSE)、[Ghost README](https://github.com/NORTHTEKDevs/ghost)、[Ghost许可证](https://github.com/NORTHTEKDevs/ghost/blob/main/LICENSE)、[VPet README及动画授权](https://github.com/LorisYounger/VPet/blob/main/README.md)。页面指向的`main`分支可变；这些条目用于设计参考，若后续考虑引入代码或依赖，需另行固定提交并复核许可证、依赖、网络和本机行为。

本轮只阅读公开仓库页面和官方文档，没有克隆、安装、运行这些候选，也没有读取本机通知正文、聊天窗口或真实项目凭证。可借鉴项限于独立来源适配、通知ID基线/队列、显式白名单、UIA能力分层和凭证格式测试；第三方采集、数据存储、网络转发、MCP运行面和前台自动化均不复用。

## 2026-10-12 通知与后台UIA项目复核

### WeChatNotifier：私聊分类路线不符合本项目边界

- 项目：[qixinmin1994/WeChatNotifier](https://github.com/qixinmin1994/WeChatNotifier)，仓库README标注MIT，页面显示项目规模很小；本次只读README与安全说明，未克隆、安装或运行。
- 可借鉴处：暂停/恢复时重建未读数基线，避免把暂停期间累积的旧消息当成新通知。这个状态处理思路可用于小K自己的Toast队列回归用例。
- 不采用的原因：README明确说明其从微信本地会话/联系人数据库读取元数据，经`wechatauto-replica`识别私聊、群聊和免打扰群；还注明上游库可能读取微信进程内存以处理加密数据库。它依赖客户端内部数据结构，与小K“不读客户端数据库、不读进程内存”的固定边界冲突；其测试微信版本也早于本机4.1.15.13。其“本地不上传”声明不改变这项来源限制。

### FlaUI-MCP：语义选择器可参考，宽工具面不接入

- 项目：[shanselman/FlaUI-MCP](https://github.com/shanselman/FlaUI-MCP)，本次只读README，未核定固定release或完整传递依赖，未克隆、安装或运行。
- 可借鉴处：用语义控件引用代替坐标、在目标变化后重新解析控件；测试夹具按独立窗口运行。小K当前只读UIA快照和唯一`AutomationId`等待继续采用有限语义目标。
- 限制与决定：README列出的工具还包括焦点切换、键盘输入、截图、关闭窗口、批量动作；所谓背景截图在空帧时回退到常规截图，超时只表示MCP请求结束，不保证挂起的UIA Provider已停止。它提供的是通用自动化服务，并不等于不打扰用户的后台保证；不集成MCP服务或其宽权限工具。

### Microsoft winapp CLI：协调器范围不能扩大解释

固定版[UI Automation文档](https://github.com/microsoft/winappCli/blob/v0.7.1/docs/ui-automation.md)说明其`set-value`命令以UIA Pattern执行并按目标状态变化做工作流仲裁；CLI对其实现声称不会取得桌面焦点。这个保证不自动适用于小K直接调用的Windows UI Automation API。微软文档指出UIA Pattern默认会自动设焦点，客户端需显式把`IUIAutomation2.AutoSetFocus`关掉；本机使用的WPF `System.Windows.Automation`公开封装没有该属性，不能把其它库中的同名API当成可用接口。因此小K继续限定为只读控件检查与状态等待，不实现后台输入，除非后续通过受支持的COM接口证明能关闭自动设焦点并完成独立实测。

**本轮结论：** GitHub上存在可复用的机制，但没有找到同时满足“不读微信客户端数据、不抢用户焦点、不假称后台安全”的微信/QQ通用操作模块。没有新增依赖，也没有修改通知权限策略。下一步仍是：基于自然到达、来源可确认的Toast样本验证私聊字段；后台操作逐应用测试，不能从通用演示推断兼容性。

来源：[WeChatNotifier README与隐私说明](https://github.com/qixinmin1994/WeChatNotifier)、[FlaUI-MCP README](https://github.com/shanselman/FlaUI-MCP/blob/main/README.md)、[Microsoft winapp CLI UIA文档](https://github.com/microsoft/winappCli/blob/main/docs/ui-automation.md)。这些公开页面可能变化；未固定并审核代码提交，因此本次仅作设计研究，不用于复制代码或添加依赖。

## 后续调研规则

每次补充候选时，记录项目官方仓库、固定版本、许可证、可复用模块、资源成本、隐私/网络路径、与小K现有接口的差异以及验证结果。优先采用可独立替换的小组件或测试工具；只有本机试验通过且没有削弱 `ToolBroker` 权限边界时，才进入正式实现。候选宣传、跑分和演示均不算小K的本机验收证据。

## 2026-10-12 长任务状态与取消体验复核

| 项目 | 本次核对内容 | 对小K的取舍 |
| --- | --- | --- |
| [shoelace66/AutoPi](https://github.com/shoelace66/AutoPi) | README列出后台任务ID、进程ID、退出码、时间戳和取消状态；宿主关闭后会显示恢复信息而不自动重跑。其代码执行和日志能力较宽。 | 参考任务中心明确取消/恢复状态；小K继续不持久化用户请求正文、进程输出和聊天内容。 |
| [KremlevLev/Rynne](https://github.com/KremlevLev/Rynne) | README描述多步后台计划在每个已核验步骤后保存检查点，并在重启后继续时避免重复副作用；这是项目方公开说明，未由小K复现。 | 小K目前把跨进程活动任务标为“结果待核对”，不会自动重放。仅在未来能够证明某类步骤幂等、目标重新核验且不跨越审批边界时，才讨论恢复计划；一般桌面操作保持人工检查。 |
| [NORTHTEKDevs/Ghost](https://github.com/NORTHTEKDevs/ghost) | README把后台焦点策略锁在操作者设定值，缺少后台路径时显式报错；其无干扰数据和隐藏桌面实现是项目自述，不作为小K效果证明。 | 小K继续由`ToolInteractionPolicy`区分后台/前台，并在后台入口拒绝需要前台的动作。此次任务中心改动进一步把运行任务的取消请求显示成独立的“正在停止”状态，避免按钮重启后仍像未响应一样可重复点击。 |

本次仅阅读上述GitHub仓库公开README，没有克隆、安装、运行或复制代码；未新增依赖。AutoPi仓库README保留MIT说明；Rynne与Ghost的许可证和依赖不是本次核验范围，不能据此引入其代码。当前任务取消状态是小K自身实现，需经仓库安全检查与界面验收后再记为交付。

来源：[AutoPi README](https://github.com/shoelace66/AutoPi#what-autopi-can-do)、[Rynne README](https://github.com/KremlevLev/Rynne#remember-and-continue)、[Ghost后台模式说明](https://github.com/NORTHTEKDevs/ghost#background-mode-agent-harness--computer-use)。仓库默认分支页面会变化；本次用作设计参考，没有锁定候选提交或验证其产品声明。

## 2026-10-12 Windows无焦点自动化与计划预览复核

| 项目 | 固定快照/公开信息 | 对小K的取舍 |
| --- | --- | --- |
| [open-computer-use](https://github.com/opensymph/open-computer-use) | 核对`main`提交`5b433b98019c18201a15d11e8c3cb0010879a3d8`，MIT。README提供UIA/Win32、本地隔离操作和按控件操作；同时提供窗口/桌面输入工具，且Windows全局输入另有环境变量开关。 | 不把MCP整套权限接入ToolBroker。参考其针对窗口的无障碍树操作方向；小K仍按固定工具逐个暴露，禁止隐藏的全局鼠标/键盘回退。其README描述并不构成小K无干扰证明。 |
| [DesktopAgent](https://github.com/alessiobianchini/DesktopAgent) | 核对`main`提交`6a721a5c1cc6c5a49b81065b9845de5d87af96c1`。README称有应用/窗口白名单、危险动作确认、限速、急停、编辑式计划预览、校验和dry-run；GitHub元数据未给出SPDX许可证，本轮未核实代码或依赖。 | 这些交互与小K的审批、撤销和安全闸门方向一致，但当前小K面向明确用户任务直接执行受限工具；不照搬整套规划UI，不采用未核明许可的源码。未来若增加多步计划，先补可读计划、工具参数校验和只读模拟，再考虑执行。 |
| [dsh-computer-use-native](https://github.com/dao-awa/dsh-computer-use-native) | 核对`master`提交`2005a001f40b47c5f9fd91a4ee827e1007ec7923`，MIT。README描述以窗口截图建立带帧校验的坐标视口，再用窗口消息`PostMessage`执行后台鼠标/键盘动作；其自测指出Chromium/WinForms部分场景可用，但Windows XAML/WinUI/Notepad忽略消息，文本输入也可能使目标窗口激活。仓库当时显示0 Stars。 | 这是很新的探索项目，项目方测试仅作线索。坐标输入、PostMessage和截图仍无法普遍保证目标安全或不干扰，且为视觉模型插件；小K不接入。可以参考“窗口尺寸变化后拒绝陈旧坐标”的防护思想，但小K目前不暴露坐标点击工具。 |

本次在线查看三个仓库的README、许可元数据和指定默认分支提交；没有克隆、安装、运行或复用代码。研究结果支持继续坚持ToolBroker窄工具和显式失败。UIA/窗口消息可作为单应用隔离实验候选，但除非在用户继续操作另一窗口时有可重复焦点、输入、目标结果和失败路径证据，不把它宣称为普遍后台控制。截图/视觉路径也不能用于读取被禁止持久化的通知或聊天内容。

来源：[open-computer-use快照](https://github.com/opensymph/open-computer-use/tree/5b433b98019c18201a15d11e8c3cb0010879a3d8)、[DesktopAgent快照](https://github.com/alessiobianchini/DesktopAgent/tree/6a721a5c1cc6c5a49b81065b9845de5d87af96c1)、[dsh-computer-use-native快照](https://github.com/dao-awa/dsh-computer-use-native/tree/2005a001f40b47c5f9fd91a4ee827e1007ec7923)。项目信息与README会变化；未进行本机集成验证。

## 2026-10-12 GitHub R0 Windows通知候选复核

| 项目 | 可借鉴内容 | 与小K的差距及决定 |
| --- | --- | --- |
| [RavelloH/WinToastRelay](https://github.com/RavelloH/WinToastRelay) | GitHub仓库标注MIT；README介绍使用`UserNotificationListener`和`NotificationChanged`事件、先建立Toast基线、按应用过滤，并通过打包身份在运行时请求通知访问。 | 可参考通知事件、基线和权限状态管理。它的产品用途是把可见通知正文发送到Bark、Telegram或自定义Webhook，并保留投递状态；这与小K的本地处理和原始通知不持久化要求不符。通用Toast转发也没有证明能可靠区分微信/QQ私聊、群聊或稳定联系人身份，故不集成。 |
| [chengamu/wechat-sdk](https://github.com/chengamu/wechat-sdk) | README展示用Windows UI Automation定位微信控件、按周期扫描界面和版本化选择器。 | README也明确列出模拟键盘、鼠标和剪贴板操作；这些会抢用户输入/焦点或改写剪贴板，且轮询读取界面并不等同于合规Toast读取。仅保留其版本诊断和选择器维护作为研究线索，不运行、不接入。 |

这轮GitHub检索没有找到能在不读客户端数据库/进程、不注入、不抢焦点且可验证稳定会话身份的微信/QQ适配器。小K已经有Windows Toast监听入口；当前闸门卡在自然可见Toast样本、真实来源关联、私聊/群聊分类和会话稳定标识，不能用第三方README声称替代本机证据。监控仍关闭，不发送试探消息。

来源：[WinToastRelay README与许可](https://github.com/RavelloH/WinToastRelay)、[WinToastRelay隐私说明](https://github.com/RavelloH/WinToastRelay/blob/main/PRIVACY.md)、[wechat-sdk README](https://github.com/chengamu/wechat-sdk)。功能与许可信息来自仓库自述，没有作为小K实测或验收结论。

## 2026-10-12 Aevra 与 Microsoft UI Automation Remote Operations

| 项目 | 可借鉴内容 | 评估与决定 |
| --- | --- | --- |
| [the-long-ride/aevra](https://github.com/the-long-ride/aevra) | GitHub页面标注MIT。项目维护者的桌面控制文档区分原生语义UIA动作与前台物理输入，说明不从语义路径静默降级到前台输入；还描述了按窗口租约限制动作、变更后作废旧快照、再次描述目标、聚焦窗口变化时暂停，以及结果不明时禁止重放。 | 借鉴“能力显式声明、快照失效、目标重核、结果不确定不重试”作为未来按应用增加后台操作的设计检查点。页面同时介绍远程MCP网关、管理凭据、广泛桌面/浏览器工具和浏览器扩展；项目所称后台效果是维护者文档，没有由小K或独立实验验证。不开通远程服务、不接入其MCP/代码，不据此启用通用UIA写入。 |
| [microsoft/Microsoft-UI-UIAutomation](https://github.com/microsoft/Microsoft-UI-UIAutomation) | Microsoft仓库说明Remote Operations用于把多次跨进程UIA访问打包成一次远程操作，以减少调用往返。仓库将平台API标为预览，要求预览Windows版本和对应预览SDK。 | 可跟踪稳定版发布后对只读树遍历性能的收益；Remote Operations只改变调用打包/性能，不能证明应用动作安全、不会改变焦点或适合用户并发使用。当前固定SDK为稳定版，因此不引入预览SDK或依赖。 |

**本轮取舍：** 优先完成小K设置页的候选绑定：采样来源必须选自最近一次只读诊断结果，按微信/QQ平台筛选；候选不能手输成任意AUMID，也不能自动写入监听白名单。真实通知正文仍仅在用户主动开始样本核验后短时留于本机内存。桌面写操作仍逐应用、逐动作验收。

来源：[Aevra桌面控制指南](https://github.com/the-long-ride/aevra/blob/main/docs/user-manual/19-desktop-control.md)、[Aevra许可证](https://github.com/the-long-ride/aevra/blob/main/LICENSE)、[Microsoft UI Automation Remote Operations说明](https://github.com/microsoft/Microsoft-UI-UIAutomation)。仓库页面与`main`分支可变；本次只核对公开文档和许可证线索，没有克隆、构建、安装或运行候选，也没有复用代码。

## 2026-10-12 UIA后台输入方案复核

- 参考[Microsoft winapp CLI v0.7.1 UI Automation文档](https://github.com/microsoft/winappCli/blob/v0.7.1/docs/ui-automation.md)：该项目把UIA `set-value`归为不注入物理键鼠的目标状态变化，并对该项目自身的写调用串行化；没有安装、集成CLI或添加其依赖。
- 微软[SetValue方法](https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomationvaluepattern-setvalue)与[`IUIAutomation2.AutoSetFocus`](https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomation2-get_autosetfocus)文档说明直接调用ValuePattern前必须考虑默认自动设焦点。本机WPF `System.Windows.Automation`封装没有公开`AutoSetFocus`；编译器也拒绝该调用，因此本轮撤回了未能安全编译的输入实现，没有交付后台写入功能。
- 本轮只扩展只读快照：Edit/ComboBox仅列控件类型、AutomationId和状态，不读控件名称或当前值；密码控件继续排除。等待工具只查询唯一控件的启用状态，不调用动作Pattern。
- 下一步若继续研究无焦点后台输入，需先使用受支持的COM接口正确设置`IUIAutomation2.AutoSetFocus=false`，确认作用于实际操作所用Automation对象，再以专用无害窗口验证焦点、用户并发和取消行为；在此之前不接入ToolBroker写动作。

## 2026-10-12 UIA COM隔离验证更新

本节更新并完成上一节提出的“受支持COM接口 + 同一对象 + 隔离无害窗口”验证；生产ToolBroker仍没有通用后台写入动作。

- 微软固定版[winapp CLI v0.7.1文档](https://github.com/microsoft/winappCli/blob/e3cd0b94f3e7b776ac51ba0564f4908094e979b5/docs/ui-automation.md)把`set-value`定义为UIA Pattern写入，不注入键鼠；该CLI自己的工作流仲裁不能覆盖使用者本人或其它程序对同一字段的并发操作。
- 对照固定提交[pywinauto 90eebe4](https://github.com/pywinauto/pywinauto/blob/90eebe493a85bf4063fcba05b70481c91309980f/pywinauto/controls/uia_controls.py#L2989-L3066)：`set_edit_text`直接使用ValuePattern，而`set_window_text`先明确调用`set_focus()`。这证明不同封装路径行为有差异，但该实现没有替小K关闭`IUIAutomation2.AutoSetFocus`，不直接采用。
- 新增[隔离诊断工具](../../tools/XiaoK.UiAutomationDiagnostics/README.md)，只测试工具自己创建的跨进程WinForms合成窗体。固定同一个`IUIAutomation2`对象：将`AutoSetFocus`设为false，再从该对象获取目标`AutomationElement`和`ValuePattern`。目标通过空值、唯一AutomationId、PID、Edit类型、Enabled、非密码核验；先后确认前台HWND与当前UIA焦点一致。窗口由原生`ShowWindow(SW_SHOWNOACTIVATE)`显示。固定SDK Release构建0警告/0错误，本机诊断输出PASS，合成值正确回读。
- 最早一次隔离尝试用了普通WinForms显示，发现前台已变化后在任何`SetValue`之前停止。改用SW_SHOWNOACTIVATE后，目标进程启动与显示前后前台都保持原值；这次排错说明不能只从库文档推断不抢焦点。
- `Microsoft.Windows.CsWin32` 0.3.333生成的COM定义只用于此独立工具，`PrivateAssets=all`。NuGet依赖锁固定，但含预览/实验元数据版本，因此不纳入Host/MSIX。没有复制或引入上述GitHub项目代码。

本机结论严格限于此WinForms Provider和空文本框；微信/QQ、Chromium/WebView2、其它UIA Provider、用户并发编辑、取消和发送都未验证。生产UI工具仍只读，真实消息仍无发送适配器。任何目标应用都必须单独测试；目标身份不唯一、窗口成为前台或焦点/结果变化时必须停止并反馈，结果不确定不能重试。

来源：[Microsoft winapp CLI v0.7.1固定源码](https://github.com/microsoft/winappCli/tree/e3cd0b94f3e7b776ac51ba0564f4908094e979b5)、[pywinauto固定提交源码](https://github.com/pywinauto/pywinauto/tree/90eebe493a85bf4063fcba05b70481c91309980f)、[FlaUI ValuePattern固定提交源码](https://github.com/FlaUI/FlaUI/blob/fd7cc64ab01908a0ae4cc7d05e704caa34f98d16/src/FlaUI.Core/Patterns/ValuePattern.cs)、[Microsoft SetValue API](https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomationvaluepattern-setvalue)、[Microsoft AutoSetFocus API](https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomation2-get_autosetfocus)。

GitHub CI补充：阶段提交`b5e55d8`的Build #509在无缓存Runner上发现此工具的`PackageSourceMapping`未包含Windows SDK引用包，构建未开始。只在诊断工具NuGet.Config中补充`Microsoft.Windows.SDK.NET.Ref`到nuget.org映射（提交`3537fc8`）；新提交Build #510的构建与桌面体验检查均成功。此修复不改产品依赖或模型锁。

## 2026-10-12 FlaUI与CLI写控件路径复核

- 核对[FlaUI固定提交](https://github.com/FlaUI/FlaUI/tree/fd7cc64ab01908a0ae4cc7d05e704caa34f98d16)中的UIA3 `ValuePattern.SetValue`实现：封装最终直接调用底层`IUIAutomationValuePattern.SetValue`。这适合作为语义控件调用的代码参考，但该方法本身没有建立小K所需的目标身份核验、`AutoSetFocus=false`设置、并发协调、写后读回和失败关闭策略；FlaUI README也说明UIA2/UIA3各有Provider兼容差异。项目许可证标注MIT，但本轮没有添加依赖或复制代码。
- 复核[kodroi/FlaUI.Cli](https://github.com/kodroi/FlaUI.Cli)：README中的`set-value`使用Value Pattern，但`clear`在Pattern不可用时会回退到键盘删除；同一CLI还提供点击、键盘、菜单、滚动、截图和关闭应用等宽工具面。它适合了解命令接口与结构化返回，不适合作为小K可直接委托的安全边界。仓库标注MIT；未固定提交、安装或运行。
- 两个项目都不能替代对目标窗口的身份核验和应用Provider实测。小K保留独立COM诊断工具所证明的有限结论：只对一个合成WinForms输入框验证过同一`IUIAutomation2`对象的无焦点写入；生产Host仍没有UIA写入工具，微信、QQ和WebView2仍待分别验证。

**决定：** 继续参考FlaUI的Pattern抽象与稳定语义选择器，但暂不引入FlaUI或其CLI为生产依赖，也不启用键鼠回退。下一项有价值的实机工作是由用户正常使用目标客户端时，针对一个无副作用输入框验证前台窗口、键盘焦点、剪贴板和用户输入均不受影响；未通过时该客户端仍只开放只读能力。

来源：[FlaUI固定提交的ValuePattern源码](https://github.com/FlaUI/FlaUI/blob/fd7cc64ab01908a0ae4cc7d05e704caa34f98d16/src/FlaUI.UIA3/Patterns/ValuePattern.cs)、[FlaUI仓库与UIA2/UIA3说明](https://github.com/FlaUI/FlaUI)、[FlaUI.Cli README](https://github.com/kodroi/FlaUI.Cli)。仓库默认分支与README可能变化；CLI结论来自维护者文档，本轮没有克隆、构建或运行候选。

## 2026-10-12 真实3D桌宠候选复核

| 项目 | 仓库公开信息 | 对小K的评估 |
| --- | --- | --- |
| [woooooooooolf/deskpet3d](https://github.com/woooooooooolf/deskpet3d) | C99、raylib 6.0和Win32的独立Windows桌宠，支持GLB/glTF动画、透明置顶窗口、托盘及JSON配置；仓库标注GPL-3.0，创建于2026-07-18，最近推送为2026-07-22。 | 目前最贴近“可换真实3D模型”的候选，可以参考渲染窗口、GLB载入和模型配置分离。它是较新的独立应用，不是可直接嵌入WPF的成熟组件；GPL-3.0与本项目未来发布许可的兼容性未核对，也未运行其代码，所以不引入代码或二进制。 |
| [SixOfFive/DesktopPet](https://github.com/SixOfFive/DesktopPet) | C# WinForms桌宠，用Silk.NET/OpenGL离屏渲染、SharpGLTF载入模型，再绘制到点击穿透的分层窗口；仓库自述含托盘和尺寸菜单。最近推送为2026-06-15。 | 技术栈与.NET Host较近，适合参考分层窗口、透明合成和模型动画边界。不过GitHub仓库没有声明代码许可证，不能复制或打包其代码；其README把可保存尺寸子菜单列为后续路线，也不能据此认定40%–160%缩放已经完成。 |
| [kirbycope/godot-desktop-pet](https://github.com/kirbycope/godot-desktop-pet) | Godot 4的3D桌宠样例，带本地语音/模型、屏幕读取与记忆；仓库创建于2026-10-08，最近推送为2026-10-10。 | 项目很新，GitHub未显示仓库级许可证，且把屏幕读取和AI运行时与宠物渲染绑在一起；可观察其3D场景与透明窗口做法，暂不复制、运行或作为小K依赖。 |

**当前决定：** 保留已接入的小K Host与VPet Core，不把新找到的独立宠物程序直接替换进正式运行链路。真正的3D路线先做隔离渲染原型：只接收小K的待机、聆听、思考、执行四种状态，不提供模型、联网、屏幕读取或系统控制能力；验收40%–160%缩放并保存、透明背景和空白处点击穿透、不抢前台/键盘焦点、拖动不妨碍工作、退出与Host断开后的回收，以及空闲/动画期间CPU与显存。候选模型和代码的许可证须逐项确认。只有原型达到这些条件并与现有桌宠体验比较后，才决定是否替换VPet Core显示层。

**仓库许可待决：** 小K仓库当前根目录没有`LICENSE`/`COPYING`文件；在用户确定本项目发布许可证前，不从无许可证仓库复制代码，也不把GPL候选作为小K的二进制依赖。开源仓库可用于理解公开方案，但README描述不当作本机性能或交互验收证据。

来源：[deskpet3d README](https://github.com/woooooooooolf/deskpet3d/blob/main/README_EN.md)、[deskpet3d许可证](https://github.com/woooooooooolf/deskpet3d/blob/main/LICENSE)、[SixOfFive/DesktopPet README](https://github.com/SixOfFive/DesktopPet)、[Godot Desktop Pet README](https://github.com/kirbycope/godot-desktop-pet)。仓库信息按2026-10-12公开页面核查；本轮只读，没有克隆、构建、运行或下载候选。
