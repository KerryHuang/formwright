<script setup lang="ts">
import { onBeforeUnmount, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { kindIcon, type AddKind } from './designer-graph';

// 節點面板（§10.3、U63、J79）：只列簽核、條件、分支、結束。拖放用 pointer events（滑鼠、觸控、筆同一套），不用 HTML5 drag and drop；
// 拖超過 6px 才算拖，放開時回報螢幕座標（設計頁再問畫布落在哪、是不是線上）；沒拖動的點一下（含鍵盤 Enter／Space）回報 add，加在畫面中央。
// 不 import Vue Flow。`compact`（平板）只顯示圖示，名稱放在 aria-label 與滑鼠提示。
defineProps<{ kinds: { kind: AddKind; disabled: boolean; limit?: boolean }[]; compact: boolean }>();
const emit = defineEmits<{ drop: [kind: AddKind, clientX: number, clientY: number]; add: [kind: AddKind] }>();
const { t } = useI18n();
const DRAG_PX = 6;

const drag = ref<{ kind: AddKind; pointerId: number; sx: number; sy: number; x: number; y: number; moved: boolean } | null>(null);
/** 拖過之後瀏覽器仍會補一個 click，要吞掉，否則放下之後又在中央多加一個。 */
let swallowClick = false;

const label = (k: AddKind) => t(`approval.graph.kind.${k}`);
const title = (k: { kind: AddKind; limit?: boolean }) => (k.limit ? t('approval.designer.graph.paletteLimit') : label(k.kind));

function down(e: PointerEvent, k: { kind: AddKind; disabled: boolean }) {
  if (k.disabled || (e.pointerType === 'mouse' && e.button !== 0)) return;
  (e.currentTarget as Element).setPointerCapture?.(e.pointerId);
  drag.value = { kind: k.kind, pointerId: e.pointerId, sx: e.clientX, sy: e.clientY, x: e.clientX, y: e.clientY, moved: false };
}
function move(e: PointerEvent) {
  const d = drag.value;
  if (!d || d.pointerId !== e.pointerId) return;
  d.x = e.clientX;
  d.y = e.clientY;
  if (!d.moved && Math.hypot(d.x - d.sx, d.y - d.sy) > DRAG_PX) d.moved = true;
}
function up(e: PointerEvent) {
  const d = drag.value;
  if (!d || d.pointerId !== e.pointerId) return;
  drag.value = null;
  if (!d.moved) return; // 點一下交給 click（鍵盤也走 click）
  swallowClick = true;
  setTimeout(() => { swallowClick = false; }, 0);
  emit('drop', d.kind, e.clientX, e.clientY);
}
const cancel = () => { drag.value = null; };
function click(k: { kind: AddKind; disabled: boolean }) {
  if (swallowClick) { swallowClick = false; return; }
  if (!k.disabled) emit('add', k.kind);
}
onBeforeUnmount(cancel);
</script>

<template>
  <nav class="de-palette column no-wrap" :class="{ 'de-palette-compact': compact }" :aria-label="t('approval.designer.graph.palette')" data-testid="de-palette">
    <div v-if="!compact" class="text-caption text-muted q-px-xs q-pt-xs">{{ t('approval.designer.graph.palette') }}</div>
    <button
      v-for="k in kinds" :key="k.kind" type="button" class="de-palette-item" :disabled="k.disabled" :aria-label="label(k.kind)" :title="title(k)" :data-testid="`de-palette-${k.kind}`"
      @pointerdown="down($event, k)" @pointermove="move" @pointerup="up" @pointercancel="cancel" @click="click(k)"
    >
      <q-icon :name="kindIcon(k.kind)" size="20px" />
      <span v-if="!compact" class="de-palette-label">{{ label(k.kind) }}</span>
    </button>
    <div v-if="!compact" class="text-caption text-muted q-px-xs de-palette-hint">{{ t('approval.designer.graph.paletteHint') }}</div>
  </nav>
  <div v-if="drag?.moved" class="de-palette-ghost row items-center no-wrap" :style="{ left: `${drag.x}px`, top: `${drag.y}px` }" aria-hidden="true" data-testid="de-palette-ghost">
    <q-icon :name="kindIcon(drag.kind)" size="18px" class="q-mr-xs" />{{ label(drag.kind) }}
  </div>
</template>

<style scoped>
.de-palette { gap: 4px; padding: 4px; width: 112px; flex: none; border-right: 1px solid var(--t-border); background: var(--t-panel); overflow-y: auto; min-height: 0; }
.de-palette-compact { width: 52px; align-items: center; }
.de-palette-item {
  display: flex; align-items: center; gap: 6px; min-height: 40px; padding: 6px 8px; border: 1px solid var(--t-border); border-radius: var(--t-radius);
  background: var(--t-panel); color: var(--t-text); font: inherit; text-align: left; cursor: grab; touch-action: none; user-select: none;
}
.de-palette-compact .de-palette-item { justify-content: center; width: 44px; padding: 6px 0; }
.de-palette-item:hover:not(:disabled) { border-color: var(--t-primary); }
.de-palette-item:focus-visible { outline: 2px solid var(--t-primary); outline-offset: 1px; }
.de-palette-item:disabled { opacity: 0.45; cursor: not-allowed; }
.de-palette-label { font-size: 13px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.de-palette-hint { line-height: 1.3; }
.de-palette-ghost {
  position: fixed; z-index: 9000; transform: translate(-50%, -50%); pointer-events: none; padding: 6px 10px; font-size: 13px;
  border: 2px solid var(--t-primary); border-radius: var(--t-radius); background: var(--t-panel); color: var(--t-text); opacity: 0.9;
}
</style>
