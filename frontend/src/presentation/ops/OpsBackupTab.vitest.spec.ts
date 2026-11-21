import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { BackupOverview, BackupRun } from 'src/domain/ops/ops-types';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import { HttpFail, mockHttp, type Call } from 'src/test-utils/mock-http';
import OpsBackupTab from './OpsBackupTab.vue';

const notify = vi.hoisted(() => vi.fn());
vi.mock('quasar', async (orig) => ({ ...(await orig<typeof import('quasar')>()), Notify: { create: notify, install: (o: { $q: { notify: unknown } }) => { o.$q.notify = notify; } } }));
vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
let calls: Call[];
let w: ReturnType<typeof mount> | undefined;

const run = (o: Partial<BackupRun> = {}): BackupRun => ({
  id: 1, runKey: 'k1', trigger: 'schedule', locationId: 'loc-a', status: 'succeeded', queuedUtc: '2026-11-22T00:00:00Z', startedUtc: '2026-11-22T00:00:01Z',
  snapshotUtc: '2026-11-22T00:00:02Z', finishedUtc: '2026-11-22T00:01:10Z', storeKind: 'localDirectory', setName: 'set-1', provider: 'sqlite', databaseBytes: 5_242_880,
  attachmentCount: 12, newAttachmentCount: 3, ...o,
});
const overview = (o: Partial<BackupOverview> = {}): BackupOverview => ({
  configured: true, locationValid: true, locationStatus: 'ok', locationId: 'loc-a', storeKind: 'localDirectory', displayLocation: '/srv/backups', isolated: false,
  edition: 'OnPrem', canDelete: true, provider: 'sqlite', includesKeys: false, schedule: { hour: 2, offsetMinutes: 480, nextScheduledUtc: '2026-11-22T18:00:00Z' },
  retentionCount: 7, manualRetentionDays: 7, lastSuccess: run(), runs: [run()], manualAllowed: true, ...o,
});

async function make(ov: BackupOverview, extra?: (c: Call) => unknown) {
  calls = mockHttp((c) => {
    const x = extra?.(c);
    if (x !== undefined) return x;
    if (c.url === '/system/ops/backups' && c.method === 'get') return ov;
    if (c.url === '/system/ops/backups' && c.method === 'post') return run({ status: 'queued' });
    if (c.url.startsWith('/system/ops/backups/')) return run({ id: Number(c.url.split('/').pop()), errorDetail: '錯誤摘要內容' });
    return [];
  });
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  w = mount(OpsBackupTab, {
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], i18n, [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]] },
  });
  await flushPromises();
}
const q = (id: string) => document.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
const text = (id: string) => q(id)?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
afterEach(() => { notify.mockClear(); w?.unmount(); document.body.innerHTML = ''; vi.restoreAllMocks(); });

describe('OpsBackupTab 狀態卡', () => {
  it('正常：位置、隔離、排程、保留、上次成功、金鑰說明', async () => {
    await make(overview());
    expect(text('backup-location')).toBe('/srv/backups');
    expect(text('backup-isolated')).toBe('否');
    expect(text('backup-schedule')).toBe('每天 02:00（+08:00）');
    expect(text('backup-retention')).toContain('排程備份最近 7 份、立即備份 7 天');
    expect(text('backup-last-success')).toContain('5 MB');
    expect(text('backup-keys')).toContain('不含加密金鑰');
    expect(q('backup-notice')).toBeNull();
    expect(q('backup-not-isolated')).toBeNull();
    expect(q('backup-run')!.hasAttribute('disabled')).toBe(false);
  });

  it('未設定：只有說明，沒有立即備份按鈕', async () => {
    await make(overview({ configured: false, locationValid: false, locationStatus: 'notConfigured', storeKind: undefined, displayLocation: undefined, lastSuccess: undefined, runs: [], manualAllowed: false, manualDisabledReason: 'notConfigured' }));
    expect(text('backup-notice')).toContain('Backup:Root');
    expect(q('backup-run')).toBeNull();
    expect(q('backup-keys')).toBeNull();
  });

  it.each([['locationInvalid', '不能和資料庫'], ['locationMismatch', '不同'], ['destinationUnwritable', '寫不進去']])('位置問題 %s 顯示對應提示', async (status, needle) => {
    await make(overview({ configured: true, locationValid: false, locationStatus: status, manualAllowed: false, manualDisabledReason: 'notConfigured' }));
    expect(text('backup-notice')).toContain(needle);
    expect(q('backup-notice')!.getAttribute('data-notice')).toBe(status);
    expect(q('backup-run')!.hasAttribute('disabled')).toBe(true);
  });

  it('雲端版非隔離：黃色提示；不顯示路徑（以 API 回應為準）且加「洽廠商」', async () => {
    await make(overview({ edition: 'Cloud', displayLocation: 'localDirectory' }));
    expect(q('backup-not-isolated')).not.toBeNull();
    expect(text('backup-location')).toContain('雲端版不顯示路徑');
    expect(text('backup-location')).not.toContain('/srv');
    expect(text('backup-retention')).toContain('洽廠商');
  });

  it('地端版隔離：不出黃色提示', async () => {
    await make(overview({ isolated: true }));
    expect(q('backup-not-isolated')).toBeNull();
    expect(text('backup-isolated')).toBe('是');
  });

  it('不可刪除的位置：寫不會自動刪除舊備份', async () => {
    await make(overview({ canDelete: false }));
    expect(text('backup-retention')).toContain('不會自動刪除');
  });
});

