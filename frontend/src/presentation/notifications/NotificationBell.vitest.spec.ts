import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import enUS from 'shared/i18n/en-US/ui.json';
import th from 'shared/i18n/th/ui.json';
import vi_ from 'shared/i18n/vi/ui.json';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import { setLocaleInfos, setLocaleSettings } from 'src/i18n/locale-settings';
import { NOTIFICATION_KINDS } from 'src/domain/dynamic/task-types';
import { mockHttp, type Call } from 'src/test-utils/mock-http';
import NotificationBell from './NotificationBell.vue';

let calls: Call[];

const note = (id: number, o: Record<string, unknown> = {}) =>
  ({ id, kind: 'taskAssigned', title: `任務${id}`, link: `/tasks?open=${id + 4}`, read: false, createdTime: '2026-11-10T01:00:00', ...o });

async function mountBell(list: unknown, locale = 'zh-TW') {
  setActivePinia(createPinia());
  calls = mockHttp((c) => (c.url === '/notifications' ? list : {}));
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/:p(.*)*', component: { render: () => null } }] });
  await router.push('/');
  const push = vi.spyOn(router, 'push');
  const i18n = createI18n({ legacy: false, locale, messages: { 'zh-TW': zhTW, 'en-US': enUS } });
  const w = mount(NotificationBell, {
    attachTo: document.body,
    global: { plugins: [[quasar.Quasar, { components: quasar, directives: quasar }], createPinia(), router, i18n, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
  });
  await flushPromises();
  return { w, push };
}

const body = (sel: string) => document.body.querySelector<HTMLElement>(sel);

beforeEach(() => {
  document.body.innerHTML = '';
  setLocaleInfos(['zh-TW', 'en-US', 'th', 'vi'].map((code) => ({ code, nativeName: code, quasarLang: null, fallbackCode: null, uiHash: '' })) as never);
  setLocaleSettings({ defaultLocale: 'zh-TW' });
});

const notifyData = (o: Record<string, unknown> = {}) => ({
  v: 1, node: 'n1', read: true,
  template: { title: { 'zh-TW': '{title} 已過主管核准', 'en-US': '{title} approved' }, body: { 'zh-TW': '第一行\n第二行\n第三行 {f:21}', 'en-US': 'Line one' } },
  values: { title: { t: 'PO-0001' }, flow: { i18n: { 'zh-TW': '採購單' } }, node: { i18n: { 'zh-TW': '知會' } }, initiator: { t: '王小明' }, 'f:21': { t: '99' } },
  ...o,
});

describe('NotificationBell', () => {
  it('未讀數 3 時 badge 顯示 3，0 時沒有 badge', async () => {
    const a = await mountBell({ items: [note(9)], unreadCount: 3 });
    expect(a.w.get('[data-testid="notification-badge"]').text()).toBe('3');
    a.w.unmount();
    const b = await mountBell({ items: [], unreadCount: 0 });
    expect(b.w.find('[data-testid="notification-badge"]').exists()).toBe(false);
    b.w.unmount();
  });

  it('回應格式不對時當成空清單', async () => {
    const { w } = await mountBell('oops');
    expect(w.find('[data-testid="notification-badge"]').exists()).toBe(false);
    await w.get('[data-testid="notification-bell"]').trigger('click');
    await flushPromises();
    expect(body('[data-testid="notification-empty"]')).not.toBeNull();
    w.unmount();
  });

  it('點一則：POST 標為已讀，再前往它的連結', async () => {
    const { w, push } = await mountBell({ items: [note(9)], unreadCount: 1 });
    await w.get('[data-testid="notification-bell"]').trigger('click');
    await flushPromises();
    const item = body('[data-testid="notification-item-9"]')!;
    expect(item.textContent).toContain(zhTW.notifications.kind.taskAssigned);
    expect(item.textContent).toContain('任務9');
    item.click();
    await flushPromises();
    expect(calls.some((c) => c.method === 'post' && c.url === '/notifications/9/read')).toBe(true);
    expect(push).toHaveBeenCalledWith('/tasks?open=13');
    w.unmount();
  });

  it('每則通知顯示建立時間（YYYY-MM-DD HH:mm）', async () => {
    const { w } = await mountBell({ items: [note(9)], unreadCount: 1 });
    await w.get('[data-testid="notification-bell"]').trigger('click');
    await flushPromises();
    expect(body('[data-testid="notification-time-9"]')!.textContent).toBe(quasar.date.formatDate(new Date('2026-11-10T01:00:00'), 'YYYY-MM-DD HH:mm'));
    w.unmount();
  });

  it('沒有連結的通知只標為已讀；站外連結不前往', async () => {
    const { w, push } = await mountBell({
      items: [note(1, { link: '' }), note(2, { link: '//evil.example/x' }), note(3, { link: '/\\evil.example/x' })], unreadCount: 3,
    });
    await w.get('[data-testid="notification-bell"]').trigger('click');
    await flushPromises();
    body('[data-testid="notification-item-1"]')!.click();
    body('[data-testid="notification-item-2"]')!.click();
    body('[data-testid="notification-item-3"]')!.click();
    await flushPromises();
    expect(calls.filter((c) => c.method === 'post').map((c) => c.url))
      .toEqual(['/notifications/1/read', '/notifications/2/read', '/notifications/3/read']);
    expect(push).not.toHaveBeenCalled();
    w.unmount();
  });

  it('全部標為已讀：POST /notifications/read-all', async () => {
    const { w } = await mountBell({ items: [note(9)], unreadCount: 1 });
    await w.get('[data-testid="notification-bell"]').trigger('click');
    await flushPromises();
    body('[data-testid="notification-read-all"]')!.click();
    await flushPromises();
    expect(calls.some((c) => c.method === 'post' && c.url === '/notifications/read-all')).toBe(true);
    w.unmount();
  });

  it('已讀的通知不加粗；未知種類只顯示標題', async () => {
    const { w } = await mountBell({ items: [note(1, { read: true }), note(2, { kind: 'whatever' })], unreadCount: 1 });
    await w.get('[data-testid="notification-bell"]').trigger('click');
    await flushPromises();
    expect(body('[data-testid="notification-item-1"]')!.classList.contains('text-weight-bold')).toBe(false);
    expect(body('[data-testid="notification-item-2"]')!.classList.contains('text-weight-bold')).toBe(true);
    expect(body('[data-testid="notification-item-2"]')!.textContent).toContain('任務2');
    w.unmount();
  });

  it('approval kinds show their label before the title', async () => {
    const { w } = await mountBell({ items: [note(1, { kind: 'approvalPending', title: 'PO-0001' }), note(2, { kind: 'approvalApproved', title: 'PO-0002' })], unreadCount: 2 });
    await w.get('[data-testid="notification-bell"]').trigger('click');
    await flushPromises();
    expect(body('[data-testid="notification-item-1"]')!.textContent).toContain('待你簽核： PO-0001');
    expect(body('[data-testid="notification-item-2"]')!.textContent).toContain('已核准： PO-0002');
    w.unmount();
  });

  it('維運通知依 titleKey 照目前語系組標題；沒有 titleKey 用存檔時的標題', async () => {
    const { w } = await mountBell({ items: [
      note(1, { kind: 'opsRulePaused', title: '舊標題', titleKey: 'ops.title.rulePaused', titleParams: { ruleKey: 'follow', failures: 5 } }),
      note(2, { kind: 'opsIncident', title: '存檔時的標題' }),
    ], unreadCount: 2 });
    await w.get('[data-testid="notification-bell"]').trigger('click');
    await flushPromises();
    expect(body('[data-testid="notification-item-1"]')!.textContent).toContain('維運事件： 任務規則「follow」連續失敗 5 次，已自動暫停');
    expect(body('[data-testid="notification-item-1"]')!.textContent).not.toContain('舊標題');
    expect(body('[data-testid="notification-item-2"]')!.textContent).toContain('維運事件： 存檔時的標題');
    w.unmount();
  });

  it('approval notification link opens the record detail', async () => {
    const { w, push } = await mountBell({ items: [note(7, { kind: 'approvalPending', title: 'PO-0007', link: '/dynamic/m/5' })], unreadCount: 1 });
    await w.get('[data-testid="notification-bell"]').trigger('click');
    await flushPromises();
    body('[data-testid="notification-item-7"]')!.click();
    await flushPromises();
    expect(push).toHaveBeenCalledWith('/dynamic/m/5');
    w.unmount();
  });
});

describe('approvalNotify 通知', () => {
  const approvalNote = (id: number, data: unknown, o: Record<string, unknown> = {}) =>
    note(id, { kind: 'approvalNotify', title: 'PO-0001', link: '/dynamic/m/5', data, ...o });
  const openBell = async (w: Awaited<ReturnType<typeof mountBell>>['w']) => {
    await w.get('[data-testid="notification-bell"]').trigger('click');
    await flushPromises();
  };

  it('清單顯示組好的標題與內文前 2 行（超過截斷「…」）；不顯示第 3 行', async () => {
    const { w } = await mountBell({ items: [approvalNote(1, notifyData())], unreadCount: 1 });
    await openBell(w);
    const item = body('[data-testid="notification-item-1"]')!;
    expect(item.textContent).toContain('PO-0001 已過主管核准');
    const preview = body('[data-testid="notification-preview-1"]')!;
    expect(preview.textContent).toBe('第一行\n第二行…');
    expect(preview.textContent).not.toContain('第三行');
    w.unmount();
  });

  it('依讀者語系組字：英文讀者取英文範本', async () => {
    const { w } = await mountBell({ items: [approvalNote(1, notifyData())], unreadCount: 1 }, 'en-US');
    await openBell(w);
    expect(body('[data-testid="notification-item-1"]')!.textContent).toContain('PO-0001 approved');
    expect(body('[data-testid="notification-preview-1"]')!.textContent).toBe('Line one');
    w.unmount();
  });

  it('沒有 data（或格式不對）退回 title，點擊照舊前往連結', async () => {
    const { w, push } = await mountBell({ items: [approvalNote(1, null), approvalNote(2, { v: 9 })], unreadCount: 2 });
    await openBell(w);
    expect(body('[data-testid="notification-item-1"]')!.textContent).toContain('PO-0001');
    expect(body('[data-testid="notification-preview-1"]')).toBeNull();
    body('[data-testid="notification-item-1"]')!.click();
    await flushPromises();
    expect(push).toHaveBeenCalledWith('/dynamic/m/5');
    expect(body('[data-testid="notification-detail"]')).toBeNull();
    w.unmount();
  });

  it('點下去標已讀並開詳情（全文＋開啟單據），不直接前往；按「開啟單據」才前往', async () => {
    const { w, push } = await mountBell({ items: [approvalNote(1, notifyData())], unreadCount: 1 });
    await openBell(w);
    body('[data-testid="notification-item-1"]')!.click();
    await flushPromises();
    expect(calls.some((c) => c.method === 'post' && c.url === '/notifications/1/read')).toBe(true);
    expect(push).not.toHaveBeenCalled();
    const bodyEl = body('[data-testid="notification-detail-body"]')!;
    expect(bodyEl.textContent).toBe('第一行\n第二行\n第三行 99');
    expect(bodyEl.classList.contains('notification-detail-body')).toBe(true);
    body('[data-testid="notification-detail-open"]')!.click();
    await flushPromises();
    expect(push).toHaveBeenCalledWith('/dynamic/m/5');
    w.unmount();
  });

  it('read:false：詳情沒有「開啟單據」，改寫沒有讀取權；申請人與欄位顯示無權限', async () => {
    const data = notifyData({
      read: false,
      template: { title: { 'zh-TW': '{initiator} 的 {flow}' }, body: { 'zh-TW': '金額 {f:21}' } },
      values: { title: { t: '' }, flow: { i18n: { 'zh-TW': '採購單' } }, node: { i18n: { 'zh-TW': '知會' } }, initiator: { s: 'denied' } },
    });
    const { w, push } = await mountBell({ items: [approvalNote(1, data, { link: '' })], unreadCount: 1 });
    await openBell(w);
    expect(body('[data-testid="notification-item-1"]')!.textContent).toContain('（無權限） 的 採購單');
    body('[data-testid="notification-item-1"]')!.click();
    await flushPromises();
    expect(body('[data-testid="notification-detail-body"]')!.textContent).toBe('金額 （無權限）');
    expect(body('[data-testid="notification-detail-open"]')).toBeNull();
    expect(body('[data-testid="notification-detail-no-read"]')!.textContent).toContain('你沒有這張單的讀取權，需要細節請洽申請人');
    expect(push).not.toHaveBeenCalled();
    w.unmount();
  });

  it('read:false 即使 link 有值也不給「開啟單據」', async () => {
    const { w } = await mountBell({ items: [approvalNote(1, notifyData({ read: false }))], unreadCount: 1 });
    await openBell(w);
    body('[data-testid="notification-item-1"]')!.click();
    await flushPromises();
    expect(body('[data-testid="notification-detail-open"]')).toBeNull();
    w.unmount();
  });

  it('詳情先開，標已讀卡住或失敗也不影響開啟', async () => {
    const { w } = await mountBell({ items: [approvalNote(1, notifyData())], unreadCount: 1 });
    calls.length = 0;
    mockHttp((c) => (c.url === '/notifications' ? { items: [approvalNote(1, notifyData())], unreadCount: 1 } : new Promise(() => {})));
    await openBell(w);
    body('[data-testid="notification-item-1"]')!.click();
    await flushPromises();
    expect(body('[data-testid="notification-detail"]')).not.toBeNull();
    w.unmount();
  });

  it('退回路徑標題被遮蔽（空字串）時用替代字，不只顯示種類標籤', async () => {
    const { w } = await mountBell({ items: [approvalNote(1, null, { title: '' })], unreadCount: 1 });
    await openBell(w);
    expect(body('[data-testid="notification-item-1"]')!.textContent).toContain('簽核通知： （未顯示標題）');
    w.unmount();
  });

  it('範本裡的 HTML 只當文字，不會變成節點（不用 v-html）', async () => {
    const data = notifyData({ template: { title: { 'zh-TW': '<img src=x onerror=alert(1)>' }, body: { 'zh-TW': '<b>粗體</b>' } } });
    const { w } = await mountBell({ items: [approvalNote(1, data)], unreadCount: 1 });
    await openBell(w);
    expect(body('[data-testid="notification-item-1"] img')).toBeNull();
    body('[data-testid="notification-item-1"]')!.click();
    await flushPromises();
    expect(body('[data-testid="notification-detail-body"] b')).toBeNull();
    expect(body('[data-testid="notification-detail-body"]')!.textContent).toBe('<b>粗體</b>');
    w.unmount();
  });
});

describe('notification kinds', () => {
  const APPROVAL = [
    'approvalPending', 'approvalTransferred', 'approvalAddSigned', 'approvalRejected', 'approvalReturned', 'approvalApproved',
    'approvalWithdrawn', 'approvalCancelled', 'approvalReassignedAway', 'approvalRerouted', 'approvalOverdue', 'approvalAdminAlert', 'approvalNotify',
  ];
  it('NOTIFICATION_KINDS lists all 13 approval kinds the server writes', () => {
    for (const k of APPROVAL) expect(NOTIFICATION_KINDS as readonly string[]).toContain(k);
  });
  it('every NOTIFICATION_KINDS entry has a label in all four locales, and the foreign ones hold no Chinese', () => {
    const locales = { 'zh-TW': zhTW, 'en-US': enUS, th, vi: vi_ } as Record<string, { notifications: { kind: Record<string, string> } }>;
    for (const [name, m] of Object.entries(locales)) {
      for (const k of NOTIFICATION_KINDS) {
        const text = m.notifications.kind[k];
        expect(text, `${name} ${k}`).toBeTruthy();
        if (name !== 'zh-TW') expect(text, `${name} ${k}`).not.toMatch(/[\u4e00-\u9fff]/);
      }
    }
  });
});
