import { describe, expect, it } from 'vitest';
import type { UpgradeItem } from 'src/services/api/dynamic-api';
import { countByChip, DEFAULT_CATEGORIES, filterItems, formatValue, groupItems, isLong, NOT_APPLICABLE } from './upgrade-view';

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
});
