// Formwright SQLite 壓測（安裝版設計 D-15、AC-11）：多人同時操作 API，八成讀、兩成寫。
// 執行方式見 README.md。環境變數：BASE_URL、VUS、DURATION、OUT（摘要 JSON 路徑）。
import http from 'k6/http'
import { check, sleep } from 'k6'
import { Counter, Rate } from 'k6/metrics'

const BASE = (__ENV.BASE_URL || 'http://127.0.0.1:5180') + '/api/v3/dynamic'
const VUS = Number(__ENV.VUS || 50)
const DURATION = __ENV.DURATION || '10m'

// 通過標準：讀 p95 ≤ 1 秒、寫 p95 ≤ 2 秒；5xx 與資料庫鎖錯誤都是 0
export const options = {
  scenarios: {
    users: { executor: 'constant-vus', vus: VUS, duration: DURATION },
  },
  summaryTrendStats: ['avg', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
  thresholds: {
    'http_req_duration{kind:read}': ['p(95)<1000'],
    'http_req_duration{kind:write}': ['p(95)<2000'],
    server_5xx: ['count==0'],
    lock_errors: ['count==0'],
    // 情境本身要是有效的：登入失敗或非預期 4xx（401、403 等）代表請求沒真的做到事，延遲數字不能採信
    login_failed: ['count==0'],
    unexpected_4xx: ['count==0'],
  },
}

// 讀、寫要分開統計：每個請求帶 kind tag（read／write／login）
const server5xx = new Counter('server_5xx')
const lockErrors = new Counter('lock_errors')
const unexpected4xx = new Counter('unexpected_4xx')
const reqRead = new Counter('req_read')
const reqWrite = new Counter('req_write')
const okRate = new Rate('ok_rate')
const loginFailed = new Counter('login_failed')

// Demo 示範帳號（不含需雙因素的 mfa01）；讀取用這些帳號分散登入
const READ_USERS = [
  'sales01', 'sales02', 'sales03', 'manager01', 'buyer01', 'ops01', 'designer01',
  'floor01', 'floor02', 'keeper01', 'maint01', 'planner01', 'qa01',
]
// 寫入要有新增權限：業務互動紀錄、採購單
const ACTIVITY_USERS = ['sales01', 'sales02', 'sales03', 'manager01']
const PO_USERS = ['buyer01', 'ops01']

// 每個 VU 用自己的來源 IP（站台設 Auth__ClientIpHeader=X-Forwarded-For 才採用）：Demo 一鍵登入對每個來源 IP
// 15 分鐘內限 30 次，50 個 VU 共用 127.0.0.1 會被擋，後續請求全部 401。
const clientIp = () => `10.20.${Math.floor(__VU / 250)}.${(__VU % 250) + 1}`
const JSON_HEADERS = () => ({ 'Content-Type': 'application/json', 'X-Forwarded-For': clientIp() })
const rnd = (n) => Math.floor(Math.random() * n)
const pick = (a) => a[rnd(a.length)]
const today = () => new Date().toISOString().slice(0, 10)

function track(res, kind) {
  if (kind === 'read') reqRead.add(1)
  if (kind === 'write') reqWrite.add(1)
  if (res.status >= 500) server5xx.add(1)
  const body = typeof res.body === 'string' ? res.body : ''
  if (/database is locked|SQLITE_BUSY|SQLite Error 5/i.test(body)) lockErrors.add(1)
  const ok = res.status >= 200 && res.status < 300
  if (!ok && res.status < 500) unexpected4xx.add(1)
  okRate.add(ok)
  return ok
}

function call(method, path, body, jar, kind) {
  const params = { headers: JSON_HEADERS(), tags: { kind, name: `${method} ${path.replace(/\d+/g, ':n').split('?')[0]}` } }
  if (jar) params.jar = jar
  const res = http.request(method, `${BASE}${path}`, body ? JSON.stringify(body) : null, params)
  track(res, kind)
  return res
}

function login(userId, jar) {
  const res = http.post(`${BASE}/auth/demo-login`, JSON.stringify({ userId }), {
    headers: JSON_HEADERS(), jar, tags: { kind: 'login', name: 'POST /auth/demo-login' },
  })
  check(res, { 'login ok': (r) => r.status === 200 })
  if (res.status !== 200) loginFailed.add(1)
  if (res.status >= 500) server5xx.add(1)
  return res.status === 200
}

// 每個 VU 自己的狀態（VU 內全域變數在迭代間保留）
let st = null

function init() {
  const i = __VU - 1
  st = {
    readUser: READ_USERS[i % READ_USERS.length],
    readJar: new http.CookieJar(),
    actUser: ACTIVITY_USERS[i % ACTIVITY_USERS.length],
    actJar: null,
    poUser: PO_USERS[i % PO_USERS.length],
    poJar: null,
    menu: null,
    dashboards: null,
    recordIds: [],
    myActivities: [], // { id, rv }
  }
  login(st.readUser, st.readJar)
}

function actMenu() {
  const res = call('GET', '/menu', null, st.readJar, 'read')
  if (res.status === 200) st.menu = res.json().map((m) => m.key)
}

function ensureMenu() {
  if (!st.menu) actMenu()
  return st.menu && st.menu.length ? st.menu : ['crm-activity']
}

function actList() {
  const key = pick(ensureMenu())
  const page = 1 + rnd(2)
  const res = call('GET', `/modules/${key}/records/paged?currentPage=${page}&pagesSize=20`, null, st.readJar, 'read')
  if (res.status === 200) {
    const items = res.json().items || []
    st.recordIds = items.map((x) => x.id).slice(0, 20)
  }
}

function actRecord() {
  if (!st.recordIds.length) return actList()
  call('GET', `/records/${pick(st.recordIds)}`, null, st.readJar, 'read')
}

function actSchema() {
  call('GET', `/modules/${pick(ensureMenu())}/schema`, null, st.readJar, 'read')
}

function actInbox() {
  call('GET', `/approval-tasks?box=${pick(['todo', 'done', 'submitted'])}&pagesSize=20`, null, st.readJar, 'read')
}

function actDashboard() {
  if (!st.dashboards) {
    const res = call('GET', '/dashboards', null, st.readJar, 'read')
    st.dashboards = res.status === 200 ? (res.json().assigned || []).map((d) => d.id) : []
  }
  if (!st.dashboards.length) return
  const id = pick(st.dashboards)
  const res = call('GET', `/dashboards/${id}`, null, st.readJar, 'read')
  if (res.status !== 200) return
  const cards = (res.json().layout.cards || []).slice(0, 4)
  for (const c of cards) call('POST', `/dashboards/${id}/cards/${c.id}/run`, {}, st.readJar, 'read')
}

function writeJar(userKey, jarKey) {
  if (!st[jarKey]) {
    st[jarKey] = new http.CookieJar()
    login(st[userKey], st[jarKey])
  }
  return st[jarKey]
}

function actCreateActivity() {
  const jar = writeJar('actUser', 'actJar')
  const res = call('POST', '/modules/crm-activity/records', {
    values: { f_187: `壓測互動 ${__VU}-${__ITER}`, f_188: 'opt_108', f_189: today(), f_190: 56, f_192: st.actUser },
  }, jar, 'write')
  if (res.status === 201) {
    const d = res.json()
    st.myActivities.push({ id: d.id, rv: d.rowVersion })
  }
}

function actUpdateActivity() {
  if (!st.myActivities.length) return actCreateActivity()
  const a = pick(st.myActivities)
  const jar = writeJar('actUser', 'actJar')
  const res = call('PUT', `/records/${a.id}`, {
    values: { f_187: `壓測互動（改）${__VU}-${__ITER}` }, rowVersion: a.rv,
  }, jar, 'write')
  if (res.status === 200) a.rv = res.json().rowVersion
}

// 新增採購單（含一列明細，金額達簽核門檻）後送簽核
function actPoSubmit() {
  const jar = writeJar('poUser', 'poJar')
  const created = call('POST', '/modules/inv-po/records', {
    values: { f_276: 61, f_277: 95, f_278: today(), f_279: '2099-12-31', f_280: 'opt_140', f_281: 5 },
    details: { 'inv-po-line': [{ values: { f_285: 93, f_288: 10, f_289: 5200 } }] },
  }, jar, 'write')
  if (created.status !== 201) return
  const d = created.json()
  call('POST', `/records/${d.id}/approval/submit`, { to: 141, recordRowVersion: d.rowVersion, picks: {}, comment: '壓測送簽' }, jar, 'write')
}

// 動作權重：讀約八成、寫約兩成（以請求數計，正式跑後在報告核對實際比例）
const ACTIONS = [
  [actList, 30], [actRecord, 20], [actDashboard, 6], [actMenu, 6], [actSchema, 5], [actInbox, 6],
  [actCreateActivity, 9], [actUpdateActivity, 9], [actPoSubmit, 4],
]
const TOTAL = ACTIONS.reduce((s, a) => s + a[1], 0)

function chooseAction() {
  let r = Math.random() * TOTAL
  for (const [fn, w] of ACTIONS) { if ((r -= w) < 0) return fn }
  return ACTIONS[0][0]
}

export default function () {
  if (!st) init()
  chooseAction()()
  sleep(1 + Math.random() * 2) // 使用者操作間隔 1～3 秒
}

export function handleSummary(data) {
  const out = __ENV.OUT
  const m = data.metrics
  const t = (name) => (m[name] ? m[name].values : null)
  const brief = {
    read: t('http_req_duration{kind:read}'),
    write: t('http_req_duration{kind:write}'),
    req_read: t('req_read'),
    req_write: t('req_write'),
    server_5xx: t('server_5xx'),
    lock_errors: t('lock_errors'),
    unexpected_4xx: t('unexpected_4xx'),
    login_failed: t('login_failed'),
    http_reqs: t('http_reqs'),
    iterations: t('iterations'),
    ok_rate: t('ok_rate'),
  }
  const files = {}
  if (out) {
    files[out] = JSON.stringify({ brief, full: data }, null, 2)
    files[`${out}.brief.json`] = JSON.stringify(brief, null, 2)
  }
  files.stdout = JSON.stringify(brief, null, 2) + '\n'
  return files
}
