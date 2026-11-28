import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import type { ListView, ModuleSchema, SchemaField } from 'src/domain/dynamic/types';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import ColumnChooser from 'src/presentation/dynamic/list/ColumnChooser.vue';
import FilterPanel from 'src/presentation/dynamic/list/FilterPanel.vue';
import SortEditor from 'src/presentation/dynamic/list/SortEditor.vue';
import ExportMenu from 'src/presentation/dynamic/list/ExportMenu.vue';
import ImportDialog from 'src/presentation/dynamic/list/ImportDialog.vue';
import ViewMenu from 'src/presentation/dynamic/list/ViewMenu.vue';
import { dynamicApi } from 'src/services/api/dynamic-api';
import ListPage from './ListPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: {
    getModuleSchema: vi.fn(), getViews: vi.fn(), getPagedRecords: vi.fn(),
    getMe: vi.fn(), createView: vi.fn(), updateView: vi.fn(), deleteView: vi.fn(),
    exportRecords: vi.fn(), previewImport: vi.fn(), commitImport: vi.fn(),
  },
}));
const api = vi.mocked(dynamicApi);

const field = (id: number, label: string, o: { sortable?: boolean; showInList?: boolean } = {}): SchemaField => ({
  id, key: `f_${id}`, type: 'text', label: { 'zh-TW': label }, options: [],
  config: { required: false, readOnly: false, showInList: o.showInList ?? true, isFilterable: true, isSortable: o.sortable ?? true },
});
const schema: ModuleSchema = {
  key: 'm', programCode: 'DYN_M', name: { 'zh-TW': '模組' }, titleFieldKey: 'f_1', version: 1, rules: [], permissions: { read: true, add: true, edit: true, delete: true, printExport: true, pricing: true },
  blocks: [{ id: 1, label: { 'zh-TW': '區' }, collapsed: false, columns: 2, fields: [field(1, '標題'), field(2, '甲'), field(3, '乙')] }],
};
const view = (p: Partial<ListView>): ListView => ({
  id: 9, name: { 'zh-TW': '我的檢視' }, shared: false, isOwner: true, columns: [2], sort: [], isDefault: false, staleFieldIds: [], ...p,
});
const row = { id: 1, moduleKey: 'm', title: '列一', values: {}, ownerUserId: 'a', createdUserId: 'a', createdTime: '2026-01-01T00:00:00+08:00', modifiedUserId: 'a', modifiedTime: '2026-01-01T00:00:00+08:00', rowVersion: 'v', references: {}, hostKey: null };
const lastCall = () => api.getPagedRecords.mock.calls.at(-1)![1];

let router: ReturnType<typeof createRouter>;
let schemaPatch: Partial<ModuleSchema> = {};

async function mountPage(views: ListView[] = [], queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } }), slowViews = false, forbidden = false) {
  setActivePinia(createPinia());
  api.getModuleSchema.mockImplementation(async (key) => {
    if (forbidden) throw { status: 403 };
    return { ...schema, ...schemaPatch, key };
  });
  api.getViews.mockImplementation(async () => {
    if (slowViews) await new Promise((r) => setTimeout(r, 30));
    return views;
  });
  api.getPagedRecords.mockResolvedValue({ items: [], totalCount: 100, currentPage: 1, pagesSize: 20 });
  router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/dynamic/:moduleKey', component: ListPage }] });
  await router.push('/dynamic/m');
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  // q-page needs a surrounding layout
  const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(ListPage))) };
  const w = mount(Host, {
    global: {
      plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify, Dialog: quasar.Dialog } }],
        router, i18n, [VueQueryPlugin, { queryClient }]],
    },
  });
  await flushPromises();
  return w;
}

type Wrapper = Awaited<ReturnType<typeof mountPage>>;
/** 篩選面板收在工具列的「篩選」按鈕裡：沒開就先點開。 */
async function filterPanel(w: Wrapper) {
  if (!w.findComponent(FilterPanel).exists()) {
    await w.get('[data-testid="toolbar-filter"]').trigger('click');
    await flushPromises();
  }
  return w.findComponent(FilterPanel);
}

