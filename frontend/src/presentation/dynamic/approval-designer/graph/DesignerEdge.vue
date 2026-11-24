<script setup lang="ts">
import { BaseEdge, EdgeLabelRenderer, getSmoothStepPath, type EdgeProps } from '@vue-flow/core';
import { computed, h } from 'vue';
import { useI18n } from 'vue-i18n';
import { kindIcon, type AddKind, type DesignerEdgeData } from './designer-graph';

// 設計頁的線（J74）：內建 smoothstep，轉折點由 designer-graph 依座標算好（data.bendX），與進度圖同一條規則。
// 標籤被截斷時，完整文字放進 SVG <title>（滑鼠移上去可看）。
// 線上「＋」（fg3e，§10.3）：滑鼠移到線上或選取線（觸控）時出現，放在轉折欄到終點那一段的中點（兄弟線共用起點那段，終點那段每條線各自一段）；
// 點了選型別插入。顯示與否由畫布決定（`plus`），選單開著時回報 `menu`，畫布才不會在滑鼠離開線時把它收掉。
const props = defineProps<EdgeProps<DesignerEdgeData> & { plus?: boolean; insertKinds?: { kind: AddKind; disabled: boolean; limit?: boolean }[] }>();
const emit = defineEmits<{ insert: [kind: AddKind]; menu: [open: boolean]; hover: [on: boolean] }>();
const { t } = useI18n();
const geometry = computed(() => getSmoothStepPath({
  sourceX: props.sourceX, sourceY: props.sourceY, sourcePosition: props.sourcePosition,
  targetX: props.targetX, targetY: props.targetY, targetPosition: props.targetPosition, centerX: props.data?.bendX,
}));
const short = computed(() => (typeof props.label === 'string' ? props.label : ''));
const label = computed(() => {
  const full = props.data?.fullLabel ?? short.value;
  return short.value && full !== short.value ? () => [h('title', full), short.value] : short.value || undefined;
});
const plusAt = computed(() => {
  const bx = props.data?.bendX ?? geometry.value[1];
  return { x: (bx + props.targetX) / 2, y: props.targetY };
});
</script>

<template>
  <BaseEdge :id="id" :path="geometry[0]" :label-x="geometry[1]" :label-y="geometry[2]" :label="label" :interaction-width="20" :label-show-bg="true" :label-bg-padding="[4, 2]" :label-bg-border-radius="3" />
  <EdgeLabelRenderer v-if="plus">
    <div
      class="de-plus nodrag nopan" :style="{ transform: `translate(-50%, -50%) translate(${plusAt.x}px, ${plusAt.y}px)` }"
      @mouseenter="emit('hover', true)" @mouseleave="emit('hover', false)"
    >
      <q-btn round unelevated size="xs" color="primary" icon="add" :aria-label="t('approval.designer.graph.insert')" :title="t('approval.designer.graph.insert')" data-testid="de-edge-plus">
        <q-menu @show="emit('menu', true)" @hide="emit('menu', false)">
          <q-list dense style="min-width: 140px">
            <q-item
              v-for="k in insertKinds ?? []" :key="k.kind" v-close-popup clickable :disable="k.disabled" :data-testid="`de-insert-${k.kind}`"
              @click="emit('insert', k.kind)"
            >
              <q-item-section avatar style="min-width: 28px"><q-icon :name="kindIcon(k.kind)" size="18px" /></q-item-section>
              <q-item-section>
                <q-item-label>{{ t(`approval.graph.kind.${k.kind}`) }}</q-item-label>
                <q-item-label v-if="k.limit" caption>{{ t('approval.designer.graph.paletteLimit') }}</q-item-label>
              </q-item-section>
            </q-item>
          </q-list>
        </q-menu>
      </q-btn>
    </div>
  </EdgeLabelRenderer>
</template>

<style scoped>
.de-plus { position: absolute; pointer-events: all; z-index: 1; }
</style>
