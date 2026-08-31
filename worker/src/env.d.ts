// 通过 wrangler secret 注入的密钥（不属于配置文件，因此在这里补充类型）
interface Env {
  TG_BOT_TOKEN?: string;
  TG_WEBHOOK_SECRET?: string;
  WXPUSHER_APP_TOKEN?: string;
}
