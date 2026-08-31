# AGENTS.md

## 项目概述

FF14HouseReminder：国服 FF14 房屋抽签时间提醒 WPF 桌面端（net8.0-windows10.0.19041.0，竖长型 UI）。

- `src/FF14HouseReminder.App`：WPF 桌面端
- `worker/`：Cloudflare Workers 版提醒 Bot（Telegram 交互 + Cron 定时推送，给朋友零安装使用），见 `worker/README.md`

配套的游戏内数据直报插件在**另一个仓库**（G:\HouseWatcher，不公开发布），本仓库的公开构建通过
`-p:PublicBuild=true`（定义 `PUBLIC_BUILD` 常量）剥离本地直报功能，见 `Services/BuildFlags.cs`。

## 构建

```powershell
dotnet build ffxivhouse.slnx -c Release
.\publish.ps1          # 打包 full（自用）+ public（公开）两个版本到 publish\
```

注意：切换 PublicBuild 编译常量后必须清理 obj 再构建（publish.ps1 已处理）。

## 关键约定

- **所有源文件必须是 UTF-8（无 BOM 亦可）**：本机 PowerShell 5.1 的 `Get-Content/Set-Content` 默认按 GB2312 读写，会把中文源码改坏。改文件一律用专用编辑工具，不要用 PowerShell 重定向/Set-Content 改写含中文的文件。
- 桌面端配置目录：`%AppData%\FF14HouseReminder\`（config.json / reminders.json / app.log）
- 售楼中心 API：`https://house.ffxiv.cyou/api/sales?server={id}`，请求必须带 UA `FF14HouseReminder/版本 (+仓库地址)`，轮询 ≥5 分钟。
- 房屋字段与网站 API 保持一致（PascalCase：`Server/Area/Slot/ID/Price/Size/State/EndTime` 等）。
- State 语义：0 未知(按 9 天周期=5 天申请+4 天公示推测)、1 申请期、2 公示期、3 准备期；EndTime 为当前阶段结束时间（准备期=下轮开始时间）。
- 本地直报（仅非公开版）：`POST http://127.0.0.1:17863/api/ingest`，头 `X-Ingest-Token`，体为 `{source, entries:[HouseEntry]}`。
- 提醒类型见 `Models/Enums.cs` 的 ReminderType；提醒去重 Key 锚定阶段结束时间。
- 设置是主窗口右侧展开面板（Views/SettingsPanel），不是弹窗。

## UI 风格

Y2K 复古网页风：浅灰米底 `#D8D8CF`、面板条 `#EBEBE4`、细边框 `#ABABA0`、白色内容区、
橄榄色强调 `#8B8B5E`、方形按钮（RetroBtn 样式）、无圆角。
