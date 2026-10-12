# KWS 本机诊断工具

此工具在 CPU 单线程上运行仓库锁定的 Sherpa-ONNX Zipformer KWS 候选，读取一个 16 kHz、单声道、16-bit PCM WAV，并只输出命中关键词和耗时。检测实现复用`XiaoK.Voice.SherpaKeywordSpotter`，限定输入采样率和单次帧长度，只返回关键词表中配置的标签。音频只在当前进程内存中处理，不上传、不保存；工具不会打开麦克风。

## 构建

使用仓库固定的 .NET SDK 10.0.401，并按 `packages.lock.json` 锁定 NuGet 依赖：

```powershell
.\.tools\dotnet\dotnet.exe restore tools/XiaoK.KwsDiagnostics/XiaoK.KwsDiagnostics.csproj --locked-mode
.\.tools\dotnet\dotnet.exe build tools/XiaoK.KwsDiagnostics/XiaoK.KwsDiagnostics.csproj -c Release --no-restore
```

Sherpa-ONNX C# 包在语音模块固定为 `org.k2fsa.sherpa.onnx` 1.13.5，并通过`packages.lock.json`锁定。1.13.8 元包要求多个目标平台的1.13.8原生运行时，而NuGet上其中部分平台包未提供该版本；因此选用可完整锁定还原的1.13.5。Host现在通过`XiaoK.Voice`间接引用Sherpa运行时，但尚未接入实时麦克风音频图；只有用户在设置中启用唤醒监听时才允许打开麦克风。诊断工具本身始终不访问麦克风。

## 运行

模型目录参数指向锁清单中的固定 revision 目录。默认关键词表使用模型公开测试夹具里的 `test_wavs/keywords.txt`：

```powershell
.\.tools\dotnet\dotnet.exe run --project tools/XiaoK.KwsDiagnostics/XiaoK.KwsDiagnostics.csproj -c Release --no-build -- `
  --model-dir models/speech/kws/sherpa-onnx-kws-zipformer-zh-en-3M-2025-12-20/afc4a534ef85f2db4f84f05ed78eaac385b3387e `
  --wav models/speech/kws/sherpa-onnx-kws-zipformer-zh-en-3M-2025-12-20/afc4a534ef85f2db4f84f05ed78eaac385b3387e/test_wavs/zh_3.wav
```

可用 `--keywords <本地关键词文件>` 覆盖关键词表；该文件必须放在模型目录内部，便于运行时限制本地模型和关键词来源。工具拒绝非 PCM16、非单声道、非 16 kHz 或大于 64 MiB 的 WAV；不会重采样。它只验证运行时能否加载、处理音频以及返回关键词，不代表“小K”唤醒词准确率、误唤醒率或麦克风端到端通过。
