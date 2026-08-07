# Windows 小工具集

这里收集一些轻量、免安装的 Windows 桌面小工具。每个工具都有独立目录，源码、图标、使用说明和可执行文件放在一起。

## 当前工具

| 工具 | 主要功能 | 启动方式 |
| --- | --- | --- |
| [倒计时提醒](./countdown-reminder/) | 同时管理多个倒计时，支持桌面悬浮、到点声音提醒和系统托盘运行 | 运行 [`倒计时提醒.exe`](./countdown-reminder/倒计时提醒.exe) |
| [Claude Console](./claude-Console/) | 在系统托盘中查看 Claude Code 本周额度和 5 小时额度 | 运行 [`Claude Console.exe`](<./claude-Console/Claude Console.exe>) |
| [Clash 流量哨兵](./clash-traffic-sentinel/) | 统计真正经过 Clash Verge 代理节点的流量，提供应用与域名排行和异常流量提醒 | 运行 [`Clash 流量哨兵.exe`](<./clash-traffic-sentinel/Clash 流量哨兵.exe>) |

## 快速使用

1. 进入需要的工具目录。
2. 下载或直接运行目录中的 `.exe` 文件。
3. 根据该工具 README 中的说明操作。

这些工具均为 Windows 桌面程序，不需要安装。首次运行自行编译或从网络下载的 EXE 时，Windows 可能显示安全确认，请核对文件来源后再运行。

## 仓库结构

```text
.
├─ README.md
├─ countdown-reminder/
│  ├─ README.md
│  ├─ CountdownReminder.cs
│  ├─ CountdownReminder.ico
│  └─ 倒计时提醒.exe
├─ claude-Console/
│  ├─ README.md
│  ├─ Claude Console.exe
│  ├─ src/
│  ├─ tests/
│  └─ tools/
└─ clash-traffic-sentinel/
   ├─ README.md
   ├─ Clash 流量哨兵.exe
   ├─ src/
   ├─ tests/
   └─ tools/
```

## 隐私说明

- 倒计时提醒完全在本地运行，不访问网络。
- Claude Console 只在运行时读取本机 Claude Code 登录状态，并通过 Claude 的 HTTPS 接口查询额度；不会把令牌写入程序、配置文件或日志。
- Clash 流量哨兵只读取 Clash Verge 的本地命名管道和配置；统计保存在 EXE 同目录的本地数据库，不上传、不抓包，也不解密 HTTPS。
- 仓库中不包含个人账号、访问令牌、刷新令牌或 API 密钥。

更详细的启动、构建和操作说明请查看各工具目录中的 README。
