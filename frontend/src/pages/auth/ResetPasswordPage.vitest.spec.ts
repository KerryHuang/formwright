import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import ResetPasswordPage from './ResetPasswordPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { checkResetLink: vi.fn(), resetPasswordByLink: vi.fn(), getAuthOptions: vi.fn() } }));
const api = vi.mocked(dynamicApi);
let mounted: ReturnType<typeof mount> | undefined;
let queryClient: QueryClient;
let router: ReturnType<typeof createRouter>;

async function mountAt(hash: string, min = 8) {
  api.getAuthOptions.mockResolvedValue({ passwordReset: 'available', passwordMinLength: min } as never);
  window.history.replaceState(null, '', `/reset-password${hash}`);
  setActivePinia(createPinia());
  router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/:p(.*)*', component: { template: '<i/>' } }] });
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  queryClient = new QueryClient();
  mounted = mount(ResetPasswordPage, {
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], i18n, router, [VueQueryPlugin, { queryClient }]] },
  });
  await flushPromises();
}
const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
async function fill(id: string, value: string) {
  const el = q(id) as HTMLInputElement;
  el.value = value;
  el.dispatchEvent(new Event('input'));
  await flushPromises();
}
async function submit(pw: string, confirm = pw) {
  await fill('reset-new', pw);
  await fill('reset-confirm', confirm);
  q('reset-submit')!.click();
  await flushPromises();
}

describe('ResetPasswordPage', () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(() => { mounted?.unmount(); document.body.innerHTML = ''; });

  it('reads the token after #, clears it from the address bar, checks it, and keeps it out of vue-query caches', async () => {
    api.checkResetLink.mockResolvedValue(undefined);
    api.resetPasswordByLink.mockResolvedValue(undefined);
    await mountAt('#t=SecretTok42');
    expect(api.checkResetLink).toHaveBeenCalledWith('SecretTok42');
    expect(window.location.hash).toBe('');
    expect(window.location.pathname).toBe('/reset-password');
    await submit('brand-new-pass');
    expect(api.resetPasswordByLink).toHaveBeenCalledWith('SecretTok42', 'brand-new-pass');
    const cached = JSON.stringify([queryClient.getMutationCache().getAll().map((m) => m.state), queryClient.getQueryCache().getAll().map((x) => x.state)]);
    expect(cached).not.toContain('SecretTok42');
    expect(cached).not.toContain('brand-new-pass');
    expect(router.currentRoute.value.path).toBe('/login');
  });

  it('expired or used link shows the invalid message and a link to request again (R5, R7)', async () => {
    api.checkResetLink.mockRejectedValue({ status: 400, code: 'resetLinkInvalid' });
    await mountAt('#t=used');
    expect(q('reset-invalid')!.textContent).toBe(zhTW.auth.resetInvalid);
    expect(q('reset-again')).not.toBeNull();
    mounted?.unmount();
    await mountAt('');
    expect(api.checkResetLink).toHaveBeenCalledTimes(1);
    expect(q('reset-invalid')).not.toBeNull();
  });

  it('the minimum length comes from auth/options; mismatch and short passwords are not sent (R10)', async () => {
    api.checkResetLink.mockResolvedValue(undefined);
    await mountAt('#t=tok', 12);
    expect(document.body.textContent).toContain(zhTW.auth.newPassword.replace('{min}', '12'));
    await submit('eleven-char');
    expect(q('reset-error')!.textContent).toBe(zhTW.auth.passwordTooShort.replace('{min}', '12'));
    await submit('twelve-chars', 'twelve-charX');
    expect(q('reset-error')!.textContent).toBe(zhTW.auth.passwordMismatch);
    expect(api.resetPasswordByLink).not.toHaveBeenCalled();
  });

  it('a link used up in the meantime switches to the invalid message on submit', async () => {
    api.checkResetLink.mockResolvedValue(undefined);
    api.resetPasswordByLink.mockRejectedValue({ status: 400, code: 'resetLinkInvalid' });
    await mountAt('#t=tok');
    await submit('brand-new-pass');
    expect(q('reset-invalid')).not.toBeNull();
  });
});
