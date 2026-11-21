import type { I18nText } from 'src/domain/dynamic/types';
import type { AppInfo, UpgradeCategory, UpgradeItem } from 'src/services/api/dynamic-api';

/**
 * Filter chips in display order (design §10.2 point 4). DEFAULT_CATEGORIES is only the page's initial filter (a screen rule);
 * whether an item can be ticked comes from the backend's `applicable` / `defaultSelected`.
 */
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

/**
 * Module code → name for group titles: opened modules from the apps list, modules new in this version from their
 * module item; contributions (`<master>/<module>`) from that master's modules. Unknown codes are left out (title falls back to the code).
 */
export function moduleNames(apps: AppInfo[], appKey: string, items: UpgradeItem[]): Map<string, I18nText> {
  const map = new Map<string, I18nText>();
  const byApp = new Map(apps.map((a) => [a.key, a]));
  for (const m of byApp.get(appKey)?.modules ?? []) map.set(m.code, m.name);
  for (const i of items) {
    if (!i.module || map.has(i.module)) continue;
    const slash = i.module.indexOf('/');
    const hit = slash > 0 ? byApp.get(i.module.slice(0, slash))?.modules.find((m) => m.code === i.module!.slice(slash + 1)) : undefined;
    if (hit) map.set(i.module, hit.name);
    else if (i.section === 'modules' && i.label) map.set(i.module, i.label);
  }
  return map;
}

/** Initial selection: what the backend marked applicable and selected by default (design §20 point 3; never re-decided here). */
export function defaultSelection(items: UpgradeItem[]): Set<string> {
  return new Set(items.filter((i) => i.applicable && i.defaultSelected).map((i) => i.id));
}

/**
 * Ticking or unticking one item (design §6.5): ticking also ticks everything it depends on (transitively);
 * unticking also unticks everything that depends on it. Returns the new selection and the ids pulled in or dropped with it.
 */
export function toggleItem(items: UpgradeItem[], selected: Set<string>, id: string, on: boolean): { selected: Set<string>; together: string[] } {
  const byId = new Map(items.map((i) => [i.id, i]));
  const next = new Set(selected);
  const together: string[] = [];
  const visit = (current: string) => {
    if (on === next.has(current)) return;
    if (on) next.add(current); else next.delete(current);
    if (current !== id) together.push(current);
    const linked = on ? byId.get(current)?.dependsOn ?? [] : items.filter((i) => i.dependsOn.includes(current)).map((i) => i.id);
    linked.filter((x) => byId.get(x)?.applicable !== false).forEach(visit);
  };
  visit(id);
  return { selected: next, together };
}

/** Confirm dialog numbers (design §10.2 point 7): ticked count per category, and how many "both" items overwrite the customer's edits. */
export function confirmCounts(items: UpgradeItem[], selected: Set<string>): { byCategory: Record<string, number>; overwrite: number; total: number } {
  const byCategory: Record<string, number> = {};
  let total = 0;
  for (const i of items) {
    if (!selected.has(i.id)) continue;
    byCategory[i.category] = (byCategory[i.category] ?? 0) + 1;
    total += 1;
  }
  return { byCategory, overwrite: byCategory.both ?? 0, total };
}
