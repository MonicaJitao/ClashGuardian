# ClashGuardian

一个面向 Clash for Windows 的轻量级系统代理恢复工具。它在后台检测 Google 的连通性，发现持续异常时，尝试重置本机系统代理。

## 功能

- 约每秒检测一次 Google 连通性；首次失败后快速复查，减少偶发波动造成的误触发。
- 连续检测失败且代理地址可确认属于 Clash 时，关闭并重新启用 Windows 系统代理。
- 监控未暂停且持续失败时约每 5 秒再次尝试；可以在系统托盘查看状态、立即检测、暂停或恢复监控。
- 不依赖快捷键和前台窗口，使用远程桌面时也可以在本机后台运行。
- 若无法确认 Clash 代理归属，或检测到代理设置被其他程序改变，不会继续重置；无法确认代理已恢复时会暂停并提示。

## 运行环境

- Windows 桌面系统。
- Clash for Windows 0.20.7，使用本机 HTTP 系统代理。
- Clash 已启动，且本机代理地址能够被识别为该 Clash 进程所有。

目前仅针对上述版本和场景开发，其他 Clash 客户端、版本及代理模式尚未验证。

## 开始使用

1. 下载并解压项目提供的压缩包。
2. 启动 Clash for Windows，并开启 **System Proxy**。
3. 双击运行 `ClashGuardian.exe`。请确保 `ClashProbe.exe` 与它放在同一个文件夹。
4. 程序启动后自动开始监控。双击系统托盘图标查看状态；右键可暂停、开启监控或退出。

如果正在运行旧版，请先在托盘中退出旧版，再启动新版。

## 工作方式

程序访问 [Google generate_204](https://www.google.com/generate_204) 检查连接。确认故障后，它通过 Windows 接口重置本机系统代理，并保留当前识别到的代理地址和绕过规则。

这个过程**不会实际点击 Clash 界面中的 System Proxy 开关**，因此界面上的开关不一定会闪动。它与手动在 Clash 中开关的效果不能保证在所有情况下完全一致。

程序以故障后 **5 秒内开始处理**为目标，但实际响应时间受网络、Windows 和 Clash 状态影响，不能保证每次都能在 5 秒内开始处理。

## 使用限制

- 如果需要手动关闭系统代理，请先暂停本程序；否则检测失败时，程序可能重新开启代理。
- 本程序管理的是运行它的**本机**系统代理，不会直接修改远程桌面内部另一台电脑的代理设置。
- 使用独立代理配置、不遵循 Windows 系统代理的应用，可能不受本程序影响。
- 当前版本已通过模拟故障和模拟代理测试；真实故障下是否能恢复连接，仍需在实际使用中验证。

## 从源码构建

以下命令适用于安装了 .NET Framework 4.5 或更高版本的 64 位 Windows。在项目的 `source` 目录中打开 PowerShell：

```powershell
$csc = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$workdir = (Get-Location).Path

$main = Start-Process -FilePath $csc -WorkingDirectory $workdir -NoNewWindow -Wait -PassThru -ArgumentList '/nologo /optimize+ /target:winexe /out:..\ClashGuardian.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll Core.cs Program.cs Probe.cs WindowsProxyBackend.cs'
if ($main.ExitCode -ne 0) { throw '主程序编译失败' }

$probe = Start-Process -FilePath $csc -WorkingDirectory $workdir -NoNewWindow -Wait -PassThru -ArgumentList '/nologo /optimize+ /target:exe /out:..\ClashProbe.exe Probe.cs ProbeWorkerMain.cs'
if ($probe.ExitCode -ne 0) { throw '检测组件编译失败' }
```

运行基础逻辑测试：

```powershell
$testBuild = Start-Process -FilePath $csc -WorkingDirectory $workdir -NoNewWindow -Wait -PassThru -ArgumentList '/nologo /out:CoreTests.exe Core.cs CoreTests.cs'
if ($testBuild.ExitCode -ne 0) { throw '测试程序编译失败' }

$tests = Start-Process -FilePath '.\CoreTests.exe' -WorkingDirectory $workdir -NoNewWindow -Wait -PassThru
if ($tests.ExitCode -ne 0) { throw '测试未通过' }
```

该测试使用模拟代理，不会修改本机系统代理。

## 退出与卸载

在系统托盘右键点击程序图标，选择 **退出**；删除解压后的程序文件夹即可。
