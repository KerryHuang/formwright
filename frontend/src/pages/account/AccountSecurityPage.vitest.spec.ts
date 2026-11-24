import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import type { AccountSecurity, TwoFactorStatus } from 'src/domain/dynamic/types';
import { dynamicApi } from 'src/services/api/dynamic-api';
import AccountSecurityPage from './AccountSecurityPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: {
  getAccountSecurity: vi.fn(), setMyEmail: vi.fn(), resendMyEmail: vi.fn(),
  startTwoFactorSetup: vi.fn(), enableTwoFactor: vi.fn(), disableTwoFactor: vi.fn(), regenerateRecoveryCodes: vi.fn(), clearTrustedDevices: vi.fn(),
} }));
const api = vi.mocked(dynamicApi);
let mounted: ReturnType<typeof mount> | undefined;
let queryClient: QueryClient;

const base: AccountSecurity = { userId: 'sales01', email: 'a@example.com', emailVerified: false, mailAvailable: true, canChangeEmail: true };

async function mountPage(data: Partial<AccountSecurity> = {}) {
  api.getAccountSecurity.mockResolvedValue({ ...base, ...data });
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/:p(.*)*', component: { template: '<i/>' } }] });
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  queryClient = new QueryClient();
  const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(AccountSecurityPage))) };
  mounted = mount(Host, {
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify, Dialog: quasar.Dialog } }], i18n, router, [VueQueryPlugin, { queryClient }]] },
  });
  await flushPromises();
}
const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
async function fill(id: string, value: string) {
  const el = q(id)!.querySelector('input') ?? (q(id) as HTMLInputElement);
  (el as HTMLInputElement).value = value;
  el.dispatchEvent(new Event('input'));
  await flushPromises();
}

