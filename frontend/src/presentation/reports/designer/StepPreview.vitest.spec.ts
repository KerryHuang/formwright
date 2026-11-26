import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import I18nTextInput from 'src/presentation/dynamic/designer/I18nTextInput.vue';
import { emptyState } from './designer-state';
import StepPreview from './StepPreview.vue';

const mountStep = (originalName?: Record<string, string>) => mount(StepPreview, {
  props: { state: { ...emptyState(), name: { 'zh-TW': '月報' } }, result: null, loading: false, errors: [], saveErrors: [], canSave: true, saving: false, originalName },
  global: { plugins: [[quasar.Quasar, { components: quasar }], createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } })] },
});

describe('StepPreview', () => {
  // 審查 🟢8：報表重新載入（originalName 換了）時，多語輸入框的 original 跟著換，不留建立元件當下的舊值
  it('passes a copy of originalName to the name input and follows later changes', async () => {
    const original = { 'zh-TW': '月報' };
    const w = mountStep(original);
    const input = () => w.findComponent(I18nTextInput);
    expect(input().props('original')).toEqual({ 'zh-TW': '月報' });
    expect(input().props('original')).not.toBe(original);
    await w.setProps({ originalName: { 'zh-TW': '週報', 'en-US': 'Weekly' } });
    expect(input().props('original')).toEqual({ 'zh-TW': '週報', 'en-US': 'Weekly' });
    await w.setProps({ originalName: undefined });
    expect(input().props('original')).toBeUndefined();
  });
});
