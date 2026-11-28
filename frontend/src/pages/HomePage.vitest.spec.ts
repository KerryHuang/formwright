import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter, RouterView } from 'vue-router';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import { http } from 'src/services/api/http';
import { useIdentityStore } from 'src/stores/use-identity-store';
import HomePage from './HomePage.vue';

const me = (o: { designerRead?: boolean; designerAdd?: boolean; admin?: boolean } = {}) => ({
  userId: 'u', displayName: 'Alice', isSystemAdmin: o.admin ?? false, groups: [],
  designer: { read: o.designerRead ?? false, add: o.designerAdd ?? false, edit: false, delete: false },
});
const mod = (key: string, name = `name-${key}`, appName?: string) =>
  ({ key, name: { 'zh-TW': name }, appName: appName === undefined ? undefined : { 'zh-TW': appName } });
const record = (moduleKey: string, id: number, title = `title-${id}`) => ({ id, moduleKey, title });

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
afterEach(() => localStorage.clear());

async function run(opts: { me?: ReturnType<typeof me>; menu?: unknown[]; recent?: { moduleKey: string; recordId: number }[]; gate?: Promise<void>; menuStatus?: number; approvals?: number; approvalStatus?: number; records?: Record<number, unknown>; recordStatus?: Record<number, number>; summary?: unknown; summaryStatus?: number; dashboards?: { id: number; name: unknown }[] } = {}) {
  setActivePinia(createPinia());
  useIdentityStore().setSession('u');
  if (opts.recent) localStorage.setItem('formwright:recent-records:u', JSON.stringify(opts.recent));
  http.defaults.adapter = async (config) => {
    if (config.url === '/me') return { data: opts.me ?? me(), status: 200, statusText: 'OK', headers: {}, config };
    if (config.url === '/tasks/summary') {
      if (opts.summaryStatus) return Promise.reject({ config, response: { status: opts.summaryStatus, data: {}, headers: {}, config } });
      return { data: opts.summary ?? { dueToday: 0, overdue: 0 }, status: 200, statusText: 'OK', headers: {}, config };
    }
    if (opts.dashboards && config.url === '/dashboards') {
      return { data: { assigned: opts.dashboards.map((d) => ({ ...d, version: 1, adjusted: false, hasUpdate: false })), mine: [], sharedReports: [] }, status: 200, statusText: 'OK', headers: {}, config };
    }
    const dash = opts.dashboards?.find((d) => config.url === `/dashboards/${d.id}`);
    if (dash) {
      return { data: { ...dash, kind: 'assigned', version: 1, adjusted: false, hasUpdate: false, layout: { cards: [] }, reportNames: {} }, status: 200, statusText: 'OK', headers: {}, config };
    }
    const reject = (status: number) => Promise.reject({ config, response: { status, data: {}, headers: {}, config } });
    if (config.url === '/approval-tasks') {
      if (opts.approvalStatus) return reject(opts.approvalStatus);
      expect(config.params).toMatchObject({ box: 'todo' });
      return { data: { items: [], totalCount: opts.approvals ?? 0, currentPage: 1, pagesSize: 20 }, status: 200, statusText: 'OK', headers: {}, config };
    }
    const rec = /^\/records\/(\d+)$/.exec(config.url ?? '');
    if (rec) {
      const id = Number(rec[1]);
      if (opts.recordStatus?.[id]) return reject(opts.recordStatus[id]!);
      return { data: opts.records?.[id], status: 200, statusText: 'OK', headers: {}, config };
    }
    if (opts.gate) await opts.gate;
    if (opts.menuStatus) return Promise.reject({ status: opts.menuStatus });
    return { data: opts.menu ?? [], status: 200, statusText: 'OK', headers: {}, config };
  };
  const stub = { template: '<i/>' };
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', component: HomePage },
      { path: '/reports', component: stub },
      { path: '/tasks', component: stub },
      { path: '/system/:rest(.*)', component: stub },
      { path: '/approvals', component: stub },
      { path: '/dynamic/:moduleKey', component: stub },
      { path: '/dynamic/:moduleKey/:id', component: stub },
    ],
  });
  await router.push('/');
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount({ render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(RouterView))) }, {
    global: { plugins: [[quasar.Quasar, { components: quasar }], router, i18n, [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]] },
  });
  await flushPromises();
  return { router, w };
}