describe('AccountSecurityPage', () => {
  beforeEach(() => { vi.clearAllMocks(); vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} }); });
  afterEach(() => { mounted?.unmount(); document.body.innerHTML = ''; });

  it('shows the email with its unverified state, resend, and the password card linking to change-password', async () => {
    await mountPage();
    expect(q('security-email')?.textContent).toBe('a@example.com');
    expect(q('security-email-status')?.textContent).toContain(zhTW.accountSecurity.unverified);
    expect(q('security-resend')).not.toBeNull();
    expect(q('security-password-link')?.getAttribute('href')).toBe('/change-password');
  });

  it('no resend when mail cannot be sent; says so instead', async () => {
    await mountPage({ mailAvailable: false });
    expect(q('security-resend')).toBeNull();
    expect(q('security-email-card')?.textContent).toContain(zhTW.accountSecurity.mailUnavailable);
  });

  it('change email sends the current password and keeps it out of the shared cache', async () => {
    api.setMyEmail.mockResolvedValue({ email: 'b@example.com', emailVerified: false, mailAvailable: true });
    await mountPage();
    q('security-email-edit')!.click();
    await flushPromises();
    await fill('security-email-input', ' b@example.com ');
    await fill('security-password-input', 'my-secret-pw');
    q('security-email-save')!.click();
    await flushPromises();
    expect(api.setMyEmail).toHaveBeenCalledWith({ email: 'b@example.com', password: 'my-secret-pw' });
    expect(JSON.stringify(queryClient.getMutationCache().getAll().map((m) => m.state))).not.toContain('my-secret-pw');
  });

  it('accounts without a password cannot change the email here', async () => {
    await mountPage({ canChangeEmail: false, email: null });
    expect(q('security-email-edit')).toBeNull();
    expect(q('security-email-none')).not.toBeNull();
  });

  describe('two-factor card (account security design §8.2)', () => {
    const off: TwoFactorStatus = { enabled: false, recoveryCodesLeft: 0, unreadable: false, blocked: null, remindAtOrBelow: 2, hasPassword: true };
    const on: TwoFactorStatus = { ...off, enabled: true, recoveryCodesLeft: 10 };
    const click = async (id: string) => { q(id)!.click(); await flushPromises(); };

    it('enable: password → QR and key → code → recovery codes shown once; secrets cleared after closing', async () => {
      await mountPage({ twoFactor: off });
      expect(q('security-2fa-status')!.textContent).toContain(zhTW.twoFactor.statusOff);
      api.startTwoFactorSetup.mockResolvedValue({ secret: 'JBSWY3DPEHPK3PXP', otpAuthUri: 'otpauth://totp/Formwright:sales01?secret=JBSWY3DPEHPK3PXP', pendingMinutes: 10 });
      api.enableTwoFactor.mockResolvedValue({ recoveryCodes: ['AAAA-BBBB-CCCC-DDDD', 'EEEE-FFFF-GGGG-HHHH'] });
      await click('security-2fa-enable');
      expect(q('security-2fa-code')).toBeNull();
      await fill('security-2fa-password', 'pw-1');
      await click('security-2fa-submit');
      expect(api.startTwoFactorSetup).toHaveBeenCalledWith({ password: 'pw-1' });
      expect(q('security-2fa-qr')!.getAttribute('src')).toMatch(/^data:image\/svg\+xml/);
      expect(q('security-2fa-secret')!.textContent).toBe('JBSWY3DPEHPK3PXP');
      await fill('security-2fa-confirm-code', '123456');
      await click('security-2fa-finish');
      expect(api.enableTwoFactor).toHaveBeenCalledWith('123456');
      expect(q('security-2fa-codes')!.textContent).toContain('AAAA-BBBB-CCCC-DDDD');
      expect(JSON.stringify(queryClient.getMutationCache().getAll().map((m) => m.state))).not.toContain('pw-1');
      await click('security-2fa-codes-done');
      expect(q('security-2fa-codes')).toBeNull();
      // 對話框關掉後金鑰與復原碼也不留在 mutation 狀態（審查 S3：頁面還開著，gcTime: 0 不會回收）；reset 後由 gcTime: 0 在下一輪事件迴圈移除
      await new Promise((r) => setTimeout(r, 0));
      const states = JSON.stringify(queryClient.getMutationCache().getAll().map((m) => m.state));
      expect(states).not.toContain('JBSWY3DPEHPK3PXP');
      expect(states).not.toContain('AAAA-BBBB-CCCC-DDDD');
    });

    it('disable can use a recovery code; regenerate needs the authenticator code', async () => {
      await mountPage({ twoFactor: on });
      expect(q('security-2fa-left')!.textContent).toContain('10');
      api.disableTwoFactor.mockResolvedValue(undefined);
      await click('security-2fa-disable');
      await fill('security-2fa-password', 'pw-1');
      await click('security-2fa-toggle');
      await fill('security-2fa-recovery', ' AAAA-BBBB-CCCC-DDDD ');
      await click('security-2fa-submit');
      expect(api.disableTwoFactor).toHaveBeenCalledWith({ password: 'pw-1', recoveryCode: 'AAAA-BBBB-CCCC-DDDD' });

      api.regenerateRecoveryCodes.mockResolvedValue({ recoveryCodes: ['ZZZZ-YYYY-XXXX-WWWW'] });
      await click('security-2fa-regenerate');
      expect(q('security-2fa-toggle')).toBeNull();
      await fill('security-2fa-password', 'pw-1');
      await fill('security-2fa-code', '654321');
      await click('security-2fa-submit');
      expect(api.regenerateRecoveryCodes).toHaveBeenCalledWith({ password: 'pw-1', code: '654321' });
      expect(q('security-2fa-codes')!.textContent).toContain('ZZZZ-YYYY-XXXX-WWWW');
      await click('security-2fa-codes-done');
      await new Promise((r) => setTimeout(r, 0));
      expect(JSON.stringify(queryClient.getMutationCache().getAll().map((m) => m.state))).not.toContain('ZZZZ-YYYY-XXXX-WWWW');
    });

    it('low recovery codes warn; unreadable key defaults rebind to a recovery code', async () => {
      await mountPage({ twoFactor: { ...on, recoveryCodesLeft: 2, unreadable: true } });
      expect(q('security-2fa-left')!.textContent).toContain(zhTW.twoFactor.recoveryLow);
      expect(q('security-2fa-unreadable')).not.toBeNull();
      expect(q('security-2fa-regenerate')).toBeNull();
      await click('security-2fa-rebind');
      expect(q('security-2fa-recovery')).not.toBeNull();
    });

    it('keys not persisted or demo site: explains why and offers no enable button', async () => {
      await mountPage({ twoFactor: { ...off, blocked: 'keysNotPersisted' } });
      expect(q('security-2fa-keys')!.textContent).toContain(zhTW.twoFactor.keysNotPersisted);
      expect(q('security-2fa-enable')).toBeNull();
      mounted?.unmount();
      document.body.innerHTML = '';
      await mountPage({ twoFactor: { ...off, blocked: 'demoSite' } });
      expect(q('security-2fa-demo')).not.toBeNull();
      expect(q('security-2fa-enable')).toBeNull();
    });
  });

  describe('記住的裝置與強制（帳號安全設計 §8.5、§8.7）', () => {
    const on: TwoFactorStatus = { enabled: true, recoveryCodesLeft: 10, unreadable: false, blocked: null, remindAtOrBelow: 2, hasPassword: true };
    const devices = [{ label: 'Chrome 131／macOS', createdUtc: '2026-11-20T01:00:00Z', lastUsedUtc: '2026-11-24T02:30:00Z' }];

    it('在強制範圍內不給關閉，只能重新綁定', async () => {
      await mountPage({ twoFactor: { ...on, required: true } });
      expect(q('security-2fa-disable')).toBeNull();
      expect(q('security-2fa-rebind')).not.toBeNull();
      expect(q('security-2fa-required')).not.toBeNull();
    });

    it('列出記住的裝置（營運時區的最近使用時間），確認後全部取消記住', async () => {
      api.clearTrustedDevices.mockResolvedValue(undefined);
      await mountPage({ twoFactor: on, trustedDevices: devices });
      expect(q('security-devices-card')!.textContent).toContain('記住的裝置：1 台');
      expect(q('security-devices')!.textContent).toContain('Chrome 131／macOS');
      expect(q('security-devices')!.textContent).toContain('2026-11-24 10:30');
      q('security-devices-clear')!.click();
      await flushPromises();
      const ok = [...document.body.querySelectorAll('.q-dialog button')].find((b) => b.textContent?.includes(zhTW.trustedDevices.clearAll)) as HTMLElement;
      ok.click();
      await flushPromises();
      expect(api.clearTrustedDevices).toHaveBeenCalledTimes(1);
    });

    it('Demo 站：按鈕停用並寫「Demo 站不能變更」；沒啟用雙因素時不顯示這張卡', async () => {
      await mountPage({ twoFactor: { ...on, blocked: 'demoSite' }, trustedDevices: devices, trustedDevicesBlocked: 'demoSite' });
      expect(q('security-devices-demo')!.textContent).toContain(zhTW.trustedDevices.demoSite);
      expect(q('security-devices-clear')!.hasAttribute('disabled') || q('security-devices-clear')!.classList.contains('disabled')).toBe(true);
      mounted?.unmount();
      document.body.innerHTML = '';
      await mountPage({ twoFactor: { ...on, enabled: false }, trustedDevices: [] });
      expect(q('security-devices-card')).toBeNull();
    });
  });
});
