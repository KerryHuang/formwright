import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import enUS from 'shared/i18n/en-US/ui.json';
import th from 'shared/i18n/th/ui.json';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import ReportListPage from './ReportListPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { getReports: vi.fn() } }));
const api = vi.mocked(dynamicApi);
const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
let mounted: ReturnType<typeof mount> | undefined;
let router: ReturnType<typeof createRouter>;

async function mountPage(locale = 'zh-TW') {
  router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/reports', component: ReportListPage }, { path: '/reports/:id', component: { template: '<i/>' } }],
  });
  await router.push('/reports');
  const i18n = createI18n({ legacy: false, locale, messages: { 'zh-TW': zhTW, 'en-US': enUS, th } });
  mounted = mount({ template: '<q-layout><q-page-container><router-view /></q-page-container></q-layout>' }, {
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar }], i18n, router, [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]] },
  });
  await flushPromises();
}

describe('ReportListPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });
  afterEach(() => { mounted?.unmount(); document.body.innerHTML = ''; });

  it('lists own and shared reports with owner, and opens one on click', async () => {
    api.getReports.mockResolvedValue([
      { id: 1, name: { 'zh-TW': '我的月報' }, ownerName: '我', isOwner: true, sharedGroupIds: [2], mainModuleKey: 'orders' },
      { id: 2, name: { 'zh-TW': '別人的報表' }, ownerName: '小王', isOwner: false, sharedGroupIds: [2], mainModuleKey: 'orders' },
    ]);
    await mountPage();
    expect(q('report-row-1')!.textContent).toContain('我的月報');
    expect(q('report-row-1')!.textContent).toContain('已分享');
    expect(q('report-row-2')!.textContent).toContain('小王');
    expect(q('report-row-2')!.textContent).not.toContain('已分享');
    expect(q('report-row-2')!.classList.contains('row-clickable')).toBe(true);
    q('report-row-2')!.click();
    await flushPromises();
    expect(router.currentRoute.value.path).toBe('/reports/2');
  });

  // 名稱多語設計 §18.3 S1、S2：名稱依介面語系解析，沒有該語系的譯名時沿退回鏈（預設語系繁中）
  it.each([['en-US', 'Orders'], ['th', '訂單']])('S1/S2 shows the report name in the %s interface as %s', async (locale, expected) => {
    api.getReports.mockResolvedValue([
      { id: 1, name: { 'zh-TW': '訂單', 'en-US': 'Orders' }, ownerName: '我', isOwner: true, sharedGroupIds: [], mainModuleKey: 'orders' },
    ]);
    await mountPage(locale);
    expect(q('report-row-1')!.querySelector('td')!.textContent!.trim()).toBe(expected);
  });

  // kr3：內建報表在上方自成一區（名稱與說明），擁有者顯示「系統」，不出現在「我的報表」
  it('lists built-in reports in their own section above my reports', async () => {
    api.getReports.mockResolvedValue([
      {
        id: 10, name: { 'zh-TW': '各負責人負載', 'en-US': 'Workload by assignee' }, ownerName: '', isOwner: false, sharedGroupIds: [], mainModuleKey: '',
        builtIn: true, builtInCode: 'task-workload', description: { 'zh-TW': '每列是一位負責人目前未完成的任務。' },
      },
      { id: 1, name: { 'zh-TW': '我的月報' }, ownerName: '我', isOwner: true, sharedGroupIds: [], mainModuleKey: 'orders', builtIn: false },
    ]);
    await mountPage();
    const section = q('builtin-reports')!;
    expect(section.textContent).toContain('內建報表');
    expect(q('builtin-report-10')!.textContent).toContain('各負責人負載');
    expect(q('builtin-report-10')!.textContent).toContain('每列是一位負責人目前未完成的任務。');
    expect(q('builtin-report-10')!.textContent).toContain('系統');
    expect(q('report-row-10')).toBeNull();
    expect(q('report-row-1')!.textContent).toContain('我的月報');
    q('builtin-report-10')!.click();
    await flushPromises();
    expect(router.currentRoute.value.path).toBe('/reports/10');
  });

  it('shows the built-in name in English', async () => {
    api.getReports.mockResolvedValue([
      { id: 10, name: { 'zh-TW': '各負責人負載', 'en-US': 'Workload by assignee' }, ownerName: '', isOwner: false, sharedGroupIds: [], mainModuleKey: '', builtIn: true, builtInCode: 'task-workload' },
    ]);
    await mountPage('en-US');
    expect(q('builtin-reports')!.textContent).toContain('Built-in reports');
    expect(q('builtin-report-10')!.textContent).toContain('Workload by assignee');
    expect(document.body.textContent).toContain('No reports yet');
  });

  it('shows the empty text when there are no reports', async () => {
    api.getReports.mockResolvedValue([]);
    await mountPage();
    expect(document.body.textContent).toContain('還沒有報表');
    expect(q('builtin-reports')).toBeNull();
  });

  it('403 shows the forbidden notice', async () => {
    api.getReports.mockRejectedValue({ status: 403 });
    await mountPage();
    expect(q('report-create')).toBeNull();
  });
});
