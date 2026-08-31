# FF14 抽房提醒 Bot + Web 版（Cloudflare Workers）

给朋友用的零安装提醒服务：
- **Telegram Bot**：关注/提醒全部通过对话完成
- **Web 版**（移动端适配）：浏览在售房屋、管理关注列表；通过 Bot `/start` 回复的专属链接绑定身份

两者共用同一份订阅数据：网页管理关注，Bot 到点推送。

## Cloudflare 自动部署（push 即发布）

Cloudflare 控制台 → **Workers → ff14house-bot → Settings → Builds → Connect to Git**：

1. 连接 GitHub 仓库 `fivood/ffxivhouse`
2. Root directory 设为 `worker`
3. Build command 留空，Deploy command 填 `npx wrangler deploy`
4. 保存后，推送到 main 分支即自动部署

（也可以不走 Git 集成，本地 `npx wrangler deploy` 随时手动发版。）

## 手动部署

```powershell
cd worker
npm install
npx wrangler login
npx wrangler secret put TG_BOT_TOKEN        # BotFather 给的 Token
npx wrangler secret put TG_WEBHOOK_SECRET   # 随机字符串，用于校验 webhook
npx wrangler deploy
```

注册 Telegram Webhook（首次或换域名时）：

```powershell
curl "https://api.telegram.org/bot<TOKEN>/setWebhook" `
  -d "url=https://ff14.70015.net/webhook" `
  -d "secret_token=<TG_WEBHOOK_SECRET>"
```

## Bot 命令

| 命令 | 说明 |
|---|---|
| `/start` | 帮助 + 网页版专属绑定链接 |
| `/watch 萌芽池 白银乡 14 43` | 关注一套房（默认"计划抽"），也支持 `14区43号` 写法 |
| `/list` | 我的关注 + 当前阶段 + 倒计时 |
| `/mode 序号` | 切换 计划抽 / 已报名（开奖提醒仅已报名） |
| `/unwatch 序号` | 取消关注 |
| `/lead 24,1` | 设置截止前提醒提前量（小时） |
| `/servers` | 服务器列表 |

## 机制说明

- Cron 每 2 分钟检查一次；售楼中心 API 在 KV 里缓存 5 分钟（遵守其轮询礼仪）
- 订阅存 KV（`sub:{chatId}`），房屋数据缓存 `houses:{serverId}`
- Web 绑定令牌 = HMAC(chatId, TG_WEBHOOK_SECRET)，只有 Bot 能生成并发给本人；浏览器存 localStorage
- 提醒去重 key 锚定阶段结束时间，状态与推测逻辑与桌面端一致（9 天周期 = 5 天申请 + 4 天公示）
- 数据超过 2 小时未更新或处于推测状态时，推送文案会带警告

## 本地开发

```powershell
# .dev.vars 里放 TG_WEBHOOK_SECRET=随便（不入库），TG_BOT_TOKEN 留空则消息只打印不真发
npm run dev
# 触发 cron：访问 http://127.0.0.1:8787/__scheduled
```

