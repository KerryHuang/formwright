import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import VerifyEmailPage from './VerifyEmailPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { verifyEmail: vi.fn() } }));
const api = vi.mocked(dynamicApi);
let mounted: ReturnType<typeof mount> | undefined;
let queryClient: QueryClient;

async function mountAt(hash: string) {
  window.history.replaceState(null, '', `/verify-email${hash}`);
  setActivePinia(createPinia());
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/:p(.*)*', component: { template: '<i/>' } }] });
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  queryClient = new QueryClient();
  mounted = mount(VerifyEmailPage, {
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], i18n, router, [VueQueryPlugin, { queryClient }]] },
  });
  await flushPromises();
}
const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`);

describe('VerifyEmailPage', () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(() => mounted?.unmount());

  it('reads the token after #, clears it from the address bar, and shows success', async () => {
    api.verifyEmail.mockResolvedValue(undefined);
    await mountAt('#t=AbC123');
    expect(api.verifyEmail).toHaveBeenCalledWith('AbC123');
    expect(window.location.hash).toBe('');
    expect(window.location.pathname).toBe('/verify-email');
    expect(q('verify-ok')?.textContent).toBe(zhTW.accountSecurity.verifyOk);
  });

  it('the token never lands in the vue-query caches (au1a review)', async () => {
    api.verifyEmail.mockResolvedValue(undefined);
    await mountAt('#t=SecretTok42');
    expect(api.verifyEmail).toHaveBeenCalledWith('SecretTok42');
    const cached = JSON.stringify([queryClient.getMutationCache().getAll().map((m) => m.state), queryClient.getQueryCache().getAll().map((x) => x.state)]);
    expect(cached).not.toContain('SecretTok42');
  });

  it('shows the expired message on 400 and when there is no token', async () => {
    api.verifyEmail.mockRejectedValue({ status: 400, code: 'verifyLinkInvalid' });
    await mountAt('#t=used');
    expect(q('verify-invalid')).not.toBeNull();
    mounted?.unmount();
    await mountAt('');
    expect(api.verifyEmail).toHaveBeenCalledTimes(1);
    expect(q('verify-invalid')).not.toBeNull();
  });

  it('a server outage is not reported as an expired link', async () => {
    api.verifyEmail.mockRejectedValue({ status: 503 });
    await mountAt('#t=x');
    expect(q('verify-failed')).not.toBeNull();
  });
});
