import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { camelize, defineComponent, h, ref } from 'vue';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { Edge, Node } from '@vue-flow/core';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import DesignerCanvas from './DesignerCanvas.vue';
import type { DesignerEdgeData, DesignerNodeData } from './designer-graph';

// 真實的拖拉、縮放只能在瀏覽器驗；這裡換成會記錄呼叫的替身（其餘匯出仍用真的）。
const flowFns = vi.hoisted(() => ({ fitView: vi.fn(), setCenter: vi.fn(), zoomIn: vi.fn(), zoomOut: vi.fn(), screenToFlowCoordinate: vi.fn((p: { x: number; y: number }) => ({ x: p.x - 10, y: p.y - 20 })) }));
vi.mock('@vue-flow/core', async (orig) => ({ ...(await orig<typeof import('@vue-flow/core')>()), useVueFlow: () => ({ ...flowFns, viewport: ref({ x: 0, y: 0, zoom: 1 }), dimensions: ref({ width: 390, height: 600 }) }) }));

let lastAttrs: Record<string, unknown> = {};
const VueFlowStub = defineComponent({
  name: 'VueFlow', inheritAttrs: false,
  setup: (_, { attrs }) => () => (lastAttrs = attrs, h('div', { 'data-testid': 'vf', 'data-props': JSON.stringify(Object.fromEntries(Object.entries(attrs).filter(([k]) => !/^on[A-Z]/.test(k)).map(([k, v]) => [camelize(k), v]))), onClick: () => (attrs.onNodeClick as (e: unknown) => void)({ node: { id: 'a' } }), onDblclick: () => (attrs.onNodesInitialized as () => void)() })),
});
const nodes = [{ id: 'a', position: { x: 100, y: 50 }, data: {} }] as unknown as Node<DesignerNodeData>[];
function mountCanvas(draggable = true, initialNodes?: string[], more: Record<string, unknown> = {}) {
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  return mount(DesignerCanvas, {
    props: { canvasId: 'designer-po', nodes, edges: [] as Edge<DesignerEdgeData>[], draggable, initialNodes, ...more },
    global: { plugins: [[quasar.Quasar, { components: quasar }], i18n], stubs: { VueFlow: VueFlowStub } },
  });
}
const props = (w: ReturnType<typeof mountCanvas>) => JSON.parse(w.get('[data-testid="vf"]').attributes('data-props')!) as Record<string, unknown>;

beforeEach(() => { Object.values(flowFns).forEach((f) => f.mockClear()); });