describe('ListPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    schemaPatch = {};
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  /** 沒有任何資料的清單。 */
  async function mountEmpty(views: ListView[] = []) {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const w = await mountPage(views, qc);
    api.getPagedRecords.mockResolvedValue({ items: [], totalCount: 0, currentPage: 1, pagesSize: 20 });
    await qc.invalidateQueries();
    await flushPromises();
    return w;
  }
  const emptyText = (w: Awaited<ReturnType<typeof mountPage>>) => w.get('[data-testid="empty-state"]').text();

  it('empty without filter or keyword: names the module and offers the same add action', async () => {
    const w = await mountEmpty();
    expect(emptyText(w)).toContain('還沒有任何模組。');
    expect(w.find('[data-testid="empty-state-hint"]').exists()).toBe(false);
    expect(w.get('.q-table__bottom--nodata').text()).not.toContain('warning');
    await w.get('[data-testid="empty-new-btn"]').trigger('click');
    await flushPromises();
    expect(router.currentRoute.value.path).toBe('/dynamic/m/new');
  });

  it('empty without add permission: no button', async () => {
    schemaPatch = { permissions: { ...schema.permissions, add: false } };
    const w = await mountEmpty();
    expect(emptyText(w)).toContain('還沒有任何模組。');
    expect(w.find('[data-testid="empty-new-btn"]').exists()).toBe(false);
  });

  it.each([
    ['filter', async (w: Awaited<ReturnType<typeof mountPage>>) => { (await filterPanel(w)).vm.$emit('update:modelValue', { all: [{ field: 2, op: 'eq', value: 'x' }] }); }],
    ['keyword', async (w: Awaited<ReturnType<typeof mountPage>>) => { w.findAllComponents(quasar.QInput).find((c) => c.attributes('data-testid') === 'keyword' || c.element.closest('[data-testid="keyword"]') || c.element.querySelector('[data-testid="keyword"]'))!.vm.$emit('update:modelValue', 'zzzz'); }],
  ])('empty because of a %s: says nothing matches, no add button', async (_name, apply) => {
    const w = await mountEmpty();
    await apply(w);
    await flushPromises();
    expect(emptyText(w)).toContain('沒有符合條件的資料。');
    expect(emptyText(w)).toContain('試著放寬篩選條件或清除關鍵字。');
    expect(w.find('[data-testid="empty-new-btn"]').exists()).toBe(false);
  });

  it('empty because of the approval switch counts as filtered', async () => {
    schemaPatch = { approvalEnabled: true };
    const w = await mountEmpty();
    w.findAllComponents(quasar.QBtnToggle).at(0)!.vm.$emit('update:modelValue', 'pending');
    await flushPromises();
    expect(emptyText(w)).toContain('沒有符合條件的資料。');
  });

  it('does not show the empty state while records are still loading', async () => {
    const w = await mountPage();
    api.getPagedRecords.mockReturnValue(new Promise(() => {}));
    (await filterPanel(w)).vm.$emit('update:modelValue', { all: [{ field: 2, op: 'eq', value: 'x' }] });
    await flushPromises();
    expect(w.find('[data-testid="empty-state"]').exists()).toBe(false);
  });

  it('empty result, then a new condition that answers 400: does not claim nothing matches', async () => {
    const w = await mountEmpty();
    expect(w.find('[data-testid="empty-state"]').exists()).toBe(true);
    api.getPagedRecords.mockRejectedValue({ status: 400, errors: { filter: ['篩選條件不正確'] } });
    (await filterPanel(w)).vm.$emit('update:modelValue', { all: [{ field: 2, op: 'eq', value: 'x' }] });
    await flushPromises();
    expect(lastCall().filter).toContain('"x"');
    expect(w.find('[data-testid="empty-state"]').exists()).toBe(false);
    expect(w.get('.q-table__bottom--nodata').text()).toContain(zhTW.dynamic.noData);
    expect(document.body.textContent).toContain('篩選條件不正確');
  });

  it('rows are visibly clickable and open the record detail', async () => {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const w = await mountPage([], qc);
    api.getPagedRecords.mockResolvedValue({ items: [row], totalCount: 1, currentPage: 1, pagesSize: 20 });
    await qc.invalidateQueries();
    await flushPromises();
    const tr = w.get('tbody tr');
    expect(tr.classes()).toEqual(expect.arrayContaining(['row-clickable', 'row-chevron']));
    await tr.trigger('click');
    await flushPromises();
    expect(router.currentRoute.value.fullPath).toBe('/dynamic/m/1');
  });

  describe('工具列單列（#141）', () => {
    const btn = (w: Wrapper, name: string) => w.get(`[data-testid="toolbar-${name}"]`);

    it('資料表上方只有一列控制：沒有常駐的篩選面板與手風琴，三顆按鈕在關鍵字右邊', async () => {
      const w = await mountPage();
      expect(w.findComponent(FilterPanel).exists()).toBe(false);
      expect(w.findComponent(SortEditor).exists()).toBe(false);
      expect(w.findComponent(ColumnChooser).exists()).toBe(false);
      expect(w.findComponent(quasar.QExpansionItem).exists()).toBe(false);
      expect(btn(w, 'filter').text()).toContain(zhTW.dynamic.filter.title);
      expect(btn(w, 'sort').text()).toContain(zhTW.dynamic.sort.title);
      expect(btn(w, 'columns').text()).toContain(zhTW.dynamic.columns.button);
      const toolbar = w.get('[data-testid="list-toolbar"]').html();
      const order = ['view-btn', 'keyword', 'toolbar-filter', 'toolbar-sort', 'toolbar-columns', 'new-btn'].map((id) => toolbar.indexOf(id));
      expect(order).toEqual([...order].sort((a, b) => a - b));
      expect(order).not.toContain(-1);
    });

    it('按鈕後面是套用中的篩選條件數與排序欄數；欄位按鈕不顯示數量', async () => {
      const w = await mountPage([view({
        isDefault: true, columns: [2, 3], sort: [{ field: 2, order: 'asc' }],
        filter: { all: [{ field: 2, op: 'eq', value: 'x' }, { field: 3, op: 'eq', value: 'y' }] },
      })]);
      expect(btn(w, 'filter').text()).toContain(`${zhTW.dynamic.filter.title} 2`);
      expect(btn(w, 'sort').text()).toContain(`${zhTW.dynamic.sort.title} 1`);
      expect(btn(w, 'columns').text()).not.toMatch(/\d/);
      (await filterPanel(w)).vm.$emit('update:modelValue', undefined);
      await flushPromises();
      expect(btn(w, 'filter').text()).not.toMatch(/\d/);
    });

    it('寬螢幕開下拉面板，改動即時生效、關掉不還原', async () => {
      const w = await mountPage();
      await btn(w, 'sort').trigger('click');
      await flushPromises();
      expect(w.findComponent(quasar.QMenu).exists()).toBe(true);
      expect(w.findComponent(quasar.QDialog).findComponent(SortEditor).exists()).toBe(false);
      w.findComponent(SortEditor).vm.$emit('update:modelValue', [{ field: 2, order: 'desc' }]);
      await flushPromises();
      expect(lastCall().sort).toEqual([{ field: 2, order: 'desc' }]);
      await btn(w, 'sort').trigger('click');
      await flushPromises();
      expect(btn(w, 'sort').text()).toContain(`${zhTW.dynamic.sort.title} 1`);
    });

    it('欄位面板改欄位後清單跟著變', async () => {
      const w = await mountPage();
      await btn(w, 'columns').trigger('click');
      await flushPromises();
      w.findComponent(ColumnChooser).vm.$emit('update:modelValue', [3]);
      await flushPromises();
      expect(w.findAll('th').map((h) => h.text()).join()).not.toContain('甲');
    });

    it('篩選面板打開時直接展開，不用再點一層', async () => {
      const w = await mountPage();
      await filterPanel(w);
      expect(w.findComponent(quasar.QExpansionItem).props('modelValue')).toBe(true);
    });

    it('手機開全螢幕對話框，有「完成」可以關閉', async () => {
      window.innerWidth = 390;
      window.dispatchEvent(new Event('resize'));
      await new Promise((r) => setTimeout(r, 250));
      try {
        const w = await mountPage();
        await filterPanel(w);
        const dialog = w.findAllComponents(quasar.QDialog).find((d) => d.findComponent(FilterPanel).exists())!;
        expect(dialog.props('maximized')).toBe(true);
        const done = document.body.querySelector('[data-testid="toolbar-filter-done"]') as HTMLElement;
        expect(done.textContent).toContain(zhTW.dynamic.toolbar.done);
        done.click();
        await flushPromises();
        expect(dialog.props('modelValue')).toBe(false);
        w.unmount();
      } finally {
        window.innerWidth = 1024;
        window.dispatchEvent(new Event('resize'));
        await new Promise((r) => setTimeout(r, 250));
      }
    });

    it('沒有套用檢視時檢視按鈕寫「不使用已存檢視」', async () => {
      const w = await mountPage();
      expect(w.get('[data-testid="view-btn"]').text()).toContain('不使用已存檢視');
    });
  });

  it('resets page to 1 when filter changes', async () => {
    const w = await mountPage();
    w.findComponent(quasar.QTable).vm.$emit('update:pagination', { page: 2, rowsPerPage: 20 });
    await flushPromises();
    expect(lastCall().currentPage).toBe(2);
    (await filterPanel(w)).vm.$emit('update:modelValue', { all: [{ field: 2, op: 'eq', value: 'x' }] });
    await flushPromises();
    expect(lastCall().currentPage).toBe(1);
    expect(lastCall().filter).toBe('{"all":[{"field":2,"op":"eq","value":"x"}]}');
  });

  it('shows stale notice', async () => {
    const w = await mountPage([view({ isDefault: true, staleFieldIds: [7, 8] })]);
    expect(w.text()).toContain('此檢視有 2 個欄位已停用或不再開放篩選、排序，已略過');
  });

  it('header click replaces sort with single column', async () => {
    const w = await mountPage([view({ isDefault: true, columns: [2, 3], sort: [{ field: 2, order: 'asc' }, { field: 3, order: 'desc' }] })]);
    expect(lastCall().sort).toEqual([{ field: 2, order: 'asc' }, { field: 3, order: 'desc' }]);
    const headers = w.findAll('th.sortable');
    expect(headers.length).toBe(3);
    await headers[2]!.trigger('click');
    await flushPromises();
    expect(lastCall().sort).toEqual([{ field: 3, order: 'asc' }]);
    expect(lastCall().currentPage).toBe(1);
  });

  it('initialises from refetched views, not from the cached ones', async () => {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const stale = view({ isDefault: true, columns: [2], filter: { all: [{ field: 2, op: 'eq', value: 'x' }, { field: 3, op: 'eq', value: 'y' }] } });
    qc.setQueryData(['views', 'm'], [stale]);
    const fresh = view({ isDefault: true, columns: [2], filter: { all: [{ field: 3, op: 'eq', value: 'y' }] }, staleFieldIds: [2] });
    await mountPage([fresh], qc, true);
    await new Promise((r) => setTimeout(r, 80));
    await flushPromises();
    expect(api.getViews).toHaveBeenCalled();
    expect(api.getPagedRecords.mock.calls.length).toBeGreaterThan(0);
    for (const [, q] of api.getPagedRecords.mock.calls) expect(q.filter).toBe('{"all":[{"field":3,"op":"eq","value":"y"}]}');
  });

  it('ListPage shows forbidden notice on 403', async () => {
    const w = await mountPage([], undefined, false, true);
    expect(w.text()).toContain(zhTW.dynamic.forbidden);
    expect(w.text()).not.toContain(zhTW.dynamic.loadFailed);
  });

  it('ListPage hides the new button without add', async () => {
    const w = await mountPage();
    expect(w.find('[data-testid="new-btn"]').exists()).toBe(true);
    schemaPatch = { permissions: { ...schema.permissions, add: false } };
    const w2 = await mountPage();
    expect(w2.find('[data-testid="new-btn"]').exists()).toBe(false);
  });

  it('ListPage shows the closed-app banner only when the schema is readOnly', async () => {
    expect((await mountPage()).find('[data-testid="closed-banner"]').exists()).toBe(false);
    schemaPatch = { readOnly: true, permissions: { ...schema.permissions, add: false, edit: false, delete: false } };
    const w = await mountPage();
    expect(w.find('[data-testid="closed-banner"]').text()).toContain(zhTW.dynamic.closedBanner);
    expect(w.find('[data-testid="new-btn"]').exists()).toBe(false);
  });

  it('ListPage shows the import button only with add, and opens the import dialog', async () => {
    const w = await mountPage();
    expect(w.findComponent(ImportDialog).props('modelValue')).toBe(false);
    await w.get('[data-testid="import-btn"]').trigger('click');
    expect(w.findComponent(ImportDialog).props('modelValue')).toBe(true);
    schemaPatch = { permissions: { ...schema.permissions, add: false } };
    const w2 = await mountPage();
    expect(w2.find('[data-testid="import-btn"]').exists()).toBe(false);
  });

  it('ListPage hides the import button for extension modules', async () => {
    schemaPatch = { kind: 'extension', hostEntityKey: 'test.entity', titleFieldKey: null };
    const w = await mountPage();
    expect(w.find('[data-testid="new-btn"]').exists()).toBe(true);
    expect(w.find('[data-testid="import-btn"]').exists()).toBe(false);
    expect(w.findComponent(ImportDialog).exists()).toBe(false);
  });

  it('ListPage hands the export menu the current filter, sort and keyword', async () => {
    const w = await mountPage([view({ isDefault: true, columns: [2], sort: [{ field: 2, order: 'desc' }], filter: { all: [{ field: 2, op: 'eq', value: 'x' }] } })]);
    expect(w.findComponent(ExportMenu).props('query')).toEqual({
      filter: '{"all":[{"field":2,"op":"eq","value":"x"}]}', sort: [{ field: 2, order: 'desc' }], q: undefined,
    });
  });

  it('renders without a title column when titleFieldKey is null', async () => {
    schemaPatch = { titleFieldKey: null, blocks: [{ ...schema.blocks[0]!, fields: [field(2, '甲'), field(3, '乙')] }] };
    const w = await mountPage();
    expect(w.text()).not.toContain(zhTW.dynamic.loadFailed);
    expect(w.findAll('th').map((h) => h.text().replace('arrow_upward', ''))).toEqual(['甲', '乙', zhTW.dynamic.modifiedTime]);
  });

  it('notifies forbidden when saving a shared view returns 403', async () => {
    const w = await mountPage();
    api.createView.mockRejectedValueOnce({ status: 403 });
    w.findComponent(ViewMenu).vm.$emit('saveAs', { name: { 'zh-TW': 'v' }, shared: true, isDefault: false });
    await flushPromises();
    await new Promise((r) => setTimeout(r, 50));
    expect(document.body.textContent).toContain(zhTW.dynamic.forbidden);
  });

  it('title column header click sorts by the title field', async () => {
    const w = await mountPage();
    const headers = w.findAll('th.sortable');
    expect(headers.length).toBe(3);
    await headers[0]!.trigger('click');
    await flushPromises();
    expect(lastCall().sort).toEqual([{ field: 1, order: 'asc' }]);
  });

  it('with a title template the title column is labelled 標題 and still sorts by the title field (the server orders by the stored title)', async () => {
    schemaPatch = { hasTitleTemplate: true, blocks: [{ ...schema.blocks[0]!, fields: [field(1, '名稱'), field(2, '甲'), field(3, '乙')] }] };
    const w = await mountPage();
    expect(w.findAll('th').map((h) => h.text().replace('arrow_upward', ''))).toEqual([zhTW.dynamic.titleColumn, '甲', '乙', zhTW.dynamic.modifiedTime]);
    await w.findAll('th.sortable')[0]!.trigger('click');
    await flushPromises();
    expect(lastCall().sort).toEqual([{ field: 1, order: 'asc' }]);
  });

  it('without a title template the title column keeps the title field label', async () => {
    schemaPatch = { blocks: [{ ...schema.blocks[0]!, fields: [field(1, '名稱'), field(2, '甲'), field(3, '乙')] }] };
    const w = await mountPage();
    expect(w.findAll('th').map((h) => h.text().replace('arrow_upward', ''))).toEqual(['名稱', '甲', '乙', zhTW.dynamic.modifiedTime]);
  });

  it('notifies the first 400 message and keeps previous rows', async () => {
    const w = await mountPage();
    api.getPagedRecords.mockResolvedValue({ items: [row], totalCount: 1, currentPage: 1, pagesSize: 20 });
    w.findComponent(quasar.QTable).vm.$emit('update:pagination', { page: 1, rowsPerPage: 20, sortBy: null });
    (await filterPanel(w)).vm.$emit('update:modelValue', { all: [{ field: 3, op: 'eq', value: 'ok' }] });
    await flushPromises();
    expect(w.text()).toContain('列一');
    api.getPagedRecords.mockRejectedValueOnce({ status: 400, errors: { filter: ['欄位 2 未開放篩選'] } });
    (await filterPanel(w)).vm.$emit('update:modelValue', { all: [{ field: 2, op: 'eq', value: 'x' }] });
    await flushPromises();
    await new Promise((r) => setTimeout(r, 50));
    expect(document.body.textContent).toContain('欄位 2 未開放篩選');
    expect(w.text()).not.toContain(zhTW.dynamic.loadFailed);
    expect(w.text()).toContain('列一');
  });

  it('switching modules carries no old filter, sort or page and resets state', async () => {
    const w = await mountPage();
    w.findComponent(quasar.QTable).vm.$emit('update:pagination', { page: 3, rowsPerPage: 20 });
    (await filterPanel(w)).vm.$emit('update:modelValue', { all: [{ field: 2, op: 'eq', value: 'x' }] });
    await flushPromises();
    await w.get('[data-testid="keyword"]').setValue('abc');
    await new Promise((r) => setTimeout(r, 400));
    await flushPromises();
    expect(lastCall().filter).toBeDefined();
    expect(lastCall().q).toBe('abc');
    api.getPagedRecords.mockClear();
    await router.push('/dynamic/n');
    await flushPromises();
    const forN = api.getPagedRecords.mock.calls.filter(([k]) => k === 'n');
    expect(forN.length).toBeGreaterThan(0);
    for (const [, q] of forN) {
      expect(q.filter).toBeUndefined();
      expect(q.q).toBeUndefined();
      expect(q.sort).toEqual([]);
      expect(q.currentPage).toBe(1);
    }
    expect(api.getPagedRecords.mock.calls.every(([k]) => k === 'n')).toBe(true);
    expect((w.get('[data-testid="keyword"]').element as HTMLInputElement).value).toBe('');
  });

  it('reference column shows title from references', async () => {
    const refField: SchemaField = {
      ...field(4, '客戶'), type: 'reference',
      reference: { kind: 'module', key: 'cust', name: { 'zh-TW': 'x' }, lightUrl: '/x' },
    };
    schemaPatch = { blocks: [{ id: 1, label: { 'zh-TW': '區' }, collapsed: false, columns: 2, fields: [field(1, '標題'), refField] }] };
    const w = await mountPage([view({ isDefault: true, columns: [4] })]);
    api.getPagedRecords.mockResolvedValue({
      items: [
        { ...row, id: 1, values: { f_4: 42 }, references: { f_4: { key: '42', title: '客戶甲', state: 'ok' } } },
        { ...row, id: 2, values: { f_4: 43 }, references: { f_4: { key: '43', state: 'missing' } } },
      ], totalCount: 2, currentPage: 1, pagesSize: 20,
    });
    (await filterPanel(w)).vm.$emit('update:modelValue', undefined);
    await w.findComponent(quasar.QTable).vm.$emit('update:pagination', { page: 1, rowsPerPage: 20, sortBy: 'f_4', descending: false });
    await flushPromises();
    const cells = w.findAll('tbody tr').map((tr) => tr.text());
    expect(cells[0]).toContain('客戶甲');
    expect(cells[1]).toContain(zhTW.dynamic.reference.missing);
  });

  it('extension module list shows host title column', async () => {
    schemaPatch = { kind: 'extension', hostEntityKey: 'test.entity', titleFieldKey: null };
    const w = await mountPage();
    api.getPagedRecords.mockResolvedValue({ items: [{ ...row, title: 'EQ-0001 CNC 車床 A' }], totalCount: 1, currentPage: 1, pagesSize: 20 });
    w.findComponent(quasar.QTable).vm.$emit('update:pagination', { page: 2, rowsPerPage: 20 });
    await flushPromises();
    expect(w.find('tbody tr').text()).toContain('EQ-0001 CNC 車床 A');
  });
  it('workflow status column shows a chip coloured by the option; other enum columns stay text', async () => {
    const options = (list: [string, string, string?][]) => list.map(([value, label, color], i) => ({ id: i + 1, value, label: { 'zh-TW': label }, sequence: i, active: true, color }));
    const status: SchemaField = { ...field(5, '狀態'), key: 'status', type: 'enum', options: options([['ok', '已確認', 'blue'], ['no', '已取消', '#c10015'], ['plain', '草稿']]) };
    const source: SchemaField = { ...field(6, '來源'), key: 'src', type: 'enum', options: options([['tel', '電話', 'red']]) };
    schemaPatch = {
      blocks: [{ id: 1, label: { 'zh-TW': '區' }, collapsed: false, columns: 2, fields: [field(1, '標題'), status, source] }],
      workflow: { statusFieldKey: 'status', initialState: 'plain', states: [], transitions: [] },
    };
    const w = await mountPage([view({ isDefault: true, columns: [5, 6] })]);
    api.getPagedRecords.mockResolvedValue({
      items: ['ok', 'no', 'plain', null].map((v, i) => ({ ...row, id: i + 1, values: { status: v, src: 'tel' } })),
      totalCount: 4, currentPage: 1, pagesSize: 20,
    });
    (await filterPanel(w)).vm.$emit('update:modelValue', undefined);
    await w.findComponent(quasar.QTable).vm.$emit('update:pagination', { page: 1, rowsPerPage: 20, sortBy: 'status', descending: false });
    await flushPromises();
    const chips = w.findAll('tbody [data-testid="status-chip"]');
    expect(chips.map((c) => c.text())).toEqual(['已確認', '已取消', '草稿']);
    expect(chips[0]!.classes()).toContain('text-blue');
    expect((chips[1]!.element as HTMLElement).style.color).toBe('rgb(193, 0, 21)');
    expect(chips[2]!.classes()).toEqual(['status-chip']);
  });

  it('numeric columns are right aligned and carry cell-code; text, date and autonumber stay left', async () => {
    const code: SchemaField = { ...field(6, '單號'), key: 'no', type: 'autonumber' };
    const amount: SchemaField = { ...field(7, '金額'), key: 'amt', type: 'currency' };
    const total: SchemaField = { ...field(8, '合計'), key: 'sum', type: 'formula', config: { ...field(8, '').config, resultType: 'number' } };
    const due: SchemaField = { ...field(9, '到期'), key: 'due', type: 'formula', config: { ...field(9, '').config, resultType: 'date' } };
    schemaPatch = { blocks: [{ id: 1, label: { 'zh-TW': '區' }, collapsed: false, columns: 2, fields: [field(1, '標題'), field(2, '甲'), code, amount, total, due] }] };
    const w = await mountPage([view({ isDefault: true, columns: [2, 6, 7, 8, 9] })]);
    api.getPagedRecords.mockResolvedValue({ items: [{ ...row, values: { f_2: 'x', no: 'A-1', amt: 10, sum: 77, due: '2026-01-02' } }], totalCount: 1, currentPage: 1, pagesSize: 20 });
    (await filterPanel(w)).vm.$emit('update:modelValue', undefined);
    await w.findComponent(quasar.QTable).vm.$emit('update:pagination', { page: 1, rowsPerPage: 20, sortBy: 'f_2', descending: false });
    await flushPromises();
    expect(w.find('[data-testid="status-chip"]').exists()).toBe(false);
    const tds = w.findAll('tbody tr:first-child td');
    const byText = (t: string) => tds.find((td) => td.text().includes(t))!;
    expect(byText('A-1').classes()).toContain('cell-code');
    expect(byText('10').classes()).toContain('cell-code');
    expect(byText('x').classes()).not.toContain('cell-code');
    for (const t of ['10', '77']) expect(byText(t).classes()).toContain('text-right');
    for (const t of ['x', 'A-1', '2026-01-02']) expect(byText(t).classes()).toContain('text-left');
    const header = (label: string) => w.findAll('thead th').find((th) => th.text().includes(label))!;
    for (const label of ['金額', '合計']) expect(header(label).classes()).toContain('text-right');
    for (const label of ['甲', '單號', '到期']) expect(header(label).classes()).toContain('text-left');
  });

  describe('approval system filter (approval design 13.4.1)', () => {
    const toggle = (w: Awaited<ReturnType<typeof mountPage>>) => w.findComponent(quasar.QBtnToggle);

    it('A4C-F1: no switch row when the module has no approval transition', async () => {
      const w = await mountPage();
      expect(w.find('[data-testid="approval-filter"]').exists()).toBe(false);
    });

    it('A4C-F2: picking in-approval sends approval=pending on page 1; all drops the parameter', async () => {
      schemaPatch = { approvalEnabled: true };
      const w = await mountPage();
      expect(w.get('[data-testid="approval-filter"]').text()).toContain('簽核');
      expect(w.findAll('[data-testid="approval-toggle"] button').map((b) => b.text())).toEqual(['全部', '簽核中', '未在簽核']);
      expect(lastCall().approval).toBeUndefined();
      w.findComponent(quasar.QTable).vm.$emit('update:pagination', { page: 3, rowsPerPage: 20 });
      await flushPromises();
      expect(lastCall().currentPage).toBe(3);
      toggle(w).vm.$emit('update:modelValue', 'pending');
      await flushPromises();
      expect(lastCall().approval).toBe('pending');
      expect(lastCall().currentPage).toBe(1);
      toggle(w).vm.$emit('update:modelValue', 'none');
      await flushPromises();
      expect(lastCall().approval).toBe('none');
      toggle(w).vm.$emit('update:modelValue', 'all');
      await flushPromises();
      expect(lastCall().approval).toBeUndefined();
    });

    it('A4C-F3: switching modules puts the switch back to all', async () => {
      schemaPatch = { approvalEnabled: true };
      const w = await mountPage();
      toggle(w).vm.$emit('update:modelValue', 'pending');
      await flushPromises();
      api.getPagedRecords.mockClear();
      await router.push('/dynamic/n');
      await flushPromises();
      const forN = api.getPagedRecords.mock.calls.filter(([k]) => k === 'n');
      expect(forN.length).toBeGreaterThan(0);
      for (const [, q] of forN) expect(q.approval).toBeUndefined();
      expect(toggle(w).props('modelValue')).toBe('all');
    });

    it('A4C-F4: the export menu carries the switch', async () => {
      schemaPatch = { approvalEnabled: true };
      const w = await mountPage();
      toggle(w).vm.$emit('update:modelValue', 'pending');
      await flushPromises();
      expect(w.findComponent(ExportMenu).props('query')).toMatchObject({ approval: 'pending' });
    });

    it('A4C-F5: the filter panel clear button leaves the switch alone', async () => {
      schemaPatch = { approvalEnabled: true };
      const w = await mountPage();
      toggle(w).vm.$emit('update:modelValue', 'pending');
      await flushPromises();
      (await filterPanel(w)).vm.$emit('update:modelValue', undefined);
      await flushPromises();
      expect(lastCall().approval).toBe('pending');
      expect(toggle(w).props('modelValue')).toBe('pending');
    });

    it('clears the condition when a schema refetch turns approvalEnabled off (list and export stop carrying it)', async () => {
      schemaPatch = { approvalEnabled: true };
      const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
      const w = await mountPage([], qc);
      w.findComponent(quasar.QTable).vm.$emit('update:pagination', { page: 3, rowsPerPage: 20 });
      toggle(w).vm.$emit('update:modelValue', 'pending');
      await flushPromises();
      expect(lastCall().approval).toBe('pending');
      schemaPatch = { approvalEnabled: false };
      await qc.invalidateQueries();
      await flushPromises();
      expect(w.find('[data-testid="approval-filter"]').exists()).toBe(false);
      expect(lastCall().approval).toBeUndefined();
      expect(lastCall().currentPage).toBe(1);
      expect(w.findComponent(ExportMenu).props('query').approval).toBeUndefined();
    });

    it('applying a saved view does not touch the switch (views never store it)', async () => {
      schemaPatch = { approvalEnabled: true };
      const w = await mountPage([view({ id: 5, columns: [2] })]);
      toggle(w).vm.$emit('update:modelValue', 'none');
      await flushPromises();
      w.findComponent(ViewMenu).vm.$emit('select', 5);
      await flushPromises();
      expect(lastCall().approval).toBe('none');
    });
  });
});

