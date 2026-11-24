import { describe, expect, it } from 'vitest';
import type { AppInfo, UpgradeItem } from 'src/services/api/dynamic-api';
import type { I18nText } from 'src/domain/dynamic/types';
import { applyErrorLines, confirmCounts, confirmSections, mastersOnly, countByChip, DEFAULT_CATEGORIES, defaultSelection, filterItems, formatProp, formatValue, groupItems, isLong, moduleNames, NOT_APPLICABLE, selectableItems, splitSelection, toggleItem } from './upgrade-view';

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

  it('ticking never adds an id that is not listed on the page (review S2)', () => {
    const r = toggleItem([mk('report:r', ['field:m.gone'])], new Set(), 'report:r', true);
    expect([...r.selected]).toEqual(['report:r']);
    expect(r.together).toEqual([]);
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

  // 審查 M1：狀態機一趟失敗的 400，鍵掛第一個項目、訊息前綴是整趟的項目清單
  it('maps a workflow pass failure to its first item and names every item in the message', () => {
    const pass = [item('transition:deal:check>returned', 'added', { section: 'workflows' }), item('transition:deal:returned>sent', 'added', { section: 'workflows' })];
    expect(applyErrorLines({
      'transition:deal:check>returned.transitions': ['這一趟包含的項目：transition:deal:check>returned、transition:deal:returned>sent：bad'],
    }, pass, labelOf)).toEqual([
      { label: 'L(transition:deal:check>returned)', messages: ['這一趟包含的項目：L(transition:deal:check>returned)、L(transition:deal:returned>sent)：bad'] },
    ]);
  });

  it('is empty without errors', () => {
    expect(applyErrorLines(undefined, items, labelOf)).toEqual([]);
  });
  // S5（套件更新設計 T43、複核 N5）：新模組與它的新簽核流程互相依賴，一起勾、一起取消，不會無限遞迴
  it('a new module and its new approval flow depending on each other toggle together and stay stable', () => {
    const pair = [
      item('module:complaint', 'added', { section: 'modules', module: 'complaint', dependsOn: ['approvalFlow:complaint-review'] }),
      item('approvalFlow:complaint-review', 'added', { section: 'approvalFlows', module: 'complaint', dependsOn: ['module:complaint'] }),
    ];
    const ticked = toggleItem(pair, new Set(), 'module:complaint', true);
    expect([...ticked.selected].sort()).toEqual(['approvalFlow:complaint-review', 'module:complaint']);
    const unticked = toggleItem(pair, ticked.selected, 'approvalFlow:complaint-review', false);
    expect([...unticked.selected]).toEqual([]);
    const again = toggleItem(pair, unticked.selected, 'approvalFlow:complaint-review', true);
    expect([...again.selected].sort()).toEqual(['approvalFlow:complaint-review', 'module:complaint']);
    expect(again.together).toEqual(['module:complaint']);
  });
});

// 走查 W2：屬性值看得懂——群組 id 換名稱、簽核關卡與簽核人摘要
describe('formatProp', () => {
  const ctx = {
    groupName: (id: number) => ({ 2: '業務', 3: '主管' } as Record<number, string>)[id],
    text: (n: I18nText) => n['zh-TW'] ?? '',
    mode: (m: string) => ({ any: '或簽', all: '會簽' } as Record<string, string>)[m] ?? m,
    source: (s: string) => ({ group: '群組', manager: '發起人的主管' } as Record<string, string>)[s] ?? s,
    fallback: '備援簽核人',
  };

  it('shows group names for group id lists, unknown ids as #id', () => {
    expect(formatProp('groups', [2, 3], ctx)).toBe('業務、主管');
    expect(formatProp('shareWith', [2, 9], ctx)).toBe('業務、#9');
  });

  it('lists approval stages by name and mode', () => {
    expect(formatProp('stages', [{ code: 'lead', name: { 'zh-TW': '主管審核' }, mode: 'any' }, { code: 'gm', name: {}, mode: 'all' }], ctx))
      .toBe('1. 主管審核（或簽）\n2. gm（會簽）');
  });

  it('summarises approvers per stage, fallback named', () => {
    expect(formatProp('approvers', { lead: { source: 'group', groups: [2] }, $fallback: { source: 'manager', level: 1 } }, ctx))
      .toBe('lead：群組 業務\n備援簽核人：發起人的主管');
  });

  it('falls back to the raw value for other properties', () => {
    expect(formatProp('priority', 2, ctx)).toBe('2');
    expect(formatProp('when', { op: 'and' }, ctx)).toBe('{"op":"and"}');
  });
});