describe('OpsBackupTab 立即備份', () => {
  it('Demo 站：停用並寫原因', async () => {
    await make(overview({ manualAllowed: false, manualDisabledReason: 'demoSite' }));
    expect(q('backup-run')!.hasAttribute('disabled')).toBe(true);
    expect(text('backup-disabled-reason')).toBe('Demo 站不開放立即備份');
  });

  it('執行中：停用並顯示目前步驟與附件進度', async () => {
    const cur = run({ id: 9, status: 'running', stage: 'attachments', progressDone: 120, progressTotal: 340 });
    await make(overview({ current: cur, runs: [cur], manualAllowed: false, manualDisabledReason: 'inProgress' }));
    expect(q('backup-run')!.hasAttribute('disabled')).toBe(true);
    expect(text('backup-disabled-reason')).toBe('附件 120／340');
  });

  it('等待清孤兒附件、排隊中', async () => {
    const cur = run({ id: 9, status: 'running', stage: 'waiting' });
    await make(overview({ current: cur, runs: [cur], manualAllowed: false, manualDisabledReason: 'inProgress' }));
    expect(text('backup-disabled-reason')).toBe('等待清孤兒附件結束');
  });

  it('執行中每 5 秒重讀；結束後停止', async () => {
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'setTimeout', 'clearTimeout'] });
    try {
      const cur = run({ id: 9, status: 'running', stage: 'database' });
      let finished = false;
      calls = mockHttp(() => (finished ? overview() : overview({ current: cur, runs: [cur], manualAllowed: false, manualDisabledReason: 'inProgress' })));
      const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
      w = mount(OpsBackupTab, {
        attachTo: document.body,
        global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], i18n, [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]] },
      });
      await vi.advanceTimersByTimeAsync(10);
      const n0 = calls.length;
      await vi.advanceTimersByTimeAsync(5_100);
      expect(calls.length).toBe(n0 + 1);
      finished = true;
      await vi.advanceTimersByTimeAsync(5_100);
      const n1 = calls.length;
      await vi.advanceTimersByTimeAsync(20_000);
      expect(calls.length).toBe(n1);
    } finally { vi.useRealTimers(); }
  });

  it('按下去 POST 並提示已排入', async () => {
    await make(overview());
    q('backup-run')!.click();
    await flushPromises();
    expect(calls.some((c) => c.method === 'post' && c.url === '/system/ops/backups')).toBe(true);
    expect(notify).toHaveBeenCalledWith(expect.objectContaining({ type: 'positive', message: expect.stringContaining('已排入') }));
  });

  it('409 backupInProgress 顯示對應訊息', async () => {
    await make(overview(), (c) => (c.method === 'post' ? new HttpFail(409, { code: 'backupInProgress' }) : undefined));
    q('backup-run')!.click();
    await flushPromises();
    expect(notify).toHaveBeenCalledWith(expect.objectContaining({ type: 'negative', message: '已有一份備份在排隊或執行中。' }));
  });

  it('locationPending 停用原因', async () => {
    await make(overview({ manualAllowed: false, manualDisabledReason: 'locationPending' }));
    expect(text('backup-disabled-reason')).toContain('一分鐘');
  });
});

describe('OpsBackupTab 歷程', () => {
  it('失敗列顯示錯誤代碼的文字；不認得的代碼原樣顯示', async () => {
    const rs = [run({ id: 3, status: 'failed', errorCode: 'destinationUnwritable' }), run({ id: 2, status: 'failed', errorCode: 'someFutureCode' })];
    await make(overview({ runs: rs }));
    expect(text('backup-row-3')).toContain('備份位置寫不進去');
    expect(text('backup-row-2')).toContain('someFutureCode');
  });

  it('別的位置的歷程標舊位置；立即備份帶操作者', async () => {
    const rs = [run({ id: 3, trigger: 'manual', requestedUserId: 'admin' }), run({ id: 2, locationId: 'loc-old' })];
    await make(overview({ runs: rs }));
    expect(q('backup-row-2')!.querySelector('[data-testid="backup-old-location"]')).not.toBeNull();
    expect(q('backup-row-3')!.querySelector('[data-testid="backup-old-location"]')).toBeNull();
    expect(text('backup-row-3')).toContain('立即（admin）');
  });

  it('沒有紀錄時顯示空狀態', async () => {
    await make(overview({ runs: [], lastSuccess: undefined }));
    expect(document.body.textContent).toContain('還沒有備份紀錄');
    expect(text('backup-last-success')).toBe('還沒有成功的備份');
  });

  it('載入更多用 cursor 接在後面', async () => {
    await make(overview({ runs: [run({ id: 5 })], nextCursor: 5 }), (c) => (c.params?.cursor === 5 ? overview({ runs: [run({ id: 4 })] }) : undefined));
    q('backup-load-more')!.click();
    await flushPromises();
    expect(q('backup-row-4')).not.toBeNull();
    expect(q('backup-row-5')).not.toBeNull();
    expect(q('backup-load-more')).toBeNull();
  });

  it('點一列開詳細並讀單筆', async () => {
    await make(overview());
    q('backup-row-1')!.click();
    await flushPromises();
    expect(calls.some((c) => c.url === '/system/ops/backups/1')).toBe(true);
    expect(q('backup-detail')!.textContent).toContain('錯誤摘要內容');
  });

  it('讀取失敗顯示載入失敗', async () => {
    await make(overview(), () => new HttpFail(500, {}));
    expect(q('backup-load-failed')).not.toBeNull();
  });
});
