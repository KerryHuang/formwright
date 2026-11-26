// render-defaults.mjs 的測試：node --test scripts/installer/
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, cpSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import { spawnSync } from 'node:child_process'

const here = dirname(fileURLToPath(import.meta.url))
const script = join(here, 'render-defaults.mjs')
const realDefaults = join(here, '..', '..', 'installer', 'install-defaults.json')

const SH = ['#!/usr/bin/env bash', 'echo before', '# >>> install-defaults', 'OLD=1', '# <<< install-defaults', 'echo after', '']
const YML = ['name: fw', '# >>> install-defaults', 'x-old: 1', '# <<< install-defaults', 'services: {}', '']

function fixture({ sh = SH.join('\n'), yml = YML.join('\n') } = {}) {
  const root = mkdtempSync(join(tmpdir(), 'fw-render-'))
  mkdirSync(join(root, 'installer', 'windows'), { recursive: true })
  mkdirSync(join(root, 'installer', 'linux'), { recursive: true })
  mkdirSync(join(root, 'deploy'), { recursive: true })
  writeFileSync(join(root, 'installer', 'linux', 'install.sh'), sh)
  writeFileSync(join(root, 'deploy', 'docker-compose.yml'), yml)
  const defaults = join(root, 'defaults.json')
  cpSync(realDefaults, defaults)
  return { root, defaults }
}
const run = (f, ...args) =>
  spawnSync(process.execPath, [script, '--root', f.root, '--defaults', f.defaults, ...args], { encoding: 'utf8' })
const read = (f, ...p) => readFileSync(join(f.root, ...p), 'utf8')

