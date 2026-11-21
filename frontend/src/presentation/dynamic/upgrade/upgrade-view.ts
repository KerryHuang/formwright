import type { UpgradeCategory, UpgradeItem } from 'src/services/api/dynamic-api';

/** Filter chips in display order (design §10.2 point 4); the first five selectable kinds are on by default. */
export const CATEGORY_ORDER: UpgradeCategory[] = [
  'newOnly', 'both', 'added', 'customerDeleted', 'skippedBefore', 'noBaseline', 'excludedAtOpen', 'customerOnly', 'removedInNew', 'unmatched',
];
export const DEFAULT_CATEGORIES: UpgradeCategory[] = ['newOnly', 'both', 'added', 'customerDeleted', 'skippedBefore', 'noBaseline', 'excludedAtOpen'];

/** Pseudo category for the "cannot apply" chip: any item the backend marked not applicable. */
export const NOT_APPLICABLE = 'notApplicable';

export function countByChip(items: UpgradeItem[]): Record<string, number> {
  const counts: Record<string, number> = {};
  for (const i of items) {
    counts[i.category] = (counts[i.category] ?? 0) + 1;
    if (!i.applicable && i.category !== 'customerOnly' && i.category !== 'removedInNew')
      counts[NOT_APPLICABLE] = (counts[NOT_APPLICABLE] ?? 0) + 1;
  }
  return counts;
}

/** Items whose category chip is on; items the backend marked not applicable also show under the "cannot apply" chip. */
export function filterItems(items: UpgradeItem[], chips: string[]): UpgradeItem[] {
  const on = new Set(chips);
  return items.filter((i) => (on.has(i.category) && (i.applicable || i.category === 'customerOnly' || i.category === 'removedInNew'))
    || (on.has(NOT_APPLICABLE) && !i.applicable && i.category !== 'customerOnly' && i.category !== 'removedInNew'));
}

const OTHER_GROUPS: Record<string, string> = {
  app: 'app', groups: 'permissions', permissions: 'permissions', fieldAccess: 'permissions', optionScopes: 'permissions',
  reports: 'reports', workflows: 'flows', conversions: 'flows', events: 'flows', checks: 'flows', printTemplates: 'flows',
  taskRules: 'flows', approvalFlows: 'flows', ledgers: 'flows', integrations: 'flows',
};

/** Grouping per design §10.2 point 5: one group per module, plus app info, groups & permissions, reports and flows. */
export function groupItems(items: UpgradeItem[]): { key: string; module: string | null; items: UpgradeItem[] }[] {
  const groups = new Map<string, { key: string; module: string | null; items: UpgradeItem[] }>();
  for (const i of items) {
    const other = OTHER_GROUPS[i.section];
    const key = other ? `:${other}` : `module:${i.module ?? ''}`;
    if (!groups.has(key)) groups.set(key, { key, module: other ? null : i.module, items: [] });
    groups.get(key)!.items.push(i);
  }
  return [...groups.values()].sort((a, b) => rank(a.key) - rank(b.key));
}

const rank = (key: string) => (key === ':app' ? 0 : key.startsWith('module:') ? 1 : key === ':permissions' ? 2 : key === ':reports' ? 3 : 4);

/** Values longer than this show as "content differs" with a view button instead of inline (design §10.3). */
export const LONG_VALUE = 80;

export function formatValue(v: unknown): string {
  if (v === null || v === undefined) return '';
  return typeof v === 'string' ? v : JSON.stringify(v);
}

export const isLong = (v: unknown) => formatValue(v).length > LONG_VALUE;
