import { flushPromises } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import type { CardOptions, DashboardCard, DashboardDetail, DashboardList } from 'src/domain/dynamic/dashboard-types';
import AddCardDialog from 'src/presentation/dashboards/AddCardDialog.vue';
import CardOptionsDialog from 'src/presentation/dashboards/CardOptionsDialog.vue';
import { http } from 'src/services/api/http';
import { card, click, detail, mountPage, okResult, q } from './dashboard-test-kit';
import DashboardPage from './DashboardPage.vue';

/** #122：卡片查詢要等版面存好。這裡的假伺服器照「已存的版面」回查詢結果，每筆 PUT 各自放行或失敗。 */

const LIST: DashboardList = { assigned: [], mine: [{ id: 3, name: { 'zh-TW': '我的看板' } }], sharedReports: [] };

/** 後端序列化的形狀：欄位順序照 CardOptions record，null 不寫出。 */
const OPTION_ORDER = ['range', 'sortKey', 'sortDesc', 'topN', 'cumulative', 'comparePeriods', 'linkDashboardId'] as const;
const asServer = (c: DashboardCard): DashboardCard => ({
  ...c,
  options: Object.fromEntries(OPTION_ORDER.filter((k) => c.options[k] != null).map((k) => [k, c.options[k]])) as unknown as CardOptions,
});

async function settle() {
  await flushPromises();
  await new Promise((r) => setTimeout(r, 20));
  await flushPromises();
}

async function mountWithServer() {
  const initial = detail(3, { kind: 'mine', layout: { cards: [card('c1')] } });
  const { w, calls } = await mountPage({ list: LIST, details: { 3: initial } }, { page: DashboardPage, route: '/dashboards/3' });
  let saved: DashboardDetail = initial;
  const puts: ((ok: boolean) => void)[] = [];
  const heldRuns = new Map<string, Promise<void>>();
  const inner = http.defaults.adapter as (c: unknown) => Promise<never>;
  http.defaults.adapter = async (config) => {
    const url = config.url!;
    const method = config.method!.toUpperCase();
    const ok = (data: unknown) => ({ data, status: 200, statusText: 'OK', headers: {}, config });
    const fail = (status: number) => Promise.reject({ response: { status, data: {} }, isAxiosError: true });
    const run = url.match(/\/cards\/([^/]+)\/run$/);
    if (!run && !(url === '/dashboards/3' && method === 'GET') && !url.endsWith('/layout')) return inner(config);
    calls.push({ method, url, body: undefined, params: undefined });
    if (url.endsWith('/layout')) {
      const body = JSON.parse(config.data as string) as { layout: DashboardDetail['layout'] };
      if (!(await new Promise<boolean>((res) => puts.push(res)))) return fail(500);
      saved = { ...saved, layout: { cards: body.layout.cards.map(asServer) } };
      return ok(saved);
    }
    if (!run) return ok(saved);
    // 結果在請求發起時就依已存版面決定，晚回應也不會變成新設定
    const found = saved.layout.cards.find((c) => c.id === run[1]);
    const hold = heldRuns.get(run[1]);
    heldRuns.delete(run[1]);
    await hold;
    if (!found) return fail(404);
    const label = `報表${found.reportId}／前${found.options.topN ?? '全部'}`;
    return ok(okResult({ groups: [{ groupKeys: ['a'], groupLabels: [label], values: { m: 3 } }] } as never));
  };
  await click('dashboard-edit-toggle');
  return {
    w,
    runs: (id: string) => calls.filter((c) => c.url.endsWith(`/cards/${id}/run`)).length,
    text: (id: string) => q(`card-${id}`)!.textContent!,
    loading: (id: string) => q(`card-${id}`)!.querySelector('.q-spinner') !== null,
    puts: () => puts.length,
    /** 放行（或讓它失敗）第 n 筆 PUT；PUT 是排隊送出的，前一筆完成才有下一筆。 */
    async put(n: number, ok = true) { expect(puts.length).toBeGreaterThan(n); puts[n](ok); await settle(); },
    holdNextRun(id: string) {
      let release!: () => void;
      heldRuns.set(id, new Promise<void>((res) => { release = res; }));
      return async () => { release(); await settle(); };
    },
    async add(reportId: number) { w.findComponent(AddCardDialog).vm.$emit('add', reportId, 'table'); await settle(); },
    async options(id: string, topN: number | null, title: string | null = null) {
      await click(`card-settings-${id}`);
      // 欄位順序與 null 照 CardOptionsDialog 送出的樣子
      const options = { sortDesc: false, cumulative: false, range: null, topN, sortKey: null, comparePeriods: null, linkDashboardId: null };
      w.findComponent(CardOptionsDialog).vm.$emit('save', { title: title ? { 'zh-TW': title } : null, viz: 'table', options });
      await settle();
    },
    async remove(id: string) { await click(`card-remove-${id}`); await settle(); },
  };
}

afterEach(() => { localStorage.clear(); document.body.innerHTML = ''; });

