import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import RecordHeaderBar, { type HeaderAction } from './RecordHeaderBar.vue';

type Props = InstanceType<typeof RecordHeaderBar>['$props'];

async function mountBar(props: Partial<Props>, width = 1366) {
  window.innerWidth = width;
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/:p(.*)*', component: { render: () => null } }] });
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const Host = {
    render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(quasar.QPage, null, () => h(
      RecordHeaderBar, { backLabel: '採購單', backTo: '/dynamic/po', backAriaLabel: '回到採購單', title: 'PO-1', actions: [], ...props },
      { badge: () => h('span', { 'data-testid': 'badge' }, '簽核中') },
    )))),
  };
  const w = mount(Host, {
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], router, i18n] },
  });
  window.dispatchEvent(new Event('resize'));
  await new Promise((r) => setTimeout(r, 150));
  await flushPromises();
  return w;
}

const run = vi.fn();
const actions = (): HeaderAction[] => [
  { key: 'submit', label: '送簽：已確認', color: 'primary', icon: 'rule', testid: 'a-submit', bottom: true, run },
  { key: 'convert', label: '轉成進貨單', outline: true, testid: 'a-convert', run },
  {
    key: 'print', label: '列印', outline: true, testid: 'a-print',
    children: [{ key: 'sheet', label: '採購單', menuLabel: '列印：採購單', testid: 'a-print-sheet', run }],
  },
  { key: 'delete', label: '刪除', color: 'negative', outline: true, negative: true, testid: 'a-delete', run },
];

describe('RecordHeaderBar', () => {
  beforeEach(() => { vi.clearAllMocks(); vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} }); });
  afterEach(() => { window.innerWidth = 1024; document.body.innerHTML = ''; });

  it('wide: back, title, badge and every action sit in one fixed bar, no bottom bar', async () => {
    const w = await mountBar({ actions: actions() });
    const bar = w.find('[data-testid="record-header-bar"]');
    expect(bar.exists()).toBe(true);
    const back = bar.find('[data-testid="record-back"]');
    expect(back.text()).toContain('採購單');
    expect(back.attributes('aria-label')).toBe('回到採購單');
    expect(back.attributes('href')).toBe('/dynamic/po');
    expect(bar.find('[data-testid="record-title"]').text()).toBe('PO-1');
    expect(bar.find('[data-testid="badge"]').exists()).toBe(true);
    for (const id of ['a-submit', 'a-convert', 'a-print', 'a-delete']) expect(bar.find(`[data-testid="${id}"]`).exists()).toBe(true);
    expect(w.find('[data-testid="record-bottom-bar"]').exists()).toBe(false);
    expect(w.find('[data-testid="record-more"]').exists()).toBe(false);
  });

  it('wide: a button runs its handler, a link action navigates, a submit action targets the form', async () => {
    const w = await mountBar({
      actions: [
        { key: 'a', label: '甲', testid: 'a-a', run },
        { key: 'b', label: '乙', testid: 'a-b', to: '/dynamic/po/1/edit' },
        { key: 'c', label: '儲存', testid: 'a-c', submitForm: 'record-form' },
      ],
    });
    await w.find('[data-testid="a-a"]').trigger('click');
    expect(run).toHaveBeenCalledTimes(1);
    expect(w.find('[data-testid="a-b"]').attributes('href')).toBe('/dynamic/po/1/edit');
    const save = w.find('[data-testid="a-c"]');
    expect(save.attributes('type')).toBe('submit');
    expect(save.attributes('form')).toBe('record-form');
  });

  it('wide: no actions leaves only back, title and badge', async () => {
    const w = await mountBar({ actions: [] });
    expect(w.find('[data-testid="record-actions"]').exists()).toBe(false);
    expect(w.find('[data-testid="record-title"]').exists()).toBe(true);
  });

  it('wide: no title shows only the back button', async () => {
    const w = await mountBar({ title: undefined });
    expect(w.find('[data-testid="record-title"]').exists()).toBe(false);
    expect(w.find('[data-testid="record-back"]').exists()).toBe(true);
  });

  it('phone: top bar has the back icon only (name kept for screen readers) and the title', async () => {
    const w = await mountBar({ actions: actions() }, 390);
    const back = w.find('[data-testid="record-back"]');
    expect(back.text()).not.toContain('採購單');
    expect(back.attributes('aria-label')).toBe('回到採購單');
    expect(w.find('[data-testid="record-title"]').text()).toBe('PO-1');
    expect(w.find('[data-testid="a-convert"]').exists()).toBe(false);
  });

  it('phone: falls back to the module name when there is no title', async () => {
    const w = await mountBar({ title: undefined, actions: [] }, 390);
    expect(w.find('[data-testid="record-title"]').text()).toBe('採購單');
  });

  it('phone: bottom bar has the main action and a more menu; delete is last and red; print templates are listed per template', async () => {
    const w = await mountBar({ actions: actions() }, 390);
    const bottom = w.find('[data-testid="record-bottom-bar"]');
    expect(bottom.exists()).toBe(true);
    expect(bottom.find('[data-testid="a-submit"]').exists()).toBe(true);
    expect(bottom.find('[data-testid="a-convert"]').exists()).toBe(false);
    await bottom.find('[data-testid="record-more"]').trigger('click');
    await flushPromises();
    const items = [...document.body.querySelectorAll('.q-menu .q-item')];
    expect(items.map((i) => i.textContent?.trim())).toEqual(['轉成進貨單', '列印：採購單', '刪除']);
    expect(items[2]!.classList.contains('text-negative')).toBe(true);
    (items[1] as HTMLElement).click();
    await flushPromises();
    expect(run).toHaveBeenCalledTimes(1);
  });

  it('phone: nothing to do means no bottom bar', async () => {
    const w = await mountBar({ actions: [] }, 390);
    expect(w.find('[data-testid="record-bottom-bar"]').exists()).toBe(false);
    expect(w.find('[data-testid="record-more"]').exists()).toBe(false);
  });

  it('phone: with only bottom actions (form page) there is no more menu', async () => {
    const w = await mountBar({
      actions: [
        { key: 'cancel', label: '取消', flat: true, bottom: true, testid: 'a-cancel', run },
        { key: 'save', label: '儲存', color: 'primary', bottom: true, testid: 'a-save', submitForm: 'record-form' },
      ],
    }, 390);
    const bottom = w.find('[data-testid="record-bottom-bar"]');
    expect(bottom.find('[data-testid="a-cancel"]').exists()).toBe(true);
    expect(bottom.find('[data-testid="a-save"]').attributes('form')).toBe('record-form');
    expect(w.find('[data-testid="record-more"]').exists()).toBe(false);
  });

  it('phone with menuOnly (another bottom bar is present): no bottom bar, one more menu in the top bar with every action', async () => {
    const w = await mountBar({ actions: actions(), menuOnly: true }, 390);
    expect(w.find('[data-testid="record-bottom-bar"]').exists()).toBe(false);
    const top = w.find('[data-testid="record-header-bar"]');
    await top.find('[data-testid="record-more"]').trigger('click');
    await flushPromises();
    const items = [...document.body.querySelectorAll('.q-menu .q-item')];
    expect(items.map((i) => i.textContent?.trim())).toEqual(['送簽：已確認', '轉成進貨單', '列印：採購單', '刪除']);
  });
});
