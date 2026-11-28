<script setup lang="ts">
/** 單據頁打不開（找不到、無權限、載入失敗）：說明原因並給出路。外觀照 EmptyState，不用紅字與警告圖示。 */
import { useI18n } from 'vue-i18n';

defineProps<{ title: string; reason: string; backLabel: string; backTo: string; retry?: () => void }>();
const { t } = useI18n();
</script>

<template>
  <div class="full-width column items-center text-center q-px-md q-py-lg" data-testid="record-unavailable">
    <q-icon name="find_in_page" size="lg" class="text-muted q-mb-sm" aria-hidden="true" />
    <div class="text-h6 record-unavailable__text">{{ title }}</div>
    <div class="text-muted q-mt-xs record-unavailable__text">{{ reason }}</div>
    <div class="row justify-center q-gutter-sm q-mt-sm">
      <q-btn color="primary" no-caps :label="backLabel" :to="backTo" data-testid="record-unavailable-back" />
      <q-btn v-if="retry" flat no-caps color="primary" :label="t('dynamic.reload')" data-testid="record-unavailable-retry" @click="retry()" />
    </div>
  </div>
</template>

<style scoped>
.record-unavailable__text { max-width: 100%; overflow-wrap: anywhere; }
</style>
