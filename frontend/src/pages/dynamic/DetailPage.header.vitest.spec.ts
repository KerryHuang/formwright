import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import type { ApprovalInstanceView } from 'src/domain/dynamic/approval-types';
import type { DynamicRecord, ModuleSchema, RecordConversion, SchemaPermissions } from 'src/domain/dynamic/types';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { useIdentityStore } from 'src/stores/use-identity-store';
import DetailPage from './DetailPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: {
    getModuleSchema: vi.fn(), getRecord: vi.fn(), deleteRecord: vi.fn(), related: vi.fn(), recordHistory: vi.fn(), getRecordTasks: vi.fn(),
    getRecordConversions: vi.fn(), getPrintTemplates: vi.fn(), printRecord: vi.fn(), getRecordApproval: vi.fn(), getEffectivePolicies: vi.fn(),
  },
}));
const api = vi.mocked(dynamicApi);

const all: SchemaPermissions = { read: true, add: true, edit: true, delete: true, printExport: true, pricing: true };
const none: SchemaPermissions = { read: true, add: false, edit: false, delete: false, printExport: false, pricing: false };
const field = { id: 1, key: 'f_1', type: 'text' as const, label: { 'zh-TW': '標題' }, options: [], config: { required: false, readOnly: false, showInList: true, isFilterable: true, isSortable: true } };
// A hand-made module, not part of any app pack: the bar must depend on schema, permissions and API answers only.
const schema = (permissions: SchemaPermissions): ModuleSchema => ({
  key: 'm', programCode: 'DYN_M', name: { 'zh-TW': '自建模組' }, titleFieldKey: 'f_1', version: 1, rules: [], permissions,
  blocks: [{ id: 1, label: { 'zh-TW': '區' }, collapsed: false, columns: 2, fields: [field] }],
});
const record: DynamicRecord = { id: 5, moduleKey: 'm', title: '記錄五', values: { f_1: '記錄五' }, ownerUserId: 'a', createdUserId: 'a', createdTime: '', modifiedUserId: 'a', modifiedTime: '', rowVersion: 'v', references: {}, hostKey: null };
const conversion = (extra: Partial<RecordConversion> = {}): RecordConversion => ({
  key: 'q2o', name: { 'zh-TW': '轉訂單' }, targetModuleKey: 'order', targetName: { 'zh-TW': '訂單' }, allowed: true, targets: [], ...extra,
});

interface Setup { schemaFail?: number; recordModule?: string; recordFail?: number; permissions?: SchemaPermissions; conversions?: RecordConversion[]; templates?: boolean; current?: ApprovalInstanceView | null; rec?: DynamicRecord }

async function mountPage(width: number, s: Setup = {}) {
  window.innerWidth = width;
  setActivePinia(createPinia());
  useIdentityStore().userId = 'me';
  if (s.schemaFail) api.getModuleSchema.mockRejectedValue({ status: s.schemaFail, message: 'x' });
  else api.getModuleSchema.mockResolvedValue(schema(s.permissions ?? all));
  if (s.recordFail) api.getRecord.mockRejectedValue({ status: s.recordFail, message: 'x' });
  else api.getRecord.mockResolvedValue(s.recordModule ? { ...record, moduleKey: s.recordModule } : s.rec ?? record);
  api.related.mockResolvedValue([] as never);
  api.recordHistory.mockResolvedValue({ items: [], totalCount: 0, currentPage: 1, pagesSize: 20 });
  api.getRecordTasks.mockResolvedValue({ items: [], canAdd: false, manualRules: [] });
  api.getRecordConversions.mockResolvedValue(s.conversions ?? []);
  api.getPrintTemplates.mockResolvedValue(s.templates ? [{ key: 'sheet', name: { 'zh-TW': '採購單' } }] : []);
  api.printRecord.mockResolvedValue('<p>預覽內容</p>');
  api.getRecordApproval.mockResolvedValue({ current: s.current ?? null, previous: [], submitTargets: [] });
  api.getEffectivePolicies.mockResolvedValue({ locales: [], values: {} });
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/dynamic/:moduleKey/:id', component: DetailPage }] });
  await router.push('/dynamic/m/5');
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(DetailPage))) };
  const w = mount(Host, {
    attachTo: document.body,
    global: {
      plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], router, i18n,
        [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]],
    },
  });
  window.dispatchEvent(new Event('resize'));
  await new Promise((r) => setTimeout(r, 150));
  await flushPromises();
  return w;
}

