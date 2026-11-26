<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRouter } from 'vue-router';
import { labelOr } from 'src/domain/ops/ops-format';
import BrandMark from 'src/presentation/brand/BrandMark.vue';
import LocaleSwitcher from 'src/presentation/locale/LocaleSwitcher.vue';
import { setupApi, type MaintenanceInfo } from 'src/services/api/setup-api';

const { t, te } = useI18n({ useScope: 'global' });
const router = useRouter();

const info = ref<MaintenanceInfo | null>(null);
const unreachable = ref(false);
const loading = ref(false);

// 不自動輪詢（D-17）：要使用者修好後重啟容器，再按「重新檢查」
async function load() {
  loading.value = true;
  unreachable.value = false;
  try {
    const r = await setupApi.getMaintenance();
    if (r === null) void router.replace('/login');
    else info.value = r;
  } catch {
    unreachable.value = true;
  } finally {
    loading.value = false;
  }
}
onMounted(load);

const errorKey = computed(() => `ops.backup.error.${info.value?.errorCode}`);
// 沒有對應句子的代碼（notConfigured、未來的新代碼）只顯示代碼那一行，避免同一個代碼印兩次
const errorSentence = computed(() => (info.value?.errorCode && te(errorKey.value) ? labelOr(t, te, errorKey.value, info.value.errorCode) : ''));
const reasonText = computed(() => (info.value ? labelOr(t, te, `maintenance.reason.${info.value.reason}`, info.value.reason) : ''));
const reload = () => window.location.reload();
</script>

<template>
  <div class="row justify-center q-pa-md">
    <q-card class="maintenance-card" data-testid="maintenance-page">
      <q-card-section class="row items-center no-wrap">
        <BrandMark class="q-mr-sm" style="font-size: 28px" />
        <div class="text-h6 col">{{ t('maintenance.title') }}</div>
        <LocaleSwitcher />
      </q-card-section>

      <q-card-section v-if="unreachable" class="q-gutter-sm">
        <div class="text-negative">{{ t('maintenance.unreachable') }}</div>
        <q-btn color="primary" :label="t('maintenance.reload')" data-testid="maintenance-reload" @click="reload" />
      </q-card-section>

      <template v-else-if="info">
        <q-card-section class="q-gutter-sm">
          <div data-testid="maintenance-reason">{{ reasonText }}</div>
          <div v-if="info.errorCode" data-testid="maintenance-error">
            <div v-if="errorSentence">{{ errorSentence }}</div>
            <div class="text-caption maintenance-code">{{ t('maintenance.errorCode', { code: info.errorCode }) }}</div>
          </div>
        </q-card-section>

        <q-card-section data-testid="maintenance-actions">
          <div class="text-subtitle2">{{ t('maintenance.actions.title') }}</div>
          <ul class="maintenance-actions">
            <li>{{ info.previousVersion ? t('maintenance.actions.rollback', { version: info.previousVersion }) : t('maintenance.actions.rollbackGeneric') }}</li>
            <li v-if="info.reason === 'preUpgradeBackupFailed'">{{ t('maintenance.actions.fixBackup') }}</li>
            <li v-if="info.reason === 'backupNotConfigured'">{{ t('maintenance.actions.fixConfig') }}</li>
            <li>{{ t('maintenance.actions.logs') }}</li>
          </ul>
        </q-card-section>

        <q-card-section class="row items-center no-wrap q-gutter-sm">
          <div class="col text-caption maintenance-code">{{ t('maintenance.version', { version: info.version }) }}</div>
          <q-btn flat :label="t('maintenance.retry')" :loading="loading" data-testid="maintenance-retry" @click="load" />
        </q-card-section>
      </template>
    </q-card>
  </div>
</template>

<style scoped>
.maintenance-card {
  max-width: 720px;
  width: 100%;
}
.maintenance-actions {
  margin: 4px 0;
  padding-inline-start: 1.25em;
}
.maintenance-code {
  font-family: monospace;
  word-break: break-all;
}
</style>
