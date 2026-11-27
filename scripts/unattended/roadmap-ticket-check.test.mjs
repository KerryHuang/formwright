// node --test scripts/unattended/roadmap-ticket-check.test.mjs（不連 GitHub）
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseRoadmap, check } from './roadmap-ticket-check.mjs';

const open = (stage) => ({ state: 'OPEN', labels: [{ name: `stage:${stage}` }] });
const closed = { state: 'CLOSED', labels: [{ name: 'stage:backlog' }] };
const road = [
  '| 工作段 | 內容 | 狀態 | 設計 |',
  '| a1 | 做 #1、#2 | ✅ 已上線（2026-11-27，abc） | x |',
  '| b1 | 做 #3 | 未開始 | x |',
  '| c1 | 做 #4 | 🚧 進行中 | x |',
  '| d1 | 沒有票 | 未開始 | x |',
  '| e1 | 尾巴不比 | ✅ 已完成；剩下見 #9 | x |',
].join('\n');

test('只取內容欄的票號、狀態分類', () => {
  assert.deepEqual(parseRoadmap(road), [
    { segment: 'a1', status: 'done', tickets: [1, 2] },
    { segment: 'b1', status: 'todo', tickets: [3] },
    { segment: 'c1', status: 'active', tickets: [4] },
  ]);
});

test('一致時沒有問題', () => {
  const issues = { 1: closed, 2: closed, 3: open('todo'), 4: open('in-progress') };
  assert.deepEqual(check(parseRoadmap(road), issues, []), []);
});

test('段 ✅ 票沒關、段未開始票已動工、段進行中票全關、合併後沒關票都報出', () => {
  const issues = { 1: closed, 2: open('backlog'), 3: open('in-progress'), 4: closed, 7: open('backlog'), 8: open('in-review') };
  const p = check(parseRoadmap(road), issues, [[7, 'aaa'], [8, 'bbb'], [1, 'ccc']]);
  assert.equal(p.length, 4);
  assert.match(p[0], /a1.*#2/);
  assert.match(p[1], /b1.*#3/);
  assert.match(p[2], /c1.*打 ✅/);
  assert.match(p[3], /#7.*aaa/);
});

test('查不到票就拋錯，不當成一致', () => {
  assert.throws(() => check(parseRoadmap(road), {}, []), /查不到票 #1/);
});

test('狀態欄無法辨識要報出', () => {
  const p = check(parseRoadmap('| z1 | 做 #5 | 規劃中 | x |'), { 5: open('backlog') }, []);
  assert.match(p[0], /無法比對/);
});
