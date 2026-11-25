import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import { dynamicApi, type AppDeleteCheck, type AppInfo } from 'src/services/api/dynamic-api';
import AppDeleteDialog from './AppDeleteDialog.vue';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { getAppDeleteCheck: vi.fn(), deleteApp: vi.fn() } }));
const api = vi.mocked(dynamicApi);

const app: AppInfo = {
  key: 'tpl-a', name: { 'zh-TW': '品質檢驗' }, kind: 'template', packageVersion: '1.0.0', status: 'Closed', rowVersion: 'r',
  modules: [{ code: 'insp', key: 'insp', id: 1, name: { 'zh-TW': '檢驗紀錄' }, status: 'Active' }, { code: 'item', key: 'item', id: 2, name: { 'zh-TW': '檢驗項目' }, status: 'Active' }],
  dependents: [], unaffiliatedReferrers: [], warnings: [],
};
const base: AppDeleteCheck = {
  canDelete: true, blockers: [], purge: { records: 0, indexRows: 0, historyRows: 0 },
  clearedValues: 0, purgedTotal: 0, cleared: [], orphaned: [], reports: [],
};
const blocked: AppDeleteCheck = {
  ...base, canDelete: false,
  blockers: [
    { kind: 'liveRecords', moduleKey: 'insp', id: 1, name: '檢驗紀錄', count: 4 },
    { kind: 'appDependent', name: 'tpl-r' },
    { kind: 'referenceField', moduleKey: 'order', id: 9, name: '檢驗單' },
  ],
};
const deletable: AppDeleteCheck = {
  ...base, purge: { records: 3, indexRows: 0, historyRows: 0 }, purgedTotal: 3,
  cleared: [{ moduleKey: 'master-bp-partner', field: '來源潛在客戶', count: 2 }],
  orphaned: [{ moduleKey: 'master-bp-partner', field: '產業', count: 1 }],
  reports: [{ id: 1, name: '不良率報表' }, { id: 2, name: '檢驗趨勢' }],
};

const make = (locale = 'zh-TW') => createI18n({ legacy: false, locale, messages: { 'zh-TW': zhTW } });
async function mountDialog() {
  const w = mount(AppDeleteDialog, {
    props: { modelValue: true, app }, attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, plugins: { Notify: quasar.Notify } }], make(),
      [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]] },
  });
  await flushPromises();
  return w;
}
const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
const submit = () => q('app-delete-submit') as HTMLButtonElement;
const tick = async (id: string) => { q(id)!.click(); await flushPromises(); };

