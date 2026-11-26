#!/usr/bin/env node
// 安裝預設值單一來源的產生腳本（設計 TD-02、D-06、D-07）。
// 用法：node scripts/installer/render-defaults.mjs [--check] [--version <v>] [--repo <owner/name>]
//   測試用：--root <repo 根目錄>、--defaults <install-defaults.json 路徑>
import { readFileSync, writeFileSync, existsSync } from 'node:fs'
import { join, dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const START = '# >>> install-defaults'
const END = '# <<< install-defaults'

function fail(msg) {
  console.error(`render-defaults: ${msg}`)
  process.exit(1)
}

function parseArgs(argv) {
  const o = { check: false, version: '', repo: '', root: null, defaults: null }
  const valueOf = (i, name) => (i + 1 < argv.length ? argv[i + 1] : fail(`${name} 缺值`))
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i]
    if (a === '--check') o.check = true
    else if (a === '--version') o.version = valueOf(i++, a)
    else if (a === '--repo') o.repo = valueOf(i++, a)
    else if (a === '--root') o.root = valueOf(i++, a)
    else if (a === '--defaults') o.defaults = valueOf(i++, a)
    else fail(`不認得的參數：${a}`)
  }
  return o
}

// 缺鍵就拋錯，不回預設值
function need(obj, path) {
  let v = obj
  for (const k of path.split('.')) {
    if (v === null || typeof v !== 'object' || !(k in v) || v[k] === '' || v[k] === null) {
      fail(`install-defaults.json 缺少 ${path}`)
    }
    v = v[k]
  }
  return v
}

// 輸入契約（S2）：不支援的字元一律拒絕，不靠跳脫硬撐
const RULES = {
  version: /^[0-9A-Za-z][0-9A-Za-z.+-]*$/,
  repo: /^[A-Za-z0-9._-]+\/[A-Za-z0-9._-]+$/,
  posixPath: /^\/[A-Za-z0-9._\/-]+$/,
  winPath: /^[A-Za-z]:\\[A-Za-z0-9._ \\-]+$/,
  name: /^[a-z0-9][a-z0-9._-]*$/,
  registry: /^[a-z0-9][a-z0-9.-]*(:[0-9]{1,5})?$/,
}

function check(label, value, rule) {
  if (typeof value !== 'string' || !RULES[rule].test(value)) fail(`${label} 含不支援的字元或格式：${JSON.stringify(value)}`)
  return value
}

function port(d) {
  const p = need(d, 'port')
  if (!Number.isInteger(p) || p < 1 || p > 65535) fail(`port 必須是 1–65535 的整數：${JSON.stringify(p)}`)
  return p
}

const q = (v) => `'${String(v).replaceAll("'", `'\\''`)}'`

function renderIss(d, { version }) {
  const lines = [
    '; 由 scripts/installer/render-defaults.mjs 依 installer/install-defaults.json 產生，不要手改',
    `#define FwPort "${port(d)}"`,
    `#define FwProgramDir "${check('windows.programDir', need(d, 'windows.programDir'), 'winPath')}"`,
    `#define FwDataDir "${check('windows.dataDir', need(d, 'windows.dataDir'), 'winPath')}"`,
    `#define FwBackupDir "${check('windows.backupDir', need(d, 'windows.backupDir'), 'winPath')}"`,
    `#define FwConfigFile "${check('windows.configFile', need(d, 'windows.configFile'), 'winPath')}"`,
  ]
  if (version) lines.push(`#define FwVersion "${check('--version', version, 'version')}"`)
  return lines.join('\n') + '\n'
}

function renderSh(d, { version, repo }) {
  const path = (k) => q(check(`linux.${k}`, need(d, `linux.${k}`), 'posixPath'))
  return [
    `FW_PORT=${port(d)}`,
    `FW_PROGRAM_DIR=${path('programDir')}`,
    `FW_DATA_DIR=${path('dataDir')}`,
    `FW_BACKUP_DIR=${path('backupDir')}`,
    `FW_CONFIG_FILE=${path('configFile')}`,
    `FW_SERVICE_USER=${q(check('linux.serviceUser', need(d, 'linux.serviceUser'), 'name'))}`,
    `FW_VERSION=${q(version ? check('--version', version, 'version') : '')}`,
    `FW_REPO=${q(repo ? check('--repo', repo, 'repo') : '')}`,
  ].join('\n')
}

