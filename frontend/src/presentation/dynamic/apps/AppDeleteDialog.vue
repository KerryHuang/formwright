<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useDeleteApp } from 'src/application/dynamic/queries/apps-queries';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { DeleteBlocker } from 'src/domain/dynamic/types';
import { dynamicApi, type AppDeleteCheck, type AppInfo } from 'src/services/api/dynamic-api';
import { isForbidden, type ApiError } from 'src/services/api/http';

/**
 * 刪除應用程式：先唯讀預檢（擋下只列原因，可刪才列會一起刪掉的東西並要勾選確認），
 * 送出時帶預檢回的 purgedTotal；後端筆數對不上回 409，這裡重新預檢。擋下的判斷全在後端，這裡只呈現。
 */
const props = defineProps<{ modelValue: boolean; app: AppInfo | null }>();
const emit = defineEmits<{ 'update:modelValue': [value: boolean]; deleted: [] }>();

const { t, te, locale } = useI18n();
const $q = useQuasar();
const del = useDeleteApp();

const loading = ref(false);
const forbidden = ref(false);
const error = ref('');
const check = ref<AppDeleteCheck | null>(null);
// 預檢結果屬於哪個目標：送出只用目前目標的結果
const checkKey = ref<string | null>(null);
const stale = ref(false);
const acknowledged = ref(false);
// 每次開啟、切換目標、關閉都換號，舊請求晚到的回應一律丟掉
let seq = 0;

const name = (n: Parameters<typeof resolveI18n>[0]) => resolveI18n(n, locale.value);
const canSubmit = computed(() => !!check.value?.canDelete && checkKey.value === props.app?.key && acknowledged.value
  && !loading.value && !error.value && !forbidden.value && !del.isPending.value);

async function load() {
  const app = props.app;
  const mine = ++seq;
  loading.value = true;
  forbidden.value = false;
  error.value = '';
  check.value = null;
  checkKey.value = null;
  acknowledged.value = false;
  if (!app) { loading.value = false; return; }
  try {
    const c = await dynamicApi.getAppDeleteCheck(app.key);
    if (mine !== seq) return;
    check.value = c;
    checkKey.value = app.key;
    acknowledged.value = false;
  } catch (e) {
    if (mine !== seq) return;
    if (isForbidden(e)) forbidden.value = true;
    else error.value = (e as ApiError).message ?? t('designer.delete.checkFailed');
  } finally {
    if (mine === seq) loading.value = false;
  }
}

watch(() => [props.modelValue, props.app?.key] as const, ([open]) => {
  if (open) { stale.value = false; void load(); } else seq++;
}, { immediate: true });

function submit() {
  const app = props.app;
  const c = check.value;
  if (!app || !c || !canSubmit.value) return;
  del.mutate({ key: app.key, purged: c.purgedTotal }, {
    onSuccess: (r) => {
      $q.notify({ type: 'positive', message: t('apps.delete.done') });
      (r.warnings ?? []).forEach((w) => $q.notify({ type: 'warning', message: w, timeout: 0, closeBtn: true }));
      emit('deleted');
      emit('update:modelValue', false);
    },
    onError: async (e) => {
      const err = e as unknown as ApiError;
      if (err.status === 409) { await load(); stale.value = true; }
      else if (isForbidden(e)) forbidden.value = true;
      else $q.notify({ type: 'negative', message: err.message ?? t('apps.delete.failed') });
    },
  });
}

const SENTENCE_KINDS = ['liveRecords', 'appDependent', 'referenceField', 'dashboard', 'personalDashboard', 'appNotClosed', 'masterInUse', 'event', 'report'];
// 有專句的種類用專句；其他種類沿用設計器的種類名稱加後端給的名稱，沒有翻譯就顯示 kind 原字，不吞掉
// 後端帶了模組顯示名稱、報表名稱就用（不顯示代碼）；舊版後端沒帶時退回原本的文案
function blockerLine(b: DeleteBlocker): string {
  if (SENTENCE_KINDS.includes(b.kind)) {
    const key = b.reportName && (b.kind === 'dashboard' || b.kind === 'personalDashboard') ? `${b.kind}Report` : b.kind;
    const module = b.kind === 'liveRecords' ? b.name : (b.moduleName ?? b.moduleKey ?? '');
    return t(`apps.delete.blocker.${key}`, { module, field: b.name, name: b.name, report: b.reportName ?? '', n: b.count ?? 0 });
  }
  const kind = te(`designer.delete.kind.${b.kind}`) ? t(`designer.delete.kind.${b.kind}`) : b.kind;
  const module = b.kind === 'moduleActive' ? null : (b.moduleName ?? b.moduleKey);
  return [kind, module !== b.name ? module : null, b.name, b.count != null ? t('designer.delete.count', { n: b.count }) : null].filter((x) => x != null && x !== '').join(' · ');
}
</script>

