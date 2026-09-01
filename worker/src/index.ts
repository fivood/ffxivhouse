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
}

/** 我的房产（炸房提醒）：手动登记 + 进房打卡 */
interface HomeEntry {
  server: number; area: number; slot: number; id: number;
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

/** YYYY-MM-DD → 北京时间当天 00:00 的 unix 秒（保守，提醒偏早不偏晚）；非法/未来返回错误 */
function parseDayStart(date: string): { ts: number } | { error: string } {
  const dm = date.match(/^(\d{4})-(\d{2})-(\d{2})$/);
  if (!dm) return { error: '日期格式应为 YYYY-MM-DD' };
  const ts = Math.floor((Date.UTC(+dm[1], +dm[2] - 1, +dm[3]) - 8 * 3600 * 1000) / 1000);
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
  chatId: number;
  leadHours: number[];
  /** 分项提醒开关，未设置＝全开 */
  notify?: NotifyFlags;
  items: WatchItem[];
  /** 我的房产（炸房提醒） */
  homes?: HomeEntry[];
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

// ═══════════════ Telegram ═══════════════

async function tgSend(env: Env, chatId: number, text: string): Promise<void> {
  if (!env.TG_BOT_TOKEN) {
    console.log(`[no-token] -> ${chatId}: ${text}`);
    return;
  }
  const resp = await fetch(`https://api.telegram.org/bot${env.TG_BOT_TOKEN}/sendMessage`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ chat_id: chatId, text }),
  });
  if (!resp.ok) console.error(`tgSend 失败 ${resp.status}: ${await resp.text()}`);
}

/** 带内联按钮的消息（用于炸房提醒的一键打卡） */
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
async function wxSend(env: Env, spt: string, title: string, body: string): Promise<void> {
  const resp = await fetch('https://wxpusher.zjiecode.com/api/send/message/simple-push', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'User-Agent': UA },
    body: JSON.stringify({ spt, content: body, summary: title, contentType: 1 }),
  });
  if (!resp.ok) console.error(`wxSend 失败 ${resp.status}: ${await resp.text()}`);
}

// ═══════════════ 订阅存取 ═══════════════

async function getSub(env: Env, chatId: number): Promise<UserSub> {
  return (await env.KV.get<UserSub>(`sub:${chatId}`, 'json'))
    ?? { chatId, leadHours: [24, 1], items: [] };
}

async function saveSub(env: Env, sub: UserSub): Promise<void> {
  await env.KV.put(`sub:${sub.chatId}`, JSON.stringify(sub));
}

// ═══════════════ Web 绑定令牌（HMAC，免账号体系） ═══════════════

const WEB_BASE = 'https://ff14.70015.net';

