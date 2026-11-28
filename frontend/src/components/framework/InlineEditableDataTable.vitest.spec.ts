import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { afterEach, describe, expect, it } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import InlineEditableDataTable from './InlineEditableDataTable.vue';

type Row = { id: string; name: string };

function mountTable(readonly = false, width = 1366, hasError?: (r: unknown) => boolean) {
  window.innerWidth = width;
  window.dispatchEvent(new Event('resize'));
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return mount(InlineEditableDataTable, {
    props: {
      columns: [{ name: 'name', label: '品名' }, { name: 'qty', label: '數量' }],
      rows: [{ id: 'a', name: '螺絲' }, { id: 'b', name: '螺帽' }] as Row[],
      rowKey: (r: unknown) => (r as Row).id, // mount() does not infer the generic T
      readonly,
      ...(hasError && { hasError }),
    },
    slots: {
      summary: ({ row }: { row: unknown }) => h('span', { class: 'sum' }, `摘要${(row as Row).name}`),
      'cell-name': ({ row, index }: { row: unknown; index: number }) => h('span', { class: 'cell' }, `${index}:${(row as Row).name}`),
      'row-actions': () => h('button', { class: 'act' }, 'x'),
    },
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar }], i18n] },
  });
}

/** Quasar 的 screen 以 debounce 更新，掛載後等它套用寬度。 */
async function mountAt(width: number, readonly = false, hasError?: (r: unknown) => boolean) {
  const w = mountTable(readonly, width, hasError);
  await new Promise((r) => setTimeout(r, 150));
  await flushPromises();
  return w;
}
const cards = (w: ReturnType<typeof mountTable>) => w.findAll('[data-testid="row-card"]');
const bodies = (w: ReturnType<typeof mountTable>) => cards(w).map((c) => c.find('[data-testid="row-card-body"]').isVisible());