const openMore = async (w: ReturnType<typeof mount>) => {
  await w.find('[data-testid="record-more"]').trigger('click');
  await flushPromises();
  return [...document.body.querySelectorAll<HTMLElement>('.q-menu .q-item')];
};

describe('DetailPage sticky header', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    document.body.innerHTML = '';
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });
  afterEach(() => { window.innerWidth = 1024; });

  it('wide: back, title, edit, delete, print and convert are all in the one fixed bar', async () => {
    const w = await mountPage(1366, { templates: true, conversions: [conversion()] });
    const bar = w.find('[data-testid="record-header-bar"]');
    expect(bar.find('[data-testid="module-title"]').text()).toContain('自建模組');
    expect(bar.find('[data-testid="record-title"]').text()).toBe('記錄五');
    expect(bar.text()).toContain(zhTW.dynamic.edit);
    expect(bar.text()).toContain(zhTW.dynamic.delete);
    expect(bar.find('[data-testid="print-menu"]').exists()).toBe(true);
    expect(bar.find('[data-testid="convert-open-q2o"]').exists()).toBe(true);
    expect(w.find('[data-testid="record-bottom-bar"]').exists()).toBe(false);
  });

  it('wide: the reason a conversion is blocked and the documents already converted stay in the content, not in the bar', async () => {
    const w = await mountPage(1366, {
      conversions: [
        conversion({ key: 'x', allowed: false, reason: '來源狀態不符', name: { 'zh-TW': '轉出貨' } }),
        conversion({ key: 'y', targets: [{ id: 9, title: 'SO-0009', moduleKey: 'order' }] }),
      ],
    });
    const bar = w.find('[data-testid="record-header-bar"]');
    expect(bar.find('[data-testid="convert-reason-x"]').exists()).toBe(false);
    expect(bar.find('[data-testid="convert-targets-y"]').exists()).toBe(false);
    expect(w.find('[data-testid="convert-reason-x"]').text()).toContain('來源狀態不符');
    expect(w.find('[data-testid="convert-targets-y"] a').text()).toBe('SO-0009');
    expect(w.find('[data-testid="convert-open-x"]').exists()).toBe(false);
  });

  it('wide: the convert button opens the convert dialog on the page', async () => {
    const w = await mountPage(1366, { conversions: [conversion({ lines: [] })] });
    await w.find('[data-testid="convert-open-q2o"]').trigger('click');
    await flushPromises();
    expect(document.body.querySelector('.q-dialog')).not.toBeNull();
  });

  it('wide: without edit, delete, print or conversion there are no action buttons', async () => {
    const w = await mountPage(1366, { permissions: none });
    expect(w.find('[data-testid="record-actions"]').exists()).toBe(false);
    expect(w.find('[data-testid="record-title"]').exists()).toBe(true);
  });

  it('phone: top bar has back and title; bottom bar has Edit and More; the page leaves room under the last block', async () => {
    const w = await mountPage(390, { templates: true });
    expect(w.find('[data-testid="module-title"]').text()).not.toContain('自建模組');
    expect(w.find('[data-testid="record-title"]').text()).toBe('記錄五');
    const bottom = w.find('[data-testid="record-bottom-bar"]');
    expect(bottom.text()).toContain(zhTW.dynamic.edit);
    expect(bottom.text()).not.toContain(zhTW.dynamic.delete);
    expect(w.find('.q-page').attributes('style')).toContain('padding-bottom: calc(88px + env(safe-area-inset-bottom))');
    const items = await openMore(w);
    expect(items.map((i) => i.textContent?.trim())).toEqual(['列印：採購單', zhTW.dynamic.delete]);
    expect(items.at(-1)!.classList.contains('text-negative')).toBe(true);
  });

  it('phone: More → print opens the preview with the document', async () => {
    const w = await mountPage(390, { templates: true });
    const items = await openMore(w);
    items[0]!.click();
    await flushPromises();
    expect(api.printRecord).toHaveBeenCalledWith(5, 'sheet', 'zh-TW');
    expect(document.body.querySelector('[data-testid="print-preview"]')).not.toBeNull();
    expect(document.body.querySelector('.q-menu')).toBeNull();
  });

  it('phone: More → delete asks first and cancelling leaves the record alone', async () => {
    const w = await mountPage(390);
    const items = await openMore(w);
    items.at(-1)!.click();
    await flushPromises();
    const dialog = document.body.querySelector('.q-dialog')!;
    expect(dialog.textContent).toContain(zhTW.dynamic.confirmDelete);
    [...dialog.querySelectorAll<HTMLElement>('button')].find((b) => b.textContent?.includes(zhTW.dynamic.cancel))!.click();
    await flushPromises();
    expect(api.deleteRecord).not.toHaveBeenCalled();
  });

  it('phone: More → convert opens the convert dialog', async () => {
    const w = await mountPage(390, { conversions: [conversion({ lines: [] })] });
    const items = await openMore(w);
    expect(items.map((i) => i.textContent?.trim())[0]).toBe(zhTW.docflow.convert.button.replace('{name}', '訂單'));
    items[0]!.click();
    await flushPromises();
    expect(document.body.querySelector('.q-dialog')).not.toBeNull();
  });

  it('phone: with the approval action bar showing, the record actions move to a More menu in the top bar and the bottom has only approval', async () => {
    const current: ApprovalInstanceView = {
      id: 7, status: 'pending', routeKind: 'flow', flowName: { 'zh-TW': '簽核' }, fromOptionId: 1, toOptionId: 2, initiatorUserId: 'a',
      submittedTime: '2026-11-12T08:00:00', completedTime: null, rowVersion: 'iv', canWithdraw: false, canCancel: false, titleSnapshot: '記錄五', actions: [],
      stages: [{
        id: 1, seq: 1, stageCode: 's1', name: { 'zh-TW': '主管' }, mode: 'all', status: 'active', skipReason: null, dueTime: null, editableFieldKeys: [],
        tasks: [{ id: 3, assigneeUserId: 'me', assigneeName: '我', originalUserId: 'me', reason: 'resolved', status: 'open', actedTime: null, canAct: true, canReturnPrevious: false }],
      }],
    };
    const rec = { ...record, approval: { status: 'pending' as const, stageName: null, stageSeq: 1, canSubmit: false } };
    const w = await mountPage(390, { templates: true, permissions: { ...all, edit: false, delete: false }, current, rec });
    expect(w.find('[data-testid="approval-mobile-bar"]').exists()).toBe(true);
    expect(w.find('[data-testid="record-bottom-bar"]').exists()).toBe(false);
    expect(w.find('[data-testid="record-more"]').attributes('aria-label')).toBe(zhTW.common.recordActions);
    expect(w.find('[data-testid="approval-more"]').exists() ? w.find('[data-testid="approval-more"]').attributes('aria-label') : zhTW.common.moreActions).toBe(zhTW.common.moreActions);
    const items = await openMore(w);
    expect(items.map((i) => i.textContent?.trim())).toContain('列印：採購單');
  });

  it('phone: nothing to do means no bottom bar and no extra room', async () => {
    const w = await mountPage(390, { permissions: none });
    expect(w.find('[data-testid="record-bottom-bar"]').exists()).toBe(false);
    expect(w.find('.q-page').attributes('style') ?? '').not.toContain('88px');
  });

  it('phone: while a print request is in flight the print items are disabled in the menu', async () => {
    const w = await mountPage(390, { templates: true });
    api.printRecord.mockReturnValue(new Promise(() => {}));
    let items = await openMore(w);
    items[0]!.click();
    await flushPromises();
    items = await openMore(w);
    expect(items[0]!.classList.contains('disabled')).toBe(true);
    expect(api.printRecord).toHaveBeenCalledTimes(1);
  });

  describe('conversions are asked for only where the content is shown', () => {
    it('a record of another module (recordMissing) sends no conversion request', async () => {
      await mountPage(1366, { recordModule: 'other' });
      expect(api.getRecordConversions).not.toHaveBeenCalled();
    });
    it('a missing schema sends no conversion request', async () => {
      await mountPage(1366, { schemaFail: 404 });
      expect(api.getRecordConversions).not.toHaveBeenCalled();
    });
    it('a failed schema load sends no conversion request', async () => {
      await mountPage(1366, { schemaFail: 500 });
      expect(api.getRecordConversions).not.toHaveBeenCalled();
    });
    it('a missing record sends no conversion request', async () => {
      await mountPage(1366, { recordFail: 404 });
      expect(api.getRecordConversions).not.toHaveBeenCalled();
    });
    it('the normal case still asks once', async () => {
      await mountPage(1366, { conversions: [conversion()] });
      expect(api.getRecordConversions).toHaveBeenCalledTimes(1);
    });
  });
});
