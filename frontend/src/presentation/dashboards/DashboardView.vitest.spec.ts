import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { detail } from 'src/pages/dashboards/dashboard-test-kit';
import DashboardView from './DashboardView.vue';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { getDashboard: vi.fn() } }));
const api = vi.mocked(dynamicApi);

describe('DashboardView loading', () => {
  it('shows a title bar and a 2×2 card skeleton (one column on narrow screens) until the dashboard arrives', async () => {
    let resolve!: (v: ReturnType<typeof detail>) => void;
    api.getDashboard.mockReturnValue(new Promise((r) => { resolve = r; }));
    const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
    const w = mount(DashboardView, {
      props: { dashboardId: 1, allowEdit: false, linkable: [] },
      global: { plugins: [[quasar.Quasar, { components: quasar }], i18n, [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]] },
    });
    await flushPromises();
    const sk = w.get('[data-testid="loading-skeleton"]');
    expect(sk.attributes('aria-busy')).toBe('true');
    expect(sk.attributes('aria-label')).toBe(zhTW.designer.loading);
    expect(sk.findAll('.q-skeleton')).toHaveLength(5);
    expect(sk.findAll('.col-12.col-sm-6')).toHaveLength(4);
    expect(w.findComponent(quasar.QSpinner).exists()).toBe(false);
    resolve(detail(1, { name: { 'zh-TW': '業務儀表板' }, layout: { cards: [] } }));
    await flushPromises();
    expect(w.find('[data-testid="loading-skeleton"]').exists()).toBe(false);
    expect(w.get('[data-testid="dashboard-name"]').text()).toBe('業務儀表板');
  });
});