describe('InlineEditableDataTable', () => {
  afterEach(async () => {
    document.body.innerHTML = '';
    window.innerWidth = 1366;
    window.dispatchEvent(new Event('resize'));
    await new Promise((r) => setTimeout(r, 150));
  });

  it('renders columns and cell slots', () => {
    const w = mountTable();
    expect(w.findAll('th').map((th) => th.text()).slice(0, 2)).toEqual(['品名', '數量']);
    expect(w.findAll('.cell').map((c) => c.text())).toEqual(['0:螺絲', '1:螺帽']);
    expect(w.findAll('.act')).toHaveLength(2);
    expect(w.find('[data-testid="add-row"]').exists()).toBe(true);
    w.find('[data-testid="add-row"]').trigger('click');
    expect(w.emitted('add')).toHaveLength(1);
  });

  it('add button hidden when readonly', () => {
    expect(mountTable(true).find('[data-testid="add-row"]').exists()).toBe(false);
  });

  it('wide screens render the table, not cards', async () => {
    const w = await mountAt(1366);
    expect(w.find('table').exists()).toBe(true);
    expect(cards(w)).toHaveLength(0);
  });

  it('phone width renders one collapsed card per row with its number and summary', async () => {
    const w = await mountAt(390);
    expect(w.find('table').exists()).toBe(false);
    expect(cards(w)).toHaveLength(2);
    expect(bodies(w)).toEqual([false, false]);
    expect(cards(w)[0]!.find('[data-testid="row-card-toggle"]').text()).toContain('#1');
    expect(cards(w)[0]!.find('.sum').text()).toBe('摘要螺絲');
    expect(cards(w)[1]!.find('[data-testid="row-card-toggle"]').text()).toContain('#2');
  });

  it('tapping the card header (or Enter / Space) expands and collapses it; several can be open', async () => {
    const w = await mountAt(390);
    const [first, second] = cards(w).map((c) => c.find('[data-testid="row-card-toggle"]'));
    expect(first!.attributes('tabindex')).toBe('0');
    expect(first!.attributes('aria-expanded')).toBe('false');
    await first!.trigger('click');
    await second!.trigger('keydown', { key: 'Enter' });
    expect(bodies(w)).toEqual([true, true]);
    expect(first!.attributes('aria-expanded')).toBe('true');
    expect(cards(w)[0]!.text()).toContain('品名');
    expect(cards(w)[0]!.find('.act').exists()).toBe(true);
    await first!.trigger('keydown', { key: ' ' });
    expect(bodies(w)).toEqual([false, true]);
  });

  it('AC-04: the row added with the button is expanded, and collapses back to its summary', async () => {
    const w = await mountAt(390);
    await w.find('[data-testid="add-row"]').trigger('click');
    expect(w.emitted('add')).toHaveLength(1);
    await w.setProps({ rows: [{ id: 'a', name: '螺絲' }, { id: 'b', name: '螺帽' }, { id: 'c', name: '墊片' }] });
    expect(bodies(w)).toEqual([false, false, true]);
    const toggle = cards(w)[2]!.find('[data-testid="row-card-toggle"]');
    await toggle.trigger('click');
    expect(bodies(w)).toEqual([false, false, false]);
    expect(toggle.text()).toContain('#3');
    expect(toggle.text()).toContain('摘要墊片');
  });

  it('rows that arrive without the add button stay collapsed', async () => {
    const w = await mountAt(390);
    await w.setProps({ rows: [{ id: 'x', name: '重新載入' }] });
    expect(bodies(w)).toEqual([false]);
  });

  it('AC-05: a row with errors expands, cannot be collapsed, and stays open once the error clears', async () => {
    const w = await mountAt(390);
    await w.setProps({ hasError: (r: unknown) => (r as Row).id === 'b' });
    expect(bodies(w)).toEqual([false, true]);
    await cards(w)[1]!.find('[data-testid="row-card-toggle"]').trigger('click');
    expect(bodies(w)).toEqual([false, true]);
    await w.setProps({ hasError: () => false });
    expect(bodies(w)).toEqual([false, true]);
  });

  it('AC-06: readonly cards are all expanded, with labels, no toggle and no add button', async () => {
    const w = await mountAt(390, true);
    expect(cards(w)).toHaveLength(2);
    expect(bodies(w)).toEqual([true, true]);
    expect(w.find('[data-testid="row-card-toggle"]').exists()).toBe(false);
    expect(w.find('.sum').exists()).toBe(false);
    expect(cards(w)[0]!.text()).toContain('品名');
    expect(w.find('[data-testid="add-row"]').exists()).toBe(false);
  });

  it('phone width with no rows says so', async () => {
    const w = await mountAt(390);
    await w.setProps({ rows: [] });
    expect(w.text()).toContain(zhTW.dynamic.noData);
  });

  it('the phone card header is at least 44px tall to tap', async () => {
    const w = await mountAt(390);
    expect((w.get('[data-testid="row-card-toggle"]').element as HTMLElement).style.minHeight).toBe('44px');
  });
});

describe('InlineEditableDataTable 欄位對齊', () => {
  it("align: 'right' 的欄位表頭與儲存格靠右，其他靠左", () => {
    const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
    const w = mount(InlineEditableDataTable, {
      props: { columns: [{ name: 'name', label: '品名' }, { name: 'qty', label: '數量', align: 'right' }], rows: [{ id: 'a' }], rowKey: (r: unknown) => (r as { id: string }).id },
      global: { plugins: [[quasar.Quasar, { components: quasar }], i18n] },
    });
    const [name, qty] = w.findAll('thead th');
    expect(name!.classes()).toContain('text-left');
    expect(qty!.classes()).toEqual(['text-right']);
    const [first, second] = w.findAll('tbody tr:first-child td');
    expect(first!.classes()).not.toContain('text-right');
    expect(second!.classes()).toContain('text-right');
  });
});
