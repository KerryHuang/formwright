import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import type { AccountSecurity } from 'src/domain/dynamic/types';
import { dynamicApi } from 'src/services/api/dynamic-api';
import AccountSecurityPage from './AccountSecurityPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { getAccountSecurity: vi.fn(), setMyEmail: vi.fn(), resendMyEmail: vi.fn() } }));
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
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], i18n, router, [VueQueryPlugin, { queryClient }]] },
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
});
