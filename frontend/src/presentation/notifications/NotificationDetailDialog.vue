<template>
  <q-dialog :model-value="modelValue" @update:model-value="emit('update:modelValue', $event)">
    <q-card class="notification-detail" data-testid="notification-detail">
      <q-card-section class="text-subtitle1 notification-detail-text" data-testid="notification-detail-title">{{ title }}</q-card-section>
      <q-card-section v-if="body" class="notification-detail-text notification-detail-body" data-testid="notification-detail-body">{{ body }}</q-card-section>
      <q-card-section v-if="!canOpen" class="text-muted" data-testid="notification-detail-no-read">{{ t('notifications.notify.noReadRight') }}</q-card-section>
      <q-card-actions align="right">
        <q-btn flat no-caps :label="t('notifications.notify.close')" data-testid="notification-detail-close" @click="emit('update:modelValue', false)" />
        <q-btn
          v-if="canOpen" unelevated no-caps color="primary" :label="t('notifications.notify.open')"
          data-testid="notification-detail-open" @click="emit('open')"
        />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>

<script setup lang="ts">
import { useI18n } from 'vue-i18n';

/** 通知節點的詳情：全文只用文字插值；沒有讀取權（canOpen=false）時不給連結，改說明原因（設計 §24 U-h）。 */
defineProps<{ modelValue: boolean; title: string; body: string; canOpen: boolean }>();
const emit = defineEmits<{ 'update:modelValue': [value: boolean]; open: [] }>();
const { t } = useI18n();
</script>

<style scoped>
.notification-detail {
  width: 480px;
  max-width: 92vw;
}
.notification-detail-text {
  overflow-wrap: anywhere;
}
.notification-detail-body {
  white-space: pre-line;
}
</style>
