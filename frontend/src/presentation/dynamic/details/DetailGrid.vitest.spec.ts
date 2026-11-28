import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { SchemaDetail, SchemaField, SchemaRule } from 'src/domain/dynamic/types';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import type { DetailRowState } from './detail-rows';
import DetailGrid from './DetailGrid.vue';

const field = (id: number, type: SchemaField['type'], label: string): SchemaField => ({
  id, key: `f_${id}`, type, label: { 'zh-TW': label }, options: [],
  config: { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false },
});
const parent = { ...field(9, 'reference', '表頭') };
const detail = (rules: SchemaRule[] = [], fields: SchemaField[] = [field(1, 'text', '品名'), field(2, 'decimal', '數量')]): SchemaDetail => ({
  moduleKey: 'line', name: { 'zh-TW': '訂單明細' }, parentFieldId: 9, parentFieldKey: 'f_9', titleFieldKey: 'f_1', fields, rules,
});
const existing = (id: number, values: Record<string, unknown>): DetailRowState => ({
  uid: `u${id}`, id, rowVersion: `v${id}`, values: { ...values }, original: values, deleted: false,
});

function mountGrid(props: { detail?: SchemaDetail; modelValue?: DetailRowState[]; readonly?: boolean; errors?: Map<string, Record<string, string>>; headerValues?: Record<string, unknown>; headerFields?: SchemaField[] }, attach = false) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(DetailGrid, {
    props: {
      detail: detail(), modelValue: [], readonly: false, errors: new Map(), ...props,
      'onUpdate:modelValue': (v: DetailRowState[]) => w.setProps({ modelValue: v }),
    },
    ...(attach && { attachTo: document.body }),
    global: { plugins: [[quasar.Quasar, { components: quasar }], createPinia(), i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
  });
  return w;
}
const rowsOf = (w: ReturnType<typeof mountGrid>) => w.props('modelValue') as DetailRowState[];

describe('DetailGrid', () => {
  beforeEach(() => {
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });

  it('renders one column per detail field, excluding parent field', () => {
    const w = mountGrid({ detail: detail([], [parent, field(1, 'text', '品名'), field(2, 'decimal', '數量')]), modelValue: [existing(1, { f_1: '螺絲', f_2: 3 })] });
    expect(w.findAll('th').map((th) => th.text()).filter(Boolean)).toEqual(['品名', '數量']);
    expect(w.findAll('tbody tr')[0]!.findAll('input')).toHaveLength(2);
  });

  it('computed columns are read-only and show stored values', () => {
    const formula = { ...field(3, 'formula', '金額'), config: { ...field(3, 'formula', '').config, resultType: 'number' as const } };
    const w = mountGrid({
      detail: detail([], [field(1, 'text', '品名'), formula, field(4, 'autonumber', '項次'), field(5, 'rollup', '合計')]),
      modelValue: [existing(1, { f_1: '螺絲', f_3: 30, f_4: '001', f_5: 7 })],
    });
    const inputs = w.findAll('tbody tr')[0]!.findAll('input');
    expect(inputs.map((i) => (i.element as HTMLInputElement).value)).toEqual(['螺絲', '30', '001', '7']);
    expect(inputs.map((i) => i.attributes('readonly') !== undefined)).toEqual([false, true, true, true]);
  });

  it('add row takes the header value for a parent-default field', async () => {
    const dateWithParent = { ...field(3, 'date', '交期'), config: { ...field(3, 'date', '').config, parentDefaultFieldId: 50 } };
    const w = mountGrid({
      detail: detail([], [field(1, 'text', '品名'), dateWithParent]), headerFields: [field(50, 'date', '表頭交期')], headerValues: { f_50: '2026-12-01' },
    });
    await w.find('[data-testid="add-row"]').trigger('click');
    expect(rowsOf(w)[0]!.values).toEqual({ f_1: null, f_3: '2026-12-01' });
  });

  it('add row appends empty row', async () => {
    const w = mountGrid({ modelValue: [existing(1, { f_1: '螺絲' })] });
    await w.find('[data-testid="add-row"]').trigger('click');
    const rows = rowsOf(w);
    expect(rows).toHaveLength(2);
    expect(rows[1]!.id).toBeUndefined();
    expect(rows[1]).toMatchObject({ deleted: false, values: { f_1: null, f_2: null } });
    expect(w.findAll('tbody tr')).toHaveLength(2);
    await w.findAll('tbody tr')[1]!.find('input').setValue('螺帽');
    expect(rowsOf(w)[1]!.values.f_1).toBe('螺帽');
    expect(rowsOf(w)[0]!.values.f_1).toBe('螺絲');
  });

  it('delete new row removes it; delete existing row marks deleted and hides it', async () => {
    const w = mountGrid({ modelValue: [existing(1, { f_1: '螺絲' })] });
    await w.find('[data-testid="add-row"]').trigger('click');
    await w.findAll('[data-testid="delete-row"]')[1]!.trigger('click');
    expect(rowsOf(w)).toHaveLength(1);
    await w.findAll('[data-testid="delete-row"]')[0]!.trigger('click');
    expect(rowsOf(w)).toHaveLength(1);
    expect(rowsOf(w)[0]!.deleted).toBe(true);
    expect(w.findAll('[data-testid="delete-row"]')).toHaveLength(0);
    expect(w.text()).not.toContain('螺絲');
  });

  it('row rule readOnlyAll locks that row', () => {
    const lock: SchemaRule = { kind: 'readOnly', targets: ['*'], condition: { field: 1, op: 'eq', value: 'closed' }, options: [] };
    const w = mountGrid({ detail: detail([lock]), modelValue: [existing(1, { f_1: 'closed' }), existing(2, { f_1: 'open' })] });
    const [locked, open] = w.findAll('tbody tr');
    expect(locked!.findAll('input').every((i) => i.attributes('readonly') !== undefined)).toBe(true);
    expect(locked!.find('[data-testid="delete-row"]').exists()).toBe(false);
    expect(open!.findAll('input').every((i) => i.attributes('readonly') === undefined)).toBe(true);
    expect(open!.find('[data-testid="delete-row"]').exists()).toBe(true);
  });

  it('row visibility false shows blank non-editable cell', () => {
    const vis: SchemaRule = { kind: 'visibility', targets: ['field:2'], condition: { field: 1, op: 'eq', value: 'show' }, options: [] };
    const w = mountGrid({ detail: detail([vis]), modelValue: [existing(1, { f_1: 'hide', f_2: 5 }), existing(2, { f_1: 'show', f_2: 6 })] });
    const [hidden, shown] = w.findAll('tbody tr');
    expect(hidden!.findAll('input')).toHaveLength(1);
    expect(hidden!.find('[data-testid="cell-hidden"]').exists()).toBe(true);
    expect(hidden!.text()).not.toContain('5');
    expect(shown!.findAll('input')).toHaveLength(2);
  });

  it('cell error shown under input', async () => {
    const w = mountGrid({
      modelValue: [existing(1, { f_1: 'a' }), existing(2, { f_1: 'b' })],
      errors: new Map([['u2', { f_2: '必須是數字', _record: '此記錄目前不可修改' }]]),
    });
    await flushPromises();
    const [first, second] = w.findAll('tbody tr');
    expect(first!.text()).not.toContain('必須是數字');
    const cells = second!.findAll('.q-field');
    expect(cells[1]!.text()).toContain('必須是數字');
    expect(cells[0]!.text()).not.toContain('必須是數字');
    expect(second!.find('[data-testid="row-error"]').text()).toContain('此記錄目前不可修改');
  });

  it('rule-required cell is marked required', () => {
    const req: SchemaRule = { kind: 'required', targets: ['field:2'], condition: { field: 1, op: 'eq', value: 'go' }, options: [] };
    const w = mountGrid({ detail: detail([req]), modelValue: [existing(1, { f_1: 'go' }), existing(2, { f_1: 'stay' })] });
    const qty = w.findAllComponents({ name: 'MoneyNumberInput' });
    expect(qty.map((c) => c.props('required'))).toEqual([true, false]);
    expect(w.findAll('th').map((th) => th.text())).not.toContain('數量 *');
  });
});

describe('DetailGrid on a phone', () => {
  const resize = async (width: number) => {
    window.innerWidth = width;
    window.dispatchEvent(new Event('resize'));
    await new Promise((r) => setTimeout(r, 150));
    await flushPromises();
  };
  beforeEach(() => {
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });
  afterEach(() => resize(1366));
  const summaries = (w: ReturnType<typeof mountGrid>) => w.findAll('[data-testid="row-card-summary"]').map((s) => s.text());
  const open = (w: ReturnType<typeof mountGrid>) => w.findAll('[data-testid="row-card-body"]').map((b) => (b.element as HTMLElement).style.display !== 'none');

  it('renders cards instead of the table; wide screens keep the table', async () => {
    const w = mountGrid({ modelValue: [existing(1, { f_1: '螺絲', f_2: 3 })] });
    await resize(390);
    expect(w.find('table').exists()).toBe(false);
    expect(w.findAll('[data-testid="row-card"]')).toHaveLength(1);
    await resize(820);
    expect(w.find('table').exists()).toBe(true);
    expect(w.find('[data-testid="row-card"]').exists()).toBe(false);
  });

  it('summary shows the first two fields that are not hidden on that row', async () => {
    const vis: SchemaRule = { kind: 'visibility', targets: ['field:2'], condition: { field: 1, op: 'eq', value: 'show' }, options: [] };
    const fields = [field(1, 'text', '品名'), field(2, 'decimal', '數量'), field(3, 'text', '備註')];
    const w = mountGrid({
      detail: detail([vis], fields),
      modelValue: [existing(1, { f_1: 'hide', f_2: 5, f_3: '急件' }), existing(2, { f_1: 'show', f_2: 6, f_3: '一般' })],
    });
    await resize(390);
    expect(summaries(w)).toEqual(['hide · 急件', 'show · 6']);
    // 隱藏欄位在卡片裡連欄名都不顯示
    const first = w.findAll('[data-testid="row-card-body"]')[0]!;
    expect(first.text()).not.toContain('數量');
  });

  it('summary skips empty fields and shows the first two that have a value', async () => {
    const fields = [field(1, 'text', '項次'), field(2, 'text', '品名'), field(3, 'decimal', '數量'), field(4, 'text', '備註')];
    const w = mountGrid({ detail: detail([], fields), modelValue: [existing(1, { f_2: '螺絲', f_3: 3, f_4: '急件' })] });
    await resize(390);
    expect(summaries(w)).toEqual(['螺絲 · 3']);
  });

  it('summary uses the reference title: stored, then newly picked', async () => {
    const product: SchemaField = { ...field(1, 'reference', '產品'), reference: { kind: 'module', key: 'product', name: { 'zh-TW': '產品' }, lightUrl: '' } };
    const stored: DetailRowState = { ...existing(1, { f_1: 7, f_2: 3 }), references: { f_1: { state: 'ok', key: '7', title: '大螺絲' } as never } };
    const w = mountGrid({ detail: detail([], [product, field(2, 'decimal', '數量')]), modelValue: [stored] });
    await resize(390);
    expect(summaries(w)).toEqual(['大螺絲 · 3']);
    const picker = w.findComponent({ name: 'PopupSelect' });
    picker.vm.$emit('picked', { key: '9', title: '小螺帽' });
    picker.vm.$emit('update:modelValue', 9);
    await flushPromises();
    expect(summaries(w)).toEqual(['小螺帽 · 3']);
  });

  it('AC-04: a new row is expanded with labelled fields; collapsed it shows what was typed', async () => {
    const required = { ...field(1, 'text', '品名'), config: { ...field(1, 'text', '').config, required: true } };
    const w = mountGrid({ detail: detail([], [required, field(2, 'decimal', '數量')]) });
    await resize(390);
    await w.find('[data-testid="add-row"]').trigger('click');
    await flushPromises();
    expect(open(w)).toEqual([true]);
    expect(w.find('[data-testid="row-card-body"]').text()).toContain('品名 *');
    await w.find('[data-testid="row-card-body"] input').setValue('螺帽');
    await w.find('[data-testid="row-card-toggle"]').trigger('click');
    expect(open(w)).toEqual([false]);
    expect(summaries(w)).toEqual(['螺帽']);
    expect(w.find('[data-testid="row-card-toggle"]').text()).toContain('#1');
  });

  it('AC-05: a row that gets errors expands and shows them', async () => {
    const w = mountGrid({ modelValue: [existing(1, { f_1: 'a' }), existing(2, { f_1: 'b' })] });
    await resize(390);
    expect(open(w)).toEqual([false, false]);
    await w.setProps({ errors: new Map([['u2', { f_2: '必填' }]]) });
    await flushPromises();
    expect(open(w)).toEqual([false, true]);
    expect(w.findAll('[data-testid="row-card"]')[1]!.text()).toContain('必填');
  });

  it('AC-06: a readonly grid is fully expanded without add or delete', async () => {
    const w = mountGrid({ readonly: true, modelValue: [existing(1, { f_1: '螺絲' })] });
    await resize(390);
    expect(open(w)).toEqual([true]);
    expect(w.find('[data-testid="row-card-toggle"]').exists()).toBe(false);
    expect(w.find('[data-testid="add-row"]').exists()).toBe(false);
    expect(w.find('[data-testid="delete-row"]').exists()).toBe(false);
  });

  const typed: SchemaField[] = [
    field(1, 'text', '品名'), field(2, 'textarea', '備註'), field(3, 'decimal', '數量'), field(4, 'date', '交期'),
    field(5, 'datetime', '到貨時間'), field(6, 'time', '時段'), field(7, 'boolean', '急件'),
    { ...field(8, 'enum', '等級'), options: [{ value: 'a', label: { 'zh-TW': '甲' }, sequence: 1, active: true }] as never },
    { ...field(10, 'multienum', '標籤'), options: [{ value: 'a', label: { 'zh-TW': '甲' }, sequence: 1, active: true }] as never },
    { ...field(11, 'reference', '產品'), reference: { kind: 'module', key: 'product', name: { 'zh-TW': '產品' }, lightUrl: '' } },
    field(12, 'autonumber', '項次'),
  ];
  /** The accessible names the card's controls get through aria-labelledby. */
  const namesOf = (root: Element) => [...root.querySelectorAll('input, textarea, select, [role="switch"], [role="combobox"]')]
    // QToggle 內部的原生 checkbox 在 aria-hidden 裡，讀屏看不到
    .filter((el) => !el.closest('[aria-hidden="true"]'))
    .map((el) => (el.getAttribute('aria-labelledby') ?? '').split(' ')
      .map((id) => root.ownerDocument.getElementById(id)?.textContent?.trim() ?? '').join(''));

  for (const readonly of [false, true]) {
    it(`every control in a card is named by its visible column label (readonly row rule: ${readonly})`, async () => {
      const lock: SchemaRule = { kind: 'readOnly', targets: ['*'], condition: { field: 1, op: 'eq', value: 'x' }, options: [] };
      const w = mountGrid({ detail: detail(readonly ? [lock] : [], typed), modelValue: [existing(1, { f_1: 'x' }), existing(2, { f_1: 'x' })] }, true);
      await resize(390);
      const bodies = w.findAll('[data-testid="row-card-body"]').map((b) => b.element);
      for (const body of bodies) expect(namesOf(body)).toEqual(typed.map((f) => f.label['zh-TW']));
      // 每一列的欄名 id 各自獨立
      const ids = [...document.body.querySelectorAll('[data-testid="row-card-body"] [id][data-cell-label]')].map((e) => e.id);
      expect(new Set(ids).size).toBe(typed.length * 2);
      w.unmount();
    });
  }

  it('the desktop table adds no aria-labelledby', async () => {
    const w = mountGrid({ detail: detail([], typed), modelValue: [existing(1, { f_1: 'x' })] }, true);
    await resize(1366);
    expect(w.find('table').exists()).toBe(true);
    expect(w.find('[aria-labelledby]').exists()).toBe(false);
    w.unmount();
  });
});

describe('DetailGrid 數字欄', () => {
  it('編輯模式下數字欄的表頭與儲存格靠右，文字欄不動', () => {
    const w = mountGrid({ modelValue: [existing(1, { f_1: '螺絲', f_2: 3 })] });
    expect(w.findAll('thead th').map((th) => th.classes().includes('text-right'))).toEqual([false, true, false]);
    expect(w.findAll('tbody tr:first-child td').map((td) => td.classes().includes('text-right'))).toEqual([false, true, false]);
  });
});
