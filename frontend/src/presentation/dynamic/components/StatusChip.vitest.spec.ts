import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import StatusChip from './StatusChip.vue';

const bridge = readFileSync(resolve(process.cwd(), 'src/css/themes/quasar-bridge.css'), 'utf8').replace(/\/\*[\s\S]*?\*\//g, '');
const bodyOf = (sel: string) => [...bridge.matchAll(/([^{}]+)\{([^{}]*)\}/g)]
  .filter((m) => m[1]!.split(',').map((s) => s.trim()).includes(sel)).map((m) => m[2]).join(';');

const chip = (color?: string) => mount(StatusChip, { props: { label: '已確認', color } });
const root = (w: ReturnType<typeof chip>) => w.get('[data-testid="status-chip"]');

describe('StatusChip', () => {
  it.each([undefined, ''])('選項沒設顏色（%j）：中性灰，不帶色盤 class 也不寫行內顏色', (color) => {
    const el = root(chip(color));
    expect(el.classes()).toEqual(['status-chip']);
    expect(el.attributes('style')).toBeUndefined();
    expect(bodyOf('.status-chip')).toMatch(/(^|[;\s])color:\s*var\(--t-muted\)/);
  });

  it('Quasar 色名：圓點與邊框沿用色盤 class 的顏色', () => {
    const el = root(chip('blue'));
    expect(el.classes()).toContain('text-blue');
    expect(el.attributes('style')).toBeUndefined();
  });

  it('色碼：用行內顏色', () => {
    const el = root(chip('#ff8800'));
    expect(el.classes()).toEqual(['status-chip']);
    expect((el.element as HTMLElement).style.color).toBe('rgb(255, 136, 0)');
  });

  it.each([undefined, 'orange', '#f2c037'])('%j：文字用一般文字色，顏色只給圓點、邊框、淡色底', (color) => {
    const w = chip(color);
    expect(w.get('.status-chip__label').text()).toBe('已確認');
    expect(w.get('.status-chip__dot').attributes('aria-hidden')).toBe('true');
    expect(bodyOf('.status-chip__label')).toMatch(/color:\s*var\(--t-text\)/);
    expect(bodyOf('.status-chip')).toMatch(/border:\s*1px solid currentcolor/i);
    expect(bodyOf('.status-chip')).toMatch(/background:\s*color-mix\(in srgb, currentcolor \d+%, transparent\)/i);
    expect(bodyOf('.status-chip__dot')).toMatch(/background:\s*currentcolor/i);
  });

  it('灰階色名保留各自的色盤值：載入 Quasar 色盤與 bridge 後，不同灰階算出來不同色', () => {
    const quasarCss = readFileSync(resolve(process.cwd(), 'node_modules/quasar/dist/quasar.css'), 'utf8');
    const palette = quasarCss.match(/^\.text-grey(-\d+)? \{[^}]*\}/gm)!.join('\n');
    const style = document.head.appendChild(document.createElement('style'));
    style.textContent = `${palette}\n${bridge}`;
    const greys = ['grey', 'grey-5', 'grey-6', 'grey-7', 'grey-8'];
    const mounted = greys.map((c) => mount(StatusChip, { props: { label: c, color: c }, attachTo: document.body }));
    const colors = mounted.map((w) => getComputedStyle(root(w).element).color);
    // 章以外的灰字（Quasar 元件內部）仍改用 muted
    const plain = document.body.appendChild(document.createElement('span'));
    plain.className = 'text-grey-8';
    const plainColor = getComputedStyle(plain).color;
    mounted.forEach((w) => w.unmount());
    plain.remove();
    style.remove();
    // Quasar 的 grey 與 grey-5 是同一個色盤值，其餘各不相同
    expect(new Set(colors.slice(1)).size).toBe(4);
    expect(colors.join()).not.toContain('var(');
    expect(plainColor).toContain('--t-muted');
  });

  it('章不可點', () => {
    const w = chip('red');
    expect(w.find('button, a, [tabindex]').exists()).toBe(false);
  });
});
