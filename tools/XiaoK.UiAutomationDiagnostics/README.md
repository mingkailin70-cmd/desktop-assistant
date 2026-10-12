# UI Automation 后台焦点隔离诊断

此工具包含两种隔离诊断：合成控件写入探针，用来验证关闭 `IUIAutomation2.AutoSetFocus` 时 `ValuePattern.SetValue` 能否保持前台与键盘焦点；微信/QQ只读结构探针，用来统计实际应用窗口暴露的控件类型和模式可用性。它不属于小K Host，不注册 ToolBroker 工具，也不自动运行。

## 安全范围

- 只启动工具自身生成的隐藏 WinForms 测试窗口；窗口使用 `SW_SHOWNOACTIVATE`，没有任务栏按钮。
- 只向工具创建的空白合成输入框写入固定测试文字，并通过具名管道与测试子进程交换窗口句柄。
- 写入前比对目标进程ID、唯一 AutomationId、Edit 控件类型、启用状态、密码标记和空值前置条件。
- 写入前后核对前台 HWND，并比较 UIA 当前焦点元素。任何显示阶段已经改变前台的情况都会在写值前失败关闭。
- 不打开其他应用，不读取截图、通知或聊天内容，不注入进程，不调用鼠标、键盘、剪贴板或发送动作。退出时只关闭它自己启动的测试子进程。
- `--inspect-live-app wechat|qq` 只匹配已知可执行文件路径，再通过 `EnumWindows` 与窗口进程ID枚举该进程的可见顶层窗口；不调用 `Process.MainWindowHandle`。UIA查询控件类型、AutomationId是否存在和全部SDK定义的Pattern可用性，并只输出计数；若存在ValuePattern，只查询IsReadOnly、IsEnabled和IsOffscreen元数据。另以 `EnumChildWindows` / `GetClassName` 只统计标准Edit、RichEdit、Button及其它子窗口类别；类别字符串仅在内存中判定，stdout只输出分类数量，不输出精确类名。不会读取窗口标题、控件名称、文本值、聊天正文或截图，不调用SetValue/Invoke/Win32窗口消息等会改动目标的操作，也不写入目标应用；探针进程最多运行20秒，超时只结束探针自身。
- 实际应用结构探针只用于回答“该窗口暴露了哪些结构化控件能力”。即便发现Edit/ValuePattern或Button/InvokePattern，也不证明它是联系人/消息框或发送按钮，不证明后台写入安全、私聊身份可靠或发送结果可核验。

## 运行

在仓库根目录执行：

```powershell
& '.\.tools\dotnet\dotnet.exe' restore '.\tools\XiaoK.UiAutomationDiagnostics\XiaoK.UiAutomationDiagnostics.csproj' --locked-mode --configfile '.\tools\XiaoK.UiAutomationDiagnostics\NuGet.Config'
& '.\.tools\dotnet\dotnet.exe' run --configuration Release --project '.\tools\XiaoK.UiAutomationDiagnostics\XiaoK.UiAutomationDiagnostics.csproj' --no-restore
& '.\.tools\dotnet\dotnet.exe' run --configuration Release --project '.\tools\XiaoK.UiAutomationDiagnostics\XiaoK.UiAutomationDiagnostics.csproj' --no-build --no-restore -- --inspect-live-app wechat
& '.\.tools\dotnet\dotnet.exe' run --configuration Release --project '.\tools\XiaoK.UiAutomationDiagnostics\XiaoK.UiAutomationDiagnostics.csproj' --no-build --no-restore -- --inspect-live-app qq
```

仅在 Windows 交互登录会话运行。看到合成控件 `PASS` 只表示该 WinForms 控件路径通过；实际应用探针的 `READ_ONLY_SCAN_COMPLETE` 只表示结构枚举期间前台HWND与UIA焦点未变化。它们都不表示微信或QQ消息操作已通过。只有针对具体应用的隔离样例重复证明目标身份、空值/前置状态、无焦点变化、无并发冲突和可观察结果后，才能评估接入其应用专用适配器；通用写入入口仍不暴露给模型。

## 依赖

`Microsoft.Windows.CsWin32` 0.3.333 只用于生成此独立诊断工具的 Windows COM 声明，设置为 `PrivateAssets=all`，不进入 Host 或 MSIX。包及 Win32 元数据按 `packages.lock.json` 固定；许可为 MIT。其生成所需元数据依赖含预览/实验标记版本，因而本项目只保留为可删除的诊断工具，不把它作为生产运行依赖。
