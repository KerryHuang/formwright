import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

// 既有守衛只掃 .vue 的 style；SVG 屬性（fill、stroke 等）另在這裡驗：不得寫死色碼，顏色走 currentColor 或 CSS 變數。
const src = readFileSync(resolve(__dirname, 'BrandMark.vue'), 'utf8');
const template = src.slice(src.indexOf('<template>'), src.indexOf('</template>'));

describe('BrandMark', () => {
  it('template 不含寫死色碼（hex、rgb、hsl）', () => {
    expect(template).not.toMatch(/#[0-9a-fA-F]{3,8}\b/);
    expect(template).not.toMatch(/\b(rgb|hsl)a?\(/i);
  });

  it('fill、stroke 只用 currentColor 或 CSS 變數', () => {
    const values = [...template.matchAll(/\b(?:fill|stroke)="([^"]*)"/g)].map((m) => m[1]);
    expect(values.length).toBeGreaterThan(0);
    for (const v of values) expect(v === 'currentColor' || v === 'none' || v.startsWith('var(')).toBe(true);
  });

  // jsdom 不套 scoped style，只能驗原始碼有規則；實際尺寸由本機走查 getBoundingClientRect 量。
  it('頂列品牌連結點擊範圍至少 44px（品牌字隱藏後仍成立）', () => {
    const layout = readFileSync(resolve(__dirname, '../../layouts/MainLayout.vue'), 'utf8');
    const rule = layout.match(/\.brand-link\s*\{([^}]*)\}/)?.[1] ?? '';
    expect(rule).toMatch(/min-width:\s*44px/);
    expect(rule).toMatch(/min-height:\s*44px/);
  });
});
