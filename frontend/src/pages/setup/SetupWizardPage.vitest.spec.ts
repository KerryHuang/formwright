import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import { applyStartupOptions } from 'src/i18n/startup';
import { setupApi, SetupValidationError, type SetupCheckItem } from 'src/services/api/setup-api';
import SetupWizardPage from './SetupWizardPage.vue';

vi.mock('src/services/api/setup-api', async (orig) => ({
  ...(await orig<typeof import('src/services/api/setup-api')>()),
  setupApi: { getStatus: vi.fn(), isReady: vi.fn(), verify: vi.fn(), runChecks: vi.fn(), complete: vi.fn(), getMaintenance: vi.fn() },
}));
const api = vi.mocked(setupApi);

const green = (backup = '/var/lib/formwright/backup'): SetupCheckItem[] => [
  { key: 'dataDirectory', ok: true, path: '/var/lib/formwright/data' },
  { key: 'keysDirectory', ok: true, path: '/var/lib/formwright/data/keys' },
  { key: 'backupDirectory', ok: true, path: backup },
  { key: 'database', ok: true },
];
const checks = (items: SetupCheckItem[]) => ({ items, ok: items.every((i) => i.ok) });
const replace = vi.fn(); // window.location.replace（S1）
const CODE = 'ABCD-EFGH-JKLM';
const WRONG = 'ZZZZ-ZZZZ-ZZZZ';

async function mountPage() {
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/:p(.*)*', component: { template: '<i/>' } }] });
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(SetupWizardPage, {
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], i18n, router] },
  });
  await flushPromises();
  return w;
}
const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
async function fill(id: string, value: string) {
  const el = q(id) as HTMLInputElement;
  el.value = value;
  el.dispatchEvent(new Event('input'));
  await flushPromises();
}
async function click(id: string) {
  q(id)!.click();
  await flushPromises();
}
async function enterCode(code = CODE) {
  await fill('setup-code-input', code);
  await click('setup-code-submit');
}
const isDisabled = (id: string) => q(id)!.hasAttribute('disabled') || q(id)!.getAttribute('aria-disabled') === 'true' || q(id)!.classList.contains('disabled');

/** 從設定碼走到備份步驟之前的每一步（全綠、預設 SQLite）。 */
async function toAdmin() {
  api.verify.mockResolvedValue(true);
  api.runChecks.mockResolvedValue(checks(green()));
  await mountPage();
  await enterCode();
  await click('setup-next'); // checks → database
  await click('setup-next'); // database → admin
}

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  vi.clearAllMocks();
  vi.stubGlobal('location', { ...window.location, replace, reload: vi.fn() });
  localStorage.clear();
  document.body.innerHTML = '';
});
afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); });

