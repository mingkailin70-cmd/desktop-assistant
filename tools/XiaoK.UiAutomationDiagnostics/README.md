# UI Automation 后台焦点隔离诊断

此工具仅验证小K采用 Windows UI Automation `ValuePattern.SetValue`时，关闭 `IUIAutomation2.AutoSetFocus` 是否能在合成控件上保持当前前台窗口和键盘焦点。它不属于小K Host，不注册 ToolBroker 工具，也不自动运行。

## 安全范围

- 只启动工具自身生成的隐藏 WinForms 测试窗口；窗口使用 `SW_SHOWNOACTIVATE`，没有任务栏按钮。
- 只向工具创建的空白合成输入框写入固定测试文字，并通过具名管道与测试子进程交换窗口句柄。
- 写入前比对目标进程ID、唯一 AutomationId、Edit 控件类型、启用状态、密码标记和空值前置条件。
- 写入前后核对前台 HWND，并比较 UIA 当前焦点元素。任何显示阶段已经改变前台的情况都会在写值前失败关闭。
- 不打开其他应用，不读取截图、通知或聊天内容，不注入进程，不调用鼠标、键盘、剪贴板或发送动作。退出时只关闭它自己启动的测试子进程。

## 运行

在仓库根目录执行：

```powershell
& '.\.tools\dotnet\dotnet.exe' restore '.\tools\XiaoK.UiAutomationDiagnostics\XiaoK.UiAutomationDiagnostics.csproj' --locked-mode --configfile '.\tools\XiaoK.UiAutomationDiagnostics\NuGet.Config'
& '.\.tools\dotnet\dotnet.exe' run --project '.\tools\XiaoK.UiAutomationDiagnostics\XiaoK.UiAutomationDiagnostics.csproj' --no-restore
```

仅在 Windows 交互登录会话运行。看到 `PASS` 只表示此 WinForms 合成控件路径通过；不表示其它应用、微信或QQ控件支持后台写入。只有针对具体应用的隔离样例重复证明目标身份、空值/前置状态、无焦点变化、无并发冲突和可观察结果后，才能评估接入其应用专用适配器；通用写入入口仍不暴露给模型。

## 依赖

`Microsoft.Windows.CsWin32` 0.3.333 只用于生成此独立诊断工具的 Windows COM 声明，设置为 `PrivateAssets=all`，不进入 Host 或 MSIX。包及 Win32 元数据按 `packages.lock.json` 固定；许可为 MIT。其生成所需元数据依赖含预览/实验标记版本，因而本项目只保留为可删除的诊断工具，不把它作为生产运行依赖。
