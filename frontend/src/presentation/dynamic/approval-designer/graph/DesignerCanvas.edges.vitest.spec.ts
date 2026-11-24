import { flushPromises, mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { nextTick } from 'vue';
import { beforeAll, describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import { useVueFlow, type Edge, type Node } from '@vue-flow/core';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import DesignerCanvas from './DesignerCanvas.vue';
import type { DesignerEdgeData, DesignerNodeData } from './designer-graph';

// 用真的 Vue Flow（不換替身）：畫布重設 edges 時，既有的線不能被 canConnect 當成「重複連線」丟掉（fg3e 走查：任何更新後線全消失）。
const data = (kind: string): DesignerNodeData => ({ kind, label: kind, summary: '', icon: 'x', width: 168, height: 84, skip: false, errors: 0, warnings: 0, selected: false, editable: true, ariaLabel: kind } as unknown as DesignerNodeData);
const nodes = (): Node<DesignerNodeData>[] => [
  { id: 'start', type: 'designer', position: { x: 0, y: 0 }, data: data('start') },
  { id: 'a', type: 'designer', position: { x: 300, y: 0 }, data: data('approval') },
  { id: 'end', type: 'designer', position: { x: 600, y: 0 }, data: data('end') },
];
const edges = (): Edge<DesignerEdgeData>[] => [
  { id: 'e1', source: 'start', target: 'a', type: 'designer', data: { bendX: 200 } as DesignerEdgeData },
  { id: 'e2', source: 'a', target: 'end', type: 'designer', data: { bendX: 500 } as DesignerEdgeData },
];
// 和 approval-graph-edit 的 canConnect 一樣：已經有同一條線就不能再連
const canConnect = (from: string, to: string) => !edges().some((e) => e.source === from && e.target === to);

describe('DesignerCanvas with the real Vue Flow', () => {
  beforeAll(() => { globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver; });
  it('keeps every existing edge when nodes and edges are replaced (canConnect only judges new connections)', async () => {
    const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
    const w = mount(DesignerCanvas, {
      props: { canvasId: 'edges-keep', nodes: nodes(), edges: edges(), draggable: true, canConnect },
      global: { plugins: [[quasar.Quasar, { components: quasar }], i18n] },
      attachTo: document.body,
    });
    await flushPromises();
    const flow = useVueFlow('edges-keep');
    expect(flow.edges.value.map((e) => e.id)).toEqual(['e1', 'e2']);
    await w.setProps({ nodes: nodes(), edges: edges() }); // 任何修改後 toVueFlow 都重算出新物件
    await nextTick();
    await flushPromises();
    expect(flow.edges.value.map((e) => e.id)).toEqual(['e1', 'e2']);
    w.unmount();
  });
});