// 共用主檔一起更新（套件更新設計 §9.6.3、T35、T37、T56）：主檔項目與應用程式項目在同一份勾選清單
describe('shared masters', () => {
  const master = (id: string, items: UpgradeItem[]) => ({
    id, name: { 'zh-TW': id }, fromVersion: '1.0.1', toVersion: '1.1.0', hasBaseline: true, groups: [], items, counts: {},
  });
  const preview = {
    items: [item('report:byFax', 'added', { section: 'reports', dependsOn: ['master:partner/field:partner.fax'] }), item('field:lead.x', 'newOnly')],
    masters: [
      master('partner', [item('field:partner.fax', 'added', { module: 'partner' }), item('field:partner.memo', 'newOnly', { module: 'partner', dependsOn: ['field:partner.fax'] })]),
      master('contact', [item('field:contact.fax', 'added', { module: 'contact', dependsOn: ['master:partner/field:partner.fax'] })]),
    ],
  };
  const all = selectableItems(preview);

  it('prefixes master items and their own dependsOn; cross-file dependsOn stays', () => {
    expect(all.map((i) => i.id)).toEqual([
      'report:byFax', 'field:lead.x', 'master:partner/field:partner.fax', 'master:partner/field:partner.memo', 'master:contact/field:contact.fax',
    ]);
    expect(all[3].dependsOn).toEqual(['master:partner/field:partner.fax']);
    expect(all[4].dependsOn).toEqual(['master:partner/field:partner.fax']);
  });

  it('ticking an app item pulls in the master item it depends on; unticking the master item drops its dependents', () => {
    const on = toggleItem(all, new Set(), 'report:byFax', true);
    expect([...on.selected].sort()).toEqual(['master:partner/field:partner.fax', 'report:byFax']);
    const off = toggleItem(all, new Set(['report:byFax', 'master:partner/field:partner.fax', 'master:partner/field:partner.memo', 'master:contact/field:contact.fax']),
      'master:partner/field:partner.fax', false);
    expect([...off.selected]).toEqual([]);
  });

  // T56：上次略過的主檔項目與依賴它的應用程式項目後端都標預設不勾，畫面照用
  it('default selection includes master items the backend selected and leaves skipped-before ones off', () => {
    const p = {
      items: [item('report:byFax', 'added', { dependsOn: ['master:partner/field:partner.fax'] })],
      masters: [master('partner', [item('field:partner.fax', 'skippedBefore'), item('field:partner.memo', 'newOnly', { defaultSelected: true })])],
    };
    expect([...defaultSelection(selectableItems(p))]).toEqual(['master:partner/field:partner.memo']);
  });

  it('splits the selection into items and masters, every listed master present', () => {
    expect(splitSelection(new Set(['report:byFax', 'master:partner/field:partner.fax']), ['partner', 'contact'])).toEqual({
      items: ['report:byFax'], masters: { partner: ['field:partner.fax'], contact: [] },
    });
  });

  it('maps masters.<id>.items.<item> errors to the master item and names its own missing items', () => {
    const labelOf = (i: UpgradeItem) => `L(${i.id})`;
    expect(applyErrorLines({
      'masters.partner.items.field:partner.memo': ['要一起套用：field:partner.fax'],
      'items.report:byFax': ['要一起套用：master:partner/field:partner.fax'],
      'masters.ghost': ['unknown'],
    }, all, labelOf)).toEqual([
      { label: 'L(master:partner/field:partner.memo)', messages: ['要一起套用：L(master:partner/field:partner.fax)'] },
      { label: 'L(report:byFax)', messages: ['要一起套用：L(master:partner/field:partner.fax)'] },
      { label: 'masters.ghost', messages: ['unknown'] },
    ]);
  });
});

describe('walkthrough G1, G3: masters in the header and the confirm dialog', () => {
  const it_ = (id: string, category: UpgradeItem['category'] = 'newOnly'): UpgradeItem => ({
    id, section: 'fields', module: 'm', label: null, category, defaultSelected: true, applicable: true, reason: null, dependsOn: [],
    properties: [], attachedPermissions: [],
  });
  const master = (id: string, items: UpgradeItem[], from = '1.0.0', to = '1.0.1') =>
    ({ id, name: { 'zh-TW': id }, fromVersion: from, toVersion: to, hasBaseline: true, groups: [], items, counts: {} });

  it('counts the app and each master separately, leaving out parts with nothing ticked', () => {
    const preview = { items: [it_('field:m.a'), it_('field:m.b', 'both')], masters: [master('partner', [it_('field:p.x')]), master('contact', [it_('field:c.y')])] };
    const sections = confirmSections(preview, new Set(['field:m.b', 'master:partner/field:p.x']));
    expect(sections.map((s) => [s.masterId, s.counts.total, s.counts.overwrite])).toEqual([[null, 1, 1], ['partner', 1, 0]]);
  });

  it('only masters move when the app version stays and a master is listed', () => {
    expect(mastersOnly({ fromVersion: '1.2.0', toVersion: '1.2.0', masters: [master('partner', [])] })).toBe(true);
    expect(mastersOnly({ fromVersion: '1.2.0', toVersion: '1.3.0', masters: [master('partner', [])] })).toBe(false);
    expect(mastersOnly({ fromVersion: '1.2.0', toVersion: '1.2.0', masters: [] })).toBe(false);
  });
});
