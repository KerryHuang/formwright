import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

/**
 * 刪除擋下清單與模組狀態的翻譯鍵守衛：後端新增原因種類或模組狀態時，四語都要有對應鍵，
 * 否則畫面會顯示 designer.delete.kind.xxx 這類原始鍵。清單從後端原始碼讀（單一來源），不在前端另寫一份。
 * 對應後端：DeleteResponses.cs（DeleteBlockerKind）、ModuleStatus.cs（ModuleStatus）。
 */
// 以測試檔自己的位置定位 repo 根目錄（frontend/src/i18n → 上三層），不依賴執行時的 cwd
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const read = (p: string) => readFileSync(resolve(root, p), 'utf8');

// 只掃 DeleteBlockerKind 類別區塊，避免撈到同檔其他常數
const kindsBody = /static class DeleteBlockerKind\s*\{([^}]*)\}/.exec(
  read('backend/src/DynamicModules.Application/Dynamic/DeleteService/Data/DeleteResponses.cs'))![1];
const kinds = [...kindsBody.matchAll(/public const string \w+ = "(\w+)";/g)].map((m) => m[1]);
// enum 成員以識別字匹配：先去掉註解，容忍 `Draft = 0` 這類賦值
const statusBody = /enum ModuleStatus\s*\{([^}]*)\}/.exec(read('backend/src/DynamicModules.Domain/Entities/Dynamic/ModuleStatus.cs'))![1]
  .replace(/\/\/.*$/gm, '').replace(/\/\*[\s\S]*?\*\//g, '');
const statuses = statusBody.split(',').map((s) => /^\s*([A-Za-z_]\w*)/.exec(s)?.[1]).filter((s): s is string => !!s);

type Tree = { designer: { delete: { kind: Record<string, string> }; moduleStatus: Record<string, string> } };
const locales = ['zh-TW', 'en-US', 'th', 'vi'];

describe('刪除擋下與模組狀態的翻譯鍵', () => {
  it('有從後端讀到種類與狀態（防正則失效）', () => {
    expect(kinds.length).toBeGreaterThan(15);
    expect(statuses).toContain('Closed');
  });

  for (const code of locales) {
    const ui = JSON.parse(read(`shared/i18n/${code}/ui.json`)) as Tree;
    it(`${code}：每種擋下原因都有 designer.delete.kind 鍵`, () => {
      expect(kinds.filter((k) => !ui.designer.delete.kind[k])).toEqual([]);
    });
    it(`${code}：每個模組狀態都有 designer.moduleStatus 鍵`, () => {
      expect(statuses.filter((s) => !ui.designer.moduleStatus[s])).toEqual([]);
    });
  }
});
