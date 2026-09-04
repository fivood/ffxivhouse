/**
 * FF14 抽房提醒 Bot（Cloudflare Workers 版）
 *
 * - Telegram Webhook 收命令：/watch /list /mode /unwatch /lead /servers /help
 * - Cron 每 2 分钟检查一次订阅，到点通过 Bot 私聊推送
 * - 房屋数据来自售楼中心 API，KV 缓存 5 分钟（遵守其轮询礼仪）
 */

// ═══════════════ 游戏静态数据 ═══════════════

interface ServerInfo { id: number; name: string }

const DATA_CENTERS: { name: string; servers: ServerInfo[] }[] = [
  {
    name: '陆行鸟', servers: [
      { id: 1167, name: '红玉海' }, { id: 1081, name: '神意之地' }, { id: 1042, name: '拉诺西亚' }, { id: 1044, name: '幻影群岛' },
      { id: 1060, name: '萌芽池' }, { id: 1173, name: '宇宙和音' }, { id: 1174, name: '沃仙曦染' }, { id: 1175, name: '晨曦王座' },
    ],
  },
  {
    name: '莫古力', servers: [
      { id: 1172, name: '白银乡' }, { id: 1076, name: '白金幻象' }, { id: 1171, name: '神拳痕' }, { id: 1170, name: '潮风亭' },
      { id: 1113, name: '旅人栈桥' }, { id: 1121, name: '拂晓之间' }, { id: 1166, name: '龙巢神殿' }, { id: 1176, name: '梦羽宝境' },
    ],
  },
  {
    name: '猫小胖', servers: [
      { id: 1043, name: '紫水栈桥' }, { id: 1169, name: '延夏' }, { id: 1106, name: '静语庄园' }, { id: 1045, name: '摩杜纳' },
      { id: 1177, name: '海猫茶屋' }, { id: 1178, name: '柔风海湾' }, { id: 1179, name: '琥珀原' },
    ],
  },
  {
    name: '豆豆柴', servers: [
      { id: 1192, name: '水晶塔' }, { id: 1183, name: '银泪湖' }, { id: 1180, name: '太阳海岸' }, { id: 1186, name: '伊修加德' },
      { id: 1201, name: '红茶川' },
    ],
  },
];

const ALL_SERVERS: ServerInfo[] = DATA_CENTERS.flatMap(dc => dc.servers);
const AREA_NAMES = ['海雾村', '薰衣草苗圃', '高脚孤丘', '白银乡', '穹顶皓天'];
const AREA_ALIASES = ['海雾', '薰衣', '高脚', '白银', '穹顶'];

/**
 * 尺寸表 [area][(plot-1)%30]，0=S 1=M 2=L
 * 由地块半边长推出（w<14=S、<18=M、否则 L），和 49902 条带 Size 的实际挂牌逐条核对过，无一不符。
 * 早先手抄的那份在白银乡 21 号、穹顶皓天 12/13/26 号上是错的。
 */
const SIZE_TABLE: number[][] = [
  [1, 2, 0, 1, 2, 1, 1, 0, 0, 0, 0, 0, 0, 1, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1],
  [1, 0, 2, 0, 1, 2, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 2, 0, 1],
  [0, 0, 0, 1, 2, 1, 0, 1, 0, 0, 1, 1, 2, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 2],
  [1, 0, 0, 0, 0, 0, 2, 1, 0, 0, 0, 0, 1, 0, 1, 2, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 2],
  [0, 1, 0, 0, 0, 0, 1, 1, 0, 0, 0, 2, 0, 0, 0, 0, 1, 1, 0, 0, 1, 2, 0, 0, 0, 1, 0, 0, 0, 2],
];

function sizeOf(area: number, plotId: number): number {
  if (area < 0 || area >= SIZE_TABLE.length || plotId < 1) return -1;
  return SIZE_TABLE[area][(plotId - 1) % 30];
}
const SIZE_NAMES = ['S', 'M', 'L'];
const STATE_NAMES = ['状态未知', '申请期（可报名）', '公示期（看结果）', '准备期（等下轮）'];

// ═══════════════ 数据模型 ═══════════════

interface HouseEntry {
  Server: number; Area: number; Slot: number; ID: number;
  Price: number; Size: number;
  FirstSeen: number; LastSeen: number;
  State: number; Participate: number; Winner: number;
  EndTime: number; UpdateTime: number;
  PurchaseType: number; RegionType: number;
}

interface WatchItem {
  server: number; area: number; slot: number; id: number;
  /** 0=计划抽 1=已报名 */
  mode: 0 | 1;
  /** 已触发的提醒去重 key */
  fired: string[];
  /**
   * 抽签金返还死线（unix 秒）。公示期见到「已报名」时盖在关注项上。
   * 死线在公示期结束后 90 天，那时房子早已从在售列表消失、阶段也不是公示期了，
   * 挂在阶段上算永远等不到，必须自己记住。
   */
  depositDeadline?: number;
  /** 申请号码（游戏里报名后给的号），纯备忘，可不填 */
  entryNo?: string;
}

/** 我的房产（炸房提醒）：手动登记 + 进房打卡 */
interface HomeEntry {
  server: number; area: number; slot: number; id: number;
  /** 群里登记时记下是谁的房，提醒发到群里才能点名（私聊登记的没有） */
  ownerId?: number;
  /** 群里显示用的称呼，有 username 就存 @xxx，能顺带 @ 到人 */
  ownerName?: string;
  /** 备注（一般是角色名，多账号区分用） */
  label: string;
  /** 最后一次进房时间（unix 秒），0=未知 */
  lastEnteredAt: number;
  /** 炸房（被拆除）时间（unix 秒），0=未炸房。拆除后 35 天内可回收资产 */
  demolishedAt?: number;
  fired: string[];
}

/** 炸房规则：45 天未进房进入拆除准备 */
const DEMOLITION_DAYS = 45;
/** 炸房提醒提前量（天） */
const DEMOLITION_LEAD_DAYS = [15, 10, 5, 1];   // 15 = 第 30 天，游戏里刚进入「自动拆除准备」的节点
/** 抽签金返还期限：公示期结束后 90 天（不论中标与否，都要点门牌确认才返还） */
const DEPOSIT_DAYS = 90;
/** 拆除后资产回收期限（天）：家具庭具 + 购地金币的 80% */
const FURNITURE_DAYS = 35;

/**
 * 游戏按日本时间数天数，00:00 跨一天。所以所有「第 N 天」的死线都是
 * 「那件事发生当天的 JST 00:00」再加 N 天，而不是从当时那一刻整整加 N×24 小时——
 * 后者会比真实死线晚最多一天，等提醒到了房子已经没了。
 */
const JST = 9 * 3600;
const jstDayStart = (sec: number) => Math.floor((sec + JST) / 86400) * 86400 - JST;
const dayDeadline = (fromSec: number, days: number) => jstDayStart(fromSec) + days * 86400;

/** YYYY-MM-DD → 日本时间当天 00:00 的 unix 秒；非法/未来返回错误 */
function parseDayStart(date: string): { ts: number } | { error: string } {
  const dm = date.match(/^(\d{4})-(\d{2})-(\d{2})$/);
  if (!dm) return { error: '日期格式应为 YYYY-MM-DD' };
  const ts = Math.floor(Date.UTC(+dm[1], +dm[2] - 1, +dm[3]) / 1000) - JST;
  if (ts > Math.floor(Date.now() / 1000)) return { error: '不能填未来的日期' };
  return { ts };
}

/** 分项提醒开关，缺省视为全开 */
interface NotifyFlags {
  entry: boolean;    // 申请期截止前（快去报名）
  results: boolean;  // 开奖（进入公示期）
  claim: boolean;    // 公示期截止前（中签确认归属死线）
  deposit: boolean;  // 抽签金返还死线（公示期结束后 90 天）
  next: boolean;     // 下轮申请期开始
}
const NOTIFY_ALL: NotifyFlags = { entry: true, results: true, claim: true, deposit: true, next: true };

interface UserSub {
  /** 账号 id，也是 KV key。TG 用户＝chat id 的字符串形式；匿名用户＝随机串 */
  id?: string;
  /** TG chat id；匿名账号没有，此时不发 Telegram */
  chatId: number;
  leadHours: number[];
  /** 分项提醒开关，未设置＝全开 */
  notify?: NotifyFlags;
  items: WatchItem[];
  /** 我的房产（炸房提醒） */
  homes?: HomeEntry[];
  /**
   * 群里是否公开自己房子的具体位置。未设置＝公开。
   * 关掉之后群里只剩「@某某 的房（备注）还剩 N 天」，房号服务器都不显示——
   * 提醒到人这个核心不受影响，只是不把地块摊给整个群。
   */
  groupPublic?: boolean;
  /** 可选：Bark 设备 key，或自建服务器的完整地址（iOS 渠道） */
  barkKey?: string;
  /** 可选：WxPusher 极简推送 SPT（微信渠道） */
  wxpusherSpt?: string;
  /** 可选：自定义昵称（网页顶部显示） */
  nickname?: string;
}

interface Phase { state: number; end: number; estimated: boolean }

// ═══════════════ 周期计算（与售楼中心前端算法对齐） ═══════════════
// 国服抽签周期 9 天 = 申请期 5 天 + 公示期 4 天，每天北京时间 23:00 切换阶段
// 锚点：2022-08-08 23:00:00 +0800（一个公示期结束/周期边界）

const ANCHOR_SEC = 1659970800;
const CYCLE_SEC = 9 * 86400;
const ENTRY_SEC = 5 * 86400;   // 申请期时长
const RESULTS_SEC = 4 * 86400; // 公示期时长

function getPhase(house: HouseEntry, nowSec: number): Phase {
  if (house.State !== 0 && house.EndTime > 0) {
    // 已知阶段，但时间可能已过：按周期向后滚动（与网站一致）
    let state = house.State;
    let end = house.EndTime;
    while (nowSec >= end) {
      if (state === 1) { end += RESULTS_SEC; state = 2; }        // 申请期 → 公示期
      else if (state === 2 || state === 3) { end += ENTRY_SEC; state = 1; } // → 下轮申请期
      else break;
    }
    return { state, end, estimated: false };
  }

  // 无抽签信息（State=0）：对齐周期锚点推测
  let boundary = ANCHOR_SEC;
  const firstSeen = Math.min(house.FirstSeen, nowSec);
  while (boundary > firstSeen + CYCLE_SEC) boundary -= CYCLE_SEC;
  while (boundary < firstSeen) boundary += CYCLE_SEC;

  if (nowSec < boundary) {
    // 还没到下个周期边界：准备期，等待开抽
    return { state: 3, end: boundary, estimated: true };
  }
  while (nowSec > boundary + CYCLE_SEC) boundary += CYCLE_SEC;
  return nowSec < boundary + ENTRY_SEC
    ? { state: 1, end: boundary + ENTRY_SEC, estimated: true }
    : { state: 2, end: boundary + CYCLE_SEC, estimated: true };
}

// ═══════════════ 售楼中心 API（KV 缓存 5 分钟） ═══════════════

const API_BASE = 'https://house.ffxiv.cyou';
const UA = 'FF14HouseReminder-Bot/0.1.0 (+https://github.com/fivood/ffxivhouse)';
const CACHE_TTL_SEC = 300;

async function getSales(env: Env, serverId: number): Promise<HouseEntry[]> {
  const cacheKey = `houses:${serverId}`;
  const cached = await env.KV.get<{ fetchedAt: number; entries: HouseEntry[] }>(cacheKey, 'json');
  const nowSec = Math.floor(Date.now() / 1000);
  if (cached && nowSec - cached.fetchedAt < CACHE_TTL_SEC) return cached.entries;

  const resp = await fetch(`${API_BASE}/api/sales?server=${serverId}`, {
    headers: { 'User-Agent': UA },
  });
  if (!resp.ok) throw new Error(`sales API ${resp.status}`);
  const entries = (await resp.json()) as HouseEntry[];
  await env.KV.put(cacheKey, JSON.stringify({ fetchedAt: nowSec, entries }), { expirationTtl: 86400 });
  return entries;
}

