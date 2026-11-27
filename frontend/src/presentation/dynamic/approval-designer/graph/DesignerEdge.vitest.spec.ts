import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { beforeAll, describe, expect, it } from 'vitest';
import { defineComponent, h } from 'vue';
import { createI18n } from 'vue-i18n';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import DesignerEdge from './DesignerEdge.vue';
import source from './DesignerEdge.vue?raw';

beforeAll(() => { (SVGElement.prototype as unknown as { getBBox: () => object }).getBBox = () => ({ x: 0, y: 0, width: 40, height: 12 }); });
const base = { id: 'e1', sourceX: 100, sourceY: 50, targetX: 400, targetY: 140, sourcePosition: 'right', targetPosition: 'left' };
// EdgeLabelRenderer 要 Vue Flow 的 store 才能 teleport；這裡換成直接畫出內容
const Labels = defineComponent({ name: 'EdgeLabelRenderer', setup: (_, { slots }) => () => h('div', { 'data-testid': 'labels' }, slots.default?.()) });
const draw = (p: Record<string, unknown>) => mount({ components: { DesignerEdge }, props: ['p'], template: '<svg><DesignerEdge v-bind="p" /></svg>' }, {
  props: { p: { ...base, ...p } }, attachTo: document.body,
  global: { plugins: [[quasar.Quasar, { components: quasar }], createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } })], stubs: { EdgeLabelRenderer: Labels } },
});
const path = (w: ReturnType<typeof draw>) => w.get('path.vue-flow__edge-path').attributes('d');

describe('DesignerEdge', () => {
  it('designer edges bend where bendX says, not at the midpoint', () => {
    const mid = path(draw({ data: { bendX: 250, fullLabel: '' } }));
    const bent = path(draw({ data: { bendX: 350, fullLabel: '' } }));
    expect(bent).not.toBe(mid);
    expect(bent).toContain('350');
  });

  it('a truncated label keeps the full text in a title', () => {
    const w = draw({ label: '金額大…', data: { bendX: 350, fullLabel: '金額大於十萬' } });
    expect(w.find('title').text()).toBe('金額大於十萬');
    expect(w.text()).toContain('金額大…');
  });

  it('a short label has no title', () => {
    const w = draw({ label: '其他', data: { bendX: 350, fullLabel: '其他' } });
    expect(w.find('title').exists()).toBe(false);
    expect(w.text()).toContain('其他');
  });

  it('a labelled edge draws a background so the line does not cross the text', () => {
    expect(draw({ label: '其他', data: { bendX: 350, fullLabel: '其他' } }).find('.vue-flow__edge-textbg').exists()).toBe(true);
  });

  it('no plus unless the canvas asks for it (hover or selected, editable only)', () => {
    expect(draw({ data: { bendX: 350, fullLabel: '' } }).find('[data-testid="de-edge-plus"]').exists()).toBe(false);
  });

  it('the plus sits halfway between the bend and the target, and its menu inserts the chosen kind; a kind at the limit is disabled', async () => {
    const kinds = [{ kind: 'approval', disabled: false }, { kind: 'condition', disabled: false }, { kind: 'fork', disabled: true, limit: true }];
    const w = draw({ data: { bendX: 300, fullLabel: '' }, plus: true, insertKinds: kinds });
    const plus = w.get('[data-testid="de-edge-plus"]');
    expect(plus.attributes('aria-label')).toBe('在這條線插入節點');
    expect((plus.element.parentElement as HTMLElement).style.transform).toContain('translate(350px, 140px)');
    await plus.trigger('click');
    await new Promise((r) => setTimeout(r, 0));
    const fork = document.querySelector('[data-testid="de-insert-fork"]')!;
    expect(fork.getAttribute('aria-disabled')).toBe('true');
    expect(fork.textContent).toContain('已達簽核節點數上限');
    document.querySelector('[data-testid="de-insert-condition"]')!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    const edge = w.findComponent(DesignerEdge);
    expect(edge.emitted('insert')).toEqual([['condition']]);
    w.unmount();
  });

  // #109：畫布縮小時「＋」跟著縮小（820 寬約 10px），透明點擊區在螢幕上固定 44px，看得到的按鈕大小不變。
  it.each([0.25, 0.5, 1, 1.5])('the touch area is 44 screen px at zoom %s, and tapping it opens the menu once', async (zoom) => {
    const w = draw({ data: { bendX: 300, fullLabel: '' }, plus: true, zoom, insertKinds: [{ kind: 'approval', disabled: false }] });
    const hit = w.get('[data-testid="de-edge-plus-hit"]');
    const style = (hit.element as HTMLElement).style;
    expect(parseFloat(style.width) * zoom).toBeCloseTo(44, 3);
    expect(parseFloat(style.height) * zoom).toBeCloseTo(44, 3);
    expect(hit.element.closest('[data-testid="de-edge-plus"]')).not.toBeNull();
    // 一次觸控的事件順序（瀏覽器在 touchend 之後才補發 mousedown、mouseup、click）
    for (const type of ['touchstart', 'touchend', 'mousedown', 'mouseup', 'click']) await hit.trigger(type);
    await new Promise((r) => setTimeout(r, 400));
    expect(document.querySelectorAll('[data-testid="de-insert-approval"]')).toHaveLength(1);
    w.unmount();
  });

  // #109 R2：透明點擊區不得攔住節點與把手。節點層在 Vue Flow 裡排在線標籤層後面（見 DesignerCanvas.edges spec），
  // 「＋」這一層只要不設 z-index，節點與把手就一律在它上面。jsdom 不套元件樣式，所以直接檢查樣式原文。
  it('nothing in the plus raises it above the nodes, so handles and nodes win the hit test (#109)', () => {
    const style = source.slice(source.indexOf('<style'));
    expect(style).toContain('.de-plus {');
    expect(style).not.toContain('z-index');
    const w = draw({ data: { bendX: 300, fullLabel: '' }, plus: true, zoom: 0.25, insertKinds: [] });
    for (let el: HTMLElement | null = w.get('[data-testid="de-edge-plus-hit"]').element as HTMLElement; el; el = el.parentElement) expect(el.style.zIndex).toBe('');
    w.unmount();
  });

  // 終點把手中心在 targetX 右邊 7；「＋」離終點節點至少半個欄距（28），一般排版下看得到的圓鈕不被把手蓋住。
  it.each([[196, 217, 196], [300, 321, 300], [300, 400, 350]])('bend %s, target %s: the plus sits at x=%s, clear of the target handle (#109)', (bendX, targetX, x) => {
    const w = draw({ targetX, data: { bendX, fullLabel: '' }, plus: true, insertKinds: [] });
    expect((w.get('[data-testid="de-edge-plus"]').element.parentElement as HTMLElement).style.transform).toContain(`translate(${x}px, 140px)`);
    w.unmount();
  });

  it.each(['Enter', ' '])('the plus is still a focusable button that opens with the keyboard (%j) (#109)', async (key) => {
    const w = draw({ data: { bendX: 300, fullLabel: '' }, plus: true, zoom: 0.25, insertKinds: [{ kind: 'approval', disabled: false }] });
    const plus = w.get('[data-testid="de-edge-plus"]');
    expect(plus.element.tagName.toLowerCase()).toBe('button');
    const code = key === 'Enter' ? 13 : 32;
    await plus.trigger('keydown', { key, keyCode: code });
    await plus.trigger('keyup', { key, keyCode: code });
    await new Promise((r) => setTimeout(r, 400));
    expect(document.querySelector('[data-testid="de-insert-approval"]')).not.toBeNull();
    w.unmount();
  });
});
