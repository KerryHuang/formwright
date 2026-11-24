import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import type { MailOutboxItem, MailOutboxPage } from 'src/domain/dynamic/types';
import { dynamicApi } from 'src/services/api/dynamic-api';
import MailOutboxSection from './MailOutboxSection.vue';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { getMailOutbox: vi.fn() } }));
const api = vi.mocked(dynamicApi);
let mounted: ReturnType<typeof mount> | undefined;

const item = (id: number, extra: Partial<MailOutboxItem> = {}): MailOutboxItem => ({
  id, createdUtc: '2026-11-24T01:00:00Z', kind: 'emailVerify', userId: 'floor02', toAddress: 'floor02b@example.com', status: 'sent', attempts: 1, ...extra,
});

async function mountAt(page: Partial<MailOutboxPage>, width = 1366) {
  window.innerWidth = width;
  api.getMailOutbox.mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 50, publicUrlMissing: false, demoInbox: false, ...page });
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  mounted = mount(MailOutboxSection, {
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
  });
  await flushPromises();
  window.dispatchEvent(new Event('resize'));
  await new Promise((r) => setTimeout(r, 150));
  await flushPromises();
}
const q = (id: string) => document.body.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

describe('MailOutboxSection', () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(() => { mounted?.unmount(); document.body.innerHTML = ''; });

  it('labels kind, status and skip reason; warns when the site URL is missing; no view button outside the demo inbox', async () => {
    await mountAt({ items: [item(1, { status: 'skipped', lastError: 'noPublicUrl' })], total: 1, publicUrlMissing: true });
    const text = q('mail-outbox')!.textContent!;
    expect(text).toContain(zhTW.mailOutbox.kinds.emailVerify);
    expect(text).toContain(zhTW.mailOutbox.statuses.skipped);
    expect(text).toContain(zhTW.mailOutbox.reasons.noPublicUrl);
    expect(q('mail-outbox-no-url')).not.toBeNull();
    expect(q('mail-outbox-view-1')).toBeNull();
  });

  it('hides the reason column when no row on the page has a reason (au1a walkthrough G3)', async () => {
    const header = () => [...document.body.querySelectorAll('thead th')].map((th) => th.textContent!.trim());
    await mountAt({ items: [item(1), item(2)], total: 2 });
    expect(header()).not.toContain(zhTW.mailOutbox.reason);
    mounted!.unmount();
    await mountAt({ items: [item(1), item(2, { status: 'skipped', lastError: 'inactive' })], total: 2 });
    expect(header()).toContain(zhTW.mailOutbox.reason);
  });

  it('demo inbox at 390px: cards, and View opens the whole message with a clickable relative link', async () => {
    await mountAt({ items: [item(7, { previewBody: '驗證你的 Email\n請點下面的連結\n/verify-email#t=Abc' })], total: 1, demoInbox: true }, 390);
    expect(q('mail-outbox-card-7')).not.toBeNull();
    q('mail-outbox-view-7')!.click();
    await flushPromises();
    const body = q('mail-outbox-body')!;
    expect(body.textContent).toContain('請點下面的連結');
    expect(body.querySelector('a')!.getAttribute('href')).toBe('/verify-email#t=Abc');
  });

  it('pages through 50 per page', async () => {
    await mountAt({ items: [item(1)], total: 120 });
    expect(q('mail-outbox-pages')).not.toBeNull();
    expect(api.getMailOutbox).toHaveBeenCalledWith(1);
  });
});
