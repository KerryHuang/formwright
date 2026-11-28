import { describe, expect, it } from 'vitest';
import zhTW from 'shared/i18n/zh-TW/ui.json';

// 一包現成的模組設定在介面上一律叫「積木包」（docs/superpowers/specs/2026-11-28-block-pack-naming-design.md §1）。
// 「應用程式」「套件」指別的東西（外部程式、作業系統的應用程式、npm 套件）時才可以保留，在下面列出鍵名與理由。
const EXCEPTIONS: Record<string, string> = {};

function leaves(o: unknown, path = ''): [string, string][] {
  if (typeof o === 'string') return [[path, o]];
  return Object.entries(o as object).flatMap(([k, v]) => leaves(v, path ? `${path}.${k}` : k));
}
const messages = leaves(zhTW);
const hasOldWord = (v: string) => /應用程式|套件/.test(v);

describe('積木包用詞', () => {
  it('繁中介面文字沒有「應用程式」「套件」（例外清單除外）', () => {
    expect(messages.length).toBeGreaterThan(1000);
    expect(messages.filter(([k, v]) => hasOldWord(v) && !(k in EXCEPTIONS))).toEqual([]);
  });

  it('例外清單的每個鍵都還存在且仍含舊詞', () => {
    const byKey = new Map(messages);
    expect(Object.keys(EXCEPTIONS).filter((k) => !hasOldWord(byKey.get(k) ?? ''))).toEqual([]);
  });
});
