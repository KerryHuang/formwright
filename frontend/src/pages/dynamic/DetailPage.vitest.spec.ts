import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import type { DynamicRecord, ModuleSchema, SchemaDetail, SchemaField, SchemaPermissions } from 'src/domain/dynamic/types';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import DetailPage from './DetailPage.vue';
import { textWithoutHistory } from 'src/test-utils/page-text';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: { getModuleSchema: vi.fn(), getRecord: vi.fn(), deleteRecord: vi.fn(), related: vi.fn(), recordHistory: vi.fn(), getRecordTasks: vi.fn(), getRecordApproval: vi.fn() },
}));
const api = vi.mocked(dynamicApi);

const field = (id: number, label: string): SchemaField => ({
  id, key: `f_${id}`, type: 'text', label: { 'zh-TW': label }, options: [],
  config: { required: false, readOnly: false, showInList: true, isFilterable: true, isSortable: true },
});
const all: SchemaPermissions = { read: true, add: true, edit: true, delete: true, printExport: true, pricing: true };
const schema = (p: Partial<ModuleSchema>): ModuleSchema => ({
  key: 'm', programCode: 'DYN_M', name: { 'zh-TW': '模組' }, titleFieldKey: 'f_1', version: 1, rules: [], permissions: all,
  blocks: [{ id: 1, label: { 'zh-TW': '區' }, collapsed: false, columns: 2, fields: [field(1, '標題'), field(2, '甲')] }], ...p,
});
const record: DynamicRecord = { id: 5, moduleKey: 'm', title: '記錄五', values: { f_1: '記錄五', f_2: 'x' } as Record<string, unknown>, ownerUserId: 'a', createdUserId: 'a', createdTime: '', modifiedUserId: 'a', modifiedTime: '', rowVersion: 'v', references: {}, hostKey: null };

async function mountPage(s: ModuleSchema, rec: DynamicRecord = record, tabs: unknown[] = []) {
  setActivePinia(createPinia());
  api.getModuleSchema.mockResolvedValue(s);
  api.getRecord.mockResolvedValue(rec);
  api.related.mockResolvedValue(tabs as never);
  api.recordHistory.mockResolvedValue({ items: [], totalCount: 0, currentPage: 1, pagesSize: 20 });
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/dynamic/:moduleKey/:id', component: DetailPage }] });
  await router.push('/dynamic/m/5');
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(DetailPage))) };
  const w = mount(Host, {
    global: {
      plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], router, i18n,
        [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]],
    },
  });
  await flushPromises();
  return w;
}

// Notify toasts live in one document-level container that outlives each wrapper, so earlier tests' toasts stay in body text; compare counts instead of absence.
const bodyCount = (text: string) => (document.body.textContent ?? '').split(text).length - 1;

