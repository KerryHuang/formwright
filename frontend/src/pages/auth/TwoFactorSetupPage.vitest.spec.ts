import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { useIdentityStore } from 'src/stores/use-identity-store';
import TwoFactorSetupPage from './TwoFactorSetupPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: {
  startTwoFactorSetup: vi.fn(), enableTwoFactor: vi.fn(), disableTwoFactor: vi.fn(), regenerateRecoveryCodes: vi.fn(), logout: vi.fn(), getAccountSecurity: vi.fn(),
} }));
const api = vi.mocked(dynamicApi);
let mounted: ReturnType<typeof mount> | undefined;

async function mountPage() {
  setActivePinia(createPinia());
  useIdentityStore().setSession('admin');
  useIdentityStore().setEnrollmentRequired(true);
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/account/two-factor-setup', component: TwoFactorSetupPage }, { path: '/:p(.*)*', component: { template: '<i/>' } }],
  });
  await router.push('/account/two-factor-setup');
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  mounted = mount({ template: '<router-view />' }, {
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], i18n, router, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
  });
  await flushPromises();
  return router;
}
const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
const click = async (id: string) => { q(id)!.click(); await flushPromises(); };
async function fill(id: string, value: string) {
  const el = (q(id)!.querySelector('input') ?? q(id)) as HTMLInputElement;
  el.value = value;
  el.dispatchEvent(new Event('input'));
  await flushPromises();
}

describe('強制綁定頁（帳號安全設計 §8.7、§12）', () => {
  beforeEach(() => { vi.clearAllMocks(); vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} }); });
  afterEach(() => { mounted?.unmount(); document.body.innerHTML = ''; });

  it('標題是「系統管理員要求啟用雙因素驗證」，沒有帳號安全頁的 API、沒有關閉；綁定完成後解除並回首頁', async () => {
    const router = await mountPage();
    expect(q('tf-setup-title')!.textContent).toBe('系統管理員要求啟用雙因素驗證');
    expect(api.getAccountSecurity).not.toHaveBeenCalled();
    expect(q('security-2fa-disable')).toBeNull();
    api.startTwoFactorSetup.mockResolvedValue({ secret: 'JBSWY3DPEHPK3PXP', otpAuthUri: 'otpauth://totp/x?secret=JBSWY3DPEHPK3PXP', pendingMinutes: 10 });
    api.enableTwoFactor.mockResolvedValue({ recoveryCodes: ['AAAA-BBBB-CCCC-DDDD'] });
    await click('security-2fa-enable');
    await fill('security-2fa-password', 'pw-1');
    await click('security-2fa-submit');
    await fill('security-2fa-confirm-code', '123456');
    await click('security-2fa-finish');
    expect(router.currentRoute.value.path).toBe('/account/two-factor-setup');
    await click('security-2fa-codes-done');
    expect(useIdentityStore().twoFactorEnrollmentRequired).toBe(false);
    expect(router.currentRoute.value.path).toBe('/');
  });

  it('只有登出能離開', async () => {
    const router = await mountPage();
    api.logout.mockResolvedValue(undefined as never);
    await click('tf-setup-logout');
    expect(api.logout).toHaveBeenCalled();
    expect(router.currentRoute.value.path).toBe('/login');
  });
});
