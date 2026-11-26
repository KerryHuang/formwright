import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import { setupApi, type MaintenanceInfo } from 'src/services/api/setup-api';
import MaintenancePage from './MaintenancePage.vue';

vi.mock('src/services/api/setup-api', async (orig) => ({
  ...(await orig<typeof import('src/services/api/setup-api')>()),
  setupApi: { getStatus: vi.fn(), isReady: vi.fn(), verify: vi.fn(), runChecks: vi.fn(), complete: vi.fn(), getMaintenance: vi.fn() },
}));
const api = vi.mocked(setupApi);
const replace = vi.fn();

const info = (o: Partial<MaintenanceInfo> = {}): MaintenanceInfo => ({
  reason: 'preUpgradeBackupFailed', errorCode: 'destinationUnwritable', version: '0.0.2', previousVersion: '0.0.1', ...o,
});

async function mountPage() {
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/:p(.*)*', component: { template: '<i/>' } }] });
  router.replace = replace as never;
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  mount(MaintenancePage, {
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], i18n, router] },
  });
  await flushPromises();
}
const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
const text = () => q('maintenance-page')!.textContent!;

beforeEach(() => {
  vi.clearAllMocks();
  document.body.innerHTML = '';
});

describe('維護頁', () => {
  it('backup failed shows reason and actions (AC-12)', async () => {
    api.getMaintenance.mockResolvedValue(info());
    await mountPage();
    for (const s of ['升級前備份失敗', '備份位置寫不進去', 'destinationUnwritable', '換回舊版映像（0.0.1）', '修好備份位置', '0.0.2']) expect(text()).toContain(s);
    expect(api.getMaintenance).toHaveBeenCalledTimes(1);
    expect(Object.entries(api).filter(([k, fn]) => k !== 'getMaintenance' && vi.mocked(fn).mock.calls.length > 0)).toEqual([]);
  });

  it('unknown error code falls back to code', async () => {
    api.getMaintenance.mockResolvedValue(info({ errorCode: 'somethingNew' }));
    await mountPage();
    const t = q('maintenance-error')!.textContent!;
    expect(t.split('somethingNew').length - 1).toBe(1);
    expect(t).toContain('錯誤代碼：');
    expect(t).not.toContain('ops.backup.error.somethingNew');
  });

  it('locale switcher present (D-20)', async () => {
    api.getMaintenance.mockResolvedValue(info());
    await mountPage();
    expect(q('locale-open')).not.toBeNull();
  });

  it('no previous version uses generic rollback', async () => {
    api.getMaintenance.mockResolvedValue(info({ previousVersion: null }));
    await mountPage();
    expect(text()).toContain('換回升級前使用的映像版本');
    expect(text()).not.toContain('（）');
    expect(text()).not.toContain('null');
  });

  it('no error code hides the row', async () => {
    api.getMaintenance.mockResolvedValue(info({ errorCode: null }));
    await mountPage();
    expect(q('maintenance-error')).toBeNull();
  });

  it('not configured reason', async () => {
    api.getMaintenance.mockResolvedValue(info({ reason: 'backupNotConfigured', errorCode: 'locationInvalid' }));
    await mountPage();
    expect(text()).toContain('備份位置沒有設定或設定無效');
    expect(text()).toContain('備份位置不合法');
    expect(text()).toContain('Backup:Root');
    expect(text()).not.toContain('修好備份位置');
  });

  it('not in maintenance goes to login', async () => {
    api.getMaintenance.mockResolvedValue(null);
    await mountPage();
    expect(replace).toHaveBeenCalledWith('/login');
  });

  it('unreachable shows reload', async () => {
    api.getMaintenance.mockRejectedValue({ status: 0, code: 'network' });
    await mountPage();
    expect(text()).toContain('連不上服務');
    expect(q('maintenance-reload')).not.toBeNull();
  });

  it('retry refetches', async () => {
    api.getMaintenance.mockResolvedValue(info());
    await mountPage();
    q('maintenance-retry')!.click();
    await flushPromises();
    expect(api.getMaintenance).toHaveBeenCalledTimes(2);
  });
});