describe('DetailPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    document.querySelectorAll('.q-dialog').forEach((el) => el.remove());
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('DetailPage hides edit and delete per permissions', async () => {
    const full = await mountPage(schema({}));
    expect(full.text()).toContain(zhTW.dynamic.edit);
    expect(full.text()).toContain(zhTW.dynamic.delete);
    const noEdit = await mountPage(schema({ permissions: { ...all, edit: false } }));
    expect(noEdit.text()).not.toContain(zhTW.dynamic.edit);
    expect(noEdit.text()).toContain(zhTW.dynamic.delete);
    const noDelete = await mountPage(schema({ permissions: { ...all, delete: false } }));
    expect(noDelete.text()).toContain(zhTW.dynamic.edit);
    expect(textWithoutHistory(noDelete)).not.toContain(zhTW.dynamic.delete);
  });

  it('hides the task panel and skips loading tasks when the module has tasks off; absent means on', async () => {
    const off = await mountPage(schema({ tasksEnabled: false }));
    expect(off.find('[data-testid="record-tasks"]').exists()).toBe(false);
    expect(api.getRecordTasks).not.toHaveBeenCalled();

    api.getRecordTasks.mockResolvedValueOnce({ items: [], canAdd: true, manualRules: [] });
    const on = await mountPage(schema({}));
    expect(api.getRecordTasks).toHaveBeenCalledWith(5);
    expect(on.find('[data-testid="record-tasks"]').exists()).toBe(true);
  });

  it('DetailPage shows the closed-app banner and no edit/delete when readOnly', async () => {
    expect((await mountPage(schema({}))).find('[data-testid="closed-banner"]').exists()).toBe(false);
    const w = await mountPage(schema({ readOnly: true, permissions: { ...all, edit: false, delete: false } }));
    expect(w.find('[data-testid="closed-banner"]').text()).toContain(zhTW.dynamic.closedBanner);
    expect(w.text()).not.toContain(zhTW.dynamic.edit);
  });

  it('the back link above the record title names the module and returns to its list', async () => {
    const w = await mountPage(schema({}));
    const back = w.find('[data-testid="module-title"]');
    expect(back.text()).toContain('模組');
    expect(back.attributes('aria-label')).toBe(zhTW.dynamic.backTo.replace('{module}', '模組'));
    expect(back.attributes('href')).toBe('/dynamic/m');
  });

  it('renders without a heading when titleFieldKey is null', async () => {
    const w = await mountPage(schema({ titleFieldKey: null, blocks: [{ id: 1, label: { 'zh-TW': '區' }, collapsed: false, columns: 2, fields: [field(2, '甲')] }] }));
    expect(w.text()).toContain('甲');
    expect(w.text()).not.toContain('記錄五');
  });

  it('does not show fields hidden by a rule', async () => {
    const hidden = { kind: 'visibility' as const, condition: { field: 1, op: 'eq' as const, value: 'nope' }, targets: ['field:2'], options: [] };
    const w = await mountPage(schema({ rules: [hidden] }));
    expect(w.text()).toContain('標題');
    expect(w.text()).not.toContain('甲');
  });

  it('still applies a Visibility rule on a formula field (stored values carry server-computed formulas)', async () => {
    const total: SchemaField = { ...field(3, '合計'), type: 'formula' };
    const hide = { kind: 'visibility' as const, condition: { field: 3, op: 'lte' as const, value: 100 }, targets: ['field:2'], options: [] };
    const s = schema({ rules: [hide], blocks: [{ id: 1, label: { 'zh-TW': '區' }, collapsed: false, columns: 2, fields: [field(1, '標題'), field(2, '甲'), total] }] });
    const base = record.values;
    record.values = { ...base, f_3: 500 }; // mountPage serves the shared `record`
    const w = await mountPage(s);
    expect(w.text()).not.toContain('甲'); // condition (total <= 100) does not hold -> hidden
    record.values = { ...base, f_3: 50 };
    const w2 = await mountPage(s);
    record.values = base;
    expect(w2.text()).toContain('甲');
  });

  describe('delete conflicts', () => {
    async function clickDelete(w: Awaited<ReturnType<typeof mountPage>>) {
      const del = w.findAll('button').find((b) => b.text() === zhTW.dynamic.delete)!;
      await del.trigger('click');
      await flushPromises();
      const confirm = Array.from(document.querySelectorAll('.q-dialog button')).find((b) => b.textContent?.trim() === zhTW.dynamic.delete) as HTMLElement;
      confirm.click();
      await flushPromises();
    }

    it('delete 409 with references opens blocked dialog', async () => {
      api.deleteRecord.mockRejectedValue({ status: 409, message: '還有其他資料參照這筆記錄，不能刪除', total: 1,
        references: [{ moduleKey: 'orders', moduleName: { 'zh-TW': '訂單' }, fieldLabel: { 'zh-TW': '客戶' }, recordId: 3, title: '單三' }] });
      const w = await mountPage(schema({}));
      await clickDelete(w);
      expect(document.querySelector('[data-testid="blocked-message"]')!.textContent).toContain('還有其他資料參照');
      expect(document.querySelectorAll('[data-testid="blocked-item"]')).toHaveLength(1);
    });

    it('delete 409 depth message shows notify', async () => {
      const staleBefore = bodyCount(zhTW.dynamic.stale);
      api.deleteRecord.mockRejectedValue({ status: 409, message: '連帶刪除超過 5 層，請先手動刪除下層資料' });
      const w = await mountPage(schema({}));
      await clickDelete(w);
      expect(document.querySelector('[data-testid="blocked-message"]')).toBeNull();
      await vi.waitFor(() => expect(document.body.textContent).toContain('連帶刪除超過 5 層'));
      expect(bodyCount(zhTW.dynamic.stale)).toBe(staleBefore);
    });

    it('a delete retried after a failure carries the same Idempotency-Key', async () => {
      api.deleteRecord.mockRejectedValueOnce({ status: 0 }).mockResolvedValue(undefined as never);
      const w = await mountPage(schema({}));
      await clickDelete(w);
      await clickDelete(w);
      const keys = api.deleteRecord.mock.calls.map((c) => c[2]);
      expect(keys[0]).toMatch(/^[A-Za-z0-9_-]{8,64}$/);
      expect(keys[1]).toBe(keys[0]);
    });

    it('delete 409 stale shows stale notify with reload', async () => {
      api.deleteRecord.mockRejectedValue({ status: 409, code: 'stale', message: 'This data was changed by someone else. Please reload' });
      const w = await mountPage(schema({}));
      await clickDelete(w);
      await vi.waitFor(() => expect(document.body.textContent).toContain(zhTW.dynamic.stale));
      expect(document.body.textContent).toContain(zhTW.dynamic.reload);
    });
  });

  describe('references', () => {
    const refField = (id: number, kind: 'module' | 'entity'): SchemaField => ({
      ...field(id, `參照${id}`), type: 'reference',
      reference: { kind, key: kind === 'module' ? 'cust' : 'test.entity', name: { 'zh-TW': 'x' }, lightUrl: '/x' },
    });
    const withRefs = (fields: SchemaField[], values: Record<string, unknown>, references: Record<string, unknown>, extra: Partial<ModuleSchema> = {}) =>
      [schema({ blocks: [{ id: 1, label: { 'zh-TW': '區' }, collapsed: false, columns: 2, fields: [field(1, '標題'), ...fields] }], ...extra }),
        { ...record, values: { f_1: 'a', ...values }, references } as DynamicRecord] as const;

    it('reference ok renders link to target record', async () => {
      const [s, r] = withRefs([refField(2, 'module'), refField(3, 'entity')], { f_2: 42, f_3: 'E-0001' }, {
        f_2: { key: '42', title: '客戶甲', state: 'ok' }, f_3: { key: 'E-0001', title: 'E-0001 甲號', state: 'ok' },
      });
      const w = await mountPage(s, r);
      const link = w.find('[data-testid="f_2"] a');
      expect(link.text()).toBe('客戶甲');
      expect(link.attributes('href')).toBe('/dynamic/cust/42');
      expect(w.find('[data-testid="f_3"]').text()).toBe('E-0001 甲號');
      expect(w.find('[data-testid="f_3"] a').exists()).toBe(false);
    });

    it('reference missing shows 已失效', async () => {
      const [s, r] = withRefs([refField(2, 'module')], { f_2: 42 }, { f_2: { key: '42', state: 'missing' } });
      const w = await mountPage(s, r);
      expect(w.find('[data-testid="f_2"]').text()).toBe(zhTW.dynamic.reference.missing);
      expect(w.find('[data-testid="f_2"] a').exists()).toBe(false);
    });

    it('reference restricted shows 無權限檢視', async () => {
      const [s, r] = withRefs([refField(2, 'module')], { f_2: 42 }, { f_2: { key: '42', title: null, state: 'restricted' } });
      const w = await mountPage(s, r);
      expect(w.find('[data-testid="f_2"]').text()).toBe(zhTW.dynamic.reference.restricted);
      expect(w.find('[data-testid="f_2"] a').exists()).toBe(false);
    });

    it('extension record shows host title', async () => {
      const [s, r] = withRefs([], {}, {}, { kind: 'extension', hostEntityKey: 'test.entity', titleFieldKey: null });
      const w = await mountPage(s, { ...r, title: 'E-0001 甲號', hostKey: 'E-0001' });
      expect(w.find('[data-testid="record-title"]').text()).toBe('E-0001 甲號');
    });
  });

  it('details render between fields and related tabs', async () => {
    const line: SchemaDetail = {
      moduleKey: 'line', name: { 'zh-TW': '訂單明細' }, parentFieldId: 9, parentFieldKey: 'f_9', titleFieldKey: 'f_7',
      fields: [field(7, '品名')], rules: [],
    };
    const lineRow: DynamicRecord = { ...record, id: 11, moduleKey: 'line', values: { f_7: '螺絲' } };
    const tab = { fieldId: 5, moduleKey: 'o', moduleName: { 'zh-TW': '關聯模組' }, fieldLabel: { 'zh-TW': '欄' }, canAdd: false };
    api.relatedPaged = vi.fn().mockResolvedValue({ items: [], totalCount: 0 });
    const w = await mountPage(schema({ details: [line] }), { ...record, details: { line: [lineRow] } }, [tab]);
    const html = w.html();
    expect(html).toContain('螺絲');
    expect(html.indexOf('data-testid="f_2"')).toBeLessThan(html.indexOf('訂單明細'));
    expect(html.indexOf('訂單明細')).toBeLessThan(html.indexOf('data-testid="related-tabs"'));
  });
});