// compose：用 YAML anchor 把預設值集中在檔頭一個區塊，服務區用 *別名 引用；
// 區塊外（restart、BackupBeforeUpgrade 等）不歸這支腳本管。
function renderCompose(d, { version, repo }) {
  const registry = check('docker.registry', need(d, 'docker.registry'), 'registry')
  const owner = repo ? check('--repo', repo, 'repo').split('/')[0].toLowerCase() : check('docker.owner', need(d, 'docker.owner'), 'name')
  const imageName = check('docker.imageName', need(d, 'docker.imageName'), 'name')
  const tag = version ? check('--version', version, 'version') : 'latest'
  const image = `${registry}/${owner}/${imageName}:${tag}`
  const p = port(d)
  const dv = check('docker.dataVolume', need(d, 'docker.dataVolume'), 'name')
  const bv = check('docker.backupVolume', need(d, 'docker.backupVolume'), 'name')
  const dataMount = check('docker.dataMount', need(d, 'docker.dataMount'), 'posixPath')
  const backupMount = check('docker.backupMount', need(d, 'docker.backupMount'), 'posixPath')
  const configFile = check('docker.configFile', need(d, 'docker.configFile'), 'posixPath')
  return [
    `x-fw-image: &fw-image \${FORMWRIGHT_IMAGE:-${image}}`,
    `x-fw-port: &fw-port "\${FORMWRIGHT_PORT:-${p}}:${p}"`,
    `x-fw-data-volume: &fw-data-volume ${dv}:${dataMount}`,
    `x-fw-backup-volume: &fw-backup-volume ${bv}:${backupMount}`,
    'x-fw-environment: &fw-environment',
    `  Install__ConfigFile: ${configFile}`,
    '  Install__InitIfMissing: "true"',
    `  Install__DataDirectory: ${dataMount}`,
    `  Install__BackupDirectory: ${backupMount}`,
    `  Server__Port: "${p}"`,
    'x-fw-volumes: &fw-volumes',
    `  ${dv}:`,
    `  ${bv}:`,
  ].join('\n')
}

function replaceBlock(text, content, file) {
  const lines = text.split('\n')
  const a = lines.findIndex((l) => l.trim() === START)
  const b = lines.findIndex((l) => l.trim() === END)
  if (a < 0 || b < 0 || b < a) fail(`${file} 缺少 ${START} / ${END} 標記`)
  return [...lines.slice(0, a + 1), content, ...lines.slice(b)].join('\n')
}

function main() {
  const o = parseArgs(process.argv.slice(2))
  const here = dirname(fileURLToPath(import.meta.url))
  const root = resolve(o.root ?? join(here, '..', '..'))
  const defaultsPath = o.defaults ?? join(root, 'installer', 'install-defaults.json')
  if (!existsSync(defaultsPath)) fail(`找不到 ${defaultsPath}`)
  const d = JSON.parse(readFileSync(defaultsPath, 'utf8'))
  const opts = { version: o.version, repo: o.repo }

  const iss = join('installer', 'windows', 'defaults.iss')
  const sh = join('installer', 'linux', 'install.sh')
  const yml = join('deploy', 'docker-compose.yml')
  const outputs = [
    { file: iss, make: () => renderIss(d, opts), whole: true },
    { file: sh, make: (t) => replaceBlock(t, renderSh(d, opts), sh) },
    { file: yml, make: (t) => replaceBlock(t, renderCompose(d, opts), yml) },
  ]

  // 先全部算好再寫：任何一份驗證失敗都不留下半套檔案
  const planned = []
  for (const out of outputs) {
    const path = join(root, out.file)
    let current = null
    if (existsSync(path)) current = readFileSync(path, 'utf8')
    else if (!out.whole) fail(`找不到 ${out.file}`)
    const next = out.make(current)
    if (next !== current) planned.push({ file: out.file, path, next })
  }
  const drift = planned.map((x) => x.file)
  if (!o.check) for (const x of planned) writeFileSync(x.path, x.next)
  if (o.check && drift.length) {
    console.error(`render-defaults: 與 install-defaults.json 不一致：\n  ${drift.join('\n  ')}`)
    process.exit(1)
  }
  console.log(o.check ? 'render-defaults: 一致' : `render-defaults: 已更新 ${drift.length} 個檔案`)
}

main()
