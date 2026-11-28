import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import DateSelect from 'src/components/framework/DateSelect.vue';
import EventSetRow from './EventSetRow.vue';

const mountRow = (value: unknown) => mount(EventSetRow, {
  props: {
    modelValue: { to: 1, source: 'const' as const, side: 'header' as const, fieldId: null, expr: '', value, allowHiddenSource: false },
    idPrefix: 'set-0-0', kind: 'update' as const, toOptions: [{ value: 1, label: '日' }],
    targetField: { id: 1, key: 'f_1', type: 'date', label: { 'zh-TW': '日' }, options: [] } as never,
    headerOptions: [], lineOptions: [], hasLine: false, hiddenHeader: new Set<number>(), hiddenLine: new Set<number>(),
  },
  global: { plugins: [[quasar.Quasar, { components: quasar }], createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } })] },
});

describe('EventSetRow 日期常數', () => {
  it('用表單同款日期欄，值照舊是 YYYY-MM-DD（#141）', async () => {
    const w = mountRow('2026-11-11');
    const date = w.findComponent(DateSelect);
    expect(date.exists()).toBe(true);
    expect(w.find('input[type="date"]').exists()).toBe(false);
    await w.get('input[data-testid="set-0-0-const"]').setValue('2026-12-01');
    expect(w.emitted('update:modelValue')!.at(-1)![0]).toMatchObject({ value: '2026-12-01' });
  });
});