describe('card runs wait for the layout save (#122)', () => {
  it('a newly added card is not run before the layout is saved, then exactly once', async () => {
    const s = await mountWithServer();
    await s.add(1);
    expect(s.runs('c2')).toBe(0);
    expect(s.loading('c2')).toBe(true);
    await s.put(0);
    expect(s.runs('c2')).toBe(1);
    expect(s.text('c2')).toContain('報表1／前全部');
  });

  it('each settings save runs the changed card exactly once with the new settings; untouched cards are not rerun', async () => {
    const s = await mountWithServer();
    await s.add(1);
    await s.put(0);
    const before = { c1: s.runs('c1'), c2: s.runs('c2') };
    for (const [n, topN] of [5, 7].entries()) {
      await s.options('c1', topN);
      expect(s.runs('c1')).toBe(before.c1 + n);
      await s.put(n + 1);
      expect(s.runs('c1')).toBe(before.c1 + n + 1);
      expect(s.text('c1')).toContain(`報表1／前${topN}`);
    }
    expect(s.runs('c2')).toBe(before.c2);
  });

  it('changing only the title does not rerun the card', async () => {
    const s = await mountWithServer();
    await s.options('c1', null);
    await s.put(0);
    const before = s.runs('c1');
    await s.options('c1', null, '新標題');
    await s.put(1);
    expect(s.runs('c1')).toBe(before);
    expect(s.text('c1')).toContain('新標題');
  });

  it('a failed add removes the new card without ever running it', async () => {
    const s = await mountWithServer();
    await s.add(1);
    await s.put(0, false);
    expect(q('dashboard-save-error')).not.toBeNull();
    expect(q('card-c2')).toBeNull();
    expect(s.runs('c2')).toBe(0);
  });

  it('a failed settings save goes back to the saved settings and stops loading', async () => {
    const s = await mountWithServer();
    await s.options('c1', 5);
    await s.put(0, false);
    expect(q('dashboard-save-error')).not.toBeNull();
    expect(s.loading('c1')).toBe(false);
    expect(s.text('c1')).toContain('報表1／前全部');
    expect(q('card-c1')!.querySelector('[data-testid="card-topn"]')).toBeNull();
  });

  it('a late run of a removed card is not reused by a new card with the same id', async () => {
    const s = await mountWithServer();
    await s.add(1);
    const releaseOld = s.holdNextRun('c2');
    await s.put(0);
    expect(s.runs('c2')).toBe(1);
    await s.remove('c2');
    await s.add(2);
    await s.put(1);
    await s.put(2);
    expect(s.runs('c2')).toBe(2);
    expect(s.text('c2')).toContain('報表2／前全部');
    await releaseOld();
    expect(s.text('c2')).toContain('報表2／前全部');
    expect(s.text('c2')).not.toContain('報表1／');
  });

  it('add then change settings: nothing runs between the two saves, then once with the last settings', async () => {
    const s = await mountWithServer();
    await s.add(1);
    await s.options('c2', 5);
    await s.put(0);
    expect(s.puts()).toBe(2);
    expect(s.runs('c2')).toBe(0);
    expect(s.loading('c2')).toBe(true);
    await s.put(1);
    expect(s.runs('c2')).toBe(1);
    expect(s.text('c2')).toContain('報表1／前5');
  });

  it('first save fails and the second succeeds: runs once with the second layout', async () => {
    const s = await mountWithServer();
    await s.options('c1', 5);
    const before = s.runs('c1');
    await s.options('c1', 7);
    await s.put(0, false);
    expect(s.runs('c1')).toBe(before);
    await s.put(1);
    expect(s.runs('c1')).toBe(before + 1);
    expect(s.text('c1')).toContain('報表1／前7');
  });

  it('first save (add) fails and the second succeeds: the new card stays and runs once', async () => {
    const s = await mountWithServer();
    await s.add(1);
    await s.options('c2', 7);
    await s.put(0, false);
    expect(s.runs('c2')).toBe(0);
    await s.put(1);
    expect(s.runs('c2')).toBe(1);
    expect(s.text('c2')).toContain('報表1／前7');
  });

  it('first save succeeds and the second fails: goes back to the first layout and runs it once', async () => {
    const s = await mountWithServer();
    await s.add(1);
    await s.options('c2', 5);
    await s.put(0);
    expect(s.runs('c2')).toBe(0);
    await s.put(1, false);
    expect(q('dashboard-save-error')).not.toBeNull();
    expect(s.runs('c2')).toBe(1);
    expect(s.loading('c2')).toBe(false);
    expect(s.text('c2')).toContain('報表1／前全部');
  });

  it('a card removed while its add is still saving is never run', async () => {
    const s = await mountWithServer();
    await s.add(1);
    await s.remove('c2');
    await s.put(0);
    expect(s.runs('c2')).toBe(0);
    await s.put(1);
    expect(q('card-c2')).toBeNull();
    expect(s.runs('c2')).toBe(0);
  });
});
