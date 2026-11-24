<script setup lang="ts">
import '@vue-flow/core/dist/style.css';
import { VueFlow, useVueFlow, type Connection, type Edge, type Node } from '@vue-flow/core';
import { computed, onBeforeUnmount, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { NODE_H, NODE_W } from 'src/domain/dynamic/approval-graph-layout';
import { initialMinZoom } from './initial-fit';
import DesignerEdge from './DesignerEdge.vue';
import DesignerNode from './DesignerNode.vue';
import { dropPosition, edgeAt, freeSpot, type AddKind, type DesignerEdgeData, type DesignerNodeData } from './designer-graph';

// 設計頁的畫布（J69、J75）。Vue Flow 只在 DesignerCanvas / DesignerNode / DesignerEdge 出現，整個設計頁是路由層動態載入的獨立塊。
// 鍵盤：Vue Flow 內建的方向鍵移動只發 nodesChange、不發 nodeDragStop，不會寫回工作副本，所以整組關掉（disable-keyboard-a11y）；
// 外層 wrapper 不再可聚焦（每個節點只剩 DesignerNode 內層一個 Tab 停點，Enter／Space 開面板），也不再帶「press delete」的描述。鍵盤移動節點 fg3e 仍不做（要進復原堆疊、套 snap）；刪除鍵由設計頁處理。
// 平移到某節點、fitView 一律由這裡以 defineExpose 提供（use-graph-designer 不碰 Vue Flow）。
// 結構編輯（fg3e，J75）：`draggable`（平板以上、有編輯權）同時開拖曳、連線（拖把手或 connectOnClick）與線上「＋」；
// 能不能連由呼叫端的 `canConnect`（J68）決定，連好、插入都只回報事件，畫布不自己改圖。
const props = defineProps<{
  canvasId: string; nodes: Node<DesignerNodeData>[]; edges: Edge<DesignerEdgeData>[]; draggable: boolean; initialNodes?: string[];
  canConnect?: (from: string, to: string) => boolean; insertKinds?: { kind: AddKind; disabled: boolean }[];
}>();
const emit = defineEmits<{
  'node-open': [id: string]; 'edge-open': [id: string]; 'pane-click': []; 'node-move': [id: string, to: { x: number; y: number }];
  connect: [from: string, to: string]; 'edge-insert': [edgeId: string, kind: AddKind];
}>();
const { t } = useI18n();
const flow = useVueFlow(props.canvasId);
const GRID = 8;
const FIT_PADDING = 0.12;

// 手機的首屏（J66，使用者 11-16 確認）：整張圖縮進 390px 只剩約 38px 寬的節點，所以改成只框住起點附近的 `initialNodes`（縮放 0.6–1，聚焦的節點在 0.6 下比畫布寬時往下降，不被左右切掉）；
// 節點量好尺寸後（nodesInitialized）才能 fitView，只做第一次。「全圖」鈕、平板以上不受影響。
let fittedInitial = false;
function fitInitial() {
  if (fittedInitial || !props.initialNodes?.length) return;
  fittedInitial = true;
  const focused = props.nodes.filter((n) => props.initialNodes?.includes(n.id));
  void flow.fitView({ nodes: props.initialNodes, padding: 0.2, minZoom: initialMinZoom(focused, NODE_W, flow.dimensions.value.width), maxZoom: 1 });
}
const fitAll = () => { void flow.fitView({ padding: FIT_PADDING, duration: 200 }); };
/** 把節點移到畫布可見區中央；`rightInset` 是右側被抽屜擋住的寬度（平板的屬性抽屜）。 */
function focusNode(id: string, rightInset = 0) {
  const n = props.nodes.find((x) => x.id === id);
  if (!n) return;
  const zoom = Math.max(flow.viewport.value.zoom, 0.6);
  void flow.setCenter(n.position.x + NODE_W / 2 + rightInset / 2 / zoom, n.position.y + NODE_H / 2, { zoom, duration: 200 });
}
const root = ref<HTMLElement | null>(null);
/** 節點面板放開指標的位置（螢幕座標）→ 新節點左上角與落在哪條線上；不在畫布內回 null。 */
function dropAt(clientX: number, clientY: number): { position: { x: number; y: number }; edgeId: string | null } | null {
  const r = root.value?.getBoundingClientRect();
  if (!r || clientX < r.left || clientX > r.right || clientY < r.top || clientY > r.bottom) return null;
  const p = flow.screenToFlowCoordinate({ x: clientX, y: clientY });
  return { position: dropPosition(p), edgeId: edgeAt(props.nodes, props.edges, p) };
}
/** 點一下節點面板（或鍵盤）：加在畫面中央，中央有節點就往下找空位。 */
function centerPosition(): { x: number; y: number } {
  const r = root.value?.getBoundingClientRect();
  const p = r ? flow.screenToFlowCoordinate({ x: r.left + r.width / 2, y: r.top + r.height / 2 }) : { x: 0, y: 0 };
  return freeSpot(Object.fromEntries(props.nodes.map((n) => [n.id, n.position])), dropPosition(p));
}
defineExpose({ fitAll, focusNode, dropAt, centerPosition });

// 線上「＋」：選取的線一直顯示（觸控）；滑鼠移到線上顯示，離開後稍等再收（讓滑鼠移得到「＋」上），選單開著時不收。
const hovered = ref<string | null>(null);
const menuEdge = ref<string | null>(null);
let leaveTimer: ReturnType<typeof setTimeout> | undefined;
function hover(id: string, on: boolean) {
  clearTimeout(leaveTimer);
  if (on) hovered.value = id;
  else leaveTimer = setTimeout(() => { if (hovered.value === id) hovered.value = null; }, 400);
}
onBeforeUnmount(() => clearTimeout(leaveTimer));
const showPlus = (id: string, selected: boolean | undefined) => editable.value && (!!selected || hovered.value === id || menuEdge.value === id);
const validConnection = (c: Connection) => !!props.canConnect && props.canConnect(c.source, c.target);
const zoomIn = () => { void flow.zoomIn({ duration: 150 }); };
const zoomOut = () => { void flow.zoomOut({ duration: 150 }); };
const editable = computed(() => props.draggable);
</script>

<template>
  <div ref="root" class="de-canvas" role="group" :aria-label="t('approval.designer.graph.canvasLabel')" data-testid="de-canvas">
    <VueFlow
      :id="canvasId" :nodes="nodes" :edges="edges" :min-zoom="0.2" :max-zoom="1.5" :fit-view-on-init="!initialNodes?.length" :snap-to-grid="true" :snap-grid="[GRID, GRID]"
      :nodes-draggable="editable" :nodes-connectable="editable" :connect-on-click="editable" :is-valid-connection="validConnection" :elements-selectable="true" :nodes-focusable="false" :edges-focusable="false" :disable-keyboard-a11y="true"
      :zoom-on-scroll="true" :pan-on-scroll="false" :zoom-on-pinch="true" :pan-on-drag="true" :zoom-on-double-click="false" :delete-key-code="null"
      :selection-key-code="null" :multi-selection-key-code="null"
      @node-click="(e) => emit('node-open', e.node.id)" @edge-click="(e) => emit('edge-open', e.edge.id)" @pane-click="emit('pane-click')" @nodes-initialized="fitInitial"
      @node-drag-stop="(e) => emit('node-move', e.node.id, { x: e.node.position.x, y: e.node.position.y })"
      @connect="(c: Connection) => emit('connect', c.source, c.target)" @edge-mouse-enter="(e) => hover(e.edge.id, true)" @edge-mouse-leave="(e) => hover(e.edge.id, false)"
    >
      <template #node-designer="p"><DesignerNode :data="p.data" @open="emit('node-open', p.id)" /></template>
      <template #edge-designer="p">
        <DesignerEdge
          v-bind="p" :plus="showPlus(p.id, p.data?.selected)" :insert-kinds="insertKinds" @insert="(k: AddKind) => emit('edge-insert', p.id, k)"
          @hover="(on: boolean) => hover(p.id, on)" @menu="(open: boolean) => (menuEdge = open ? p.id : menuEdge === p.id ? null : menuEdge)"
        />
      </template>
    </VueFlow>
    <div class="de-zoom">
      <q-btn dense flat round size="sm" icon="add" :aria-label="t('approval.graph.zoomIn')" :title="t('approval.graph.zoomIn')" data-testid="de-zoom-in" @click="zoomIn" />
      <q-btn dense flat round size="sm" icon="remove" :aria-label="t('approval.graph.zoomOut')" :title="t('approval.graph.zoomOut')" data-testid="de-zoom-out" @click="zoomOut" />
    </div>
  </div>
</template>

<style>
/* 不載入 Vue Flow 的 theme-default.css；線、文字與底色都用主題 token，深色主題一併生效。 */
.de-canvas { position: relative; width: 100%; height: 100%; min-height: 0; }
.de-canvas .vue-flow { background: transparent; }
.de-canvas .vue-flow__pane.draggable { cursor: grab; }
.de-canvas .vue-flow__node { pointer-events: auto; }
.de-canvas .vue-flow__edge { cursor: pointer; }
.de-canvas .vue-flow__edge-path { stroke: var(--t-muted); stroke-width: 1.5; fill: none; }
.de-canvas .de-edge-selected .vue-flow__edge-path { stroke: var(--t-primary); stroke-width: 2.5; }
.de-canvas .de-edge-warn .vue-flow__edge-path { stroke: var(--t-warning); stroke-width: 2.5; }
.de-canvas .de-edge-error .vue-flow__edge-path { stroke: var(--t-negative); stroke-width: 2.5; }
.de-canvas .de-edge-trial-path:not(.de-edge-error):not(.de-edge-warn) .vue-flow__edge-path { stroke: var(--t-primary); stroke-width: 3; }
.de-canvas .de-edge-trial-off { opacity: 0.3; }
.de-canvas .vue-flow__edge-text { fill: var(--t-text); font-size: 11px; }
.de-canvas .de-edge-hint .vue-flow__edge-text { fill: var(--t-muted); font-style: italic; }
.de-canvas .de-edge-error .vue-flow__edge-text { fill: var(--t-negative); }
.de-canvas .vue-flow__edge-textbg { fill: var(--t-panel); }
.de-canvas .de-zoom {
  position: absolute; top: 4px; right: 4px; z-index: 5; display: flex; gap: 2px; padding: 2px;
  background: var(--t-panel); border: 1px solid var(--t-border); border-radius: var(--t-radius);
}
</style>
