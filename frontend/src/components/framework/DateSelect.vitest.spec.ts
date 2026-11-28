import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, describe, expect, it } from 'vitest';
import { setBusinessOffset } from 'src/domain/dynamic/business-time';
import DateSelect from './DateSelect.vue';

afterEach(() => setBusinessOffset('+08:00'));

describe('DateSelect', () => {
  const mountDate = (props: Record<string, unknown>) =>
    mount(DateSelect, { props: { label: '日期', modelValue: null, ...props }, global: { plugins: [[quasar.Quasar, { components: quasar }]] } });

  it('是輸入框加日曆圖示，不是瀏覽器原生日期欄；沒開 clearable 沒有清除鈕', () => {
    const w = mountDate({ modelValue: '2026-11-11' });
    expect(w.find('input').attributes('type')).not.toBe('date');
    expect(w.text()).toContain('event');
    expect(w.find('.q-field__focusable-action').exists()).toBe(false);
  });

  it('clearable 時可以清空，送出 null', async () => {
    const w = mountDate({ modelValue: '2026-11-11', clearable: true });
    await w.get('.q-field__focusable-action').trigger('click');
    expect(w.emitted('update:modelValue')!.at(-1)).toEqual([null]);
  });

  it('emits business offset', async () => {
    setBusinessOffset('+07:00');
    const w = mount(DateSelect, { props: { modelValue: '2026-11-11T10:00:00+08:00', label: '時間', withTime: true }, global: { plugins: [[quasar.Quasar, { components: quasar }]] } });
    const input = w.find('input');
    expect((input.element as HTMLInputElement).value).toBe('2026-11-11T09:00');
    await input.setValue('2026-11-11T09:30');
    expect(w.emitted('update:modelValue')!.at(-1)).toEqual(['2026-11-11T09:30:00+07:00']);
  });
});
