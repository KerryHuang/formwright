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

function renderIss(d, { version }) {
  const lines = [
    '; 由 scripts/installer/render-defaults.mjs 依 installer/install-defaults.json 產生，不要手改',
    `#define FwPort "${need(d, 'port')}"`,
    `#define FwProgramDir "${need(d, 'windows.programDir')}"`,
    `#define FwDataDir "${need(d, 'windows.dataDir')}"`,
    `#define FwBackupDir "${need(d, 'windows.backupDir')}"`,
    `#define FwConfigFile "${need(d, 'windows.configFile')}"`,
  ]
  if (version) lines.push(`#define FwVersion "${version}"`)
  return lines.join('\n') + '\n'
}

function renderSh(d, { version, repo }) {
  return [
    `FW_PORT=${need(d, 'port')}`,
    `FW_PROGRAM_DIR=${need(d, 'linux.programDir')}`,
    `FW_DATA_DIR=${need(d, 'linux.dataDir')}`,
    `FW_BACKUP_DIR=${need(d, 'linux.backupDir')}`,
    `FW_CONFIG_FILE=${need(d, 'linux.configFile')}`,
    `FW_SERVICE_USER=${need(d, 'linux.serviceUser')}`,
    `FW_VERSION="${version}"`,
    `FW_REPO="${repo}"`,
  ].join('\n')
}

// compose：用 YAML anchor 把預設值集中在檔頭一個區塊，服務區用 *別名 引用；
// 區塊外（restart、BackupBeforeUpgrade 等）不歸這支腳本管。
function renderCompose(d, { version, repo }) {
  const owner = repo ? repo.split('/')[0].toLowerCase() : 'example-owner'
  const image = `ghcr.io/${owner}/${need(d, 'docker.imageName')}:${version || 'latest'}`
  const port = need(d, 'port')
  const dv = need(d, 'docker.dataVolume')
  const bv = need(d, 'docker.backupVolume')
  return [
    `x-fw-image: &fw-image \${FORMWRIGHT_IMAGE:-${image}}`,
    `x-fw-port: &fw-port "\${FORMWRIGHT_PORT:-${port}}:${port}"`,
    `x-fw-data-volume: &fw-data-volume ${dv}:${need(d, 'docker.dataMount')}`,
    `x-fw-backup-volume: &fw-backup-volume ${bv}:${need(d, 'docker.backupMount')}`,
    'x-fw-environment: &fw-environment',
    `  Install__ConfigFile: ${need(d, 'docker.configFile')}`,
    '  Install__InitIfMissing: "true"',
    `  Install__DataDirectory: ${need(d, 'docker.dataMount')}`,
    `  Install__BackupDirectory: ${need(d, 'docker.backupMount')}`,
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

  const drift = []
  for (const out of outputs) {
    const path = join(root, out.file)
    let current = null
    if (existsSync(path)) current = readFileSync(path, 'utf8')
    else if (!out.whole) fail(`找不到 ${out.file}`)
    const next = out.make(current)
    if (next !== current) {
      drift.push(out.file)
      if (!o.check) writeFileSync(path, next)
    }
  }
  if (o.check && drift.length) {
    console.error(`render-defaults: 與 install-defaults.json 不一致：\n  ${drift.join('\n  ')}`)
    process.exit(1)
  }
  console.log(o.check ? 'render-defaults: 一致' : `render-defaults: 已更新 ${drift.length} 個檔案`)
}

main()