describe('設定精靈', () => {
  it('wrong code stays on the code step (AC-10)', async () => {
    api.verify.mockResolvedValue(false);
    await mountPage();
    await enterCode(WRONG);
    expect(q('setup-code-invalid')!.textContent).toContain('設定碼不正確');
    expect(q('setup-step-admin')).toBeNull();
    expect(q('setup-step-checks')).toBeNull();
    expect(api.runChecks).not.toHaveBeenCalled();
    expect(localStorage.length).toBe(0);
  });

  it('red check blocks next (AC-05)', async () => {
    api.verify.mockResolvedValue(true);
    api.runChecks.mockResolvedValue(checks(green().map((i) => (i.key === 'backupDirectory' ? { ...i, ok: false, problem: 'notWritable', path: '/x/backup' } : i))));
    await mountPage();
    await enterCode();
    const text = q('setup-step-checks')!.textContent!;
    expect(text).toContain('/x/backup');
    expect(text).toContain('這個位置無法寫入');
    expect(isDisabled('setup-next')).toBe(true);
    expect(q('setup-recheck')).not.toBeNull();
  });

  it('recheck turns green and enables next', async () => {
    api.verify.mockResolvedValue(true);
    api.runChecks.mockResolvedValueOnce(checks([{ key: 'backupDirectory', ok: false, problem: 'notWritable', path: '/x' }]));
    await mountPage();
    await enterCode();
    expect(isDisabled('setup-next')).toBe(true);
    api.runChecks.mockResolvedValueOnce(checks(green()));
    await click('setup-recheck');
    expect(isDisabled('setup-next')).toBe(false);
  });

  it('default path completes (AC-03)', async () => {
    await toAdmin();
    await fill('setup-password', 'Fw-test-2026');
    await fill('setup-password-confirm', 'Fw-test-2026');
    await click('setup-next');
    await fill('setup-site-name', '青禾貿易');
    await click('setup-next');
    expect((q('setup-backup-root') as HTMLInputElement).value).toBe('/var/lib/formwright/backup');
    await click('setup-next'); // 備份位置不變也會重跑檢查
    expect(q('setup-step-done')!.textContent).toContain('雙因素驗證');
    api.complete.mockResolvedValue(undefined);
    api.getStatus.mockRejectedValueOnce({ status: 0, code: 'network' }).mockResolvedValueOnce('notInSetup');
    api.isReady.mockResolvedValue(true);
    await click('setup-complete');
    expect(api.complete).toHaveBeenCalledTimes(1);
    expect(api.complete).toHaveBeenCalledWith(CODE, { provider: 'sqlite', adminPassword: 'Fw-test-2026', siteName: '青禾貿易', backupRoot: '/var/lib/formwright/backup' });
    expect(q('setup-restarting')!.textContent).toContain('服務正在重新啟動');
    await vi.advanceTimersByTimeAsync(2000); // 第一次 reject：重啟中
    expect(q('setup-restarting')!.textContent).toContain('服務正在重新啟動');
    expect(q('setup-error')).toBeNull();
    expect(replace).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(2000);
    expect(replace).toHaveBeenCalledWith('/login');
  });

  it('existing data shows recovery and blocks next (AC-13)', async () => {
    api.verify.mockResolvedValue(true);
    api.runChecks.mockResolvedValue(checks(green().map((i) => (i.key === 'database'
      ? { ...i, ok: false, problem: 'existingData', params: { configFile: '/etc/formwright/formwright.json' } } : i))));
    await mountPage();
    await enterCode();
    const text = q('setup-step-checks')!.textContent!;
    expect(text).toContain('找回原本的設定檔');
    expect(text).toContain('/etc/formwright/formwright.json');
    expect(text).toContain('聯絡支援');
    expect(isDisabled('setup-next')).toBe(true);
    expect(q('setup-step-database')).toBeNull();
  });

  it('existingSqliteData (postgres 時資料目錄有舊資料) 顯示同樣的恢復說明', async () => {
    api.verify.mockResolvedValue(true);
    api.runChecks.mockResolvedValue(checks(green()));
    await mountPage();
    await enterCode();
    await click('setup-next');
    await click('setup-advanced');
    await click('setup-provider-postgres');
    await fill('setup-connection', 'Host=db;Database=fw');
    api.runChecks.mockResolvedValue(checks([...green(), { key: 'existingSqliteData', ok: false, problem: 'existingData', params: { configFile: '/etc/f.json' } }]));
    await click('setup-next');
    expect(q('setup-step-database')!.textContent).toContain('找回原本的設定檔');
    expect(q('setup-step-admin')).toBeNull();
  });

  it('postgres is behind advanced', async () => {
    api.verify.mockResolvedValue(true);
    api.runChecks.mockResolvedValue(checks(green()));
    await mountPage();
    await enterCode();
    await click('setup-next');
    expect(q('setup-connection')).toBeNull();
    expect(q('setup-provider-postgres')).toBeNull();
    await click('setup-advanced');
    await click('setup-provider-postgres');
    expect(q('setup-connection')).not.toBeNull();
    await fill('setup-connection', 'Host=db');
    api.runChecks.mockResolvedValue(checks([{ key: 'database', ok: false, problem: 'connectFailed', params: { detail: 'NpgsqlException' } }]));
    await click('setup-next');
    expect(api.runChecks).toHaveBeenLastCalledWith(CODE, { provider: 'postgres', connectionString: 'Host=db' });
    expect(q('setup-step-database')!.textContent).toContain('連不上資料庫');
    expect(q('setup-step-admin')).toBeNull();
  });

  it('no error-report step (D-12)', async () => {
    await toAdmin();
    const ids = ['setup-step-checks', 'setup-step-database', 'setup-step-admin', 'setup-step-company', 'setup-step-backup', 'setup-step-done'];
    const headers = document.body.querySelectorAll('.q-stepper__tab');
    expect(headers.length).toBe(7); // 設定碼＋六步
    expect(ids.length + 1).toBe(headers.length);
    expect(q('setup-code')).toBeNull(); // 已離開設定碼步驟
  });

  it('admin password mismatch is front-end only and does not advance', async () => {
    await toAdmin();
    await fill('setup-password', 'Fw-test-2026');
    await fill('setup-password-confirm', 'other');
    await click('setup-next');
    expect(q('setup-admin-mismatch')!.textContent).toContain('不一致');
    expect(q('setup-step-company')).toBeNull();
  });

  it('422 checksFailed 的備份紅燈退回備份步驟（S2）', async () => {
    await toAdmin();
    await fill('setup-password', 'Fw-test-2026');
    await fill('setup-password-confirm', 'Fw-test-2026');
    await click('setup-next');
    await fill('setup-site-name', 'A');
    await click('setup-next');
    await click('setup-next');
    api.complete.mockRejectedValue(new SetupValidationError(undefined, [{ key: 'backupDirectory', ok: false, problem: 'notWritable', path: '/gone' }]));
    await click('setup-complete');
    // 備份目錄紅燈 → 退回備份步驟（S2），不是環境檢查
    expect(q('setup-step-backup')!.textContent).toContain('/gone');
    expect(q('setup-step-done')).toBeNull();
  });

  it('422 invalid password returns to admin step with min', async () => {
    await toAdmin();
    await fill('setup-password', 'short');
    await fill('setup-password-confirm', 'short');
    await click('setup-next');
    await fill('setup-site-name', 'A');
    await click('setup-next');
    await click('setup-next');
    api.complete.mockRejectedValue(new SetupValidationError({ adminPassword: [{ code: 'passwordTooShort', params: { min: 8 } }] }));
    await click('setup-complete');
    expect(q('setup-step-admin')!.textContent).toContain('至少要 8 個字元');
  });

  it('timeout shows reload', async () => {
    await toAdmin();
    await fill('setup-password', 'Fw-test-2026');
    await fill('setup-password-confirm', 'Fw-test-2026');
    await click('setup-next');
    await fill('setup-site-name', 'A');
    await click('setup-next');
    await click('setup-next');
    api.complete.mockResolvedValue(undefined);
    api.getStatus.mockResolvedValue('pending');
    await click('setup-complete');
    await vi.advanceTimersByTimeAsync(120_000);
    expect(q('setup-timeout')!.textContent).toContain('重新整理');
    expect(q('setup-reload')).not.toBeNull();
  });

  it('locale switcher present (D-20)', async () => {
    api.verify.mockResolvedValue(true);
    applyStartupOptions(null);
    await mountPage();
    expect(q('locale-open')).not.toBeNull();
    await click('locale-open');
    for (const c of ['zh-TW', 'en-US', 'th', 'vi']) expect(q(`locale-option-${c}`)).not.toBeNull();
  });

  it('input accepts letters: no numeric inputmode (M1)', async () => {
    await mountPage();
    const input = q('setup-code-input') as HTMLInputElement;
    expect(input.getAttribute('inputmode')).not.toBe('numeric');
    expect(input.getAttribute('autocapitalize')).toBe('characters');
    expect(input.getAttribute('spellcheck')).toBe('false');
    expect(q('setup-code')!.textContent).toContain('12 碼英數字');
  });

  it('S2 postgres 的 database 紅燈退回資料庫步驟、其餘紅燈退回環境檢查', async () => {
    await toAdmin();
    await click('setup-back'); // admin → database
    await click('setup-advanced');
    await click('setup-provider-postgres');
    await fill('setup-connection', 'Host=db');
    api.runChecks.mockResolvedValue(checks(green()));
    await click('setup-next');
    await fill('setup-password', 'Fw-test-2026');
    await fill('setup-password-confirm', 'Fw-test-2026');
    await click('setup-next');
    await fill('setup-site-name', 'A');
    await click('setup-next');
    await click('setup-next');
    api.complete.mockRejectedValue(new SetupValidationError(undefined, [{ key: 'pgDump', ok: false, problem: 'pgDumpMissing' }]));
    await click('setup-complete');
    expect(q('setup-step-database')!.textContent).toContain('找不到 pg_dump');
    expect(q('setup-step-done')).toBeNull();
  });

  it('S2 資料目錄紅燈退回環境檢查', async () => {
    await toAdmin();
    await fill('setup-password', 'Fw-test-2026');
    await fill('setup-password-confirm', 'Fw-test-2026');
    await click('setup-next');
    await fill('setup-site-name', 'A');
    await click('setup-next');
    await click('setup-next');
    api.complete.mockRejectedValue(new SetupValidationError(undefined, [{ key: 'dataDirectory', ok: false, problem: 'notWritable', path: '/d' }]));
    await click('setup-complete');
    expect(q('setup-step-checks')!.textContent).toContain('/d');
  });

  it('S3 任何請求收到 403 setupCodeInvalid 都回設定碼步驟', async () => {
    api.verify.mockResolvedValue(true);
    api.runChecks.mockResolvedValueOnce(checks(green()));
    await mountPage();
    await enterCode();
    api.runChecks.mockRejectedValueOnce({ status: 403, code: 'setupCodeInvalid' });
    await click('setup-recheck');
    expect(q('setup-code-invalid')!.textContent).toContain('設定碼不正確');
    expect(q('setup-step-checks')).toBeNull();
    expect(q('setup-error')).toBeNull();
  });

  it('complete 收到 403 也回設定碼步驟（另一個分頁已完成）', async () => {
    await toAdmin();
    await fill('setup-password', 'Fw-test-2026');
    await fill('setup-password-confirm', 'Fw-test-2026');
    await click('setup-next');
    await fill('setup-site-name', 'A');
    await click('setup-next');
    await click('setup-next');
    api.complete.mockRejectedValue({ status: 403, code: 'setupCodeInvalid' });
    await click('setup-complete');
    expect(q('setup-code-invalid')).not.toBeNull();
  });

  it('其他錯誤顯示 setup-error', async () => {
    api.verify.mockResolvedValue(true);
    api.runChecks.mockResolvedValueOnce(checks(green()));
    await mountPage();
    await enterCode();
    api.runChecks.mockRejectedValueOnce({ status: 0, code: 'network' });
    await click('setup-recheck');
    expect(q('setup-error')!.textContent).toContain('連不上安裝服務');
    expect(q('setup-code-invalid')).toBeNull();
  });

  it('空白欄位時下一步 disabled', async () => {
    await toAdmin();
    expect(isDisabled('setup-next')).toBe(true); // 密碼空白
    await fill('setup-password', 'Fw-test-2026');
    await fill('setup-password-confirm', 'Fw-test-2026');
    expect(isDisabled('setup-next')).toBe(false);
    await click('setup-next');
    expect(isDisabled('setup-next')).toBe(true); // 公司名稱空白
    await fill('setup-site-name', '   ');
    expect(isDisabled('setup-next')).toBe(true);
    await fill('setup-site-name', 'A');
    expect(isDisabled('setup-next')).toBe(false);
  });

  it('PostgreSQL 連線字串空白時下一步 disabled；測試連線失敗後下一步仍會重跑檢查並被擋（9b）', async () => {
    api.verify.mockResolvedValue(true);
    api.runChecks.mockResolvedValue(checks(green()));
    await mountPage();
    await enterCode();
    await click('setup-next');
    await click('setup-advanced');
    await click('setup-provider-postgres');
    expect(isDisabled('setup-next')).toBe(true);
    await fill('setup-connection', 'Host=db');
    expect(isDisabled('setup-next')).toBe(false);
    api.runChecks.mockResolvedValue(checks([{ key: 'database', ok: false, problem: 'connectFailed', params: { detail: 'X' } }]));
    await click('setup-db-test');
    expect(q('setup-step-database')!.textContent).toContain('連不上資料庫');
    api.runChecks.mockClear();
    await click('setup-next');
    expect(api.runChecks).toHaveBeenCalledTimes(1);
    expect(q('setup-step-admin')).toBeNull();
  });

  it('狀態圖示對輔助技術隱藏、連線字串眼睛鈕有名稱（可忽略項）', async () => {
    api.verify.mockResolvedValue(true);
    api.runChecks.mockResolvedValue(checks(green()));
    await mountPage();
    await enterCode();
    expect(document.body.querySelector('[data-testid="setup-check-dataDirectory"] .q-icon')!.getAttribute('aria-hidden')).toBe('true');
    await click('setup-next');
    await click('setup-advanced');
    await click('setup-provider-postgres');
    const eye = q('setup-connection-toggle')!;
    expect(eye.tagName).toBe('BUTTON');
    expect(eye.getAttribute('aria-label')).toBe('顯示連線字串');
  });
});
