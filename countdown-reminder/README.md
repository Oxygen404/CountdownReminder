# 倒计时提醒

一个轻量的 Windows 倒计时工具，适合同时管理多个提醒，并把当前关注的任务悬浮在桌面上。

## 功能

- 同时创建和管理多个倒计时
- 为每个倒计时设置提醒内容
- 选择任意任务作为桌面悬浮窗显示
- 悬浮窗支持拖动、隐藏提醒文字，以及右键或 `Esc` 收起
- 倒计时结束时播放声音并显示置顶提醒
- 主窗口关闭后可继续驻留系统托盘
- 左键单击托盘图标恢复窗口，右键可打开或退出

## 启动

直接运行 [`倒计时提醒.exe`](./倒计时提醒.exe)，无需安装。

系统要求：Windows 10 或 Windows 11，并具备 .NET Framework 4.x 运行环境。

## 基本操作

1. 输入分钟数和提醒内容。
2. 点击“添加倒计时”。
3. 在列表中选择任务后，可以悬浮显示或取消倒计时。
4. 双击任务也可以切换到悬浮显示。
5. 关闭主窗口后，程序会收起到通知区域；通过托盘图标恢复或退出。

## 从源码构建

项目只使用 Windows 自带的 .NET Framework 类库。在当前目录打开 PowerShell，执行：

```powershell
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /target:winexe /platform:anycpu /optimize+ `
  /reference:System.dll `
  /reference:System.Core.dll `
  /reference:System.Drawing.dll `
  /reference:System.Windows.Forms.dll `
  /win32icon:CountdownReminder.ico `
  '/out:倒计时提醒.exe' `
  CountdownReminder.cs
```

如果系统只有 32 位 .NET Framework 编译器，请把路径中的 `Framework64` 改为 `Framework`。

## 自检

构建完成后可以运行内置检查：

```powershell
& '.\倒计时提醒.exe' --self-test
& '.\倒计时提醒.exe' --tray-test
```

两个命令返回退出码 `0` 表示通过。

## 文件说明

- `CountdownReminder.cs`：完整程序源码
- `CountdownReminder.ico`：应用图标
- `倒计时提醒.exe`：可直接运行的程序，当前版本 `2.2.4`

## 隐私

程序不访问网络，不上传倒计时内容。任务数据只保存在当前运行进程中；如果程序发生未处理异常，只会在 Windows 临时目录写入 `CountdownReminder-error.log` 以便排查。