describe('DesignerCanvas', () => {
  it('passes the designer settings (J75, U56 a): wheel zooms, drag pans, pinch zooms, snap 8px, no delete key, connecting by drag or click', () => {
    expect(props(mountCanvas())).toMatchObject({
      id: 'designer-po', nodesDraggable: true, nodesConnectable: true, connectOnClick: true, elementsSelectable: true, nodesFocusable: false, edgesFocusable: false, disableKeyboardA11y: true,
      zoomOnScroll: true, panOnScroll: false, zoomOnPinch: true, panOnDrag: true, snapToGrid: true, snapGrid: [8, 8], deleteKeyCode: null, fitViewOnInit: true,
    });
  });

  it('a read-only or phone canvas cannot drag nodes but can still pan, zoom and select', () => {
    expect(props(mountCanvas(false))).toMatchObject({ nodesDraggable: false, nodesConnectable: false, connectOnClick: false, panOnDrag: true, zoomOnPinch: true, elementsSelectable: true });
  });

  it('exposes fitAll and focusNode (the composable never touches Vue Flow)', () => {
    const w = mountCanvas();
    const api = w.vm as unknown as { fitAll(): void; focusNode(id: string, inset?: number): void };
    api.fitAll();
    expect(flowFns.fitView).toHaveBeenCalled();
    api.focusNode('a');
    expect(flowFns.setCenter).toHaveBeenLastCalledWith(100 + 84, 50 + 42, expect.objectContaining({ zoom: 1 }));
    api.focusNode('a', 360); // 右側抽屜擋住 360px：中心往右移半個抽屜寬，節點落在可見區中央
    expect(flowFns.setCenter).toHaveBeenLastCalledWith(100 + 84 + 180, 50 + 42, expect.anything());
    api.focusNode('missing');
    expect(flowFns.setCenter).toHaveBeenCalledTimes(2);
  });

  it('phone first screen: with initialNodes it fits only those nodes (zoom 0.6–1) once nodes are measured, instead of the whole graph', async () => {
    const w = mountCanvas(false, ['a', 'b', 'c']);
    expect(props(w)).toMatchObject({ fitViewOnInit: false });
    expect(flowFns.fitView).not.toHaveBeenCalled(); // 節點還沒量好
    await w.get('[data-testid="vf"]').trigger('dblclick'); // nodesInitialized
    expect(flowFns.fitView).toHaveBeenCalledWith({ nodes: ['a', 'b', 'c'], padding: 0.2, minZoom: 0.6, maxZoom: 1 });
    await w.get('[data-testid="vf"]').trigger('dblclick'); // 之後加節點再觸發也不重框
    expect(flowFns.fitView).toHaveBeenCalledTimes(1);
    (w.vm as unknown as { fitAll(): void }).fitAll(); // 「全圖」鈕不變
    expect(flowFns.fitView).toHaveBeenLastCalledWith({ padding: 0.12, duration: 200 });
  });

  it('without initialNodes (tablet and up) the whole graph is fitted on init as before', async () => {
    const w = mountCanvas();
    expect(props(w)).toMatchObject({ fitViewOnInit: true });
    await w.get('[data-testid="vf"]').trigger('dblclick');
    expect(flowFns.fitView).not.toHaveBeenCalled();
  });

  it('a node click reports the node id', async () => {
    const w = mountCanvas();
    await w.get('[data-testid="vf"]').trigger('click');
    expect(w.emitted('node-open')![0]).toEqual(['a']);
  });

  it('has a labelled group and zoom buttons', () => {
    const w = mountCanvas();
    expect(w.get('[data-testid="de-canvas"]').attributes('aria-label')).toBe('流程圖設計畫布');
    expect(w.get('[data-testid="de-zoom-in"]').attributes('aria-label')).toBe('放大');
  });

  it('isValidConnection asks canConnect (J68); a connection is reported, not drawn by the canvas', () => {
    const canConnect = vi.fn((from: string, to: string) => from === 'a' && to === 'b');
    const w = mountCanvas(true, undefined, { canConnect });
    const valid = lastAttrs['is-valid-connection'] as (c: { source: string; target: string }) => boolean;
    expect(valid({ source: 'a', target: 'b' })).toBe(true);
    expect(valid({ source: 'b', target: 'a' })).toBe(false);
    (lastAttrs.onConnect as (c: { source: string; target: string }) => void)({ source: 'a', target: 'b' });
    expect(w.emitted('connect')![0]).toEqual(['a', 'b']);
  });

  it('dropAt turns a pointer inside the canvas into a snapped node position and the edge under it; outside is null', () => {
    const w = mountCanvas(true, undefined, {
      nodes: [{ id: 'a', position: { x: 0, y: 0 }, data: {} }, { id: 'b', position: { x: 400, y: 0 }, data: {} }],
      edges: [{ id: 'ab', source: 'a', target: 'b', data: { bendX: 300 } }],
    });
    const el = w.get('[data-testid="de-canvas"]').element as HTMLElement;
    el.getBoundingClientRect = () => ({ left: 0, top: 0, right: 800, bottom: 600, width: 800, height: 600, x: 0, y: 0, toJSON: () => ({}) });
    const api = w.vm as unknown as { dropAt(x: number, y: number): { position: { x: number; y: number }; edgeId: string | null } | null; centerPosition(): { x: number; y: number } };
    // 替身把螢幕座標換成 (x-10, y-20)：(270, 62) → (260, 42)，落在 a→b 起點那段
    expect(api.dropAt(270, 62)).toEqual({ position: { x: 176, y: 0 }, edgeId: 'ab' });
    expect(api.dropAt(270, 400)!.edgeId).toBeNull();
    expect(api.dropAt(900, 100)).toBeNull();
    const c = api.centerPosition();
    expect(c.x % 8).toBe(0);
    expect(c.y % 8).toBe(0);
  });

  it('the plus shows on the selected edge only while editable', () => {
    const edges = [{ id: 'ab', source: 'a', target: 'b', type: 'designer', data: { bendX: 1, selected: true } }];
    const slot = (w: ReturnType<typeof mountCanvas>) => (w.vm as unknown as { $: { setupState: { showPlus(id: string, sel?: boolean): boolean } } }).$.setupState.showPlus;
    expect(slot(mountCanvas(true, undefined, { edges }))('ab', true)).toBe(true);
    expect(slot(mountCanvas(true, undefined, { edges }))('ab', false)).toBe(false);
    expect(slot(mountCanvas(false, undefined, { edges }))('ab', true)).toBe(false);
  });
});