/**
 * 玩家在游戏里看门牌报上来的「已抽选人数」：{ "区:小区:房号": [人数, 看到的时间] }。
 * 售楼中心的 Participate 一直是 0，只有进游戏点门牌才看得到，所以这份数据只能靠人报。
 * 一个服务器一个 KV key，读起来和房屋缓存一样便宜。
 */
type Reports = Record<string, [number, number]>;

const repKey = (area: number, slot: number, id: number) => `${area}:${slot}:${id}`;

/**
 * 房区空置情况：{ "区:小区": [看到的时间, 60 位 0/1（1=有人住）] }。
 * 售楼中心的列表里只有在售的地块，「有人住」是靠「不在列表里」推出来的；
 * 插件逛一圈房区能拿到全部 60 块地的真实归属，比推的准。
 */
type Wards = Record<string, [number, string]>;

const wardKey = (area: number, slot: number) => `${area}:${slot}`;

async function getWards(env: Env, serverId: number): Promise<Wards> {
  return (await env.KV.get<Wards>(`wards:${serverId}`, 'json')) ?? {};
}

async function getReports(env: Env, serverId: number): Promise<Reports> {
  return (await env.KV.get<Reports>(`rep:${serverId}`, 'json')) ?? {};
}

// ═══════════════ Telegram ═══════════════

async function tgSend(env: Env, chatId: number, text: string, parseMode?: 'HTML'): Promise<void> {
  if (!env.TG_BOT_TOKEN) {
    console.log(`[no-token] -> ${chatId}: ${text}`);
    return;
  }
  const resp = await fetch(`https://api.telegram.org/bot${env.TG_BOT_TOKEN}/sendMessage`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ chat_id: chatId, text, parse_mode: parseMode }),
  });
  if (!resp.ok) console.error(`tgSend 失败 ${resp.status}: ${await resp.text()}`);
}

/** 带内联按钮的消息（用于炸房提醒的一键打卡） */
/** 发一条带「打开面板」Mini App 按钮的消息 */
async function tgSendWebApp(env: Env, chatId: number, text: string, buttonText: string): Promise<void> {
  if (!env.TG_BOT_TOKEN) {
    console.log(`[no-token] -> ${chatId}: ${text} [web_app:${buttonText}]`);
    return;
  }
  const resp = await fetch(`https://api.telegram.org/bot${env.TG_BOT_TOKEN}/sendMessage`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      chat_id: chatId,
      text,
      disable_web_page_preview: true,
      reply_markup: { inline_keyboard: [[{ text: buttonText, web_app: { url: `${WEB_BASE}/` } }]] },
    }),
  });
  if (!resp.ok) console.log('tgSendWebApp 失败', resp.status, await resp.text());
}

async function tgSendWithButton(env: Env, chatId: number, text: string, buttonText: string, callbackData: string): Promise<void> {
  if (!env.TG_BOT_TOKEN) {
    console.log(`[no-token] -> ${chatId}: ${text} [按钮:${buttonText} data:${callbackData}]`);
    return;
  }
  const resp = await fetch(`https://api.telegram.org/bot${env.TG_BOT_TOKEN}/sendMessage`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      chat_id: chatId,
      text,
      reply_markup: {
        inline_keyboard: [[{ text: buttonText, callback_data: callbackData }]],
      },
    }),
  });
  if (!resp.ok) console.error(`tgSendWithButton 失败 ${resp.status}: ${await resp.text()}`);
}

/** 应答回调查询（消除按钮的加载圈） */
async function tgAnswerCallback(env: Env, callbackId: string, text: string): Promise<void> {
  if (!env.TG_BOT_TOKEN) return;
  await fetch(`https://api.telegram.org/bot${env.TG_BOT_TOKEN}/answerCallbackQuery`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ callback_query_id: callbackId, text }),
  });
}

/** WxPusher 极简推送（SPT，用户自助扫码获取，微信渠道） */
/** Bark（iOS）。填 device key 走官方服务器，填完整 URL 则用自建的 */
async function barkSend(env: Env, keyOrUrl: string, title: string, body: string): Promise<{ ok: boolean; msg: string }> {
  const base = /^https?:\/\//i.test(keyOrUrl)
    ? keyOrUrl.replace(/\/+$/, '')
    : `https://api.day.app/${encodeURIComponent(keyOrUrl)}`;
  try {
    const resp = await fetch(base, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'User-Agent': UA },
      // group 让同类提醒在通知中心折叠到一起
      body: JSON.stringify({ title, body, group: '抽房了吗' }),
    });
    const text = await resp.text();
    // Bark 对不存在的 key 也返回 200 外壳里的 code:400，得看 body
    let code = resp.status;
    try { code = JSON.parse(text).code ?? resp.status; } catch { /* 非 JSON 就看 HTTP 状态 */ }
    if (!resp.ok || code !== 200) {
      console.error(`barkSend 失败 ${resp.status}: ${text}`);
      return { ok: false, msg: text.slice(0, 200) };
    }
    return { ok: true, msg: '' };
  } catch (e) {
    return { ok: false, msg: String(e).slice(0, 200) };
  }
}

async function wxSend(env: Env, spt: string, title: string, body: string): Promise<void> {
  const resp = await fetch('https://wxpusher.zjiecode.com/api/send/message/simple-push', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'User-Agent': UA },
    body: JSON.stringify({ spt, content: body, summary: title, contentType: 1 }),
  });
  if (!resp.ok) console.error(`wxSend 失败 ${resp.status}: ${await resp.text()}`);
}

// ═══════════════ 订阅存取 ═══════════════

/** 一条消息发到这个账号的全部渠道（Telegram → Bark → 微信；匿名账号没有 chatId） */
async function pushToSub(env: Env, sub: UserSub, title: string, body: string): Promise<void> {
  if (sub.chatId) await tgSend(env, sub.chatId, `${title}\n\n${body}`);
  if (sub.barkKey) await barkSend(env, sub.barkKey, title, body);
  if (sub.wxpusherSpt) await wxSend(env, sub.wxpusherSpt, title, body);
}

async function getSub(env: Env, id: string | number): Promise<UserSub> {
  const key = String(id);
  const sub = (await env.KV.get<UserSub>(`sub:${key}`, 'json'))
    ?? { chatId: Number(key) || 0, leadHours: [24, 1], items: [] };
  sub.id = key;   // 老记录里没有 id，读出来时补上
  return sub;
}

async function saveSub(env: Env, sub: UserSub): Promise<void> {
  await env.KV.put(`sub:${sub.id ?? sub.chatId}`, JSON.stringify(sub));
}

// ═══════════════ Web 绑定令牌（HMAC，免账号体系） ═══════════════

const WEB_BASE = 'https://ff14.70015.net';

/** 用 webhook 密钥对 chatId 做 HMAC，生成绑定令牌（只有 Bot 能发给本人） */
async function bindToken(env: Env, chatId: string | number): Promise<string> {
  const key = await crypto.subtle.importKey(
    'raw',
    new TextEncoder().encode(env.TG_WEBHOOK_SECRET ?? 'ff14house'),
    { name: 'HMAC', hash: 'SHA-256' },
    false,
    ['sign'],
  );
  const sig = await crypto.subtle.sign('HMAC', key, new TextEncoder().encode(`bind:${chatId}`));
  return [...new Uint8Array(sig)].map(b => b.toString(16).padStart(2, '0')).join('').slice(0, 24);
}

/**
 * 校验 Telegram Mini App 的 initData，通过则返回用户 id。
 * 算法见官方文档：secret = HMAC_SHA256(key='WebAppData', msg=bot_token)，
 * 再用 secret 对按 key 排序、去掉 hash 的 `k=v` 换行串签名，与 hash 比对。
 */
