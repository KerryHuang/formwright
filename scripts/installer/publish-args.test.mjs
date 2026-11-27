// 發版與安裝 e2e 的 workflow 靜態檢查（#143）：每個發佈 WebAPI 的 dotnet publish 都要帶 -p:BuildTime，
// 漏了安裝版的 /api/version 會回 dev。e2e 只驗得到自己打的套件，release.yml 靠這裡擋。
// 用法：node --test scripts/installer/publish-args.test.mjs
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'

const workflows = join(dirname(fileURLToPath(import.meta.url)), '..', '..', '.github', 'workflows')

// 先把 bash 的行尾 \ 接成一行，再挑出非註解的 publish 指令
const publishCommands = (text) =>
  text.replace(/\\\r?\n\s*/g, ' ').split(/\r?\n/)
    .filter((l) => !l.trim().startsWith('#') && /dotnet publish\b.*DynamicModules\.WebAPI/.test(l))

// 期望數量寫死：找不到指令（改寫法、搬檔）也要失敗，不能當成通過
for (const [file, count] of [['release.yml', 2], ['installer-e2e.yml', 2]]) {
  test(`${file}：WebAPI 的 dotnet publish 都帶 -p:BuildTime`, () => {
    const commands = publishCommands(readFileSync(join(workflows, file), 'utf8'))
    assert.equal(commands.length, count, `${file} 應有 ${count} 個 publish 指令，實得 ${commands.length}（增減指令時同步改這裡）`)
    for (const c of commands) assert.match(c, /-p:BuildTime=\S/, `缺 -p:BuildTime：${c.trim()}`)
  })
}
