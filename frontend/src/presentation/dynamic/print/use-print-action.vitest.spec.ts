import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { defineComponent } from 'vue';
import { createI18n } from 'vue-i18n';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { usePrintAction } from './use-print-action';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { getPrintTemplates: vi.fn(), printRecord: vi.fn() } }));
const api = vi.mocked(dynamicApi);

async function mountHost() {
  let result!: ReturnType<typeof usePrintAction>;
  const Host = defineComponent({ setup() { result = usePrintAction(() => 'quote', () => 5); return () => null; } });
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  mount(Host, {
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
  });
  await flushPromises();
  return result;
}

describe('usePrintAction', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    api.getPrintTemplates.mockResolvedValue([{ key: 'sheet', name: { 'zh-TW': '報價單' } }]);
  });

  it('asks for the templates with the recordId, for every user: the backend picks the normal or the temporary route', async () => {
    const p = await mountHost();
    expect(api.getPrintTemplates).toHaveBeenCalledWith('quote', 5);
    expect(p.items.value).toEqual([{ key: 'sheet', name: '報價單' }]);
  });

  it('has no items when the backend lists no templates', async () => {
    api.getPrintTemplates.mockResolvedValue([]);
    const p = await mountHost();
    expect(p.items.value).toEqual([]);
  });

  it('show() loads the printable document and opens the preview with the template name', async () => {
    api.printRecord.mockResolvedValue('<p>預覽</p>');
    const p = await mountHost();
    await p.show('sheet', '報價單');
    expect(api.printRecord).toHaveBeenCalledWith(5, 'sheet', 'zh-TW');
    expect(p.open.value).toBe(true);
    expect(p.html.value).toBe('<p>預覽</p>');
    expect(p.title.value).toBe('報價單');
    expect(p.busy.value).toBe(false);
  });

  it('show() keeps the preview closed when printing fails', async () => {
    api.printRecord.mockRejectedValue(new Error('boom'));
    const p = await mountHost();
    await p.show('sheet', '報價單');
    expect(p.open.value).toBe(false);
    expect(p.busy.value).toBe(false);
  });
});