async function verifyInitData(env: Env, initData: string): Promise<number | null> {
  if (!initData || !env.TG_BOT_TOKEN) return null;
  const params = new URLSearchParams(initData);
  const hash = params.get('hash');
  if (!hash) return null;
  params.delete('hash');
  const checkString = [...params.entries()]
    .sort((a, b) => (a[0] < b[0] ? -1 : a[0] > b[0] ? 1 : 0))
    .map(([k, v]) => `${k}=${v}`)
    .join('\n');

  const enc = new TextEncoder();
  const seedKey = await crypto.subtle.importKey(
    'raw', enc.encode('WebAppData'), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
  const secret = await crypto.subtle.sign('HMAC', seedKey, enc.encode(env.TG_BOT_TOKEN));
  const signKey = await crypto.subtle.importKey(
    'raw', secret, { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
  const sig = await crypto.subtle.sign('HMAC', signKey, enc.encode(checkString));
  const hex = [...new Uint8Array(sig)].map(b => b.toString(16).padStart(2, '0')).join('');

  // 定长比较，别让比较耗时泄露信息
  if (hex.length !== hash.length) return null;
  let diff = 0;
  for (let i = 0; i < hex.length; i++) diff |= hex.charCodeAt(i) ^ hash.charCodeAt(i);
  if (diff !== 0) return null;

  // 防重放：签发超过 24 小时的不认
  const authDate = parseInt(params.get('auth_date') ?? '0', 10);
  if (!authDate || Math.floor(Date.now() / 1000) - authDate > 86400) return null;

  try {
    const user = JSON.parse(params.get('user') ?? '{}');
    return typeof user.id === 'number' ? user.id : null;
  } catch {
    return null;
  }
}

/** 校验 u/k 参数，返回 chatId 或 null */
async function checkAuth(env: Env, url: URL): Promise<string | null> {
  const id = (url.searchParams.get('u') ?? '').trim();
  const k = url.searchParams.get('k') ?? '';
  if (!/^[A-Za-z0-9]{1,32}$/.test(id) || !k) return null;
  return (await bindToken(env, id)) === k ? id : null;
}

/** POST 体的 u/k 校验 */
async function checkAuthBody(env: Env, body: { u?: number | string; k?: string }): Promise<string | null> {
  const id = String(body.u ?? '').trim();
  if (!/^[A-Za-z0-9]{1,32}$/.test(id) || !body.k) return null;
  return (await bindToken(env, id)) === body.k ? id : null;
}

// ═══════════════ 命令解析 ═══════════════

function findServer(token: string): ServerInfo | null {
  const byId = parseInt(token, 10);
  if (!Number.isNaN(byId)) return ALL_SERVERS.find(s => s.id === byId) ?? null;
  return ALL_SERVERS.find(s => s.name === token || s.name.startsWith(token)) ?? null;
}

function findArea(token: string): number {
  let i = AREA_NAMES.indexOf(token);
  if (i >= 0) return i;
  i = AREA_ALIASES.findIndex(a => token.startsWith(a));
  return i;
}

const HELP_TEXT = `🏠 抽房了吗（FF14 房屋抽签提醒）

关注房屋：
/watch 萌芽池 白银乡 14 43 — 关注（默认"计划抽"）
/list — 我的关注与倒计时
/mode 序号 [申请号] — 切换 计划抽/已报名
/unwatch 序号 — 取消关注

提醒设置：
/lead 24,1 — 提前量（小时）
/notify — 五类提醒开关
/panel — 网页面板（免绑定）

炸房提醒（30 天不进屋进拆除准备，45 天拆除）：
/myhome 萌芽池 白银乡 14 43 阿光 — 登记房产
/entered [序号] [日期] — 进屋打卡 / 补签（天数按日本时间算，00:00 跨一天）
/demolished [序号] — 标记已拆除（35 天资产回收倒计时）
/homes — 我的房产

其他：/name 昵称 · /bark key · /public on|off · /servers · /link · /help
群里用 /house 叫我（/start 群里每个 Bot 都抢答，我不应）

拉我进群 = 群内炸房监控：群友各自 /myhome 登记，
到点我在群里点名，谁看到谁顺手提醒本人一声。
群里只有 /myhome /entered /demolished /homes 四条；
序号只认你自己那几套（有两套就是 1 和 2，不用去数别人的）；
抽房关注和推送设置是个人的，只在私聊有效。
不想让群里看到具体房号：私聊发 /public off，群里就只写
「你 的房（备注）还剩 N 天」，提醒照发。

数据来源：house.ffxiv.cyou（玩家上报，可能延迟）`;

function fmtTime(unixSec: number): string {
  return new Date(unixSec * 1000).toLocaleString('zh-CN', {
    timeZone: 'Asia/Shanghai', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit', hour12: false,
  });
}

/** 游戏日的「月/日」。房屋天数按日本时间数，用北京时间显示会差一天，看着像 bug */
function fmtDay(unixSec: number): string {
  return new Date(unixSec * 1000).toLocaleString('zh-CN', {
    timeZone: 'Asia/Tokyo', month: '2-digit', day: '2-digit',
  });
}

function fmtRemain(nowSec: number, targetSec: number): string {
  const span = targetSec - nowSec;
  if (span <= 0) return '已到时间';
  const d = Math.floor(span / 86400), h = Math.floor((span % 86400) / 3600), m = Math.floor((span % 3600) / 60);
  if (d >= 1) return `剩余 ${d} 天 ${h} 小时`;
  if (h >= 1) return `剩余 ${h} 小时 ${m} 分`;
  return `剩余 ${m} 分`;
}

/** 处理内联按钮回调（entered:server:area:slot:id → 炸房打卡） */
async function handleCallback(
  env: Env, chatId: number, callbackId: string, data: string, from?: TgUser, isGroup = false,
): Promise<void> {
  const parts = data.split(':');
  if (parts[0] === 'entered' && parts.length === 5) {
    const server = parseInt(parts[1], 10);
    const area = parseInt(parts[2], 10);
    const slot = parseInt(parts[3], 10);
    const id = parseInt(parts[4], 10);
    const sub = await getSub(env, chatId);
    const home = (sub.homes ?? []).find(h =>
      h.server === server && h.area === area && h.slot === slot && h.id === id);
    if (!home) {
      await tgAnswerCallback(env, callbackId, '未找到该房产（可能已移除）');
      return;
    }
    // 群里谁都能点这个按钮，但只有房主自己知道进没进屋，替别人点等于把倒计时清错
    if (isGroup && home.ownerId && home.ownerId !== from?.id) {
      await tgAnswerCallback(env, callbackId, `这是 ${home.ownerName ?? '别人'} 的房，只能由本人打卡`);
      return;
    }
    home.lastEnteredAt = Math.floor(Date.now() / 1000);
    home.fired = [];
    await saveSub(env, sub);
    await tgAnswerCallback(env, callbackId, `✅ 已打卡！${home.label} 倒计时重置为 ${DEMOLITION_DAYS} 天`);
    return;
  }
  await tgAnswerCallback(env, callbackId, '未知操作');
}

/**
 * 群里这条房产该怎么写：房主关了公开就只写称呼和备注，不带服务器房区房号。
 * 设置存在房主自己的私聊订阅里（sub:{他的 id}），所以要按 ownerId 去查一下。
 */
async function groupPos(env: Env, h: HomeEntry, full: string, cache: Map<number, boolean>): Promise<string> {
  if (!h.ownerId) return full;                       // 私聊登记的，没有群这回事
  let open = cache.get(h.ownerId);
  if (open === undefined) {
    const owner = await getSub(env, h.ownerId);
    open = owner.groupPublic !== false;
    cache.set(h.ownerId, open);
  }
  return open ? `${h.ownerName} 的 ${full}` : `${h.ownerName} 的房（${h.label}）`;
}

/**
 * 群里按序号操作时只取自己那几套。老数据没有 ownerId，当成谁都能动（升级前登记的）。
 * 注意返回的是原对象的引用，改了照样能存回去。
 */
function mineOnly(homes: HomeEntry[], isGroup: boolean, sender?: TgUser): HomeEntry[] {
  if (!isGroup) return homes;
  return homes.filter(h => !h.ownerId || h.ownerId === sender?.id);
}

/** 群里显示用的称呼：有 username 就用 @xxx（顺带能 @ 到人），否则用名字 */
function whoIs(user?: TgUser): string {
  return user?.username ? `@${user.username}` : (user?.first_name ?? '某位群友');
}

async function handleCommand(
  env: Env, chatId: number, text: string, sender?: TgUser, isGroup = false,
): Promise<void> {
  const parts = text.trim().split(/[\s，,、]+/).filter(Boolean);
  const raw = (parts[0] ?? '').toLowerCase();
  // 群里可能还有别的 Bot，命令带 @xxx 后缀才是明确点名给谁的
  const addressed = /@\w+$/.test(raw);
  const cmd = raw.replace(/@\w+$/, '');
  const args = parts.slice(1);
  const nowSec = Math.floor(Date.now() / 1000);

  // 群里只做炸房监控。抽房是个人的事：报名与否、提前量、推送渠道都因人而异，
  // 发一整群人既吵又没意义
  if (isGroup && ['/watch', '/list', '/mode', '/unwatch', '/lead', '/notify', '/bark', '/name'].includes(cmd)) {
    await tgSend(env, chatId, `群里只做炸房监控，${cmd} 是个人设置，私聊我发一次就行。`);
    return;
  }

  // /start 和 /help 是通用命令，群里每个 Bot 都会抢答。
  // 不点名就不应，改用 /house 这种只有我认的名字
  if (isGroup && !addressed && (cmd === '/start' || cmd === '/help')) return;

  switch (cmd) {
    case '/house':
    case '/start': {
      // 群里不能挂 Mini App 按钮（Telegram 只允许私聊），而且群是共用一份列表，
      // 定位就是炸房监控：谁登记谁的房，到点在群里点名，方便别人线下捅一下本人
      if (isGroup) {
        await tgSend(env, chatId,
          '🏠 抽房了吗 — 群内炸房监控'
          + `\n\n各自登记自己的房：/myhome 服务器 房区 区号 房号 [角色名]`
          + `\n　例：/myhome 萌芽池 白银乡 14 43 阿光`
          + `\n进屋后发 /entered 打卡，/homes 看全群的倒计时。`
          + `\n快拆时我会在群里点名，看到的人顺手提醒本人一声。`
          + `\n不想让群里看到房号：私聊我发 /public off。`
          + `\n\n群里再叫我用 /house（/start 每个 Bot 都抢答）。`
          + `\n抽房关注和推送设置是个人的，私聊我发 /start。`);
        return;
      }
      // 只说一句 + 面板按钮，命令表交给 /help，别一上来糊一屏
      await tgSendWebApp(env, chatId,
        '🏠 抽房了吗 — FF14 房屋抽签提醒'
        + `\n\n点下面的按钮打开面板：关注房屋、我的房产、提醒设置都在里面。`
        + `\n想用命令发 /help，要电脑浏览器用的链接发 /link。`,
        '🌐 打开面板');
      return;
    }
    case '/bark': {
      const raw = (args[0] ?? '').trim();
      const sub2 = await getSub(env, chatId);
      if (!raw) {
        await tgSend(env, chatId, sub2.barkKey
          ? `当前 Bark：${sub2.barkKey.slice(0, 6)}…（发 /bark off 关闭）`
          : 'Bark（iOS）：装 Bark App，把里面那串 key 发过来，例 /bark AbCd1234'
            + `\n自建服务器就发完整地址。`);
        return;
      }
      if (raw === 'off' || raw === '关闭') {
        delete sub2.barkKey;
        await saveSub(env, sub2);
        await tgSend(env, chatId, '已关闭 Bark 推送。');
        return;
      }
      const isUrl = /^https?:\/\//i.test(raw);
      let urlPath = '';
      if (isUrl) {
        try { urlPath = new URL(raw).pathname.replace(/^\/+|\/+$/g, ''); } catch { urlPath = ''; }
      }
      if (isUrl ? !urlPath : !/^[A-Za-z0-9_-]{6,64}$/.test(raw)) {
        await tgSend(env, chatId, isUrl
          ? '自建服务器地址要带上 key，例：https://你的域名/AbCd1234'
          : 'key 格式不对：应是一串字母数字，或自建服务器带 key 的完整地址。');
        return;
      }
      const candidate = isUrl ? raw.replace(/\/+$/, '') : raw;
      const r = await barkSend(env, candidate, '抽房了吗', 'Bark 推送已开启，这是一条测试。');
      if (!r.ok) {
        await tgSend(env, chatId, `推送没成功，key 可能填错了（设备 token 不是 key）：
${r.msg}`);
        return;
      }
      sub2.barkKey = candidate;
      await saveSub(env, sub2);
      await tgSend(env, chatId, '已开启 Bark 推送，刚给你手机发了一条测试。');
      return;
    }

    case '/link': {
      if (isGroup) {
        await tgSend(env, chatId, '这里不发登录令牌——群里人人可见等于把这份列表交出去。私聊我发 /link。');
        return;
      }
      const token = await bindToken(env, chatId);
      // <code> 在 Telegram 里点一下就复制，省得从长链接里抠 u/k
      await tgSend(env, chatId,
        '🔗 登录用的两串（点一下复制）\n\n'
        + `u\n<code>${chatId}</code>\n\n`
        + `k\n<code>${token}</code>\n\n`
        + '桌面端和电脑浏览器可以直接粘这条整链接：\n'
        + `<code>${WEB_BASE}/#u=${chatId}&amp;k=${token}</code>\n\n`
        + '在 Telegram 里用 /panel 免登录。',
        'HTML');
      return;
    }

    case '/panel': {
      if (isGroup) {
        await tgSend(env, chatId, '面板是私人的，私聊我发 /panel 打开。群里用 /myhome、/entered、/homes 就够了。');
        return;
      }
      await tgSendWebApp(env, chatId,
        '🌐 面板：在这里管关注、我的房产、提醒开关，还能看房区图。'
        + `\n点下面的按钮直接打开，免绑定。`,
        '🌐 打开面板');
      return;
    }

    case '/help':
      await tgSend(env, chatId, HELP_TEXT);
      return;

    case '/public': {
      if (isGroup) {
        await tgSend(env, chatId, '这个设置是你个人的，私聊我发 /public 改。');
        return;
      }
      const me = await getSub(env, chatId);
      const arg = (args[0] ?? '').toLowerCase();
      if (arg !== 'on' && arg !== 'off') {
        await tgSend(env, chatId,
          `群内公开房屋位置：${me.groupPublic === false ? '关（只显示称呼和备注）' : '开（显示服务器和房号）'}`
          + `\n/public off 关掉之后，群里只会写「你 的房（备注）还剩 N 天」，`
          + `\n提醒照发、别人照样知道该提醒你，只是不把地块摊给整个群。`
          + `\n/public on 改回公开。`);
        return;
      }
      me.groupPublic = arg === 'on';
      await saveSub(env, me);
      await tgSend(env, chatId, me.groupPublic
        ? '已改为公开：群里会写出你房子的服务器和房号。'
        : '已关闭公开：群里只写「你 的房（备注）还剩 N 天」，不带位置。');
      return;
    }

    case '/servers': {
      const text = DATA_CENTERS
        .map(dc => `【${dc.name}】${dc.servers.map(s => s.name).join('、')}`)
        .join('\n');
      await tgSend(env, chatId, text);
      return;
    }

    case '/watch': {
      // 支持 "/watch 萌芽池 白银乡 14 43" 和 "/watch 萌芽池 白银乡 14区43号"
      let tokens = args.flatMap(a => {
        const m = a.match(/^(\d+)区(\d+)号?$/);
        return m ? [m[1], m[2]] : [a];
      });
      if (tokens.length < 4) {
        await tgSend(env, chatId, '格式：/watch 服务器 房区 区号 房号\n例：/watch 萌芽池 白银乡 14 43');
        return;
      }
      const server = findServer(tokens[0]);
      const area = findArea(tokens[1]);
      const slot = parseInt(tokens[2], 10);
      const plotId = parseInt(tokens[3], 10);
      if (!server || area < 0 || Number.isNaN(slot) || Number.isNaN(plotId)
        || slot < 1 || slot > 30 || plotId < 1 || plotId > 60) {
        await tgSend(env, chatId, '没看懂参数。服务器名见 /servers，房区：海雾村/薰衣草苗圃/高脚孤丘/白银乡/穹顶皓天，区号 1-30，房号 1-60。');
        return;
      }

      const sub = await getSub(env, chatId);
      if (sub.items.some(i => i.server === server.id && i.area === area && i.slot === slot - 1 && i.id === plotId)) {
        await tgSend(env, chatId, '这套房已经在你的关注列表里了。');
        return;
      }
      sub.items.push({ server: server.id, area, slot: slot - 1, id: plotId, mode: 0, fired: [] });
      await saveSub(env, sub);
      await tgSend(env, chatId,
        `✅ 已关注：${server.name} ${AREA_NAMES[area]} ${slot}区 ${plotId}号 [${SIZE_NAMES[sizeOf(area, plotId)] ?? '?'}]\n` +
        `模式：计划抽。报名后用 /mode ${sub.items.length} 标记已报名。`);
      return;
    }

    case '/list': {
      const sub = await getSub(env, chatId);
      if (sub.items.length === 0) {
        await tgSend(env, chatId, '关注列表为空。用 /watch 添加，详见 /help');
        return;
      }
      const lines: string[] = [`共 ${sub.items.length} 项（提前量 ${sub.leadHours.join(',')} 小时）：`];
      for (let i = 0; i < sub.items.length; i++) {
        const w = sub.items[i];
        const serverName = ALL_SERVERS.find(s => s.id === w.server)?.name ?? `${w.server}`;
        const pos = `${i + 1}. ${serverName} ${AREA_NAMES[w.area]} ${w.slot + 1}区 ${w.id}号 [${SIZE_NAMES[sizeOf(w.area, w.id)] ?? '?'}]`;
        const modeText = (w.mode === 0 ? '计划抽' : '已报名') + (w.entryNo ? ` #${w.entryNo}` : '');
        try {
          const sales = await getSales(env, w.server);
          const house = sales.find(h => h.Area === w.area && h.Slot === w.slot && h.ID === w.id);
          if (!house) {
            lines.push(`${pos}（${modeText}）\n　已售出或下架`);
          } else {
            const phase = getPhase(house, nowSec);
            lines.push(`${pos}（${modeText}）\n　${STATE_NAMES[phase.state]}${phase.estimated ? '（推测）' : ''} ${fmtRemain(nowSec, phase.end)} · ${fmtTime(phase.end)} 截止`);
          }
        } catch {
          lines.push(`${pos}（${modeText}）\n　数据获取失败`);
        }
      }
      await tgSend(env, chatId, lines.join('\n'));
      return;
    }

    case '/mode': {
      const n = parseInt(args[0] ?? '', 10);
      const sub = await getSub(env, chatId);
      if (Number.isNaN(n) || n < 1 || n > sub.items.length) {
        await tgSend(env, chatId, `格式：/mode 序号（1-${sub.items.length}，序号见 /list）`);
        return;
      }
      const item = sub.items[n - 1];
      item.mode = item.mode === 0 ? 1 : 0;
      // /mode 序号 号码 —— 号码是游戏里报名后给的申请号，纯备忘，可不填
      item.entryNo = item.mode === 1 ? (args[1] ?? '').trim().slice(0, 16) || undefined : undefined;
      await saveSub(env, sub);
      await tgSend(env, chatId, `第 ${n} 项已切换为「${item.mode === 1 ? '已报名' : '计划抽'}」`
        + (item.entryNo ? `，申请号码 #${item.entryNo}。` : '。')
        + (item.mode === 1 && !item.entryNo ? `\n记申请号码：/mode ${n} 号码（可不填）` : ''));
      return;
    }

    case '/unwatch': {
      const n = parseInt(args[0] ?? '', 10);
      const sub = await getSub(env, chatId);
      if (Number.isNaN(n) || n < 1 || n > sub.items.length) {
        await tgSend(env, chatId, `格式：/unwatch 序号（1-${sub.items.length}，序号见 /list）`);
        return;
      }
      sub.items.splice(n - 1, 1);
      await saveSub(env, sub);
      await tgSend(env, chatId, `已取消关注第 ${n} 项。`);
      return;
    }

    case '/lead': {
      const hours = (args[0] ?? '').split(',').map(s => parseInt(s.trim(), 10))
        .filter(h => !Number.isNaN(h) && h >= 0 && h <= 8760);
      if (hours.length === 0) {
        await tgSend(env, chatId, '格式：/lead 24,1（小时，逗号分隔）');
        return;
      }
      if (hours.length > 3) {
        await tgSend(env, chatId, '最多选 3 个提醒时间（微信渠道有频率限制）。');
        return;
      }
      const sub = await getSub(env, chatId);
      sub.leadHours = [...new Set(hours)].sort((a, b) => b - a);
      await saveSub(env, sub);
      await tgSend(env, chatId, `提前量已设为 ${sub.leadHours.join(',')} 小时。`);
      return;
    }

    case '/notify': {
      const sub = await getSub(env, chatId);
      const flags = sub.notify ?? NOTIFY_ALL;
      const keys: (keyof NotifyFlags)[] = ['entry', 'results', 'claim', 'deposit', 'next'];
      const labels = ['报名截止（申请期结束前）', '开奖（进入公示期）',
        '确认归属死线（公示期结束前，逾期扣 50%）',
        '抽签金返还死线（公示期后 90 天，要点门牌）', '下轮开抽（新申请期开始）'];
      const n = parseInt(args[0] ?? '', 10);
      if (!Number.isNaN(n) && n >= 1 && n <= keys.length) {
        const key = keys[n - 1];
        const next = { ...flags, [key]: !flags[key] };
        sub.notify = next;
        await saveSub(env, sub);
        await tgSend(env, chatId, `已${next[key] ? '开启' : '关闭'}：${labels[n - 1]}`);
        return;
      }
      await tgSend(env, chatId,
        '提醒开关（发 /notify 序号 切换）：\n' +
        keys.map((k, i) => `${i + 1}. ${flags[k] ? '✅' : '❌'} ${labels[i]}`).join('\n') +
        '\n\n' + `提前量：${sub.leadHours.join(',')} 小时（/lead 修改）`);
      return;
    }

    case '/name': {
      const name = args.join(' ').trim();
      const sub = await getSub(env, chatId);
      if (!name) {
        delete sub.nickname;
        await saveSub(env, sub);
        await tgSend(env, chatId, '已清除昵称。');
        return;
      }
      if (name.length > 16) {
        await tgSend(env, chatId, '昵称最长 16 个字符。');
        return;
      }
      sub.nickname = name;
      await saveSub(env, sub);
      await tgSend(env, chatId, `昵称已设为「${name}」，网页版顶部会显示。`);
      return;
    }

    case '/myhome': {
      // /myhome 服务器 房区 区号 房号 [角色名/备注]
      let tokens = args.flatMap(a => {
        const m = a.match(/^(\d+)区(\d+)号?$/);
        return m ? [m[1], m[2]] : [a];
      });
      if (tokens.length < 4) {
        await tgSend(env, chatId, '格式：/myhome 服务器 房区 区号 房号 [角色名]\n例：/myhome 萌芽池 白银乡 14 43 阿光');
        return;
      }
      const server = findServer(tokens[0]);
      const area = findArea(tokens[1]);
      const slot = parseInt(tokens[2], 10);
      const plotId = parseInt(tokens[3], 10);
      const label = tokens.slice(4).join(' ');
      if (!server || area < 0 || Number.isNaN(slot) || Number.isNaN(plotId)
        || slot < 1 || slot > 30 || plotId < 1 || plotId > 60) {
        await tgSend(env, chatId, '没看懂参数。服务器名见 /servers，房区：海雾村/薰衣草苗圃/高脚孤丘/白银乡/穹顶皓天，区号 1-30，房号 1-60。');
        return;
      }

      const sub = await getSub(env, chatId);
      sub.homes ??= [];
      const existing = sub.homes.find(h => h.server === server.id && h.area === area && h.slot === slot - 1 && h.id === plotId);
      if (existing) {
        if (isGroup && existing.ownerId && existing.ownerId !== sender?.id) {
          await tgSend(env, chatId, `这套房是 ${existing.ownerName ?? '别人'} 登记的，换个房号吧。`);
          return;
        }
        if (label) existing.label = label;
        await saveSub(env, sub);
        await tgSend(env, chatId, `这套房已登记过${label ? '，备注已更新' : ''}。`);
        return;
      }
      const nowSec = Math.floor(Date.now() / 1000);
      sub.homes.push({
        server: server.id, area, slot: slot - 1, id: plotId,
        label: label || '我的房',
        lastEnteredAt: nowSec, // 默认以登记时间为起点
        fired: [],
        ...(isGroup ? { ownerId: sender?.id, ownerName: whoIs(sender) } : {}),
      });
      await saveSub(env, sub);
      await tgSend(env, chatId,
        `🏠 已登记：${server.name} ${AREA_NAMES[area]} ${slot}区 ${plotId}号（${label || '我的房'}）`
        + (isGroup ? `，房主 ${whoIs(sender)}` : '') + `\n` +
        `倒计时 ${DEMOLITION_DAYS} 天起算。最近没进过屋的话，进屋后发 /entered 校准。`);
      return;
    }

    case '/entered': {
      const sub = await getSub(env, chatId);
      // 群里那份列表是全群的，序号要是按全群数，一个人有两套就得先去数别人的。
      // 所以群里只认自己的房，序号也按自己那几套从 1 数起
      const homes = mineOnly(sub.homes ?? [], isGroup, sender);
      if (homes.length === 0) {
        await tgSend(env, chatId, isGroup
          ? '你还没在这个群登记房产，用 /myhome 登记。'
          : '还没有登记房产，用 /myhome 登记。');
        return;
      }

      // 参数解析：数字=序号，日期（8-30 / 2026-08-30 / 8月30日）=补签日期
      let idx = homes.length === 1 ? 0 : -1;
      let dateSec = 0; // 0=现在
      for (const arg of args) {
        const asNum = parseInt(arg, 10);
        const dm = arg.match(/^(?:(\d{4})[-/年])?(\d{1,2})[-/月](\d{1,2})日?$/);
        if (dm) {
          const nowJst = new Date(Date.now() + JST * 1000);
          let year = dm[1] ? parseInt(dm[1], 10) : nowJst.getUTCFullYear();
          const month = parseInt(dm[2], 10) - 1;
          const day = parseInt(dm[3], 10);
          if (month < 0 || month > 11 || day < 1 || day > 31) {
            await tgSend(env, chatId, `日期「${arg}」看不懂，格式如 8-30 或 2026-08-30`);
            return;
          }
          let ts = Math.floor(Date.UTC(year, month, day) / 1000) - JST; // 日本时间当天 00:00
          if (ts > nowSec) ts = Math.floor(Date.UTC(year - 1, month, day) / 1000) - JST; // 未来日期视为去年
          dateSec = ts;
        } else if (!Number.isNaN(asNum) && idx === -1) {
          idx = asNum - 1;
        }
      }
      if (idx < 0) {
        const list = homes.map((h, i) => `${i + 1}. ${h.label}`).join('\n');
        await tgSend(env, chatId, `你有多套房产，请指定序号：/entered 序号 [日期]\n${list}`);
        return;
      }
      if (idx >= homes.length) {
        await tgSend(env, chatId, `序号超出范围（1-${homes.length}）。`);
        return;
      }
      if (isGroup && homes[idx].ownerId && homes[idx].ownerId !== sender?.id) {
        await tgSend(env, chatId, `第 ${idx + 1} 项是 ${homes[idx].ownerName ?? '别人'} 的房，只能本人打卡。`);
        return;
      }

      homes[idx].lastEnteredAt = dateSec > 0 ? dateSec : nowSec;
      homes[idx].fired = [];
      await saveSub(env, sub);
      const h = homes[idx];
      const serverName = ALL_SERVERS.find(s => s.id === h.server)?.name ?? `${h.server}`;
      const when = dateSec > 0 ? `（补签至 ${fmtDay(dateSec)}）` : '';
      await tgSend(env, chatId,
        `✅ 已打卡${when}：${serverName} ${AREA_NAMES[h.area]} ${h.slot + 1}区 ${h.id}号（${h.label}）\n` +
        `炸房倒计时重置为 ${DEMOLITION_DAYS} 天（到 ${fmtDay(dayDeadline(homes[idx].lastEnteredAt, DEMOLITION_DAYS))} 为止）。`);
      return;
    }

    case '/demolished': {
      const sub = await getSub(env, chatId);
      const homes = mineOnly(sub.homes ?? [], isGroup, sender);
      if (homes.length === 0) {
        await tgSend(env, chatId, isGroup
          ? '你还没在这个群登记房产，用 /myhome 登记。'
          : '还没有登记房产，用 /myhome 登记。');
        return;
      }
      let idx = homes.length === 1 ? 0 : -1;
      if (args.length > 0) {
        const n = parseInt(args[0], 10);
        if (!Number.isNaN(n)) idx = n - 1;
      }
      if (idx < 0 || idx >= homes.length) {
        const list = homes.map((h, i) => `${i + 1}. ${h.label}`).join('\n');
        await tgSend(env, chatId, `请指定序号：/demolished 序号\n${list}`);
        return;
      }
      const h = homes[idx];
      if (isGroup && h.ownerId && h.ownerId !== sender?.id) {
        await tgSend(env, chatId, `第 ${idx + 1} 项是 ${h.ownerName ?? '别人'} 的房，只能本人标记。`);
        return;
      }
      if (h.demolishedAt && h.demolishedAt > 0) {
        h.demolishedAt = 0;
        await saveSub(env, sub);
        await tgSend(env, chatId, `已取消「${h.label}」的炸房标记，恢复进屋倒计时。`);
      } else {
        h.demolishedAt = Math.floor(Date.now() / 1000);
        h.fired = [];
        await saveSub(env, sub);
        await tgSend(env, chatId,
          `已标记「${h.label}」被拆除。\n🪑 ${FURNITURE_DAYS} 天内去管理人处回收家具庭具 + 购地金的 80%（到 ${fmtDay(dayDeadline(h.demolishedAt, FURNITURE_DAYS))} 为止）。`);
      }
      return;
    }

    case '/homes': {
      const sub = await getSub(env, chatId);
      const homes = sub.homes ?? [];
      if (homes.length === 0) {
        await tgSend(env, chatId, '还没有登记房产，用 /myhome 登记。');
        return;
      }
      const nowSec = Math.floor(Date.now() / 1000);
      const cache = new Map<number, boolean>();
      const lines = await Promise.all(homes.map(async (h, i) => {
        const serverName = ALL_SERVERS.find(s => s.id === h.server)?.name ?? `${h.server}`;
        const full = `${serverName} ${AREA_NAMES[h.area]} ${h.slot + 1}区 ${h.id}号（${h.label}）`;
        // 群里全局序号没意义（命令只认自己那几套），改成标各人的第几套
        const seq = isGroup
          ? (() => {
              const mine = homes.filter(x => x.ownerId && x.ownerId === h.ownerId);
              return mine.length > 1 ? `#${mine.indexOf(h) + 1} ` : '';
            })()
          : `${i + 1}. `;
        const pos = seq + (isGroup ? await groupPos(env, h, full, cache) : full);
        if (h.demolishedAt && h.demolishedAt > 0) {
          const fDeadline = dayDeadline(h.demolishedAt, FURNITURE_DAYS);
          const days = Math.floor((fDeadline - nowSec) / 86400);
          return `${pos}\n　💥 已炸房，资产回收${days >= 0 ? `还剩 ${days} 天` : '已到期！'}（${fmtDay(fDeadline)} 到期）`;
        }
        if (h.lastEnteredAt <= 0) return `${pos}\n　进屋时间未知，进屋后发 /entered${seq.trim() ? ' ' + seq.trim().slice(1) : ''}`;
        const deadline = dayDeadline(h.lastEnteredAt, DEMOLITION_DAYS);
        const remain = deadline - nowSec;
        const days = Math.floor(remain / 86400);
        const mark = days <= 5 ? '🔴' : days <= 10 ? '🟠' : '🟢';
        return `${pos}\n　${mark} 剩余 ${days} 天（最后进屋 ${fmtDay(h.lastEnteredAt)}）`;
      }));
      await tgSend(env, chatId, lines.join('\n'));
      return;
    }

    default:
      // 群里别的 Bot 的命令也会被 Telegram 送过来（隐私模式只过滤非 / 开头的消息）。
      // 不是明确点名给我的就闭嘴，否则群里每有人用别的 Bot 我就刷一条「未知命令」
      if (isGroup && !addressed) return;
      await tgSend(env, chatId, '未知命令，发 /help 查看用法。');
  }
}

// ═══════════════ 提醒引擎 ═══════════════

interface DueReminder {
  chatId: number; title: string; body: string;
  /** 可选：一键打卡按钮对应的房产 */
  homeRef?: { server: number; area: number; slot: number; id: number };
}

async function runReminders(env: Env): Promise<void> {
  const nowSec = Math.floor(Date.now() / 1000);
  // 房主的「群内是否公开」设置存在他自己的订阅里，一轮跑下来只查一次
  const publicCache = new Map<number, boolean>();
  const subKeys = await env.KV.list({ prefix: 'sub:' });
  if (subKeys.keys.length === 0) return;

  // 先收集需要哪些服务器的数据
  const subs: UserSub[] = [];
  for (const key of subKeys.keys) {
    const sub = await env.KV.get<UserSub>(key.name, 'json');
    // 有关注或有房产的用户才参与提醒
    if (sub && (sub.items.length > 0 || (sub.homes?.length ?? 0) > 0)) subs.push(sub);
  }
  if (subs.length === 0) return;

  const serverIds = [...new Set(subs.flatMap(s => s.items.map(i => i.server)))];

  // 拉取（有 5 分钟 KV 缓存）
  const salesByServer = new Map<number, HouseEntry[]>();
  for (const serverId of serverIds) {
    try {
      salesByServer.set(serverId, await getSales(env, serverId));
    } catch (e) {
      console.error(`拉取服务器 ${serverId} 失败`, e);
    }
  }

  for (const sub of subs) {
    let dirty = false;
    const due: DueReminder[] = [];

    // ── 炸房提醒（45 天未进房 / 拆除后资产回收 35 天）──
    for (const h of sub.homes ?? []) {
      const serverName = ALL_SERVERS.find(s => s.id === h.server)?.name ?? `${h.server}`;
      // 群登记的房带上房主：提醒发到群里，别人得知道这条说的是谁
      const pos = await groupPos(env, h,
        `${serverName} ${AREA_NAMES[h.area]} ${h.slot + 1}区 ${h.id}号（${h.label}）`, publicCache);

      // 已炸房：资产回收 35 天死线
      if (h.demolishedAt && h.demolishedAt > 0) {
        const fDeadline = dayDeadline(h.demolishedAt, FURNITURE_DAYS);
        // 同上：只发当前所处的那一档（补的炸房日期很旧时，别先发一条过时的「还剩 10 天」）
        for (let i = 0; i < DEMOLITION_LEAD_DAYS.length; i++) {
          const days = DEMOLITION_LEAD_DAYS[i];
          const fireSec = fDeadline - days * 86400;
          const upper = i + 1 < DEMOLITION_LEAD_DAYS.length
            ? fDeadline - DEMOLITION_LEAD_DAYS[i + 1] * 86400
            : fDeadline;
          if (nowSec < fireSec || nowSec >= upper) continue;
          const key = `furn|${fDeadline}|${days}`;
          if (h.fired.includes(key)) continue;
          h.fired.push(key);
          dirty = true;
          due.push({
            chatId: sub.chatId,
            title: `🪑 拆除资产回收即将到期：还剩 ${days} 天`,
            body: `${pos}\n可去管理人处回收部分家具庭具 + 购地金的 80%，`
              + `${fmtDay(fDeadline)} 截止，逾期无法回收！`,
            homeRef: { server: h.server, area: h.area, slot: h.slot, id: h.id },
          });
        }
        if (nowSec >= fDeadline && !h.fired.includes(`furn|${fDeadline}|over`)) {
          h.fired.push(`furn|${fDeadline}|over`);
          dirty = true;
          due.push({
            chatId: sub.chatId,
            title: '🪑 拆除资产回收已到期',
            body: `${pos}\n回收期限已到（家具庭具 + 购地金的 80%）！没回收的话立刻去管理人处确认！`,
            homeRef: { server: h.server, area: h.area, slot: h.slot, id: h.id },
          });
        }
        continue; // 炸房的不再做进房倒计时
      }

      if (h.lastEnteredAt <= 0) continue;
      const deadlineSec = dayDeadline(h.lastEnteredAt, DEMOLITION_DAYS);

      // 只发当前所处的那一档：补签一个很旧的进屋日期时，剩 5 天却先发一条「还剩 10 天」是错的
      for (let i = 0; i < DEMOLITION_LEAD_DAYS.length; i++) {
        const days = DEMOLITION_LEAD_DAYS[i];
        const fireSec = deadlineSec - days * 86400;
        const upper = i + 1 < DEMOLITION_LEAD_DAYS.length
          ? deadlineSec - DEMOLITION_LEAD_DAYS[i + 1] * 86400
          : deadlineSec;
        if (nowSec < fireSec || nowSec >= upper) continue;
        const key = `demo|${deadlineSec}|${days}`;
        if (h.fired.includes(key)) continue;
        h.fired.push(key);
        if (h.fired.length > 20) h.fired.splice(0, h.fired.length - 20);
        dirty = true;
        due.push({
          chatId: sub.chatId,
          title: days >= 15 ? '⚠️ 已进入自动拆除准备' : `🚨 炸房警告：还剩 ${days} 天`,
          // 15 天档＝连续 30 天未进屋，游戏里此时才刚被列为撤除对象（任务情报里会显示）
          body: `${pos}\n已超过 ${DEMOLITION_DAYS - days} 天未进屋，`
            + (days >= 15
                ? `已被列为撤除对象（任务情报-房屋可见剩余天数）。`
                : days <= 1
                  ? `今天必须进屋，否则将被自动拆除！`
                  : `记得上线进一次屋（要进入室内才算）。`)
            + `\n部队房任一成员进屋即可。进屋后点下方按钮打卡。`,
          homeRef: { server: h.server, area: h.area, slot: h.slot, id: h.id },
        });
      }

      // 已过期（只提醒一次）
      if (nowSec >= deadlineSec && !h.fired.includes(`demo|${deadlineSec}|over`)) {
        h.fired.push(`demo|${deadlineSec}|over`);
        dirty = true;
        due.push({
          chatId: sub.chatId,
          title: '🚨 炸房倒计时已到',
          body: `${pos}\n已超过 ${DEMOLITION_DAYS} 天未进屋，可能已进入拆除流程！请立即上线进屋抢救！`,
          homeRef: { server: h.server, area: h.area, slot: h.slot, id: h.id },
        });
      }
    }

    const notify = sub.notify ?? NOTIFY_ALL;

    // 群订阅（Telegram 的群 chat id 是负数）只跑房产那段，抽房不发到群里
    for (const w of sub.chatId < 0 ? [] : sub.items) {
      const serverName = ALL_SERVERS.find(s => s.id === w.server)?.name ?? `${w.server}`;
      const pos = `${serverName} ${AREA_NAMES[w.area]} ${w.slot + 1}区 ${w.id}号 [${SIZE_NAMES[sizeOf(w.area, w.id)] ?? '?'}]`;

      // 抽签金返还：死线在公示期结束后 90 天，那时房子早已不在在售列表里，
      // 所以这段必须走在下面的 `if (!house) continue` 之前，只认关注项自己记的死线
      if (w.depositDeadline) {
        if (nowSec >= w.depositDeadline) {
          w.depositDeadline = undefined;   // 已到期，别再留着
          dirty = true;
        } else if (notify.deposit) {
          for (const h of sub.leadHours) {
            if (nowSec < w.depositDeadline - h * 3600) continue;
            const key = `${w.server}:${w.area}:${w.slot}:${w.id}|5|${w.depositDeadline}|${h}`;
            if (w.fired.includes(key)) continue;
            w.fired.push(key);
            if (w.fired.length > 50) w.fired.splice(0, w.fired.length - 50);
            dirty = true;
            due.push({
              chatId: sub.chatId,
              title: '💰 抽签金返还即将截止',
              body: `${pos}\n申请时全额支付的金币要点门牌确认才返还，系统不会自动退！`
                + `\n${fmtTime(w.depositDeadline)} 截止（公示期后 ${DEPOSIT_DAYS} 天），逾期不再返还。`,
            });
          }
        }
      }

      const house = salesByServer.get(w.server)
        ?.find(h => h.Area === w.area && h.Slot === w.slot && h.ID === w.id);
      if (!house) continue;

      const phase = getPhase(house, nowSec);
      const suffix = (phase.estimated ? '\n（推测数据，建议登录游戏复核）' : '')
        + (nowSec - house.LastSeen > 7200 ? '\n⚠ 数据已较久未更新，请以游戏内实际为准' : '');

      const consider = (type: number, leadH: number | null, fireAtSec: number, title: string, body: string, anchorSec?: number) => {
        const anchor = anchorSec ?? phase.end;
        // 提前量已过但阶段未结束：立即补发一次。
        // 去重位统一写成 now——否则 24h/1h 两个提前量都已过时会在同一秒发出两条一模一样的
        let fire = fireAtSec;
        let leadKey: string = `${leadH ?? 'x'}`;
        if (fire <= nowSec && anchor > nowSec) { fire = nowSec; leadKey = 'now'; }
        if (fire > nowSec) return; // 未到时间
        const key = `${w.server}:${w.area}:${w.slot}:${w.id}|${type}|${anchor}|${leadKey}`;
        if (w.fired.includes(key)) return;
        w.fired.push(key);
        if (w.fired.length > 50) w.fired.splice(0, w.fired.length - 50);
        dirty = true;
        due.push({ chatId: sub.chatId, title, body: body + suffix });
      };

      if (phase.state === 1) {
        // 新一轮开抽：挂在申请期开始那一刻发，同样不能挂在上一阶段的结束
        if (w.mode === 0 && notify.next) {
          consider(3, null, phase.end - ENTRY_SEC, '🔔 新一轮抽签开始',
            `${pos}
已开放抽签预约，申请期将于 ${fmtTime(phase.end)} 截止，想去抽记得上线报名！`);
        }
        if (w.mode === 0) {
          if (notify.entry) {
            for (const h of sub.leadHours) {
              consider(0, h, phase.end - h * 3600, '⏰ 抽房报名即将截止',
                `${pos}\n申请期将于 ${fmtTime(phase.end)} 截止，想去抽记得上线报名！`
                + `\n报名需全额支付土地价格，参加后无法取消。`);
            }
          }
        }
      } else if (phase.state === 2) {
        // 开奖：申请期一结束就进公示期，挂在公示期开始那一刻发（挂在阶段结束发永远等不到）
        if (w.mode === 1 && notify.results) {
          consider(1, null, phase.end - RESULTS_SEC, '🎉 抽房结果已公布',
            `${pos}
已进入公示期，你参与抽签的房子开奖了，快去查看结果！`
            + `
公示期将于 ${fmtTime(phase.end)} 截止。`);
        }
        // 确认归属死线（两种模式都提醒）
        if (notify.claim) {
          for (const h of sub.leadHours) {
            consider(2, h, phase.end - h * 3600, '⚠️ 公示期即将截止（确认归属死线）',
              `${pos}
公示期将于 ${fmtTime(phase.end)} 截止。中签请立即购入，逾期将失去资格并被扣除 50% 申请金！`);
          }
        }
        // 已报名的，把抽签金返还死线记在关注项上；房子从在售列表消失后还能按它提醒
        if (w.mode === 1) {
          const depositEnd = phase.end + DEPOSIT_DAYS * 86400;
          if (w.depositDeadline !== depositEnd) {
            w.depositDeadline = depositEnd;
            dirty = true;
          }
        }
      }
    }

    for (const r of due) {
      // 匿名账号（没绑 TG）不发 Telegram，只走 Bark / 微信
      if (r.chatId && r.homeRef) {
        const ref = r.homeRef;
        await tgSendWithButton(env, r.chatId, `${r.title}\n\n${r.body}`,
          '✅ 已进屋（重置倒计时）', `entered:${ref.server}:${ref.area}:${ref.slot}:${ref.id}`);
        // 渠道优先级：Telegram → Bark → 微信
        if (sub.barkKey) await barkSend(env, sub.barkKey, r.title, r.body);
        if (sub.wxpusherSpt) await wxSend(env, sub.wxpusherSpt, r.title, r.body);
      } else {
        await pushToSub(env, sub, r.title, r.body);
      }
    }
    if (dirty) await saveSub(env, sub);
  }

  console.log(`提醒检查完成：${subs.length} 个订阅，${serverIds.length} 个服务器`);
}

/** 「抽了」的回执：房子信息 + 申请号码（填了才有）+ 本轮截止时间 */
async function pushEntered(env: Env, sub: UserSub, w: WatchItem): Promise<void> {
  const serverName = ALL_SERVERS.find(x => x.id === w.server)?.name ?? `${w.server}`;
  const size = SIZE_NAMES[sizeOf(w.area, w.id)] ?? '?';
  let body = `${serverName} ${AREA_NAMES[w.area]} ${w.slot + 1}区 ${w.id}号 [${size}]`;
  if (w.entryNo) body += `\n申请号码 #${w.entryNo}`;
  try {
    const house = (await getSales(env, w.server))
      .find(h => h.Area === w.area && h.Slot === w.slot && h.ID === w.id);
    if (house) {
      const phase = getPhase(house, Math.floor(Date.now() / 1000));
      body += `\n${phase.state === 1 ? '申请期' : '本阶段'}将于 ${fmtTime(phase.end)} 结束`;
    }
  } catch { /* 拿不到房屋数据不影响回执 */ }
  body += `\n开奖和确认归属的提醒都会照常发。`;
  await pushToSub(env, sub, '📝 已记下：你报名了', body);
}

// ═══════════════ Web API ═══════════════

function json(data: unknown, status = 200): Response {
  return new Response(JSON.stringify(data), {
    status,
    headers: { 'Content-Type': 'application/json; charset=utf-8' },
  });
}

/** 关注项 + 实时状态（供网页版展示） */
async function enrichWatch(env: Env, sub: UserSub): Promise<unknown[]> {
  const nowSec = Math.floor(Date.now() / 1000);
  const result: unknown[] = [];
  for (const w of sub.items) {
    const serverName = ALL_SERVERS.find(s => s.id === w.server)?.name ?? `${w.server}`;
    const base = {
      server: w.server, serverName, area: w.area, areaName: AREA_NAMES[w.area],
      slot: w.slot, slotNo: w.slot + 1, id: w.id,
      size: SIZE_NAMES[sizeOf(w.area, w.id)] ?? '?', mode: w.mode, entryNo: w.entryNo ?? '',
    };
    try {
      const sales = await getSales(env, w.server);
      const house = sales.find(h => h.Area === w.area && h.Slot === w.slot && h.ID === w.id);
      if (!house) {
        result.push({ ...base, gone: true });
      } else {
        const phase = getPhase(house, nowSec);
        const rep = (await getReports(env, w.server))[repKey(w.area, w.slot, w.id)];
        const fresh = rep && nowSec - rep[1] < CYCLE_SEC ? rep : null;
        result.push({
          ...base,
          gone: false,
          price: house.Price,
          participate: fresh ? fresh[0] : house.Participate,
          participateAt: fresh ? fresh[1] : 0,
          state: phase.state, stateName: STATE_NAMES[phase.state],
          estimated: phase.estimated, phaseEnd: phase.end,
          stale: nowSec - house.LastSeen > 7200,
          purchaseType: house.PurchaseType, regionType: house.RegionType,
        });
      }
    } catch {
      result.push({ ...base, gone: false, error: true });
    }
  }
  return result;
}

interface GhAsset { name: string; size: number; browser_download_url: string }
interface GhRelease { tag_name: string; html_url: string; body?: string; assets: GhAsset[] }

/** 最新 release（缓存 10 分钟：更新检查是每次启动都发的，别把 GitHub 的额度耗光） */
async function getLatestRelease(env: Env): Promise<GhRelease> {
  const cached = await env.KV.get<{ at: number; rel: GhRelease }>('release:latest', 'json');
  const nowSec = Math.floor(Date.now() / 1000);
  if (cached && nowSec - cached.at < 600) return cached.rel;

  const resp = await fetch('https://api.github.com/repos/fivood/ffxivhouse/releases/latest', {
    headers: { 'User-Agent': UA, Accept: 'application/vnd.github+json' },
  });
  if (!resp.ok) throw new Error(`GitHub ${resp.status}`);
  const rel = (await resp.json()) as GhRelease;
  await env.KV.put('release:latest', JSON.stringify({ at: nowSec, rel }), { expirationTtl: 3600 });
  return rel;
}

async function handleApi(request: Request, env: Env, url: URL): Promise<Response> {
  const path = url.pathname;

  if (path === '/api/servers' && request.method === 'GET') {
    return json(DATA_CENTERS.map(dc => ({ name: dc.name, servers: dc.servers })));
  }

  if (path === '/api/sales' && request.method === 'GET') {
    const server = parseInt(url.searchParams.get('server') ?? '', 10);
    if (Number.isNaN(server)) return json({ error: 'server 参数无效' }, 400);
    try {
      const nowSec = Math.floor(Date.now() / 1000);
      const entries = await getSales(env, server);
      const reports = await getReports(env, server);
      return json(entries.map(h => {
        const phase = getPhase(h, nowSec);
        // 超过一个周期的旧数据当没有：那是上一轮的人数
        const rep = reports[repKey(h.Area, h.Slot, h.ID)];
        const fresh = rep && nowSec - rep[1] < CYCLE_SEC ? rep : null;
        return {
          server: h.Server, area: h.Area, areaName: AREA_NAMES[h.Area] ?? '?',
          slot: h.Slot, slotNo: h.Slot + 1, id: h.ID, price: h.Price,
          size: SIZE_NAMES[h.Size >= 0 && h.Size <= 2 ? h.Size : sizeOf(h.Area, h.ID)] ?? '?',
          state: phase.state, stateName: STATE_NAMES[phase.state],
          estimated: phase.estimated, phaseEnd: phase.end,
          participate: fresh ? fresh[0] : h.Participate,
          participateAt: fresh ? fresh[1] : 0,
          stale: nowSec - h.LastSeen > 7200,
          purchaseType: h.PurchaseType, regionType: h.RegionType,
        };
      }));
    } catch {
      return json({ error: '数据获取失败' }, 502);
    }
  }

  if (path === '/api/watch' && request.method === 'GET') {
    const chatId = await checkAuth(env, url);
    if (chatId == null) return json({ error: '未绑定或令牌无效，给 Bot 发 /link 拿新链接' }, 401);
    const sub = await getSub(env, chatId);
    const nowSec = Math.floor(Date.now() / 1000);
    return json({
      leadHours: sub.leadHours,
      notify: sub.notify ?? NOTIFY_ALL,
      groupPublic: sub.groupPublic !== false,
      barkKey: sub.barkKey ?? '',
      wxpusherSpt: sub.wxpusherSpt ?? '',
      nickname: sub.nickname ?? '',
      homes: (sub.homes ?? []).map(h => ({
        server: h.server, serverName: ALL_SERVERS.find(s => s.id === h.server)?.name ?? `${h.server}`,
        area: h.area, areaName: AREA_NAMES[h.area], slot: h.slot, slotNo: h.slot + 1,
        id: h.id, label: h.label,
        lastEnteredAt: h.lastEnteredAt,
        demolishedAt: h.demolishedAt ?? 0,
        deadline: h.lastEnteredAt > 0 ? dayDeadline(h.lastEnteredAt, DEMOLITION_DAYS) : 0,
        furnitureDeadline: (h.demolishedAt ?? 0) > 0 ? dayDeadline(h.demolishedAt ?? 0, FURNITURE_DAYS) : 0,
        remainDays: h.lastEnteredAt > 0 ? Math.floor((dayDeadline(h.lastEnteredAt, DEMOLITION_DAYS) - nowSec) / 86400) : -1,
      })),
      items: await enrichWatch(env, sub),
    });
  }

  if (path === '/api/watch' && request.method === 'POST') {
    const body = (await request.json()) as { u?: number; k?: string; server?: number; area?: number; slot?: number; id?: number };
    const chatId = await checkAuthBody(env, body);
    if (chatId == null) return json({ error: '未绑定或令牌无效' }, 401);
    const { server, area, slot, id } = body;
    if (typeof server !== 'number' || typeof area !== 'number' || typeof slot !== 'number' || typeof id !== 'number'
      || !ALL_SERVERS.some(s => s.id === server) || area < 0 || area > 4 || slot < 0 || slot > 29 || id < 1 || id > 60) {
      return json({ error: '参数无效' }, 400);
    }
    const sub = await getSub(env, chatId);
    if (sub.items.some(i => i.server === server && i.area === area && i.slot === slot && i.id === id)) {
      return json({ ok: true, message: '已在关注列表中' });
    }
    sub.items.push({ server, area, slot, id, mode: 0, fired: [] });
    await saveSub(env, sub);
    return json({ ok: true });
  }

  if (path === '/api/watch' && request.method === 'DELETE') {
    const body = (await request.json()) as { u?: number; k?: string; server?: number; area?: number; slot?: number; id?: number };
    const chatId = await checkAuthBody(env, body);
    if (chatId == null) return json({ error: '未绑定或令牌无效' }, 401);
    const sub = await getSub(env, chatId);
    const before = sub.items.length;
    sub.items = sub.items.filter(i =>
      !(i.server === body.server && i.area === body.area && i.slot === body.slot && i.id === body.id));
    if (sub.items.length === before) return json({ error: '未找到该关注项' }, 404);
    await saveSub(env, sub);
    return json({ ok: true });
  }

  if (path === '/api/mode' && request.method === 'POST') {
    const body = (await request.json()) as { u?: number; k?: string; server?: number; area?: number; slot?: number; id?: number; mode?: number; entryNo?: string };
    const chatId = await checkAuthBody(env, body);
    if (chatId == null) return json({ error: '未绑定或令牌无效' }, 401);
    const sub = await getSub(env, chatId);
    const item = sub.items.find(i =>
      i.server === body.server && i.area === body.area && i.slot === body.slot && i.id === body.id);
    if (!item) return json({ error: '未找到该关注项' }, 404);
    const was = { mode: item.mode, entryNo: item.entryNo };
    // 带 mode 就是明确指定（插件/桌面端用，重复调用结果一样）；不带才是切换
    item.mode = body.mode === 0 || body.mode === 1 ? body.mode : item.mode === 0 ? 1 : 0;
    // 申请号码只在「已报名」时有意义，改回计划抽就清掉
    item.entryNo = item.mode === 1 ? (body.entryNo ?? '').trim().slice(0, 16) || undefined : undefined;
    await saveSub(env, sub);
    // 回执只在真有变化时发：插件会反复上报同一条，没变就别响
    if (item.mode === 1 && (was.mode !== 1 || was.entryNo !== item.entryNo))
      await pushEntered(env, sub, item);
    return json({ ok: true, mode: item.mode });
  }

  // 插件逛房区时上报整区 60 块地的归属。
  // 支持一次传多个小区——有插件会在登录时把全服 150 个小区一次刷完，
  // 一个小区发一次请求的话，同一个 KV 键上的并发读改写会互相覆盖，丢掉大半
  if (path === '/api/ward' && request.method === 'POST') {
    const body = (await request.json()) as {
      u?: number; k?: string; server?: number;
      area?: number; slot?: number; owned?: string;
      wards?: { area?: number; slot?: number; owned?: string }[];
    };
    if ((await checkAuthBody(env, body)) == null) return json({ error: '未绑定或令牌无效' }, 401);

    const server = body.server;
    if (typeof server !== 'number' || !ALL_SERVERS.some(s => s.id === server)) {
      return json({ error: 'server 参数无效' }, 400);
    }

    const incoming = body.wards ?? [{ area: body.area, slot: body.slot, owned: body.owned }];
    const nowSec = Math.floor(Date.now() / 1000);
    const wards = await getWards(env, server);
    let count = 0;
    for (const w of incoming.slice(0, 200)) {
      if (typeof w.area !== 'number' || w.area < 0 || w.area > 4) continue;
      if (typeof w.slot !== 'number' || w.slot < 0 || w.slot > 29) continue;
      if (typeof w.owned !== 'string' || !/^[01]{60}$/.test(w.owned)) continue;
      wards[wardKey(w.area, w.slot)] = [nowSec, w.owned];
      count++;
    }
    if (count === 0) return json({ error: '参数无效（owned 需要 60 位 0/1）' }, 400);

    // 三十天没人再逛过的就别留着了，房区变化很快
    for (const [k, v] of Object.entries(wards)) if (nowSec - v[0] > 30 * 86400) delete wards[k];
    await env.KV.put(`wards:${server}`, JSON.stringify(wards), { expirationTtl: 60 * 86400 });
    return json({ ok: true, count });
  }

  // 房区空置情况（网页的房区图用；没人逛过的房区就没有，前端退回按在售列表推测）
  if (path === '/api/wards' && request.method === 'GET') {
    const server = parseInt(url.searchParams.get('server') ?? '', 10);
    if (Number.isNaN(server)) return json({ error: 'server 参数无效' }, 400);
    const wards = await getWards(env, server);
    const out: Record<string, { at: number; owned: string }> = {};
    for (const [k, v] of Object.entries(wards)) out[k] = { at: v[0], owned: v[1] };
    return json(out);
  }

  // ── 桌面端更新：版本检查和安装包都从这儿走 ──
  // 国内直连 GitHub Release 经常超时，绕一道自己的域名稳得多，
  // 顺带躲开 GitHub API 对未认证请求的速率限制
  if (path === '/api/latest' && request.method === 'GET') {
    try {
      const rel = await getLatestRelease(env);
      const asset = rel.assets.find(a => a.name.endsWith('-public.zip'));
      if (!asset) return json({ error: '这一版没有公开版安装包' }, 404);
      return json({
        version: rel.tag_name.replace(/^[vV]/, ''),
        name: asset.name,
        size: asset.size,
        url: `${WEB_BASE}/dl/${encodeURIComponent(asset.name)}`,
        page: rel.html_url,
        notes: (rel.body ?? '').slice(0, 2000),
      });
    } catch (e) {
      return json({ error: `拿不到版本信息：${e}` }, 502);
    }
  }

  // 插件上报门牌上的已抽选人数（游戏内才看得到，售楼中心拿不到）
  if (path === '/api/report' && request.method === 'POST') {
    const body = (await request.json()) as {
      u?: number; k?: string;
      reports?: { server?: number; area?: number; slot?: number; id?: number; participate?: number }[];
    };
    if ((await checkAuthBody(env, body)) == null) return json({ error: '未绑定或令牌无效' }, 401);

    const nowSec = Math.floor(Date.now() / 1000);
    const byServer = new Map<number, Reports>();
    let count = 0;
    for (const r of (body.reports ?? []).slice(0, 300)) {
      const { server, area, slot, id, participate } = r;
      if (typeof server !== 'number' || !ALL_SERVERS.some(s => s.id === server)) continue;
      if (typeof area !== 'number' || area < 0 || area > 4) continue;
      if (typeof slot !== 'number' || slot < 0 || slot > 29) continue;
      if (typeof id !== 'number' || id < 1 || id > 60) continue;
      if (typeof participate !== 'number' || participate < 0 || participate > 999) continue;
      let m = byServer.get(server);
      if (!m) { m = await getReports(env, server); byServer.set(server, m); }
      m[repKey(area, slot, id)] = [Math.floor(participate), nowSec];
      count++;
    }
    if (count === 0) return json({ error: 'reports 为空或参数无效' }, 400);

    for (const [server, m] of byServer) {
      // 顺手清掉超过一个周期的旧记录，别让这个 key 无限长大
      for (const [k, v] of Object.entries(m)) if (nowSec - v[1] > CYCLE_SEC) delete m[k];
      await env.KV.put(`rep:${server}`, JSON.stringify(m), { expirationTtl: CYCLE_SEC * 2 });
    }
    return json({ ok: true, count });
  }

  // 匿名账号：没有 Telegram 也能用。前端第一次写数据时调用
  if (path === '/api/register' && request.method === 'POST') {
    const id = 'a' + crypto.randomUUID().replace(/-/g, '').slice(0, 15);
    await saveSub(env, { id, chatId: 0, leadHours: [24, 1], items: [] });
    return json({ u: id, k: await bindToken(env, id) });
  }

  // 绑 TG 时把匿名账号的数据并过去，免得攒的关注白丢
  if (path === '/api/merge' && request.method === 'POST') {
    const body = (await request.json()) as { u?: string; k?: string; fromU?: string; fromK?: string };
    const to = await checkAuthBody(env, body);
    const from = await checkAuthBody(env, { u: body.fromU, k: body.fromK });
    if (to == null || from == null) return json({ error: '令牌无效' }, 401);
    if (to === from) return json({ ok: true, merged: 0 });

    const dst = await getSub(env, to);
    const src = await getSub(env, from);
    const keyOf = (w: { server: number; area: number; slot: number; id: number }) =>
      `${w.server}:${w.area}:${w.slot}:${w.id}`;
    const have = new Set(dst.items.map(keyOf));
    let merged = 0;
    for (const w of src.items) if (!have.has(keyOf(w))) { dst.items.push(w); merged++; }
    const homes = dst.homes ?? [];
    const haveHome = new Set(homes.map(keyOf));
    for (const h of src.homes ?? []) if (!haveHome.has(keyOf(h))) { homes.push(h); merged++; }
    dst.homes = homes;
    dst.barkKey ??= src.barkKey;
    dst.wxpusherSpt ??= src.wxpusherSpt;
    dst.notify ??= src.notify;
    await saveSub(env, dst);
    await env.KV.delete(`sub:${from}`);
    return json({ ok: true, merged });
  }

  // Mini App 免绑定登录：拿 initData 换这个人的 u/k
  if (path === '/api/tgauth' && request.method === 'POST') {
    const body = (await request.json()) as { initData?: string };
    const chatId = await verifyInitData(env, body.initData ?? '');
    if (chatId == null) return json({ error: 'initData 校验失败' }, 401);
    return json({ u: chatId, k: await bindToken(env, chatId) });
  }

  // 分项提醒开关
  if (path === '/api/notify' && request.method === 'POST') {
    const body = (await request.json()) as { u?: number; k?: string; notify?: Partial<NotifyFlags> };
    const chatId = await checkAuthBody(env, body);
    if (chatId == null) return json({ error: '未绑定或令牌无效' }, 401);
    const inc = body.notify ?? {};
    const sub2 = await getSub(env, chatId);
    const cur = sub2.notify ?? NOTIFY_ALL;
    const next: NotifyFlags = {
      entry: typeof inc.entry === 'boolean' ? inc.entry : cur.entry,
      results: typeof inc.results === 'boolean' ? inc.results : cur.results,
      claim: typeof inc.claim === 'boolean' ? inc.claim : cur.claim,
      deposit: typeof inc.deposit === 'boolean' ? inc.deposit : cur.deposit,
      next: typeof inc.next === 'boolean' ? inc.next : cur.next,
    };
    sub2.notify = next;
    await saveSub(env, sub2);
    return json({ ok: true, notify: next });
  }

  // 群内是否公开自己房子的位置（个人设置，影响 Bot 在群里怎么写你的房）
  if (path === '/api/grouppublic' && request.method === 'POST') {
    const body = (await request.json()) as { u?: number; k?: string; open?: boolean };
    const chatId = await checkAuthBody(env, body);
    if (chatId == null) return json({ error: '未绑定或令牌无效' }, 401);
    const sub = await getSub(env, chatId);
    sub.groupPublic = body.open !== false;
    await saveSub(env, sub);
    return json({ ok: true, groupPublic: sub.groupPublic });
  }

  if (path === '/api/lead' && request.method === 'POST') {
    const body = (await request.json()) as { u?: number; k?: string; hours?: number[] };
    const chatId = await checkAuthBody(env, body);
    if (chatId == null) return json({ error: '未绑定或令牌无效' }, 401);
    const hours = (body.hours ?? []).filter(h => typeof h === 'number' && h >= 0 && h <= 8760);
    if (hours.length === 0) return json({ error: 'hours 参数无效' }, 400);
    if (hours.length > 3) return json({ error: '最多选 3 个提醒时间（微信渠道有频率限制）' }, 400);
    const sub = await getSub(env, chatId);
    sub.leadHours = [...new Set(hours)].sort((a, b) => b - a);
    await saveSub(env, sub);
    return json({ ok: true, leadHours: sub.leadHours });
  }

  if (path === '/api/home' && request.method === 'POST') {
    const body = (await request.json()) as { u?: number; k?: string; server?: number; area?: number; slot?: number; id?: number; label?: string };
    const chatId = await checkAuthBody(env, body);
    if (chatId == null) return json({ error: '未绑定或令牌无效' }, 401);
    const { server, area, slot, id } = body;
    if (typeof server !== 'number' || typeof area !== 'number' || typeof slot !== 'number' || typeof id !== 'number'
      || !ALL_SERVERS.some(s => s.id === server) || area < 0 || area > 4 || slot < 0 || slot > 29 || id < 1 || id > 60) {
      return json({ error: '参数无效' }, 400);
    }
    const sub = await getSub(env, chatId);
    sub.homes ??= [];
    const existing = sub.homes.find(h => h.server === server && h.area === area && h.slot === slot && h.id === id);
    if (existing) {
      if (body.label) existing.label = body.label.slice(0, 16);
      await saveSub(env, sub);
      return json({ ok: true, message: '已登记过，备注已更新' });
    }
    sub.homes.push({
      server, area, slot, id,
      label: (body.label ?? '').slice(0, 16) || '我的房',
      lastEnteredAt: Math.floor(Date.now() / 1000),
      fired: [],
    });
    await saveSub(env, sub);
    return json({ ok: true });
  }

  if (path === '/api/home' && request.method === 'DELETE') {
    const body = (await request.json()) as { u?: number; k?: string; server?: number; area?: number; slot?: number; id?: number };
    const chatId = await checkAuthBody(env, body);
    if (chatId == null) return json({ error: '未绑定或令牌无效' }, 401);
    const sub = await getSub(env, chatId);
    const before = (sub.homes ?? []).length;
    sub.homes = (sub.homes ?? []).filter(h =>
      !(h.server === body.server && h.area === body.area && h.slot === body.slot && h.id === body.id));
    if (sub.homes.length === before) return json({ error: '未找到该房产' }, 404);
    await saveSub(env, sub);
    return json({ ok: true });
  }

  if (path === '/api/entered' && request.method === 'POST') {
    const body = (await request.json()) as { u?: number; k?: string; server?: number; area?: number; slot?: number; id?: number; date?: string };
    const chatId = await checkAuthBody(env, body);
    if (chatId == null) return json({ error: '未绑定或令牌无效' }, 401);
    const sub = await getSub(env, chatId);
    const home = (sub.homes ?? []).find(h =>
      h.server === body.server && h.area === body.area && h.slot === body.slot && h.id === body.id);
    if (!home) return json({ error: '未找到该房产' }, 404);

    // 可选补签日期（YYYY-MM-DD），按日本时间当天 00:00 起算（游戏就是这么数天的）
    const day = body.date ? parseDayStart(body.date) : null;
    if (day && 'error' in day) return json(day, 400);
    home.lastEnteredAt = day ? day.ts : Math.floor(Date.now() / 1000);
    home.fired = [];
    await saveSub(env, sub);
    return json({ ok: true });
  }

  if (path === '/api/demolished' && request.method === 'POST') {
    const body = (await request.json()) as { u?: number; k?: string; server?: number; area?: number; slot?: number; id?: number; date?: string };
    const chatId = await checkAuthBody(env, body);
    if (chatId == null) return json({ error: '未绑定或令牌无效' }, 401);
    const sub = await getSub(env, chatId);
    const home = (sub.homes ?? []).find(h =>
      h.server === body.server && h.area === body.area && h.slot === body.slot && h.id === body.id);
    if (!home) return json({ error: '未找到该房产' }, 404);
    // 带日期=按该日期标记/更正炸房日；不带=在标记与取消之间切换
    const day = body.date ? parseDayStart(body.date) : null;
    if (day && 'error' in day) return json(day, 400);
    home.demolishedAt = day ? day.ts
      : (home.demolishedAt && home.demolishedAt > 0) ? 0 : Math.floor(Date.now() / 1000);
    home.fired = [];
    await saveSub(env, sub);
    return json({ ok: true, demolishedAt: home.demolishedAt });
  }

  if (path === '/api/bark' && request.method === 'POST') {
    const body = (await request.json()) as { u?: number; k?: string; key?: string };
    const chatId = await checkAuthBody(env, body);
    if (chatId == null) return json({ error: '未绑定或令牌无效' }, 401);
    let key = (body.key ?? '').trim();
    // 允许直接粘 Bark App 里那条完整推送地址（自建服务器就是这么用的）
    if (/^https?:\/\//i.test(key)) {
      key = key.replace(/\/+$/, '');
      // 自建地址必须带上 key 那一段，只给域名会 POST 到根路径、静默失败
      let path = '';
      try { path = new URL(key).pathname.replace(/^\/+|\/+$/g, ''); } catch { path = ''; }
      if (!path) {
        return json({ error: '自建服务器地址要带上 key，例：https://你的域名/AbCd1234' }, 400);
      }
    } else if (key && !/^[A-Za-z0-9_-]{6,64}$/.test(key)) {
      return json({ error: 'Bark key 格式不对（应是一串字母数字，或自建服务器带 key 的完整地址）' }, 400);
    }
    const sub2 = await getSub(env, chatId);
    if (key) {
      // 先试着推一条，通了才存——设备 token 和 key 长得都像，填错了只会静默失败
      const r = await barkSend(env, key, '抽房了吗', 'Bark 推送已开启，这是一条测试。');
      if (!r.ok) return json({ error: `Bark 推送不通，key 可能填错了：${r.msg}` }, 400);
      sub2.barkKey = key;
    } else {
      delete sub2.barkKey;
    }
    await saveSub(env, sub2);
    return json({ ok: true, barkKey: sub2.barkKey ?? '' });
  }

  if (path === '/api/wxpusher' && request.method === 'POST') {
    const body = (await request.json()) as { u?: number; k?: string; spt?: string };
    const chatId = await checkAuthBody(env, body);
    if (chatId == null) return json({ error: '未绑定或令牌无效' }, 401);
    const spt = (body.spt ?? '').trim();
    if (spt && !spt.startsWith('SPT_')) return json({ error: 'SPT 格式不对（应以 SPT_ 开头）' }, 400);
    const sub = await getSub(env, chatId);
    if (spt) sub.wxpusherSpt = spt; else delete sub.wxpusherSpt;
    await saveSub(env, sub);
    return json({ ok: true, wxpusherSpt: sub.wxpusherSpt ?? '' });
  }

  return json({ error: 'not found' }, 404);
}

// ═══════════════ 入口 ═══════════════

interface TgUser { id: number; first_name?: string; username?: string }

interface TgUpdate {
  message?: { chat: { id: number; type?: string }; from?: TgUser; text?: string };
  callback_query?: {
    id: string;
    data?: string;
    from?: TgUser;
    message?: { chat: { id: number; type?: string } };
  };
}

export default {
  /** Telegram Webhook + Web API */
  async fetch(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
    const url = new URL(request.url);

    if (url.pathname.startsWith('/api/')) {
      return handleApi(request, env, url);
    }

    // 安装包中转：只认最新 release 里真实存在的资产名，
    // 不然这就成了一个任给 URL 的公开代理
    if (url.pathname.startsWith('/dl/') && request.method === 'GET') {
      const want = decodeURIComponent(url.pathname.slice(4));
      try {
        const rel = await getLatestRelease(env);
        const asset = rel.assets.find(a => a.name === want);
        if (!asset) return new Response('not found', { status: 404 });
        const upstream = await fetch(asset.browser_download_url, { headers: { 'User-Agent': UA } });
        if (!upstream.ok || !upstream.body) return new Response('upstream error', { status: 502 });
        return new Response(upstream.body, {
          headers: {
            'Content-Type': 'application/zip',
            'Content-Length': String(asset.size),
            'Content-Disposition': `attachment; filename="${asset.name}"`,
            'Cache-Control': 'public, max-age=86400',
          },
        });
      } catch {
        return new Response('bad gateway', { status: 502 });
      }
    }

    if (url.pathname === '/webhook' && request.method === 'POST') {
      // 校验 Telegram 的 secret_token 头
      const secret = request.headers.get('X-Telegram-Bot-Api-Secret-Token');
      if (!env.TG_WEBHOOK_SECRET || secret !== env.TG_WEBHOOK_SECRET) {
        return new Response('forbidden', { status: 403 });
      }
      const update = (await request.json()) as TgUpdate;

      // 内联按钮回调（炸房提醒一键打卡）
      if (update.callback_query?.data && update.callback_query.message) {
        const cb = update.callback_query;
        const cbGroup = (cb.message!.chat.type ?? 'private') !== 'private';
        ctx.waitUntil(handleCallback(env, cb.message!.chat.id, cb.id, cb.data!, cb.from, cbGroup));
        return new Response('ok', { status: 200 });
      }

      const chatId = update.message?.chat.id;
      const text = update.message?.text;
      if (chatId && text) {
        // 群里是一群人共用一份列表，得知道每条命令是谁发的
        const isGroup = (update.message?.chat.type ?? 'private') !== 'private';
        // 先响应 Telegram，命令处理放后台
        ctx.waitUntil(handleCommand(env, chatId, text, update.message?.from, isGroup));
      }
      return new Response('ok', { status: 200 });
    }

    return new Response('not found', { status: 404 });
  },

  /** Cron：每 2 分钟检查提醒 */
  async scheduled(controller: ScheduledController, env: Env, ctx: ExecutionContext): Promise<void> {
    ctx.waitUntil(runReminders(env));
  },
} satisfies ExportedHandler<Env>;