describe('DetailPage 找不到、無權限、載入失敗', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });
  const reject = (status: number) => () => Promise.reject({ status });
  const u = zhTW.dynamic.unavailable;
  const named = (text: string) => text.replace('{module}', '模組');
  const back = (w: Awaited<ReturnType<typeof mountPage>>) => w.get('[data-testid="record-unavailable-back"]');
  const box = (w: Awaited<ReturnType<typeof mountPage>>) => w.get('[data-testid="record-unavailable"]');

  async function mountFailing(set: () => void) {
    setActivePinia(createPinia());
    api.getRecordApproval.mockImplementation(reject(404));
    set();
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/dynamic/:moduleKey/:id', component: DetailPage }, { path: '/:p(.*)*', component: { render: () => null } }] });
    await router.push('/dynamic/m/5');
    const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
    const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(DetailPage))) };
    const w = mount(Host, {
      global: {
        plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], router, i18n,
          [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]],
      },
    });
    await flushPromises();
    return w;
  }
  const withSchema = (recordStatus: number) => mountFailing(() => {
    api.getModuleSchema.mockResolvedValue(schema({}));
    api.getRecord.mockImplementation(reject(recordStatus));
  });

  it('單據不存在：說明原因、回到模組列表、沒有紅字', async () => {
    const w = await withSchema(404);
    expect(box(w).text()).toContain(named(u.recordMissingTitle));
    expect(box(w).text()).toContain(u.recordMissingReason);
    expect(back(w).text()).toContain(named(u.backToList));
    expect(back(w).attributes('href')).toBe('/dynamic/m');
    expect(w.find('[data-testid="record-unavailable-retry"]').exists()).toBe(false);
    expect(w.html()).not.toContain('text-negative');
  });

  it('單據屬於別的模組：一樣是找不到', async () => {
    const w = await mountFailing(() => {
      api.getModuleSchema.mockResolvedValue(schema({}));
      api.getRecord.mockResolvedValue({ ...record, moduleKey: 'other' });
    });
    expect(box(w).text()).toContain(named(u.recordMissingTitle));
  });

  it('單據 404、schema 還沒回來：先給通用文案與回首頁，schema 回來後換成模組名與回列表', async () => {
    let answer!: (s: ModuleSchema) => void;
    const w = await mountFailing(() => {
      api.getModuleSchema.mockImplementation(() => new Promise<ModuleSchema>((resolve) => { answer = resolve; }));
      api.getRecord.mockImplementation(reject(404));
    });
    expect(box(w).get('.text-h6').text()).toBe(u.recordMissingTitle.replace('{module}', u.fallbackModule));
    expect(box(w).text()).toContain(u.recordMissingReason);
    expect(back(w).text()).toContain(u.backHome);
    expect(back(w).attributes('href')).toBe('/');
    answer(schema({}));
    await flushPromises();
    expect(box(w).get('.text-h6').text()).toBe(named(u.recordMissingTitle));
    expect(back(w).text()).toContain(named(u.backToList));
    expect(back(w).attributes('href')).toBe('/dynamic/m');
  });

  it('模組不存在：回到首頁', async () => {
    const w = await mountFailing(() => {
      api.getModuleSchema.mockImplementation(reject(404));
      api.getRecord.mockImplementation(reject(404));
    });
    expect(box(w).text()).toContain(u.moduleMissingTitle);
    expect(box(w).text()).toContain(u.moduleMissingReason);
    expect(back(w).text()).toContain(u.backHome);
    expect(back(w).attributes('href')).toBe('/');
  });

  it('單據 403（schema 正常）：無權限、回到列表', async () => {
    const w = await withSchema(403);
    expect(box(w).text()).toContain(named(u.forbiddenTitle));
    expect(box(w).text()).toContain(u.forbiddenReason);
    expect(back(w).text()).toContain(named(u.backToList));
    expect(w.find('[data-testid="record-unavailable-retry"]').exists()).toBe(false);
  });

  it('schema 403：模組名未知、回到首頁', async () => {
    const w = await mountFailing(() => {
      api.getModuleSchema.mockImplementation(reject(403));
      api.getRecord.mockImplementation(reject(403));
    });
    expect(box(w).get('.text-h6').text()).toBe(u.forbiddenTitleUnknown);
    expect(back(w).text()).toContain(u.backHome);
    expect(back(w).attributes('href')).toBe('/');
  });

  it('單據 500：重新載入會重送單據查詢，成功後顯示單據；schema 不重抓', async () => {
    const w = await withSchema(500);
    expect(box(w).text()).toContain(named(u.failedTitle));
    expect(box(w).text()).toContain(u.failedReason);
    expect(back(w).text()).toContain(named(u.backToList));
    expect(api.getRecord).toHaveBeenCalledTimes(1);
    api.getRecord.mockResolvedValue(record);
    api.related.mockResolvedValue([] as never);
    api.recordHistory.mockResolvedValue({ items: [], totalCount: 0, currentPage: 1, pagesSize: 20 });
    await w.get('[data-testid="record-unavailable-retry"]').trigger('click');
    await flushPromises();
    expect(api.getRecord).toHaveBeenCalledTimes(2);
    expect(api.getModuleSchema).toHaveBeenCalledTimes(1);
    expect(w.find('[data-testid="record-unavailable"]').exists()).toBe(false);
    expect(w.text()).toContain('記錄五');
  });

  it('schema 與單據都 500：模組名未知、回首頁，重新載入兩個都重抓', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const w = await mountFailing(() => {
      api.getModuleSchema.mockImplementation(reject(500));
      api.getRecord.mockImplementation(reject(500));
    });
    await vi.advanceTimersByTimeAsync(10_000); // schema 查詢遇 500 會自己重試幾次才放棄
    expect(box(w).get('.text-h6').text()).toBe(u.failedTitleUnknown);
    expect(back(w).attributes('href')).toBe('/');
    const before = { schema: api.getModuleSchema.mock.calls.length, record: api.getRecord.mock.calls.length };
    await w.get('[data-testid="record-unavailable-retry"]').trigger('click');
    await flushPromises();
    expect(api.getModuleSchema.mock.calls.length).toBe(before.schema + 1);
    expect(api.getRecord.mock.calls.length).toBe(before.record + 1);
    vi.useRealTimers();
  });
});

