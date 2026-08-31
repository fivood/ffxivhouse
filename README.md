# 抽房了吗（FF14 房屋抽签提醒）

国服 FF14 房屋抽签时间提醒工具。数据来源：[艾欧泽亚售楼中心](https://house.ffxiv.cyou)（玩家上报数据）。

![license](https://img.shields.io/badge/license-MIT-green)

**三种使用方式**：

| 方式 | 适合谁 | 怎么用 |
|---|---|---|
| 🌐 网页版 | 手机/不想装东西 | 打开 https://ff14.70015.net （先找 Bot 拿绑定链接） |
| 🤖 Telegram Bot | 只想收提醒 | 找 @fivood_house_bot 发 `/start` |
| 💻 Windows 桌面端 | 重度玩家 | [Releases](../../releases) 下载，功能最全 |

## 功能

- 浏览 4 个大区 27 个服务器的在售房屋，按房区 / 尺寸 / 限购类型筛选，多种排序（含「参与人数最少」捡漏排序）
- 关注房屋后自动生成提醒：
  - **申请期截止前**提醒报名（计划抽的房）
  - **开奖提醒**（进入公示期，已报名的房）
  - **公示期截止前**提醒领房 / 领回押金（逾期有损失）
  - **下轮开抽**提醒（准备期结束）
- 提醒时间点可选 72/48/24/12/6/3/1 小时前，多选
- **炸房提醒**：登记自有房产，45 天未进屋自动预警（10/5/1 天三级提醒），支持手动打卡与补签日期；已炸房可标记（旧家具 35 天保管倒计时），炸房日期可自选/更正
- 提醒渠道：Telegram Bot / WxPusher APP / Windows 通知（桌面端）
- 数据滞后 / 推测数据均有显著标注，避免被旧数据误导

## 桌面端使用

1. 下载 Releases 里的公开版 zip，解压即用（无需安装 .NET）
2. 选择服务器 → 浏览在售房屋 → 点「＋关注」
3. 到点自动弹出 Windows 通知；需要 TG/微信提醒在右侧「⚙ 设置」里配置
4. 关闭窗口最小化到系统托盘；支持开机自启 + 任务计划兜底（程序没开也能收到提醒）

首次运行被 SmartScreen 拦截属正常现象（个人开发者无代码签名证书），点「仍要运行」即可。

## 仓库结构

```
src/FF14HouseReminder.App/   # WPF 桌面端（.NET 8）
worker/                      # Cloudflare Workers：TG Bot + 网页版（见 worker/README.md）
```

## 构建

```powershell
dotnet build ffxivhouse.slnx -c Release
```

发布打包（full=含本地直报能力的自用版，public=公开版）：

```powershell
.\publish.ps1          # 两个版本都打
.\publish.ps1 public   # 只打公开版
```

### 关于 full 版的本地直报

完整版（full）内置一个本地 HTTP 接收服务（`127.0.0.1:17863`），用于接收配套游戏内插件的直报数据。
**插件源码不在本仓库**（单独私有维护），没有插件时该服务静默闲置，不影响任何功能。
公开版（public）通过 `PublicBuild` 编译常量完全剥离该功能。

## 发版

推送 tag 即自动构建公开版并创建 Release（CI 见 `.github/workflows/release.yml`）：

```powershell
git tag v0.2.0; git push origin v0.2.0
```

worker 目录连接了 Cloudflare Builds，push 到 main 自动部署到 ff14.70015.net。

## 合规说明

- 请求售楼中心 API 时带规范 User-Agent，轮询间隔 ≥5 分钟并带随机抖动
- 数据为玩家上报，可能存在延迟；界面与提醒文案会标注「推测数据 / 数据滞后」
- 本工具与 Square Enix、盛趣游戏无关。FF14 相关商标归原持有人所有。
