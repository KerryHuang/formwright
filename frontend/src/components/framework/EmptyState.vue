<script setup lang="ts">
/** 空狀態（設計 2026-11-27 §1.1）：一句原因、選填的例子或說明、選填的主要動作（呼叫端依權限決定放不放）。不用警告圖示。 */
defineProps<{ message: string; hint?: string }>();
defineSlots<{ action?: () => unknown }>();
</script>

<template>
  <div class="full-width column items-center text-center q-px-md q-py-lg" data-testid="empty-state">
    <q-icon name="inbox" size="lg" class="text-muted q-mb-sm" aria-hidden="true" />
    <div class="text-body1 empty-state__text">{{ message }}</div>
    <div v-if="hint" class="text-muted q-mt-xs empty-state__text" data-testid="empty-state-hint">{{ hint }}</div>
    <div v-if="$slots.action" class="q-mt-md" data-testid="empty-state-action"><slot name="action" /></div>
  </div>
</template>

<style scoped>
.empty-state__text { max-width: 100%; overflow-wrap: anywhere; }
</style>