describe('DetailPage 狀態章與數字', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });
  const option = (id: number, label: string, color?: string) => ({ id, value: `opt_${id}`, label: { 'zh-TW': label }, sequence: id, active: true, color });
  const status: SchemaField = { ...field(3, '狀態'), type: 'enum', options: [option(1, '已確認', 'blue')] };
  const source: SchemaField = { ...field(4, '來源'), type: 'enum', options: [option(2, '電話', 'red')] };
  const amount: SchemaField = { ...field(5, '金額'), type: 'decimal' };
  const blocks = [{ id: 1, label: { 'zh-TW': '區' }, collapsed: false, columns: 2, fields: [field(1, '標題'), status, source, amount] }];
  const workflow = { statusFieldKey: 'f_3', initialState: 'opt_1', states: [{ value: 'opt_1', readOnly: false }], transitions: [] };
  const rec = { ...record, values: { f_1: '記錄五', f_3: 'opt_1', f_4: 'opt_2', f_5: 10 } };

  it('狀態欄值與標題列都是章；一般下拉欄位維持文字；數字值掛 num', async () => {
    const w = await mountPage(schema({ blocks, workflow }), rec);
    const value = w.get('[data-testid="f_3"] [data-testid="status-chip"]');
    expect(value.text()).toBe('已確認');
    expect(value.classes()).toContain('text-blue');
    expect(w.get('[data-testid="record-header-bar"] [data-testid="status-chip"]').text()).toBe('已確認');
    expect(w.find('[data-testid="f_4"] [data-testid="status-chip"]').exists()).toBe(false);
    expect(w.get('[data-testid="f_4"]').text()).toBe('電話');
    expect(w.get('[data-testid="f_5"]').classes()).toContain('num');
    expect(w.get('[data-testid="f_4"]').classes()).not.toContain('num');
  });

  it('沒有狀態流程的模組不出現章', async () => {
    const w = await mountPage(schema({ blocks }), rec);
    expect(w.find('[data-testid="status-chip"]').exists()).toBe(false);
  });

  it('狀態欄是空值就沒有章', async () => {
    const w = await mountPage(schema({ blocks, workflow }), { ...rec, values: { ...rec.values, f_3: null } });
    expect(w.find('[data-testid="status-chip"]').exists()).toBe(false);
  });

  it('狀態欄被規則隱藏時，標題列也不顯示章', async () => {
    const hide = { kind: 'visibility' as const, condition: { field: 5, op: 'gt' as const, value: 100 }, targets: ['field:3'], options: [] };
    const w = await mountPage(schema({ blocks, workflow, rules: [hide] }), rec);
    expect(w.find('[data-testid="status-chip"]').exists()).toBe(false);
  });
});
