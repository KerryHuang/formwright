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
const mod = (key: string, section?: string, icon?: string) =>
  ({ key, name: { 'zh-TW': `name-${key}` }, section: section === undefined ? undefined : { 'zh-TW': section }, icon });

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
afterEach(() => localStorage.clear());

async function run(opts: { me?: ReturnType<typeof me>; menu?: unknown[]; recent?: string[]; gate?: Promise<void>; menuStatus?: number; summary?: unknown; summaryStatus?: number; dashboards?: { id: number; name: unknown }[] } = {}) {
  setActivePinia(createPinia());
  useIdentityStore().setSession('u');
  if (opts.recent) localStorage.setItem('formwright:recent-modules:u', JSON.stringify(opts.recent));
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
      { path: '/dynamic/:moduleKey', component: stub },
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

  it('hides the whole card when the summary request fails', async () => {
    const { w } = await run({ summaryStatus: 500 });
    expect(w.find('[data-testid="home-tasks"]').exists()).toBe(false);
    expect(w.find('[data-testid="home-welcome"]').exists()).toBe(true);
  });

  it('stays on the home page for everyone and greets the user by name', async () => {
    for (const m of [me({ designerRead: true }), me()]) {
      const { router, w } = await run({ me: m, menu: [mod('orders')] });
      expect(router.currentRoute.value.path).toBe('/');
      expect(w.get('[data-testid="home-welcome"]').text()).toContain('Alice');
      expect(w.get('[data-testid="home-welcome"]').text()).toContain('Formwright');
    }
  });

  it('shows module cards grouped by menu section, linking to the module', async () => {
    const { w } = await run({ menu: [mod('orders'), mod('crm1', '業務', 'people'), mod('crm2', '業務')] });
    const groups = w.findAll('[data-testid="home-app-group"]');
    expect(groups.map((g) => g.get('[data-testid="home-group-title"]').text())).toEqual([zhTW.menu.custom, '業務']);
    const cards = w.findAll('[data-testid="home-app-card"]');
    expect(cards.map((c) => c.get('.q-item__section--main').text())).toEqual(['name-orders', 'name-crm1', 'name-crm2']);
    expect(cards.map((c) => c.attributes('href'))).toEqual(['/dynamic/orders', '/dynamic/crm1', '/dynamic/crm2']);
  });

  it('each module group is a tile-grid, so the bento style can size the tiles', async () => {
    const { w } = await run({ menu: [mod('orders'), mod('crm1', '業務', 'people')] });
    for (const g of w.findAll('[data-testid="home-app-group"]')) expect(g.find('.tile-grid').exists()).toBe(true);
  });

  it('lists recent modules (max 5, only ones still in the menu), most recent first', async () => {
    const menu = ['a', 'b', 'c', 'd', 'e', 'f'].map((k) => mod(k));
    const { w } = await run({ menu, recent: ['f', 'gone', 'e', 'd', 'c', 'b', 'a'] });
    const hrefs = w.findAll('[data-testid="home-recent-item"]').map((c) => c.attributes('href'));
    expect(hrefs).toEqual(['/dynamic/f', '/dynamic/e', '/dynamic/d', '/dynamic/c', '/dynamic/b']);
  });

  it('hides the recent section when nothing was opened or storage is unavailable', async () => {
    expect((await run({ menu: [mod('a')] })).w.find('[data-testid="home-recent"]').exists()).toBe(false);
    const spy = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('blocked'); });
    const { w } = await run({ menu: [mod('a')], recent: ['a'] });
    spy.mockRestore();
    expect(w.find('[data-testid="home-recent"]').exists()).toBe(false);
    expect(w.findAll('[data-testid="home-app-card"]')).toHaveLength(1);
  });

  it('links to my reports', async () => {
    const { w } = await run({ menu: [mod('a')] });
    expect(w.get('[data-testid="home-reports"]').attributes('href')).toBe('/reports');
  });

  it('shows system shortcuts only to people with the matching permission', async () => {
    const links = async (m: ReturnType<typeof me>) =>
      (await run({ me: m, menu: [mod('a')] })).w.findAll('[data-testid="home-system-link"]').map((l) => l.attributes('href'));
    expect(await links(me())).toEqual([]);
    expect((await run({ menu: [mod('a')] })).w.find('[data-testid="home-system"]').exists()).toBe(false);
    expect(await links(me({ admin: true }))).toEqual(['/system/users', '/system/groups', '/system/organization', '/system/dashboards', '/system/appearance', '/system/policies', '/system/locales', '/system/mail', '/system/ops', '/system/audit', '/system/ai-access']);
    expect(await links(me({ designerRead: true }))).toEqual(['/system/dynamic-modules', '/system/conversions', '/system/demo-data']);
  });

  it('shows a friendly empty state without modules, but not while the menu is still loading', async () => {
    let release!: () => void;
    const gate = new Promise<void>((r) => { release = r; });
    const pending = await run({ gate, menu: [] });
    expect(pending.w.find('[data-testid="home-empty"]').exists()).toBe(false);
    release();
    await flushPromises();
    expect(pending.w.get('[data-testid="home-empty"]').text()).toContain(zhTW.home.noModules);
    expect(pending.w.find('[data-testid="home-reports"]').exists()).toBe(true);
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