describe('ListPage on a phone', () => {
  const resize = async (width: number) => {
    window.innerWidth = width;
    window.dispatchEvent(new Event('resize'));
    await new Promise((r) => setTimeout(r, 150));
    await flushPromises();
  };
  beforeEach(() => {
    vi.clearAllMocks();
    schemaPatch = {};
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });
  afterEach(() => resize(1366));

  async function mountWithRows(width: number, items: unknown[]) {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const w = await mountPage([], qc);
    api.getPagedRecords.mockResolvedValue({ items: items as never, totalCount: 100, currentPage: 1, pagesSize: 20 });
    await qc.invalidateQueries();
    await resize(width);
    return w;
  }
  const flow = {
    statusFieldKey: 'f_4', initialState: 'draft', states: [{ value: 'draft', readOnly: false }, { value: 'done', readOnly: true }],
  };
  const withStatus = () => {
    schemaPatch = {
      workflow: flow as never,
      blocks: [{ ...schema.blocks[0]!, fields: [...schema.blocks[0]!.fields,
        { ...field(4, '狀態', { showInList: false }), type: 'enum', options: [{ value: 'draft', label: { 'zh-TW': '草稿' }, sequence: 1, active: true }] as never }] }],
    };
  };

  it('renders cards with title, status, labelled values and modified time; empty values are left out', async () => {
    withStatus();
    const w = await mountWithRows(390, [{ ...row, values: { f_2: '甲值', f_3: '', f_4: 'done' } }]);
    expect(w.find('table').exists()).toBe(false);
    const card = w.get('[data-testid="record-card-1"]');
    const header = card.get('[data-testid="record-card-header"]');
    expect(header.text()).toContain('列一');
    expect(header.get('[data-testid="record-card-status"]').text()).toBeTruthy();
    expect(header.get('[data-testid="status-chip"]').text()).toBe(header.get('[data-testid="record-card-status"]').text());
    const body = card.get('[data-testid="record-card-fields"]');
    expect(body.text()).toContain('甲');
    expect(body.text()).toContain('甲值');
    expect(body.text()).toContain(zhTW.dynamic.modifiedTime);
    expect(body.text()).not.toContain('乙');
    expect(body.text()).not.toContain('標題');
    expect(body.text()).not.toContain('狀態');
  });

  it('shows the status value in the header even when the status column is not listed', async () => {
    withStatus();
    const w = await mountWithRows(390, [{ ...row, values: { f_4: 'draft' } }]);
    expect(w.get('[data-testid="record-card-status"]').text()).toBe('草稿');
  });

  it('tapping a card, or Enter / Space on it, opens the record detail', async () => {
    for (const act of [{ type: 'click' }, { type: 'keydown', key: 'Enter' }, { type: 'keydown', key: ' ' }]) {
      const w = await mountWithRows(390, [row]);
      const card = w.get('[data-testid="record-card-1"]');
      expect(card.attributes('tabindex')).toBe('0');
      expect(card.classes()).toContain('card-clickable');
      await card.trigger(act.type, { key: act.key });
      await flushPromises();
      expect(router.currentRoute.value.fullPath).toBe('/dynamic/m/1');
    }
  });

  it('paging still asks the server for the next page', async () => {
    const w = await mountWithRows(390, [row]);
    w.findComponent(quasar.QTable).vm.$emit('update:pagination', { page: 2, rowsPerPage: 20 });
    await flushPromises();
    expect(lastCall().currentPage).toBe(2);
    expect(w.find('.q-table__bottom').exists()).toBe(true);
  });

  it('wide screens keep the table', async () => {
    const w = await mountWithRows(820, [row]);
    expect(w.find('table').exists()).toBe(true);
    expect(w.find('[data-testid="record-card-1"]').exists()).toBe(false);
  });

  it('long status and title can wrap: the status is width-limited and shrinkable', async () => {
    withStatus();
    const w = await mountWithRows(390, [{ ...row, values: { f_4: 'draft' } }]);
    const status = w.get('[data-testid="record-card-status"]');
    expect(status.classes()).not.toContain('col-auto');
    const style = (status.element as HTMLElement).style;
    expect([style.maxWidth, style.minWidth, style.overflowWrap]).toEqual(['50%', '0px', 'anywhere']);
    const title = w.get('[data-testid="record-card-title"]').element as HTMLElement;
    expect([title.style.minWidth, title.style.overflowWrap]).toEqual(['0px', 'anywhere']);
  });

  it('the card is announced as a link named after the record', async () => {
    const w = await mountWithRows(390, [row]);
    const card = w.get('[data-testid="record-card-1"]');
    expect(card.attributes('role')).toBe('link');
    expect(card.attributes('aria-label')).toBe('列一');
  });
});
