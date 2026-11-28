import { describe, expect, it } from 'vitest';
import { BUILTIN_LOCALES } from 'src/i18n/locale';
import { setLocaleInfos } from 'src/i18n/locale-settings';
import { appNameHints, groupMenu, systemLinks } from './menu-nav';

const item = (key: string, section?: string, closed?: boolean) =>
  ({ key, name: { 'zh-TW': key }, section: section === undefined ? undefined : { 'zh-TW': section }, closed });
const sec = (key: string, section: Record<string, string>) => ({ key, name: { 'zh-TW': key }, section });

describe('groupMenu 沒有繁中的分區', () => {
  it('以第一個有值的語系分組，標題照退回鏈', () => {
    const g = groupMenu([sec('a', { 'en-US': 'Sales' }), sec('b', { 'en-US': 'Sales' }), item('x')], 'th');
    expect(g.map((x) => x.title)).toEqual([undefined, 'Sales']);
    expect(g[1].items.map((i) => i.key)).toEqual(['a', 'b']);
  });
});

describe('groupMenu', () => {
  it('分區只有新裝的語系有值時，以那個語系分組', () => {
    setLocaleInfos([...BUILTIN_LOCALES, { code: 'ja', nativeName: '日本語', quasarLang: 'ja', fallbackCode: null, uiHash: '' }]);
    const g = groupMenu([sec('a', { ja: '販売' }), sec('b', { ja: '販売' })], 'ja');
    setLocaleInfos(BUILTIN_LOCALES);
    expect(g).toHaveLength(1);
    expect(g[0]!.key).toBe('販売');
    expect(g[0]!.title).toBe('販売');
  });
  it('puts unsectioned items first (title undefined), then sections by name, keeping /menu order inside a group', () => {
    const g = groupMenu([item('b', 'Z'), item('a1', 'A'), item('x'), item('a2', 'A'), item('y', '')], 'zh-TW');
    expect(g.map((x) => x.title)).toEqual([undefined, 'A', 'Z']);
    expect(g[0].items.map((i) => i.key)).toEqual(['x', 'y']);
    expect(g[1].items.map((i) => i.key)).toEqual(['a1', 'a2']);
  });
  it('pulls closed-app items into one trailing group, whatever their section', () => {
    const g = groupMenu([item('c1', 'A', true), item('x'), item('a1', 'A'), item('c2', undefined, true)], 'zh-TW');
    expect(g.map((x) => x.closed ?? false)).toEqual([false, false, true]);
    expect(g[2].items.map((i) => i.key)).toEqual(['c1', 'c2']);
    expect(g[1].items.map((i) => i.key)).toEqual(['a1']);
  });
  it('has no closed group when every app is open', () => expect(groupMenu([item('a')], 'zh-TW').some((x) => x.closed)).toBe(false));
  it('is empty for no items', () => expect(groupMenu([], 'zh-TW')).toEqual([]));
  it('shows the section in the current locale, falling back to zh-TW when it has no translation', () => {
    const g = groupMenu([sec('p', { 'zh-TW': '主檔', 'en-US': 'Master data' }), item('legacy', '庫存')], 'en-US');
    expect(g.map((x) => x.title)).toEqual(['Master data', '庫存']);
  });
  it('groups by the zh-TW section across apps, titled by the first module, and keeps zh-TW order in every locale', () => {
    const g = groupMenu([
      sec('s1', { 'zh-TW': '銷售', 'en-US': 'Sales' }),
      sec('m1', { 'zh-TW': '主檔', 'en-US': 'Master data' }),
      sec('m2', { 'zh-TW': '主檔', 'en-US': 'Masters' }),
      item('m3', '主檔'),
    ], 'en-US');
    expect(g.map((x) => x.key)).toEqual(['主檔', '銷售']);
    expect(g.map((x) => x.title)).toEqual(['Master data', 'Sales']);
    expect(g[0].items.map((i) => i.key)).toEqual(['m1', 'm2', 'm3']);
  });
});

