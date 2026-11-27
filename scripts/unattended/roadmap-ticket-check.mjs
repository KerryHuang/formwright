#!/usr/bin/env node
// roadmap 與 GitHub 票的同步比對（規則見 .claude/rules/docs.md「roadmap 與票同步」）。
// 用法：node scripts/unattended/roadmap-ticket-check.mjs [--roadmap docs/roadmap.md] [--since-days 14]
//        [--issues-json F] [--commits-json F]   給了就不打 gh／git（測試用）
// 退出碼：0 一致；1 有差異；3 查詢失敗（結果不可用）
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

const ACTIVE = new Set(['in-progress', 'in-review']);

// 段的票＝「內容」欄的 #n；狀態欄的 #n 是尾巴引用，不比。
export function parseRoadmap(text) {
  const rows = [];
  for (const line of text.split('\n')) {
    if (!line.startsWith('| ') || line.startsWith('| 工作段')) continue;
    const cols = line.split('|').map((s) => s.trim());
    if (cols.length < 5) continue;
    const tickets = [...cols[2].matchAll(/#(\d+)/g)].map((m) => Number(m[1]));
    if (!tickets.length) continue;
    const s = cols[3];
    const status = s.startsWith('✅') ? 'done' : s.startsWith('🚧') ? 'active' : s.startsWith('未開始') ? 'todo' : 'unknown';
    rows.push({ segment: cols[1], status, tickets });
  }
  return rows;
}

export function ticketStage(issue) {
  if (issue.state !== 'OPEN') return 'closed';
  const st = issue.labels.map((l) => l.name).filter((n) => n.startsWith('stage:'));
  return st.length === 1 ? st[0].slice(6) : 'unmapped';
}

export function check(rows, issues, commits) {
  const problems = [];
  const stageOf = (n) => {
    const i = issues[n];
    if (!i) throw new Error(`查不到票 #${n}`);
    return ticketStage(i);
  };
  for (const r of rows) {
    const stages = r.tickets.map((n) => [n, stageOf(n)]);
    const list = (f) => stages.filter(([, s]) => f(s)).map(([n, s]) => `#${n}（${s}）`).join('、');
    if (r.status === 'unknown') problems.push(`${r.segment}：狀態欄不是 ✅／🚧／未開始開頭，無法比對`);
    if (r.status === 'done' && list((s) => s !== 'closed')) problems.push(`${r.segment}：段已 ✅，票還開著 ${list((s) => s !== 'closed')}`);
    if (r.status === 'todo' && list((s) => s === 'closed' || ACTIVE.has(s))) problems.push(`${r.segment}：段寫未開始，票已動工或已關 ${list((s) => s === 'closed' || ACTIVE.has(s))}，段要改 🚧 進行中`);
    if (r.status === 'active' && stages.every(([, s]) => s === 'closed')) problems.push(`${r.segment}：段裡的票都關了，段要打 ✅`);
  }
  for (const [n, sha] of commits) {
    const s = stageOf(n);
    if (s !== 'closed' && !ACTIVE.has(s)) problems.push(`#${n}：已有合併 commit ${sha}，票還在 ${s}；做完就關票留 SHA，沒做完改 in-progress`);
  }
  return problems;
}

function gh(args) {
  return JSON.parse(execFileSync('gh', args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }));
}

function main(argv) {
  const opt = { roadmap: 'docs/roadmap.md', 'since-days': '14' };
  for (let i = 0; i < argv.length; i += 2) opt[argv[i].replace(/^--/, '')] = argv[i + 1];
  const rows = parseRoadmap(readFileSync(opt.roadmap, 'utf8'));
  let commits;
  if (opt['commits-json']) commits = JSON.parse(readFileSync(opt['commits-json'], 'utf8'));
  else {
    const log = execFileSync('git', ['log', 'origin/main', `--since=${opt['since-days']}.days`, '--format=%h %s'], { encoding: 'utf8' });
    const seen = new Map();
    for (const l of log.split('\n')) for (const m of l.matchAll(/\(#(\d+)\)/g)) if (!seen.has(+m[1])) seen.set(+m[1], l.split(' ')[0]);
    commits = [...seen];
  }
  let issues;
  if (opt['issues-json']) issues = JSON.parse(readFileSync(opt['issues-json'], 'utf8'));
  else {
    issues = {};
    const wanted = new Set([...rows.flatMap((r) => r.tickets), ...commits.map(([n]) => n)]);
    for (const n of wanted) issues[n] = gh(['issue', 'view', String(n), '--json', 'state,labels']);
  }
  const problems = check(rows, issues, commits);
  console.log(`roadmap 帶票的段 ${rows.length}、近 ${opt['since-days']} 天提到票的 commit ${commits.length} 張票`);
  for (const p of problems) console.log(`- ${p}`);
  console.log(problems.length ? `❌ ${problems.length} 處不一致` : '✅ roadmap 與票一致');
  return problems.length ? 1 : 0;
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    process.exit(main(process.argv.slice(2)));
  } catch (e) {
    console.error(`查詢失敗，結果不可用：${e.message}`);
    process.exit(3);
  }
}