describe('AppDeleteDialog', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    document.body.innerHTML = '';
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('blocked lists the reasons and never deletes', async () => {
    api.getAppDeleteCheck.mockResolvedValue(blocked);
    await mountDialog();
    expect(api.getAppDeleteCheck).toHaveBeenCalledWith('tpl-a');
    const text = q('app-delete-blockers')!.parentElement!.textContent!;
    expect(text).toContain('目前無法刪除：');
    expect(text).toContain('模組『檢驗紀錄』還有 4 筆記錄，請先刪除記錄');
    expect(text).toContain('『tpl-r』依賴它，請先處理');
    expect(text).toContain('模組『order』的欄位『檢驗單』參照它');
    expect(submit().disabled).toBe(true);
    submit().click();
    await flushPromises();
    expect(api.deleteApp).not.toHaveBeenCalled();
  });

  it('uses module display name and report name when the backend sends them', async () => {
    api.getAppDeleteCheck.mockResolvedValue({
      ...blocked,
      blockers: [
        { kind: 'referenceField', moduleKey: 'order', moduleName: '訂單', id: 9, name: '檢驗單' },
        { kind: 'dashboard', id: 3, name: '品質看板', reportName: '不良率報表' },
        { kind: 'personalDashboard', id: 4, name: '品質看板', reportName: '不良率報表' },
      ],
    });
    await mountDialog();
    const text = q('app-delete-blockers')!.textContent!;
    expect(text).toContain('模組『訂單』的欄位『檢驗單』參照它');
    expect(text).not.toContain('order');
    expect(text).toContain('報表『不良率報表』還在儀表板『品質看板』上，請先移除卡片');
    expect(text).toContain('報表『不良率報表』還在儀表板『品質看板』的個人版上，請先移除卡片');
  });

  it('unknown blocker kind falls back to the backend name', async () => {
    api.getAppDeleteCheck.mockResolvedValue({ ...blocked, blockers: [{ kind: 'someNewKind' as never, name: 'X' }] });
    await mountDialog();
    expect(q('app-delete-blockers')!.textContent).toContain('X');
    expect(q('app-delete-blockers')!.textContent).toContain('someNewKind');
  });

  it('deletable lists what goes and needs the acknowledgement', async () => {
    api.getAppDeleteCheck.mockResolvedValue(deletable);
    api.deleteApp.mockResolvedValue({ warnings: ['共用主檔保留'] });
    const w = await mountDialog();
    const text = document.body.textContent!;
    expect(text).toContain('檢驗紀錄');
    expect(text).toContain('檢驗項目');
    expect(text).toContain('將永久清除 3 筆已刪除的記錄');
    expect(text).toContain('來源潛在客戶');
    expect(text).toContain('產業');
    expect(text).toContain('開啟時建立的群組不會刪除');
    expect(q('app-delete-reports')!.textContent).toContain('不良率報表');
    expect(q('app-delete-reports')!.textContent).toContain('檢驗趨勢');
    expect(q('app-delete-reports')!.querySelectorAll('li')).toHaveLength(2);
    expect(submit().disabled).toBe(true);
    await tick('app-delete-confirm');
    expect(submit().disabled).toBe(false);
    const notify = vi.spyOn(w.vm.$q, 'notify');
    await tick('app-delete-submit');
    expect(api.deleteApp).toHaveBeenCalledWith('tpl-a', 3);
    expect(notify).toHaveBeenCalledWith(expect.objectContaining({ message: '共用主檔保留', timeout: 0 }));
    expect(w.emitted('deleted')).toHaveLength(1);
  });

  it('no reports section when the app has none', async () => {
    api.getAppDeleteCheck.mockResolvedValue({ ...deletable, reports: [] });
    await mountDialog();
    expect(q('app-delete-reports')).toBeNull();
  });

  it('409 mismatch reloads the check and clears the acknowledgement', async () => {
    api.getAppDeleteCheck.mockResolvedValueOnce(deletable).mockResolvedValueOnce({ ...deletable, purge: { records: 4, indexRows: 0, historyRows: 0 }, purgedTotal: 4 });
    api.deleteApp.mockRejectedValue({ status: 409, message: 'x' });
    const w = await mountDialog();
    await tick('app-delete-confirm');
    await tick('app-delete-submit');
    expect(api.getAppDeleteCheck).toHaveBeenCalledTimes(2);
    expect(q('app-delete-stale')!.textContent).toContain('資料已變更，請重新檢查');
    expect(document.body.textContent).toContain('將永久清除 4 筆已刪除的記錄');
    expect(submit().disabled).toBe(true);
    expect(w.emitted('deleted')).toBeUndefined();
    await tick('app-delete-confirm');
    await tick('app-delete-submit');
    expect(api.deleteApp).toHaveBeenLastCalledWith('tpl-a', 4);
  });

  it('a stale check response for the previous target never overwrites the current one', async () => {
    const appB: AppInfo = { ...app, key: 'app-b', name: { 'zh-TW': '乙應用程式' }, modules: [] };
    const checkB: AppDeleteCheck = { ...deletable, purgedTotal: 5, purge: { records: 5, indexRows: 0, historyRows: 0 }, reports: [{ id: 7, name: '乙的報表' }] };
    let resolveA!: (c: AppDeleteCheck) => void;
    let resolveB!: (c: AppDeleteCheck) => void;
    api.getAppDeleteCheck.mockImplementation((key: string) => new Promise<AppDeleteCheck>((r) => { if (key === 'tpl-a') resolveA = r; else resolveB = r; }));
    api.deleteApp.mockResolvedValue({ warnings: [] });
    const w = await mountDialog();
    await w.setProps({ modelValue: false });
    await w.setProps({ app: appB, modelValue: true });
    await flushPromises();
    resolveB(checkB);
    await flushPromises();
    resolveA(deletable);
    await flushPromises();
    expect(q('app-delete-reports')!.textContent).toContain('乙的報表');
    expect(q('app-delete-reports')!.textContent).not.toContain('不良率報表');
    expect(document.body.textContent).toContain('將永久清除 5 筆已刪除的記錄');
    await tick('app-delete-confirm');
    await tick('app-delete-submit');
    expect(api.deleteApp).toHaveBeenCalledWith('app-b', 5);
  });

  it('switching the target while open reloads and clears the acknowledgement', async () => {
    const appB: AppInfo = { ...app, key: 'app-b', modules: [] };
    api.getAppDeleteCheck.mockResolvedValueOnce(deletable).mockResolvedValueOnce({ ...deletable, purgedTotal: 5 });
    const w = await mountDialog();
    await tick('app-delete-confirm');
    expect(submit().disabled).toBe(false);
    await w.setProps({ app: appB });
    await flushPromises();
    expect(api.getAppDeleteCheck).toHaveBeenLastCalledWith('app-b');
    expect(submit().disabled).toBe(true);
    await tick('app-delete-confirm');
    await tick('app-delete-submit');
    expect(api.deleteApp).toHaveBeenCalledWith('app-b', 5);
  });

  it.each([
    ['403', { status: 403 }, 'app-delete-forbidden'],
    ['generic', { status: 500, message: 'boom' }, 'app-delete-error'],
  ])('delete-check failure (%s) shows the error and never deletes', async (_n, err, id) => {
    api.getAppDeleteCheck.mockRejectedValue(err);
    await mountDialog();
    expect(q(id)).not.toBeNull();
    expect(submit().disabled).toBe(true);
    submit().click();
    await flushPromises();
    expect(api.deleteApp).not.toHaveBeenCalled();
  });
});