describe('systemLinks', () => {
  const me = (isSystemAdmin: boolean, read: boolean) =>
    ({ isSystemAdmin, designer: { read, add: false, edit: false, delete: false } }) as never;
  it('shows nothing without permission or before me loads', () => {
    expect(systemLinks(me(false, false))).toEqual([]);
    expect(systemLinks(undefined)).toEqual([]);
  });
  it('system admins get users, groups, organization, dashboards, appearance, policies, mail, ops, audit, AI access, locales; designer readers get designer, conversions and demo data', () => {
    expect(systemLinks(me(true, false)).map((l) => l.to))
      .toEqual(['/system/users', '/system/groups', '/system/organization', '/system/dashboards', '/system/appearance', '/system/policies', '/system/locales',
        '/system/mail', '/system/ops', '/system/audit', '/system/ai-access']);
    expect(systemLinks(me(false, true)).map((l) => l.to)).not.toContain('/system/locales');
    expect(systemLinks(me(false, true)).map((l) => l.to)).not.toContain('/system/ops');
    expect(systemLinks(me(true, false)).find((l) => l.to === '/system/organization'))
      .toEqual({ to: '/system/organization', label: 'menu.organization', icon: 'account_tree', testid: 'organization-menu' });
    expect(systemLinks(me(false, true)).map((l) => l.testid)).not.toContain('organization-menu');
    expect(systemLinks(me(true, true)).map((l) => l.to)).toContain('/system/apps');
    expect(systemLinks(me(false, true)).map((l) => l.to)).not.toContain('/system/apps');
    expect(systemLinks(me(false, true)).map((l) => l.to)).toEqual(['/system/dynamic-modules', '/system/conversions', '/system/demo-data']);
    expect(systemLinks(me(true, true))).toHaveLength(15);
  });
  it('PAX-36 只有權限設定的帳號（GET me 的設計器只有隱含讀取）：有動態模組設計，沒有使用者、群組、應用程式', () => {
    const permOnly = { isSystemAdmin: false, designer: { read: true, add: false, edit: false, delete: false }, permissionAdmin: true } as never;
    const links = systemLinks(permOnly).map((l) => l.testid);
    expect(links).toContain('designer-menu');
    for (const id of ['users-menu', 'groups-menu', 'apps-menu']) expect(links).not.toContain(id);
  });
});

describe('appNameHints 同名模組加積木包名稱', () => {
  const app = (zh: string, en = zh) => ({ 'zh-TW': zh, 'en-US': en });
  const m = (key: string, name: Record<string, string>, appName?: Record<string, string>, section?: string) =>
    ({ key, name, appName, section: section ? { 'zh-TW': section } : undefined });

  it('跨分組同名時，兩邊都帶自己的積木包名稱', () => {
    const h = appNameHints([
      m('inv-ship', { 'zh-TW': '出貨單' }, app('進銷存'), '庫存'), m('mes-ship', { 'zh-TW': '出貨單' }, app('MES 生產製造'), '生產'),
    ], 'zh-TW');
    expect([...h]).toEqual([['inv-ship', '進銷存'], ['mes-ship', 'MES 生產製造']]);
  });
  it('一邊是自己設計的模組（沒有積木包）：有積木包的照樣加，另一個不加', () => {
    const h = appNameHints([m('inv-ship', { 'zh-TW': '出貨單' }, app('進銷存')), m('mine', { 'zh-TW': '出貨單' })], 'zh-TW');
    expect([...h]).toEqual([['inv-ship', '進銷存']]);
  });
  it('不同名不加', () => {
    expect(appNameHints([m('a', { 'zh-TW': '報價單' }, app('CRM')), m('b', { 'zh-TW': '訂單' }, app('CRM'))], 'zh-TW').size).toBe(0);
  });
  it('以目前語系解析後的名稱判斷，積木包名稱也用目前語系', () => {
    const items = [
      m('a', { 'zh-TW': '訂單', 'en-US': 'Orders' }, app('進銷存', 'Inventory')),
      m('b', { 'zh-TW': '銷售訂單', 'en-US': 'Orders' }, app('生產', 'MES')),
    ];
    expect(appNameHints(items, 'zh-TW').size).toBe(0);
    expect([...appNameHints(items, 'en-US')]).toEqual([['a', 'Inventory'], ['b', 'MES']]);
  });
});