describe('HomePage', () => {
  const items = (w: Awaited<ReturnType<typeof run>>['w']) => w.findAll('[data-testid="home-recent-item"]');

  it('shows today-due and overdue task counts, each linking to the filtered task list', async () => {
    const { w } = await run({ summary: { dueToday: 2, overdue: 1 } });
    const due = w.get('[data-testid="home-tasks-due-today"]');
    const overdue = w.get('[data-testid="home-tasks-overdue"]');
    expect(due.text()).toContain('2');
    expect(due.text()).toContain(zhTW.home.tasksDueToday);
    expect(due.attributes('href')).toBe('/tasks?filter=today');
    expect(overdue.text()).toContain('1');
    expect(overdue.text()).toContain(zhTW.home.tasksOverdue);
    expect(overdue.attributes('href')).toBe('/tasks?filter=overdue');
  });

  it('hides the whole task card when the summary request fails', async () => {
    const { w } = await run({ summaryStatus: 500 });
    expect(w.find('[data-testid="home-tasks"]').exists()).toBe(false);
    expect(w.find('[data-testid="home-welcome"]').exists()).toBe(true);
  });

  it('the title is the greeting with the name, not the product name', async () => {
    for (const m of [me({ designerRead: true }), me()]) {
      const { router, w } = await run({ me: m, menu: [mod('orders')] });
      expect(router.currentRoute.value.path).toBe('/');
      const title = w.get('[data-testid="home-welcome"]');
      expect(title.text()).toBe(zhTW.home.welcome.replace('{name}', 'Alice'));
      expect(title.classes()).toContain('text-h5');
      expect(w.text()).not.toContain('Formwright');
    }
  });

  it('shows how many approvals wait for me, linking to the approval center', async () => {
    const { w } = await run({ approvals: 3 });
    const card = w.get('[data-testid="home-approvals"]');
    expect(card.text()).toContain(zhTW.home.approvalsTodo);
    expect(card.text()).toContain('3');
    expect(card.attributes('href')).toBe('/approvals');
  });

  it.each([[403], [500]])('hides the approval card when the inbox answers %s', async (status) => {
    const { w } = await run({ approvalStatus: status });
    expect(w.find('[data-testid="home-approvals"]').exists()).toBe(false);
    expect(w.find('[data-testid="home-welcome"]').exists()).toBe(true);
    expect(w.find('[data-testid="home-tasks"]').exists()).toBe(true);
  });

  it('lists recently opened records, newest first, with title and module name, linking to the record', async () => {
    const { w } = await run({
      menu: [mod('orders', '訂單'), mod('ship', '出貨單', '進銷存'), mod('mes-ship', '出貨單', 'MES 生產製造')],
      recent: [{ moduleKey: 'ship', recordId: 2 }, { moduleKey: 'orders', recordId: 1 }, { moduleKey: 'orders', recordId: 3 }],
      records: { 1: record('orders', 1, 'SO-001'), 2: record('ship', 2, 'SH-002'), 3: record('orders', 3, '') },
    });
    expect(items(w).map((i) => i.attributes('href'))).toEqual(['/dynamic/ship/2', '/dynamic/orders/1', '/dynamic/orders/3']);
    expect(items(w)[0]!.text()).toContain('SH-002');
    expect(items(w)[0]!.text()).toContain('出貨單');
    expect(items(w)[0]!.text()).toContain('進銷存');
    expect(items(w)[1]!.text()).toContain('訂單');
    expect(items(w)[2]!.text()).toContain('#3');
    expect(w.get('[data-testid="home-recent"]').text()).toContain(zhTW.home.recent);
  });

  it('shows at most 5 and skips modules that left the menu without asking for their records', async () => {
    const recent = [9, 1, 2, 3, 4, 5, 6].map((id) => ({ moduleKey: id === 9 ? 'gone' : 'a', recordId: id }));
    const { w } = await run({ menu: [mod('a')], recent, records: Object.fromEntries(recent.map((r) => [r.recordId, record(r.moduleKey, r.recordId)])) });
    expect(items(w).map((i) => i.attributes('href'))).toEqual([1, 2, 3, 4, 5].map((id) => `/dynamic/a/${id}`));
  });

  it.each([[404], [403]])('skips a record that answers %s and keeps the others', async (status) => {
    const { w } = await run({
      menu: [mod('a')], recent: [{ moduleKey: 'a', recordId: 1 }, { moduleKey: 'a', recordId: 2 }],
      recordStatus: { 1: status }, records: { 2: record('a', 2) },
    });
    expect(items(w).map((i) => i.attributes('href'))).toEqual(['/dynamic/a/2']);
    expect(w.find('.q-notification').exists()).toBe(false);
  });

  it('hides the recent section when nothing was opened, everything is skipped, or storage is unavailable', async () => {
    expect((await run({ menu: [mod('a')] })).w.find('[data-testid="home-recent"]').exists()).toBe(false);
    const gone = await run({ menu: [mod('a')], recent: [{ moduleKey: 'a', recordId: 1 }], recordStatus: { 1: 404 } });
    expect(gone.w.find('[data-testid="home-recent"]').exists()).toBe(false);
    const spy = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('blocked'); });
    const { w } = await run({ menu: [mod('a')], recent: [{ moduleKey: 'a', recordId: 1 }], records: { 1: record('a', 1) } });
    spy.mockRestore();
    expect(w.find('[data-testid="home-recent"]').exists()).toBe(false);
  });

  it('has no module tiles, report tile or system tiles: the side menu is the way in', async () => {
    const { w } = await run({ me: me({ admin: true, designerRead: true }), menu: [mod('orders')] });
    for (const id of ['home-app-group', 'home-app-card', 'home-reports', 'home-system', 'home-system-link'])
      expect(w.find(`[data-testid="${id}"]`).exists(), id).toBe(false);
    expect(w.find('[data-testid="home-empty"]').exists()).toBe(false);
    expect(w.text()).not.toContain('name-orders');
  });

  it('shows a friendly empty state without modules, but not while the menu is still loading', async () => {
    let release!: () => void;
    const gate = new Promise<void>((r) => { release = r; });
    const pending = await run({ gate, menu: [] });
    expect(pending.w.find('[data-testid="home-empty"]').exists()).toBe(false);
    release();
    await flushPromises();
    expect(pending.w.get('[data-testid="home-empty"]').text()).toContain(zhTW.home.noModules);
  });

  it('an empty system points whoever can add modules to the designer instead of telling them to ask an admin', async () => {
    const { w } = await run({ me: me({ designerRead: true, designerAdd: true }), menu: [] });
    const empty = w.get('[data-testid="home-empty"]');
    expect(empty.text()).toContain(zhTW.home.noModulesDesigner);
    expect(empty.text()).not.toContain('請聯絡系統管理員');
    expect(w.get('[data-testid="home-empty-designer-link"]').attributes('href')).toBe('/system/dynamic-modules');
  });

  it('shows the forbidden notice when the menu cannot be loaded', async () => {
    const { w } = await run({ menuStatus: 500 });
    expect(w.text()).toContain(zhTW.dynamic.forbidden);
  });

  it.each([
    ['multilingual object names', (zh: string) => ({ 'zh-TW': zh, 'en-US': `${zh}-en` })],
    ['plain string names (old data or an old cache)', (zh: string) => zh],
  ])('dashboard dropdown and title show resolved text for %s, never raw JSON', async (_label, name) => {
    const { w } = await run({ dashboards: [{ id: 1, name: name('業務儀表板') }, { id: 2, name: name('品質儀表板') }] });
    const select = w.findComponent(quasar.QSelect);
    expect(select.text()).toContain('業務儀表板');
    expect((select.props('options') as { name: string }[]).map((o) => o.name)).toEqual(['業務儀表板', '品質儀表板']);
    expect(w.get('[data-testid="dashboard-name"]').text()).toBe('業務儀表板');
    // jsdom 沒有 scrollTo；Quasar 開下拉後會捲到目前選項
    Element.prototype.scrollTo = () => {};
    (select.vm as unknown as { showPopup(): void }).showPopup();
    await flushPromises();
    await new Promise((r) => setTimeout(r, 50));
    const shown = `${w.text()} ${document.body.textContent}`;
    expect(shown).toContain('品質儀表板');
    expect(shown).not.toContain('{');
    expect(shown).not.toContain('[object Object]');
  });

});
