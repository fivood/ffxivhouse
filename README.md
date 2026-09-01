# 抽房了吗（FF14 房屋抽签提醒）

国服 FF14 房屋抽签时间提醒工具。数据来源：[艾欧泽亚售楼中心](https://house.ffxiv.cyou)（玩家上报数据）。

![license](https://img.shields.io/badge/license-MIT-green)

**三种使用方式**：

| 方式 | 适合谁 | 怎么用 |
|---|---|---|
| 🌐 网页版 | 手机 / 不想装东西 | 打开 https://ff14.70015.net （先找 Bot 拿绑定链接） |
| 🤖 Telegram Bot | 只想收提醒 | 找 @fivood_house_bot 发 `/start` |
| 💻 Windows 桌面端 | 重度玩家 | [Releases](../../releases) 下载，功能最全 |

三端共用一份关注列表：桌面端本地存，网页版和 Bot 存在同一个账号下，绑定后互通。

## 功能

**看房**

- 4 个大区 28 个服务器的在售房屋，按房区 / 尺寸 / 限购类型筛选
- 多种排序，含「参与人数最少」的捡漏排序
- **房区图**：常显的房区地图，鼠标滑过房屋条目（手机上点）即高亮对应地块，
  看得出这套房在小区里的具体位置和邻居。5 个房区 × 主城区（1-30 号）/ 扩展区（31-60 号）

**抽签提醒**

关注房屋后自动生成四类提醒：

| 提醒 | 触发时机 |
|---|---|
| 报名提醒 | 申请期截止前（计划抽的房） |
| 开奖提醒 | 进入公示期（已报名的房） |
| 领房提醒 | 公示期截止前，提醒领房 / 领回押金（逾期有损失） |
| 下轮开抽 | 准备期结束 |

提前量可选 72 / 48 / 24 / 12 / 6 / 3 / 1 小时，最多同时开 3 个。

**炸房提醒**

登记自有房产后自动预警：连续 30 天未进屋会进入「自动拆除准备」，45 天自动拆除，提醒分 15 / 10 / 5 / 1 天四档。进屋后手动打卡重置倒计时，
忘了打卡可以按日期补签。打卡按钮会随倒计时分五档变色（每 9 天一档：蓝 → 青 → 绿 → 黄 → 红）。
房子已经炸了可以标记，转成旧家具 35 天保管倒计时。

**提醒渠道**：Telegram Bot / WxPusher APP / Windows 通知（桌面端）

数据滞后与推测数据在界面和提醒文案里都有显著标注，避免被旧数据误导。

## 桌面端使用

1. 下载 Releases 里的 zip，解压即用（无需安装 .NET）
2. 选择服务器 → 浏览在售房屋 → 点「＋关注」
3. 到点自动弹出 Windows 通知；需要 TG / 微信提醒在右侧「⚙ 设置」里配置
4. 关闭窗口最小化到系统托盘；支持开机自启 + 任务计划兜底（程序没开也能收到提醒）

首次运行被 SmartScreen 拦截属正常现象（个人开发者无代码签名证书），点「仍要运行」即可。

配置目录在 `%AppData%\FF14HouseReminder\`（`config.json` / `reminders.json` / `app.log`）。

## 仓库结构

```
src/FF14HouseReminder.App/   # WPF 桌面端（.NET 8）
worker/                      # Cloudflare Workers：TG Bot + 网页版（见 worker/README.md）
worker/public/maps/          # 房区底图，网页端和桌面端共用同一份（见该目录 README）
```

## 构建

```powershell
dotnet build ffxivhouse.slnx -c Release
```

打包成单文件免安装版（输出到 `publish\public\`）：

```powershell
.\publish.ps1 public
```

## 发版

推送 tag 即自动构建并创建 Release（CI 见 `.github/workflows/release.yml`）：

```powershell
git tag v0.4.7; git push origin v0.4.7
```

`worker/` 目录连接了 Cloudflare Builds，push 到 main 自动部署到 ff14.70015.net。

## 合规说明

- 请求售楼中心 API 时带规范 User-Agent，轮询间隔 ≥5 分钟并带随机抖动
- 数据为玩家上报，可能存在延迟；界面与提醒文案会标注「推测数据 / 数据滞后」
- 本工具与 Square Enix、盛趣游戏无关。FF14 相关商标归原持有人所有。
