<script setup lang="ts">
/** 流程狀態章。color 是選項設定的顏色：Quasar 色名或 #rrggbb，沒設就是中性灰。樣式在 quasar-bridge.css。 */
import { computed } from 'vue';

const props = defineProps<{ label: string; color?: string }>();
const hex = computed(() => props.color?.startsWith('#'));
</script>

<template>
  <span
    class="status-chip" :class="color && !hex ? `text-${color}` : undefined"
    :style="hex ? { color } : undefined" data-testid="status-chip"
  >
    <span class="status-chip__dot" aria-hidden="true" />
    <span class="status-chip__label">{{ label }}</span>
  </span>
</template>
