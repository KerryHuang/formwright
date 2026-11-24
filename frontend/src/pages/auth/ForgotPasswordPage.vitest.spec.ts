import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import ForgotPasswordPage from './ForgotPasswordPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { forgotPassword: vi.fn(), getAuthOptions: vi.fn() } }));
const api = vi.mocked(dynamicApi);
let mounted: ReturnType<typeof mount> | undefined;

async function mountPage(options: Record<string, unknown>) {
  api.getAuthOptions.mockResolvedValue({ demoLoginEnabled: false, passwordReset: 'available', passwordMinLength: 8, ...options } as never);
  setActivePinia(createPinia());
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/:p(.*)*', component: { template: '<i/>' } }] });
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  mounted = mount(ForgotPasswordPage, {
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], i18n, router, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
  });
  await flushPromises();
}
const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
async function request(userId: string) {
  const el = q('forgot-user') as HTMLInputElement;
  el.value = userId;
  el.dispatchEvent(new Event('input'));
  await flushPromises();
  q('forgot-submit')!.click();
  await flushPromises();
}

describe('ForgotPasswordPage', () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(() => { mounted?.unmount(); document.body.innerHTML = ''; });

  it('shows the same message with the link minutes whatever the account (R1)', async () => {
    api.forgotPassword.mockResolvedValue({ linkMinutes: 45 });
    await mountPage({});
    expect(q('forgot-demo-hint')).toBeNull();
    await request('nobody');
    expect(api.forgotPassword).toHaveBeenCalledWith('nobody');
    expect(q('forgot-sent')!.textContent).toContain(zhTW.auth.forgotSent.replace('{minutes}', '45'));
    expect(q('forgot-user')).toBeNull();
  });

  it('when mail is not available, only tells the user to contact an administrator (R14)', async () => {
    await mountPage({ passwordReset: 'contactAdmin' });
    expect(q('forgot-contact-admin')!.textContent).toBe(zhTW.auth.forgotContactAdmin);
    expect(q('forgot-user')).toBeNull();
  });

  it('demo site shows that only mfa01 can demonstrate a reset (R22)', async () => {
    await mountPage({ demoLoginEnabled: true });
    expect(q('forgot-demo-hint')!.textContent).toContain(zhTW.auth.forgotDemoHint);
  });

  it('a network failure keeps the form and shows a retry message', async () => {
    api.forgotPassword.mockRejectedValue({ status: 0 });
    await mountPage({});
    await request('sales01');
    expect(q('forgot-error')!.textContent).toBe(zhTW.auth.forgotFailed);
    expect(q('forgot-user')).not.toBeNull();
  });
});
