import type { I18nText } from 'src/domain/dynamic/types';
import type { AppInfo, UpgradeCategory, UpgradeItem, UpgradePreview } from 'src/services/api/dynamic-api';

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

/** Lookups for readable property values (walkthrough W2): group ids → names, i18n texts, approval mode and approver source labels. */
export interface PropContext {
  groupName: (id: number) => string | undefined;
  text: (t: I18nText) => string;
  mode: (mode: string) => string;
  source: (source: string) => string;
  fallback: string;
}

const isRecord = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v);
const groupList = (v: unknown, ctx: PropContext) =>
  (Array.isArray(v) ? v : []).map((g) => (typeof g === 'number' ? ctx.groupName(g) ?? `#${g}` : String(g))).join('、');

/**
 * Readable property value: group-id lists show group names; approval-flow stages list "n. name (mode)"; approvers list
 * "stage: source groups" one line each. Anything else falls back to {@link formatValue}.
 */
export function formatProp(name: string, v: unknown, ctx: PropContext): string {
  if (v === null || v === undefined) return '';
  if ((name === 'groups' || name === 'shareWith') && Array.isArray(v) && v.every((g) => typeof g === 'number' || typeof g === 'string'))
    return groupList(v, ctx);
  if (name === 'stages' && Array.isArray(v) && v.every(isRecord))
    return v.map((st, k) => {
      const label = isRecord(st.name) ? ctx.text(st.name as I18nText) : String(st.code ?? '');
      return `${k + 1}. ${label || String(st.code ?? '')}${typeof st.mode === 'string' ? `（${ctx.mode(st.mode)}）` : ''}`;
    }).join('\n');
  if (name === 'approvers' && isRecord(v))
    return Object.entries(v).map(([code, a]) => {
      if (!isRecord(a)) return `${code}：${formatValue(a)}`;
      const groups = groupList([...(Array.isArray(a.groups) ? a.groups : []), ...(Array.isArray(a.candidateGroups) ? a.candidateGroups : [])], ctx);
      const source = typeof a.source === 'string' ? ctx.source(a.source) : '';
      return `${code === '$fallback' ? ctx.fallback : code}：${[source, groups].filter(Boolean).join(' ')}`;
    }).join('\n');
  return formatValue(v);
}

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

/** A shared master's item on this page: same form as the backend's cross-file dependsOn `master:<master id>/<item id>` (§9.6.3, T37). */
export const masterItemId = (master: string, id: string) => `master:${master}/${id}`;

/** The item id without its `master:<id>/` prefix. */
export const localItemId = (id: string) => (id.startsWith('master:') ? id.slice(id.indexOf('/') + 1) : id);

/**
 * One list for ticking across the app and the shared masters updated with it (§9.6.3): master items get `master:<id>/` ids,
 * their own dependsOn (plain ids inside that master) gets the same prefix, and `master:` dependsOn already points across.
 */
export function selectableItems(preview: Pick<UpgradePreview, 'items' | 'masters'>): UpgradeItem[] {
  return [
    ...preview.items,
    ...preview.masters.flatMap((m) => m.items.map((i) => ({
      ...i, id: masterItemId(m.id, i.id), dependsOn: i.dependsOn.map((d) => (d.startsWith('master:') ? d : masterItemId(m.id, d))),
    }))),
  ];
}

/** Splits the page's selection into the apply body's `items` and `masters` (every listed master present, possibly empty). */
export function splitSelection(selected: Set<string>, masters: string[]): { items: string[]; masters: Record<string, string[]> } {
  const out = { items: [] as string[], masters: Object.fromEntries(masters.map((m) => [m, [] as string[]])) };
  for (const id of selected) {
    const slash = id.indexOf('/');
    const master = id.startsWith('master:') && slash > 0 ? id.slice(7, slash) : null;
    if (master !== null && out.masters[master]) out.masters[master].push(id.slice(slash + 1));
    else out.items.push(id);
  }
  return out;
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
    // only ids listed on the page: an unknown one would be sent and rejected as upgradeItemUnknown (review S2)
    linked.filter((x) => byId.has(x) && byId.get(x)!.applicable !== false).forEach(visit);
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

/**
 * Confirm dialog numbers split by part (walkthrough G3): the app first, then each shared master with something ticked;
 * parts with nothing ticked are left out. `masterId` is null for the app.
 */
export function confirmSections(
  preview: Pick<UpgradePreview, 'items' | 'masters'>, selected: Set<string>,
): { masterId: string | null; counts: ReturnType<typeof confirmCounts> }[] {
  const all = selectableItems(preview);
  return [
    { masterId: null as string | null, counts: confirmCounts(preview.items, selected) },
    ...preview.masters.map((m) => ({ masterId: m.id, counts: confirmCounts(all.filter((i) => i.id.startsWith(`master:${m.id}/`)), selected) })),
  ].filter((s) => s.counts.total > 0);
}

/** Only shared masters move (the app's own version stays, T55): the header and success message talk about the masters (walkthrough G1). */
export const mastersOnly = (preview: Pick<UpgradePreview, 'fromVersion' | 'toVersion' | 'masters'>) =>
  preview.fromVersion === preview.toVersion && preview.masters.length > 0;

/**
 * 400 from apply (design §8.3): one line per error key, the item part shown with its label.
 * Keys are `items.<id>`, `<id>.<detail>`, `<id>` or `masters.<master id>.items.<id>` (items from {@link selectableItems});
 * ids contain dots, so the longest listed id that prefixes the key wins.
 */
export function applyErrorLines(
  errors: Record<string, string[]> | undefined, items: UpgradeItem[], labelOf: (i: UpgradeItem) => string,
): { label: string; messages: string[] }[] {
  const ids = [...items].sort((a, b) => b.id.length - a.id.length);
  const byId = new Map(ids.map((i) => [i.id, i]));
  // 訊息裡的項目識別（例如「要一起套用：field:customer.tier」）也換成名稱
  const pattern = ids.length ? new RegExp(ids.map((i) => i.id.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|'), 'g') : null;
  const named = (m: string) => (pattern ? m.replace(pattern, (id) => labelOf(byId.get(id)!)) : m);
  return Object.entries(errors ?? {}).map(([key, messages]) => {
    // 主檔的鍵是 masters.<主檔 id>.items.<項目識別>（T35），換成本頁的 master:<主檔 id>/<項目識別>；訊息裡主檔自己的項目識別也換名稱
    const at = key.startsWith('masters.') ? key.indexOf('.items.') : -1;
    const master = at > 0 ? key.slice(8, at) : null;
    const bare = master !== null ? masterItemId(master, key.slice(at + 7)) : key.startsWith('items.') ? key.slice(6) : key;
    const hit = ids.find((i) => bare === i.id || bare.startsWith(`${i.id}.`));
    const own = master !== null ? ids.filter((i) => i.id.startsWith(`master:${master}/`)) : [];
    const local = own.length ? new RegExp(own.map((i) => localItemId(i.id).replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|'), 'g') : null;
    const named2 = (m: string) => (local ? named(m).replace(local, (id) => labelOf(byId.get(masterItemId(master!, id))!)) : named(m));
    return { label: hit ? labelOf(hit) : bare, messages: messages.map(named2) };
  });
}
