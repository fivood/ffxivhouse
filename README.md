# 抽房了吗（FF14 房屋抽签提醒）

国服 FF14 房屋抽签时间提醒桌面工具。数据来源：[艾欧泽亚售楼中心](https://house.ffxiv.cyou)（玩家上报数据）。

![license](https://img.shields.io/badge/license-MIT-green)

## 功能

- 浏览 4 个大区 27 个服务器的在售房屋，按房区 / 尺寸 / 限购类型筛选
- 关注房屋后自动生成提醒：
  - **申请期截止前**提醒报名（计划抽的房）
  - **开奖提醒**（进入公示期，已报名的房）
  - **公示期截止前**提醒领房 / 领回押金（逾期有损失）
  - **下轮开抽**提醒（准备期结束）
- 提醒渠道：Windows 通知（Toast）+ Telegram Bot + WxPusher（微信）
- 托盘常驻 + 开机自启 + Windows 任务计划兜底（程序没开也能收到提醒）
- 启动时检查新版本（GitHub Release）
- 数据滞后 / 推测数据均有显著标注，避免被旧数据误导

## 下载

到 [Releases](../../releases) 下载 `抽房了吗-x.y.z-public.zip`，解压即用（无需安装 .NET）。

首次运行被 SmartScreen 拦截属正常现象（个人开发者无代码签名证书），点「仍要运行」即可。

## 使用

1. 主界面选择你的服务器，浏览在售房屋
2. 点「＋关注」把想抽的房加入关注列表（默认「计划抽」，报名后可点「标记已报名」切换）
3. 到点自动弹出 Windows 通知；需要微信/TG 提醒的话在右侧「⚙ 设置」里配置
4. 关闭窗口会最小化到系统托盘，右键托盘图标可退出

## 构建

```powershell
dotnet build ffxivhouse.slnx -c Release
```

发布打包（full=含扩展直报能力的自用版，public=公开版）：

```powershell
.\publish.ps1          # 两个版本都打
.\publish.ps1 public   # 只打公开版
```

## 合规说明

- 请求售楼中心 API 时带规范 User-Agent，轮询间隔 ≥5 分钟并带随机抖动
- 数据为玩家上报，可能存在延迟；界面与提醒文案会标注「推测数据 / 数据滞后」
- 本工具与 Square Enix、盛趣游戏无关。FF14 相关商标归原持有人所有。

## 发版

推送 tag 即自动构建公开版并创建 Release（CI 见 .github/workflows/release.yml）：

```powershell
git tag v0.1.0; git push origin v0.1.0
```
