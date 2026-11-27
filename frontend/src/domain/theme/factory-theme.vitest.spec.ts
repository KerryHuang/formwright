import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import factory from 'shared/theme/factory-theme.json';
import { FACTORY_THEME } from './theme-model';

/** index.html 的內嵌 script 在 bundle 載入前就要畫面，只能保留字面值；這裡守住它和單一來源一致（tokens.css 的 :root 由 themes.vitest.spec.ts 守）。 */
const html = readFileSync(resolve(process.cwd(), 'index.html'), 'utf8');

describe('出廠外觀預設的單一來源 shared/theme/factory-theme.json', () => {
  it('FACTORY_THEME 就是 JSON 的內容', () => {
    expect(FACTORY_THEME).toEqual(factory);
  });

  it.each(['mode', 'style'] as const)('index.html 內嵌 script 的預設 %s 和 JSON 一致', (key) => {
    const literals = [...html.matchAll(new RegExp(`var ${key} = '([^']*)';`, 'g'))].map((m) => m[1]);
    expect(literals).toEqual([factory[key]]);
  });
});