/** 用 webhook 密钥对 chatId 做 HMAC，生成绑定令牌（只有 Bot 能发给本人） */
async function bindToken(env: Env, chatId: number): Promise<string> {
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

/** 校验 u/k 参数，返回 chatId 或 null */
async function checkAuth(env: Env, url: URL): Promise<number | null> {
  const chatId = parseInt(url.searchParams.get('u') ?? '', 10);
  const k = url.searchParams.get('k') ?? '';
  if (Number.isNaN(chatId) || !k) return null;
  return (await bindToken(env, chatId)) === k ? chatId : null;
}

/** POST 体的 u/k 校验 */
async function checkAuthBody(env: Env, body: { u?: number; k?: string }): Promise<number | null> {
  if (typeof body.u !== 'number' || !body.k) return null;
  return (await bindToken(env, body.u)) === body.k ? body.u : null;
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

命令：
/watch 服务器 房区 区号 房号 — 关注（默认"计划抽"）
　例：/watch 萌芽池 白银乡 14 43
/list — 我的关注与倒计时
/mode 序号 — 切换 计划抽/已报名
/unwatch 序号 — 取消关注
/lead 24,1 — 截止前提醒提前量（小时，逗号分隔）
/name 名字 — 设置网页版显示的昵称
/servers — 服务器列表
/help — 本帮助

提醒时机：报名截止前 / 开奖 / 公示期确认归属死线 / 抽签金返还死线 / 下轮开抽

炸房提醒（连续 30 天未进屋进入拆除准备，45 天自动拆除）：
/myhome 服务器 房区 区号 房号 [角色名] — 登记我的房产
　例：/myhome 萌芽池 白银乡 14 43 阿光
/entered [序号] [日期] — 进屋打卡；带日期=补签（如 /entered 1 8-30）
/demolished [序号] — 标记房子已被拆除（开始 35 天资产回收倒计时），再发一次取消
/homes — 我的房产与炸房倒计时

数据来源：house.ffxiv.cyou（玩家上报，可能有延迟）`;

function fmtTime(unixSec: number): string {
  return new Date(unixSec * 1000).toLocaleString('zh-CN', {
    timeZone: 'Asia/Shanghai', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit', hour12: false,
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
async function handleCallback(env: Env, chatId: number, callbackId: string, data: string): Promise<void> {
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
    home.lastEnteredAt = Math.floor(Date.now() / 1000);
    home.fired = [];
    await saveSub(env, sub);
    await tgAnswerCallback(env, callbackId, `✅ 已打卡！${home.label} 倒计时重置为 ${DEMOLITION_DAYS} 天`);
    return;
  }
  await tgAnswerCallback(env, callbackId, '未知操作');
}

async function handleCommand(env: Env, chatId: number, text: string): Promise<void> {
  const parts = text.trim().split(/[\s，,、]+/).filter(Boolean);
  const cmd = (parts[0] ?? '').toLowerCase().replace(/@\w+$/, '');
  const args = parts.slice(1);
  const nowSec = Math.floor(Date.now() / 1000);

  switch (cmd) {
    case '/start': {
      const token = await bindToken(env, chatId);
      await tgSend(env, chatId,
        `${HELP_TEXT}\n\n🌐 网页版（管理关注更方便）：\n${WEB_BASE}/#u=${chatId}&k=${token}\n（此链接含你的专属令牌，别分享给他人）`);
      return;
    }
    case '/help':
      await tgSend(env, chatId, HELP_TEXT);
      return;

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
        const modeText = w.mode === 0 ? '计划抽' : '已报名';
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
      item.fired = [];
      await saveSub(env, sub);
      await tgSend(env, chatId, `第 ${n} 项已切换为「${item.mode === 1 ? '已报名' : '计划抽'}」。`);
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
      });
      await saveSub(env, sub);
      await tgSend(env, chatId,
        `🏠 已登记：${server.name} ${AREA_NAMES[area]} ${slot}区 ${plotId}号（${label || '我的房'}）\n` +
        `已按现在起算 ${DEMOLITION_DAYS} 天倒计时。如果最近没进过屋，进屋后发 /entered 校准。\n` +
        `提醒：连续 30 天未进屋会被列为撤除对象，45 天自动拆除，以游戏内规则为准。`);
      return;
    }

    case '/entered': {
      const sub = await getSub(env, chatId);
      const homes = sub.homes ?? [];
      if (homes.length === 0) {
        await tgSend(env, chatId, '还没有登记房产，用 /myhome 登记。');
        return;
      }

      // 参数解析：数字=序号，日期（8-30 / 2026-08-30 / 8月30日）=补签日期
      let idx = homes.length === 1 ? 0 : -1;
      let dateSec = 0; // 0=现在
      for (const arg of args) {
        const asNum = parseInt(arg, 10);
        const dm = arg.match(/^(?:(\d{4})[-/年])?(\d{1,2})[-/月](\d{1,2})日?$/);
        if (dm) {
          const nowUtc8 = new Date(Date.now() + 8 * 3600 * 1000);
          let year = dm[1] ? parseInt(dm[1], 10) : nowUtc8.getUTCFullYear();
          const month = parseInt(dm[2], 10) - 1;
          const day = parseInt(dm[3], 10);
          if (month < 0 || month > 11 || day < 1 || day > 31) {
            await tgSend(env, chatId, `日期「${arg}」看不懂，格式如 8-30 或 2026-08-30`);
            return;
          }
          let ts = Math.floor((Date.UTC(year, month, day) - 8 * 3600 * 1000) / 1000); // 北京时间当天 00:00
          if (ts > nowSec) ts = Math.floor((Date.UTC(year - 1, month, day) - 8 * 3600 * 1000) / 1000); // 未来日期视为去年
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

      homes[idx].lastEnteredAt = dateSec > 0 ? dateSec : nowSec;
      homes[idx].fired = [];
      await saveSub(env, sub);
      const h = homes[idx];
      const serverName = ALL_SERVERS.find(s => s.id === h.server)?.name ?? `${h.server}`;
      const when = dateSec > 0 ? `（补签至 ${fmtTime(dateSec).slice(0, 5)}）` : '';
      await tgSend(env, chatId,
        `✅ 已打卡${when}：${serverName} ${AREA_NAMES[h.area]} ${h.slot + 1}区 ${h.id}号（${h.label}）\n` +
        `炸房倒计时重置为 ${DEMOLITION_DAYS} 天（至 ${fmtTime(homes[idx].lastEnteredAt + DEMOLITION_DAYS * 86400).slice(0, 5)}）。`);
      return;
    }

    case '/demolished': {
      const sub = await getSub(env, chatId);
      const homes = sub.homes ?? [];
      if (homes.length === 0) {
        await tgSend(env, chatId, '还没有登记房产，用 /myhome 登记。');
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
      if (h.demolishedAt && h.demolishedAt > 0) {
        h.demolishedAt = 0;
        await saveSub(env, sub);
        await tgSend(env, chatId, `已取消「${h.label}」的炸房标记，恢复进屋倒计时。`);
      } else {
        h.demolishedAt = Math.floor(Date.now() / 1000);
        h.fired = [];
        await saveSub(env, sub);
        await tgSend(env, chatId,
          `已标记「${h.label}」被拆除。\n🪑 ${FURNITURE_DAYS} 天内可去住宅区管理人处回收部分家具庭具 + 购地金币的 80%（至 ${fmtTime(h.demolishedAt + FURNITURE_DAYS * 86400)}），到期前会再提醒你。`);
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
      const lines = homes.map((h, i) => {
        const serverName = ALL_SERVERS.find(s => s.id === h.server)?.name ?? `${h.server}`;
        const pos = `${i + 1}. ${serverName} ${AREA_NAMES[h.area]} ${h.slot + 1}区 ${h.id}号（${h.label}）`;
        if (h.demolishedAt && h.demolishedAt > 0) {
          const fDeadline = h.demolishedAt + FURNITURE_DAYS * 86400;
          const days = Math.floor((fDeadline - nowSec) / 86400);
          return `${pos}\n　💥 已炸房，资产回收${days >= 0 ? `还剩 ${days} 天` : '已到期！'}（${fmtTime(fDeadline).slice(0, 5)} 到期）`;
        }
        if (h.lastEnteredAt <= 0) return `${pos}\n　进屋时间未知，进屋后发 /entered ${i + 1}`;
        const deadline = h.lastEnteredAt + DEMOLITION_DAYS * 86400;
        const remain = deadline - nowSec;
        const days = Math.floor(remain / 86400);
        const mark = days <= 5 ? '🔴' : days <= 10 ? '🟠' : '🟢';
        return `${pos}\n　${mark} 剩余 ${days} 天（最后进屋 ${fmtTime(h.lastEnteredAt)}）`;
      });
      await tgSend(env, chatId, lines.join('\n'));
      return;
    }

    default:
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
      const pos = `${serverName} ${AREA_NAMES[h.area]} ${h.slot + 1}区 ${h.id}号（${h.label}）`;

      // 已炸房：资产回收 35 天死线
      if (h.demolishedAt && h.demolishedAt > 0) {
        const fDeadline = h.demolishedAt + FURNITURE_DAYS * 86400;
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
            body: `${pos}\n自动拆除后 ${FURNITURE_DAYS} 天内可去住宅区管理人处回收部分家具庭具，`
              + `以及购买土地所花金币的 80%。`
              + `\n将于 ${fmtTime(fDeadline)} 到期，逾期无法回收！`,
            homeRef: { server: h.server, area: h.area, slot: h.slot, id: h.id },
          });
        }
        if (nowSec >= fDeadline && !h.fired.includes(`furn|${fDeadline}|over`)) {
          h.fired.push(`furn|${fDeadline}|over`);
          dirty = true;
          due.push({
            chatId: sub.chatId,
            title: '🪑 拆除资产回收已到期',
            body: `${pos}\n自动拆除后 ${FURNITURE_DAYS} 天回收期限已到（家具庭具 + 购地金币的 80%）！`
              + `若还没回收，请立刻去住宅区管理人处确认！`,
            homeRef: { server: h.server, area: h.area, slot: h.slot, id: h.id },
          });
        }
        continue; // 炸房的不再做进房倒计时
      }

      if (h.lastEnteredAt <= 0) continue;
      const deadlineSec = h.lastEnteredAt + DEMOLITION_DAYS * 86400;

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
                ? `已被列为撤除对象、进入「自动拆除准备」状态，任务情报-房屋里能看到剩余天数。`
                : days <= 1
                  ? `今天必须进屋，否则将被自动拆除！`
                  : `记得上线进一次屋（要进入室内才算）。`)
            + `\n个人房只认房主进屋；部队房部队任一成员进屋即可解除。`
            + `\n进屋后点下方按钮或发 /entered 打卡。`,
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

    for (const w of sub.items) {
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
              body: `${pos}\n申请抽选时全额支付的金币，要你去点门牌确认才会返还，系统不会自动退！`
                + `\n返还期限为公示期结束后 ${DEPOSIT_DAYS} 天，将于 ${fmtTime(w.depositDeadline)} 截止，逾期不再返还。`
                + `\n（不论中标与否都适用：落选是全额返还，中标未购入是扣 50% 后的余额。）`,
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
                + `\n报名需全额支付土地价格，参加后无法主动取消，且每个角色同时只能参与一处土地。`);
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
      if (r.homeRef) {
        const ref = r.homeRef;
        await tgSendWithButton(env, r.chatId, `${r.title}\n\n${r.body}`,
          '✅ 已进屋（重置倒计时）', `entered:${ref.server}:${ref.area}:${ref.slot}:${ref.id}`);
      } else {
        await tgSend(env, r.chatId, `${r.title}\n\n${r.body}`);
      }
      if (sub.wxpusherSpt) await wxSend(env, sub.wxpusherSpt, r.title, r.body);
    }
    if (dirty) await saveSub(env, sub);
  }

  console.log(`提醒检查完成：${subs.length} 个订阅，${serverIds.length} 个服务器`);
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
      size: SIZE_NAMES[sizeOf(w.area, w.id)] ?? '?', mode: w.mode,
    };
    try {
      const sales = await getSales(env, w.server);
      const house = sales.find(h => h.Area === w.area && h.Slot === w.slot && h.ID === w.id);
      if (!house) {
        result.push({ ...base, gone: true });
      } else {
        const phase = getPhase(house, nowSec);
        result.push({
          ...base,
          gone: false,
          price: house.Price,
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
      return json(entries.map(h => {
        const phase = getPhase(h, nowSec);
        return {
          server: h.Server, area: h.Area, areaName: AREA_NAMES[h.Area] ?? '?',
          slot: h.Slot, slotNo: h.Slot + 1, id: h.ID, price: h.Price,
          size: SIZE_NAMES[h.Size >= 0 && h.Size <= 2 ? h.Size : sizeOf(h.Area, h.ID)] ?? '?',
          state: phase.state, stateName: STATE_NAMES[phase.state],
          estimated: phase.estimated, phaseEnd: phase.end,
          participate: h.Participate,
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
    if (chatId == null) return json({ error: '未绑定或令牌无效，请通过 Bot /start 获取专属链接' }, 401);
    const sub = await getSub(env, chatId);
    const nowSec = Math.floor(Date.now() / 1000);
    return json({
      leadHours: sub.leadHours,
      notify: sub.notify ?? NOTIFY_ALL,
      wxpusherSpt: sub.wxpusherSpt ?? '',
      nickname: sub.nickname ?? '',
      homes: (sub.homes ?? []).map(h => ({
        server: h.server, serverName: ALL_SERVERS.find(s => s.id === h.server)?.name ?? `${h.server}`,
        area: h.area, areaName: AREA_NAMES[h.area], slot: h.slot, slotNo: h.slot + 1,
        id: h.id, label: h.label,
        lastEnteredAt: h.lastEnteredAt,
        demolishedAt: h.demolishedAt ?? 0,
        deadline: h.lastEnteredAt > 0 ? h.lastEnteredAt + DEMOLITION_DAYS * 86400 : 0,
        furnitureDeadline: (h.demolishedAt ?? 0) > 0 ? (h.demolishedAt ?? 0) + FURNITURE_DAYS * 86400 : 0,
        remainDays: h.lastEnteredAt > 0 ? Math.floor((h.lastEnteredAt + DEMOLITION_DAYS * 86400 - nowSec) / 86400) : -1,
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
    const body = (await request.json()) as { u?: number; k?: string; server?: number; area?: number; slot?: number; id?: number };
    const chatId = await checkAuthBody(env, body);
    if (chatId == null) return json({ error: '未绑定或令牌无效' }, 401);
    const sub = await getSub(env, chatId);
    const item = sub.items.find(i =>
      i.server === body.server && i.area === body.area && i.slot === body.slot && i.id === body.id);
    if (!item) return json({ error: '未找到该关注项' }, 404);
    item.mode = item.mode === 0 ? 1 : 0;
    item.fired = [];
    await saveSub(env, sub);
    return json({ ok: true, mode: item.mode });
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

    // 可选补签日期（YYYY-MM-DD），按北京时间当天 00:00 起算（保守，提醒偏早不偏晚）
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

interface TgUpdate {
  message?: { chat: { id: number }; text?: string };
  callback_query?: {
    id: string;
    data?: string;
    message?: { chat: { id: number } };
  };
}

export default {
  /** Telegram Webhook + Web API */
  async fetch(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
    const url = new URL(request.url);

    if (url.pathname.startsWith('/api/')) {
      return handleApi(request, env, url);
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
        ctx.waitUntil(handleCallback(env, cb.message!.chat.id, cb.id, cb.data!));
        return new Response('ok', { status: 200 });
      }

      const chatId = update.message?.chat.id;
      const text = update.message?.text;
      if (chatId && text) {
        // 先响应 Telegram，命令处理放后台
        ctx.waitUntil(handleCommand(env, chatId, text));
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