<template>
  <q-dialog :model-value="modelValue" persistent data-testid="app-delete-dialog" @update:model-value="emit('update:modelValue', $event)">
    <q-card class="app-delete-card">
      <q-card-section class="text-h6 wrap-text">{{ t('apps.delete.title', { name: app ? name(app.name) : '' }) }}</q-card-section>

      <q-card-section v-if="loading" class="text-muted">{{ t('apps.delete.checking') }}</q-card-section>
      <q-card-section v-else-if="forbidden" class="text-negative" data-testid="app-delete-forbidden">{{ t('designer.delete.forbidden') }}</q-card-section>
      <q-card-section v-else-if="error" class="text-negative wrap-text" data-testid="app-delete-error">{{ error }}</q-card-section>

      <q-card-section v-else-if="check && !check.canDelete">
        <div class="q-mb-sm">{{ t('apps.delete.blockedIntro') }}</div>
        <q-list dense bordered separator data-testid="app-delete-blockers">
          <q-item v-for="(b, i) in check.blockers" :key="`${b.kind}-${b.moduleKey}-${b.id}-${i}`">
            <q-item-section class="wrap-text">{{ blockerLine(b) }}</q-item-section>
          </q-item>
        </q-list>
      </q-card-section>

      <q-card-section v-else-if="check" class="q-gutter-sm">
        <div v-if="stale" class="text-negative" data-testid="app-delete-stale">{{ t('apps.delete.stale') }}</div>
        <div>{{ t('apps.delete.willDelete') }}</div>
        <div v-if="app?.modules.length" data-testid="app-delete-members">
          <div class="text-weight-medium">{{ t('apps.delete.members') }}</div>
          <ul class="q-my-none"><li v-for="m in app.modules" :key="m.key" class="wrap-text">{{ name(m.name) }}</li></ul>
        </div>
        <div v-if="check.reports.length" data-testid="app-delete-reports">
          <div class="text-weight-medium">{{ t('apps.delete.reports') }}</div>
          <ul class="q-my-none"><li v-for="r in check.reports" :key="r.id" class="wrap-text">{{ r.name }}</li></ul>
        </div>
        <ul class="q-my-none text-negative" data-testid="app-delete-purge">
          <li v-if="check.purge.records > 0">{{ t('apps.delete.purge', { n: check.purge.records }) }}</li>
          <li v-for="c in check.cleared" :key="`c-${c.moduleKey}-${c.field}`" class="wrap-text">{{ t('apps.delete.cleared', { field: c.field, module: c.moduleKey, n: c.count }) }}</li>
          <li v-for="c in check.orphaned" :key="`o-${c.moduleKey}-${c.field}`" class="wrap-text">{{ t('apps.delete.orphaned', { field: c.field, module: c.moduleKey, n: c.count }) }}</li>
        </ul>
        <div class="text-muted">{{ t('apps.delete.groupsKept') }}</div>
        <q-checkbox v-model="acknowledged" class="app-delete-check" :label="t('apps.delete.confirm')" data-testid="app-delete-confirm" />
      </q-card-section>

      <q-card-actions align="right">
        <q-btn flat no-caps :label="t('dynamic.cancel')" :disable="del.isPending.value" data-testid="app-delete-close" @click="emit('update:modelValue', false)" />
        <q-btn color="negative" no-caps :label="t('apps.delete.submit')" :disable="!canSubmit" :loading="del.isPending.value" data-testid="app-delete-submit" @click="submit" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>

<style scoped>
.app-delete-card { width: 100%; max-width: 560px; }
.wrap-text { overflow-wrap: anywhere; word-break: break-word; }
.app-delete-check :deep(.q-checkbox__label) { white-space: normal; }
</style>
