# Claude Console

Windows 通知区域中的 Claude Code 额度悬浮卡片。

## 使用

直接运行根目录下的 `Claude Console.exe`。程序不会打开普通主窗口：

- 左键单击托盘图标：显示或收起额度卡片
- 右键单击托盘图标：刷新、设置开机启动或退出
- 点击卡片以外区域：自动收起

程序默认每五分钟刷新一次。它复用本机 Claude Code 登录状态，不保存账号或令牌。

## 构建

在 PowerShell 中运行：

```powershell
.\build.ps1
```

要求 Windows 自带的 .NET Framework 4.8。构建产物会输出为 `Claude Console.exe`。

## 项目结构

- `src/Program.cs`：托盘程序、悬浮界面与额度读取
- `tools/IconMaker.cs`：构建时生成应用图标
- `tests/Tests.cs`：解析及真实读取测试
- `app.manifest`：Windows DPI 与系统兼容配置