test('renders iss defines from json', () => {
  const f = fixture()
  const r = run(f)
  assert.equal(r.status, 0, r.stderr)
  const iss = read(f, 'installer', 'windows', 'defaults.iss')
  assert.match(iss, /#define FwPort "8080"/)
  assert.match(iss, /#define FwDataDir "C:\\ProgramData\\Formwright\\data"/)
  assert.match(iss, /#define FwProgramDir "C:\\Program Files\\Formwright"/)
  assert.doesNotMatch(iss, /FwVersion/)
})

test('iss gets FwVersion only with --version', () => {
  const f = fixture()
  assert.equal(run(f, '--version', '1.2.3').status, 0)
  assert.match(read(f, 'installer', 'windows', 'defaults.iss'), /#define FwVersion "1.2.3"/)
})

test('rewrites only the marked block', () => {
  const f = fixture()
  assert.equal(run(f, '--version', '1.2.3', '--repo', 'Some-Owner/X').status, 0)
  const lines = read(f, 'installer', 'linux', 'install.sh').split('\n')
  const a = lines.indexOf('# >>> install-defaults')
  const b = lines.indexOf('# <<< install-defaults')
  assert.deepEqual(lines.slice(0, a), SH.slice(0, 3 - 1))
  assert.deepEqual(lines.slice(b + 1), SH.slice(5))
  const block = lines.slice(a + 1, b).join('\n')
  assert.match(block, /^FW_PORT=8080$/m)
  assert.match(block, /^FW_DATA_DIR='\/var\/lib\/formwright\/data'$/m)
  assert.match(block, /^FW_VERSION='1\.2\.3'$/m)
  assert.match(block, /^FW_REPO='Some-Owner\/X'$/m)
  assert.doesNotMatch(block, /OLD=1/)
})

test('empty version and repo are written as empty strings', () => {
  const f = fixture()
  assert.equal(run(f).status, 0)
  const sh = read(f, 'installer', 'linux', 'install.sh')
  assert.match(sh, /^FW_VERSION=''$/m)
  assert.match(sh, /^FW_REPO=''$/m)
})

test('compose image is lowercase', () => {
  const f = fixture()
  assert.equal(run(f, '--version', '0.1.0', '--repo', 'Some-Owner/X').status, 0)
  assert.match(read(f, 'deploy', 'docker-compose.yml'), /ghcr\.io\/some-owner\/formwright:0\.1\.0/)
})

test('compose default image comes from install-defaults.json (D-19)', () => {
  const f = fixture()
  assert.equal(run(f).status, 0)
  assert.ok(
    read(f, 'deploy', 'docker-compose.yml').includes('${FORMWRIGHT_IMAGE:-ghcr.io/kerryhuang/formwright:latest}'),
  )
})

test('compose default image follows the json owner', () => {
  const f = fixture()
  const d = JSON.parse(readFileSync(f.defaults, 'utf8'))
  d.docker.registry = 'registry.example.test'
  d.docker.owner = 'acme'
  writeFileSync(f.defaults, JSON.stringify(d))
  assert.equal(run(f).status, 0)
  assert.ok(read(f, 'deploy', 'docker-compose.yml').includes('registry.example.test/acme/formwright:latest'))
})

test('compose gives the container the same port as the mapping (M5)', () => {
  const f = fixture()
  const d = JSON.parse(readFileSync(f.defaults, 'utf8'))
  d.port = 9090
  writeFileSync(f.defaults, JSON.stringify(d))
  assert.equal(run(f).status, 0)
  const yml = read(f, 'deploy', 'docker-compose.yml')
  assert.ok(yml.includes('"${FORMWRIGHT_PORT:-9090}:9090"'))
  assert.match(yml, /^ {2}Server__Port: "9090"$/m)
})

test('rendering twice is stable and check passes', () => {
  const f = fixture()
  assert.equal(run(f).status, 0)
  assert.equal(run(f, '--check').status, 0)
})

test('check fails on drift', () => {
  const f = fixture()
  assert.equal(run(f).status, 0)
  const d = JSON.parse(readFileSync(f.defaults, 'utf8'))
  d.port = 9090
  writeFileSync(f.defaults, JSON.stringify(d))
  const r = run(f, '--check')
  assert.equal(r.status, 1)
  assert.match(r.stdout + r.stderr, /install\.sh/)
  assert.match(r.stdout + r.stderr, /defaults\.iss/)
})

test('check does not write files', () => {
  const f = fixture()
  run(f, '--check')
  assert.equal(read(f, 'installer', 'linux', 'install.sh'), SH.join('\n'))
})

test('missing marker fails', () => {
  const f = fixture({ sh: '#!/usr/bin/env bash\necho hi\n' })
  const r = run(f)
  assert.equal(r.status, 1)
  assert.match(r.stderr, /install\.sh/)
})

test('missing target file fails', () => {
  const f = fixture()
  writeFileSync(join(f.root, 'deploy', 'docker-compose.yml'), YML.join('\n'))
  const r = spawnSync(process.execPath, [script, '--root', join(f.root, 'nowhere'), '--defaults', f.defaults], { encoding: 'utf8' })
  assert.equal(r.status, 1)
})

test('missing key in defaults fails', () => {
  const f = fixture()
  const d = JSON.parse(readFileSync(f.defaults, 'utf8'))
  delete d.linux.dataDir
  writeFileSync(f.defaults, JSON.stringify(d))
  const r = run(f)
  assert.equal(r.status, 1)
  assert.match(r.stderr, /dataDir/)
})

// S2：輸入契約與 shell quoting
const withDefaults = (mutate) => {
  const f = fixture()
  const d = JSON.parse(readFileSync(f.defaults, 'utf8'))
  mutate(d)
  writeFileSync(f.defaults, JSON.stringify(d))
  return f
}

for (const [name, mutate, args] of [
  ['linux path with space', (d) => (d.linux.dataDir = '/var/lib/my formwright'), []],
  ['linux path with $()', (d) => (d.linux.dataDir = '/var/$(id)'), []],
  ['linux path with quote', (d) => (d.linux.configFile = "/etc/x'y"), []],
  ['linux path relative', (d) => (d.linux.programDir = 'opt/formwright'), []],
  ['service user with semicolon', (d) => (d.linux.serviceUser = 'a;b'), []],
  ['port zero', (d) => (d.port = 0), []],
  ['port string', (d) => (d.port = '80; rm'), []],
  ['port too large', (d) => (d.port = 70000), []],
  ['windows path with quote', (d) => (d.windows.dataDir = 'C:\\a"b'), []],
  ['docker mount not absolute', (d) => (d.docker.dataMount = 'data'), []],
  ['volume name with slash', (d) => (d.docker.dataVolume = 'a/b'), []],
  ['version with quote', () => {}, ['--version', '1.0"; id; "']],
  ['version with space', () => {}, ['--version', '1.0 0']],
  ['repo without slash', () => {}, ['--repo', 'owner']],
  ['repo with shell chars', () => {}, ['--repo', 'a/b;c']],
]) {
  test(`rejects unsupported value: ${name}`, () => {
    const f = withDefaults(mutate)
    const r = run(f, ...args)
    assert.equal(r.status, 1, r.stdout)
    assert.equal(read(f, 'installer', 'linux', 'install.sh'), SH.join('\n'), 'must not write files')
  })
}

test('windows path with spaces is accepted', () => {
  const f = fixture()
  assert.equal(run(f).status, 0)
  assert.match(read(f, 'installer', 'windows', 'defaults.iss'), /Program Files/)
})
