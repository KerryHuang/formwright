import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import DesignerPalette from './DesignerPalette.vue';
import { PALETTE_KINDS } from './designer-graph';

const kinds = (off: string[] = [], limit = true) => PALETTE_KINDS.map((kind) => ({ kind, disabled: off.includes(kind), limit: limit && off.includes(kind) }));
const mountPalette = (compact = false, off: string[] = [], limit = true) => mount(DesignerPalette, {
  props: { kinds: kinds(off, limit), compact },
  global: { plugins: [[quasar.Quasar, { components: quasar }], createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } })] },
});
const pointer = (type: string, x: number, y: number) => {
  const e = new Event(type, { bubbles: true }) as PointerEvent;
  Object.assign(e, { pointerId: 1, pointerType: 'touch', button: 0, clientX: x, clientY: y });
  return e;
};

describe('DesignerPalette', () => {
  it('lists approval, condition, notify, fork and end (U63, fg5d), with names', () => {
    const w = mountPalette();
    expect(w.findAll('button').map((b) => b.attributes('aria-label'))).toEqual(['簽核', '條件', '通知', '分支', '結束']);
    expect(w.text()).toContain('拖到畫布或線上');
  });

  it('compact (tablet) shows icons only, names stay in aria-label', () => {
    const w = mountPalette(true);
    expect(w.find('.de-palette-label').exists()).toBe(false);
    expect(w.get('[data-testid="de-palette-fork"]').attributes('aria-label')).toBe('分支');
  });

  it('a tap or Enter adds at the centre', async () => {
    const w = mountPalette();
    await w.get('[data-testid="de-palette-approval"]').trigger('click');
    expect(w.emitted('add')).toEqual([['approval']]);
    expect(w.emitted('drop')).toBeUndefined();
  });

  it('dragging past a few pixels shows a ghost and drops at the release point; the click after it is swallowed', async () => {
    const w = mountPalette();
    const b = w.get('[data-testid="de-palette-condition"]');
    b.element.dispatchEvent(pointer('pointerdown', 10, 10));
    b.element.dispatchEvent(pointer('pointermove', 12, 11));
    await w.vm.$nextTick();
    expect(w.find('[data-testid="de-palette-ghost"]').exists()).toBe(false); // 還沒超過 6px
    b.element.dispatchEvent(pointer('pointermove', 300, 200));
    await w.vm.$nextTick();
    expect(w.get('[data-testid="de-palette-ghost"]').text()).toContain('條件');
    b.element.dispatchEvent(pointer('pointerup', 300, 200));
    await b.trigger('click');
    expect(w.emitted('drop')).toEqual([['condition', 300, 200]]);
    expect(w.emitted('add')).toBeUndefined();
    await w.vm.$nextTick();
    expect(w.find('[data-testid="de-palette-ghost"]').exists()).toBe(false);
  });

  it('a kind at the policy limit is disabled and says why', async () => {
    const w = mountPalette(false, ['fork']);
    const b = w.get('[data-testid="de-palette-fork"]');
    expect(b.attributes('disabled')).toBeDefined();
    expect(b.attributes('title')).toBe('已達簽核節點數上限');
    // 政策還沒讀回：停用但不說已達上限
    expect(mountPalette(false, ['fork'], false).get('[data-testid="de-palette-fork"]').attributes('title')).toBe('分支');
  });
});
