import { describe, expect, it } from 'vitest';
import type { AppInfo, UpgradeItem } from 'src/services/api/dynamic-api';
import { applyErrorLines, confirmCounts, countByChip, DEFAULT_CATEGORIES, defaultSelection, filterItems, formatValue, groupItems, isLong, moduleNames, NOT_APPLICABLE, toggleItem } from './upgrade-view';

const item = (id: string, category: UpgradeItem['category'], extra: Partial<UpgradeItem> = {}): UpgradeItem => ({
  id, section: 'fields', module: 'lead', label: null, category, defaultSelected: false, applicable: true, reason: null,
  dependsOn: [], properties: [], attachedPermissions: [], ...extra,
});

describe('upgrade view', () => {
  const items = [
    item('a', 'newOnly'), item('b', 'customerOnly', { applicable: false }), item('c', 'added'),
    item('d', 'newOnly', { applicable: false, reason: 'typeChanged' }), item('e', 'removedInNew', { applicable: false }),
  ];

  it('default chips hide customer-only, removed and not-applicable items', () => {
    expect(filterItems(items, DEFAULT_CATEGORIES).map((i) => i.id)).toEqual(['a', 'c']);
  });

  it('the cannot-apply chip shows items the backend marked not applicable', () => {
    expect(filterItems(items, [NOT_APPLICABLE]).map((i) => i.id)).toEqual(['d']);
    expect(countByChip(items)).toMatchObject({ newOnly: 2, customerOnly: 1, added: 1, removedInNew: 1, [NOT_APPLICABLE]: 1 });
  });

  it('groups app info first, then modules, permissions, reports, flows', () => {
    const grouped = groupItems([
      item('r', 'added', { section: 'reports', module: 'lead' }), item('p', 'both', { section: 'permissions' }),
      item('f', 'added'), item('x', 'newOnly', { section: 'app', module: null }), item('w', 'added', { section: 'workflows' }),
    ]);
    expect(grouped.map((g) => g.key)).toEqual([':app', 'module:lead', ':permissions', ':reports', ':flows']);
  });

  it('formats values and flags long ones', () => {
    expect(formatValue({ a: 1 })).toBe('{"a":1}');
    expect(formatValue(null)).toBe('');
    expect(isLong('x'.repeat(81))).toBe(true);
  });

  it('module names come from the opened app, the new module item, or the master for contributions', () => {
    const app = (key: string, modules: [string, string][]): AppInfo => ({
      key, name: { 'zh-TW': key }, packageVersion: '1.0.0', status: 'Open', rowVersion: 'r', dependents: [], unaffiliatedReferrers: [], warnings: [],
      modules: modules.map(([code, name], id) => ({ code, key: `k-${code}`, id, name: { 'zh-TW': name }, status: 'Active' })),
    });
    const names = moduleNames([app('crm', [['lead', '潛在客戶']]), app('partner', [['partner', '往來對象']])], 'crm', [
      item('module:visit', 'added', { section: 'modules', module: 'visit', label: { 'zh-TW': '拜訪' } }),
      item('contribution:partner/partner', 'newOnly', { section: 'contributions', module: 'partner/partner' }),
      item('field:ghost.x', 'newOnly', { module: 'ghost' }),
    ]);
    expect(names.get('lead')).toEqual({ 'zh-TW': '潛在客戶' });
    expect(names.get('visit')).toEqual({ 'zh-TW': '拜訪' });
    expect(names.get('partner/partner')).toEqual({ 'zh-TW': '往來對象' });
    expect(names.has('ghost')).toBe(false);
  });
});

describe('selection (§6.5, §10.2 point 7)', () => {
  const mk = (id: string, dependsOn: string[] = [], category: UpgradeItem['category'] = 'added', applicable = true): UpgradeItem => ({
    id, section: 'fields', module: 'm', label: null, category, defaultSelected: applicable, applicable, reason: null, dependsOn,
    properties: [], attachedPermissions: [],
  });
  const items = [mk('report:r', ['field:m.a']), mk('field:m.a', ['field:m.b']), mk('field:m.b'), mk('field:m.c', [], 'both')];

  it('ticking pulls in dependencies transitively', () => {
    const r = toggleItem(items, new Set(), 'report:r', true);
    expect([...r.selected].sort()).toEqual(['field:m.a', 'field:m.b', 'report:r']);
    expect(r.together.sort()).toEqual(['field:m.a', 'field:m.b']);
  });

  it('unticking drops dependents transitively', () => {
    const r = toggleItem(items, new Set(['report:r', 'field:m.a', 'field:m.b']), 'field:m.b', false);
    expect([...r.selected]).toEqual([]);
    expect(r.together.sort()).toEqual(['field:m.a', 'report:r']);
  });

  it('confirm counts per category and overwritten edits', () => {
    const c = confirmCounts(items, new Set(['field:m.c', 'field:m.b']));
    expect(c).toEqual({ byCategory: { both: 1, added: 1 }, overwrite: 1, total: 2 });
  });

  it('default selection follows the backend', () => {
    expect([...defaultSelection([mk('a'), mk('b', [], 'customerOnly', false)])]).toEqual(['a']);
  });
});

describe('apply errors (§8.3, review M5)', () => {
  const items = [item('field:customer.tier', 'added'), item('field:customer.tierNote', 'added'), item('report:byStatus', 'added')];
  const labelOf = (i: UpgradeItem) => `L(${i.id})`;

  it('maps items.<id>, <id>.<detail> and bare keys to the item label, longest id first', () => {
    expect(applyErrorLines({
      'items.report:byStatus': ['要一起套用：field:customer.tier'],
      'field:customer.tierNote.config.maxLength': ['too long'],
      'report:byStatus.definition': ['bad'],
      'groupMap.sales': ['mapped'],
    }, items, labelOf)).toEqual([
      { label: 'L(report:byStatus)', messages: ['要一起套用：L(field:customer.tier)'] },
      { label: 'L(field:customer.tierNote)', messages: ['too long'] },
      { label: 'L(report:byStatus)', messages: ['bad'] },
      { label: 'groupMap.sales', messages: ['mapped'] },
    ]);
  });

  it('is empty without errors', () => {
    expect(applyErrorLines(undefined, items, labelOf)).toEqual([]);
  });
});
