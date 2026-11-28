import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import RecordUnavailable from './RecordUnavailable.vue';

async function mountIt(props: { retry?: () => void } = {}) {
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/:p(.*)*', component: { render: () => null } }] });
  await router.push('/here');
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return mount(RecordUnavailable, {
    props: { title: '標題', reason: '原因', backLabel: '回到列表', backTo: '/dynamic/m', ...props },
    global: { plugins: [[quasar.Quasar, { components: quasar }], router, i18n] },
  });
}

describe('RecordUnavailable', () => {
  it('顯示標題、原因與返回連結；沒給 retry 就沒有重新載入', async () => {
    const w = await mountIt();
    const box = w.get('[data-testid="record-unavailable"]');
    expect(box.text()).toContain('標題');
    expect(box.text()).toContain('原因');
    const back = w.get('[data-testid="record-unavailable-back"]');
    expect(back.text()).toContain('回到列表');
    expect(back.attributes('href')).toBe('/dynamic/m');
    expect(w.find('[data-testid="record-unavailable-retry"]').exists()).toBe(false);
    expect(w.get('.q-icon').attributes('aria-hidden')).toBe('true');
    expect(w.html()).not.toContain('text-negative');
  });

  it('給了 retry 就多一顆重新載入，按下呼叫它', async () => {
    const retry = vi.fn();
    const w = await mountIt({ retry });
    const button = w.get('[data-testid="record-unavailable-retry"]');
    expect(button.text()).toContain(zhTW.dynamic.reload);
    await button.trigger('click');
    expect(retry).toHaveBeenCalledTimes(1);
  });
});
