<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { loadBackupPage, useBackupRun, useBackups, useRequestBackup } from 'src/application/dynamic/queries/ops-queries';
import { formatBytes, formatTime, labelOr } from 'src/domain/ops/ops-format';
import type { BackupRun } from 'src/domain/ops/ops-types';
import type { ApiError } from 'src/services/api/http';

// 備份分頁（備份設計 §14.1、§14.3）：狀態卡、立即備份、歷程。所有判斷以 API 回應為準（雲端版不回路徑、停用原因由後端給），前端不自己判斷版本。
const { t, te, locale } = useI18n();
const $q = useQuasar();
const query = useBackups();
const o = computed(() => query.data.value);

const LOCATION_NOTICES = ['notConfigured', 'providerUnsupported', 'locationInvalid', 'locationMismatch', 'destinationUnwritable'];
const notice = computed(() => (o.value && LOCATION_NOTICES.includes(o.value.locationStatus) ? o.value.locationStatus : null));
// 雲端版而儲存不是隔離的（§7.3）：版別以 API 為準
const notIsolated = computed(() => !!o.value && o.value.edition === 'Cloud' && o.value.configured && o.value.locationValid && !o.value.isolated);

const pad = (n: number) => String(n).padStart(2, '0');
const offsetText = computed(() => {
  const m = o.value?.schedule.offsetMinutes ?? 0;
  return `${m < 0 ? '-' : '+'}${pad(Math.floor(Math.abs(m) / 60))}:${pad(Math.abs(m) % 60)}`;
});
const scheduleText = computed(() => t('ops.backup.scheduleValue', { time: `${pad(o.value?.schedule.hour ?? 0)}:00`, offset: offsetText.value }));
const locationText = computed(() => {
  const v = o.value;
  if (!v?.storeKind) return '—';
  return v.displayLocation && v.displayLocation !== v.storeKind ? v.displayLocation : t('ops.backup.locationCloud', { kind: v.storeKind });
});
const size = (n?: number) => (n === undefined || n === null ? '—' : formatBytes(n, locale.value));
const lastSuccessText = computed(() => {
  const r = o.value?.lastSuccess;
  return r ? t('ops.backup.lastSuccessValue', { time: formatTime(r.snapshotUtc), size: size(r.databaseBytes), count: r.attachmentCount ?? 0 }) : t('ops.backup.neverSucceeded');
});

function stepText(r: BackupRun): string {
  if (r.status === 'queued') return t('ops.backup.step.queued');
  if (r.stage === 'attachments' && r.progressTotal) return t('ops.backup.step.attachmentsCount', { done: r.progressDone ?? 0, total: r.progressTotal });
  return r.stage ? t(`ops.backup.step.${r.stage}`) : t('ops.backup.step.queued');
}
const disabledText = computed(() => {
  const v = o.value;
  if (!v || v.manualAllowed) return '';
  if (v.current) return stepText(v.current);
  return v.manualDisabledReason ? labelOr(t, te, `ops.backup.disabled.${v.manualDisabledReason}`, v.manualDisabledReason) : '';
});
const inProgress = computed(() => !!o.value?.current);

const request = useRequestBackup();
function runNow() {
  request.mutate(undefined, {
    onSuccess: () => $q.notify({ type: 'positive', message: t('ops.backup.requested') }),
    onError: (e) => {
      const err = e as unknown as ApiError;
      const key = err.code ? `ops.backup.requestError.${err.code}` : err.status === 403 ? 'ops.backup.requestError.forbidden' : '';
      $q.notify({ type: 'negative', message: key && te(key) ? t(key) : err.message || t('ops.saveFailed') });
    },
  });
}

// 歷程：第一頁來自總覽查詢（執行中每 5 秒更新），「載入更多」接在後面
const more = ref<BackupRun[]>([]);
const cursor = ref<number | null>(null);
const loadingMore = ref(false);
const moreFailed = ref(false);
watch(() => o.value?.nextCursor, (c) => { if (!more.value.length) cursor.value = c ?? null; }, { immediate: true });
async function loadMore() {
  if (cursor.value === null) return;
  loadingMore.value = true;
  moreFailed.value = false;
  try {
    const page = await loadBackupPage(cursor.value);
    more.value = [...more.value, ...page.runs];
    cursor.value = page.nextCursor ?? null;
  } catch {
    moreFailed.value = true;
  } finally {
    loadingMore.value = false;
  }
}
const rows = computed(() => {
  const first = o.value?.runs ?? [];
  const seen = new Set(first.map((r) => r.id));
  return [...first, ...more.value.filter((r) => !seen.has(r.id))];
});
const isOldLocation = (r: BackupRun) => !!o.value?.locationId && r.locationId !== o.value.locationId;

function duration(r: BackupRun): string {
  if (!r.startedUtc || !r.finishedUtc) return '—';
  const s = Math.max(0, Math.round((new Date(r.finishedUtc).getTime() - new Date(r.startedUtc).getTime()) / 1000));
  return s >= 60 ? t('ops.backup.duration.minutes', { n: Math.floor(s / 60), s: s % 60 }) : t('ops.backup.duration.seconds', { s });
}
const triggerText = (r: BackupRun) => (r.trigger === 'manual' ? t('ops.backup.trigger.manual', { user: r.requestedUserId ?? '—' }) : t('ops.backup.trigger.schedule'));
const errorText = (r: BackupRun) => (r.errorCode ? labelOr(t, te, `ops.backup.error.${r.errorCode}`, r.errorCode) : '');
const attachmentsText = (r: BackupRun) => (r.status === 'succeeded' ? `${r.newAttachmentCount ?? 0}／${r.attachmentCount ?? 0}` : '—');
const statusText = (r: BackupRun) => labelOr(t, te, `ops.backup.status.${r.status}`, r.status);
const STATUS_COLOR: Record<string, string> = { succeeded: 'positive', failed: 'negative', interrupted: 'warning', running: 'info', queued: 'info' };

const columns = computed(() => [
  { name: 'time', label: t('ops.backup.col.time'), field: (r: BackupRun) => formatTime(r.queuedUtc), align: 'left' as const },
  { name: 'trigger', label: t('ops.backup.col.trigger'), field: (r: BackupRun) => triggerText(r), align: 'left' as const },
  { name: 'status', label: t('ops.backup.col.status'), field: (r: BackupRun) => statusText(r), align: 'left' as const },
  { name: 'database', label: t('ops.backup.col.database'), field: (r: BackupRun) => size(r.databaseBytes), align: 'right' as const },
  { name: 'attachments', label: t('ops.backup.col.attachments'), field: (r: BackupRun) => attachmentsText(r), align: 'right' as const },
  { name: 'duration', label: t('ops.backup.col.duration'), field: (r: BackupRun) => duration(r), align: 'right' as const },
  { name: 'error', label: t('ops.backup.col.error'), field: (r: BackupRun) => errorText(r), align: 'left' as const },
]);
// 820 以下隱藏「耗時」「觸發」（§14.3）
const visibleColumns = computed(() => columns.value.map((c) => c.name).filter((n) => $q.screen.gt.sm || (n !== 'duration' && n !== 'trigger')));

// 詳細：手機全螢幕、其他寬度右側滑出
const openId = ref<number | null>(null);
const detail = useBackupRun(openId);
const d = computed(() => detail.data.value);
type Field = [string, string];
const fields = computed<Field[]>(() => {
  const r = d.value;
  if (!r) return [];
  const f = (k: string, v: string | number | undefined | null): Field[] => (v === undefined || v === null || v === '' ? [] : [[t(`ops.backup.detail.${k}`), String(v)]]);
  return [
    [t('ops.backup.col.status'), statusText(r)],
    [t('ops.backup.col.trigger'), triggerText(r)],
    ...f('set', r.setName), ...f('provider', r.provider), ...f('version', r.appVersion), ...f('migration', r.migrationId),
    [t('ops.backup.detail.queued'), formatTime(r.queuedUtc)],
    ...(r.startedUtc ? f('started', formatTime(r.startedUtc)) : []),
    ...(r.snapshotUtc ? f('snapshot', formatTime(r.snapshotUtc)) : []),
    ...(r.finishedUtc ? f('finished', formatTime(r.finishedUtc)) : []),
    ...(r.databaseBytes !== undefined ? ([[t('ops.backup.col.database'), size(r.databaseBytes)]] as Field[]) : []),
    ...f('newAttachments', r.newAttachmentCount === undefined ? undefined : `${r.newAttachmentCount}（${size(r.newAttachmentBytes)}）`),
    ...f('totalAttachments', r.attachmentCount === undefined ? undefined : `${r.attachmentCount}（${size(r.totalAttachmentBytes)}）`),
    ...f('skipped', r.skippedAttachments || undefined), ...f('pruned', r.prunedSets || undefined), ...f('pruneError', r.pruneError),
    ...(r.errorCode ? ([[t('ops.backup.col.error'), errorText(r)]] as Field[]) : []), ...f('errorDetail', r.errorDetail),
  ];
});
</script>

<template>
  <div v-if="query.isError.value && !o" class="text-negative" data-testid="backup-load-failed">{{ t('ops.loadFailed') }}</div>
  <div v-else-if="!o" class="text-muted">{{ t('designer.loading') }}</div>
  <div v-else class="column no-wrap q-gutter-y-md backup-tab">
    <q-banner v-if="notice" dense class="bg-negative-soft" data-testid="backup-notice" :data-notice="notice">{{ t(`ops.backup.notice.${notice}`) }}</q-banner>
    <q-banner v-if="notIsolated" dense class="bg-warning-soft" data-testid="backup-not-isolated">{{ t('ops.backup.notice.notIsolated') }}</q-banner>

    <q-card flat bordered data-testid="backup-status">
      <q-list class="status-grid">
        <q-item v-if="o.configured">
          <q-item-section>
            <q-item-label caption>{{ t('ops.backup.location') }}</q-item-label>
            <q-item-label class="break-all" data-testid="backup-location">{{ locationText }}</q-item-label>
          </q-item-section>
        </q-item>
        <q-item v-if="o.configured && o.locationValid">
          <q-item-section>
            <q-item-label caption>{{ t('ops.backup.isolated') }}</q-item-label>
            <q-item-label data-testid="backup-isolated">{{ t(o.isolated ? 'ops.backup.isolatedYes' : 'ops.backup.isolatedNo') }}</q-item-label>
          </q-item-section>
        </q-item>
        <q-item v-if="o.locationValid">
          <q-item-section>
            <q-item-label caption>{{ t('ops.backup.schedule') }}</q-item-label>
            <q-item-label data-testid="backup-schedule">{{ scheduleText }}</q-item-label>
          </q-item-section>
        </q-item>
        <q-item v-if="o.locationValid">
          <q-item-section>
            <q-item-label caption>{{ t('ops.backup.next') }}</q-item-label>
            <q-item-label data-testid="backup-next">{{ formatTime(o.schedule.nextScheduledUtc) }}</q-item-label>
          </q-item-section>
        </q-item>
        <q-item v-if="o.locationValid">
          <q-item-section>
            <q-item-label caption>{{ t('ops.backup.retention') }}</q-item-label>
            <q-item-label data-testid="backup-retention">
              <template v-if="o.canDelete">{{ t('ops.backup.retentionValue', { count: o.retentionCount, days: o.manualRetentionDays }) }}</template>
              <template v-else>{{ t('ops.backup.retentionNoDelete') }}</template>
              <template v-if="o.edition === 'Cloud'">；{{ t('ops.backup.retentionCloud') }}</template>
            </q-item-label>
          </q-item-section>
        </q-item>
        <q-item v-if="o.locationValid">
          <q-item-section>
            <q-item-label caption>{{ t('ops.backup.lastSuccess') }}</q-item-label>
            <q-item-label data-testid="backup-last-success">{{ lastSuccessText }}</q-item-label>
          </q-item-section>
        </q-item>
      </q-list>
      <q-separator v-if="o.locationValid" />
      <q-card-section v-if="o.locationValid" class="text-caption text-muted" data-testid="backup-keys">{{ t('ops.backup.keysNote') }}</q-card-section>
    </q-card>

    <div v-if="o.configured" class="column no-wrap q-gutter-y-xs">
      <div>
        <q-btn
          color="primary" no-caps icon="backup" :label="t('ops.backup.runNow')" :disable="!o.manualAllowed" :loading="request.isPending.value || inProgress"
          data-testid="backup-run" @click="runNow"
        />
      </div>
      <div v-if="disabledText" class="text-caption text-muted" data-testid="backup-disabled-reason" :data-reason="o.manualDisabledReason">{{ disabledText }}</div>
    </div>

    <div>
      <div class="text-subtitle1 q-mb-xs">{{ t('ops.backup.history') }}</div>
      <q-banner v-if="query.isError.value" dense class="bg-negative-soft q-mb-sm" data-testid="backup-refresh-failed">{{ t('ops.loadFailed') }}</q-banner>
      <q-table
        flat bordered row-key="id" :rows="rows" :columns="columns" :visible-columns="visibleColumns" :rows-per-page-options="[0]" hide-pagination
        :no-data-label="t('ops.backup.empty')" :grid="$q.screen.lt.sm" data-testid="backup-table"
      >
        <template #body="{ row, cols }">
          <q-tr class="cursor-pointer" tabindex="0" :data-testid="`backup-row-${row.id}`" @click="openId = row.id" @keydown.enter.self.prevent="openId = row.id">
            <template v-for="c in cols" :key="c.name">
              <q-td v-if="c.name === 'status'">
                <q-badge :color="STATUS_COLOR[row.status as string] ?? 'grey'">{{ c.value }}</q-badge>
                <q-badge v-if="isOldLocation(row)" outline color="grey" class="q-ml-xs" data-testid="backup-old-location">{{ t('ops.backup.oldLocation') }}</q-badge>
              </q-td>
              <q-td v-else-if="c.name === 'error'" style="white-space: normal; overflow-wrap: anywhere">{{ c.value }}</q-td>
              <q-td v-else :align="c.align">{{ c.value }}</q-td>
            </template>
          </q-tr>
        </template>
        <template #item="{ row }">
          <div class="col-12 q-py-xs">
            <q-card flat bordered class="cursor-pointer" tabindex="0" :data-testid="`backup-card-${row.id}`" @click="openId = row.id" @keydown.enter.self.prevent="openId = row.id">
              <q-card-section class="q-py-sm">
                <div class="row items-center q-gutter-xs">
                  <q-badge :color="STATUS_COLOR[row.status as string] ?? 'grey'">{{ statusText(row) }}</q-badge>
                  <q-badge v-if="isOldLocation(row)" outline color="grey" data-testid="backup-old-location">{{ t('ops.backup.oldLocation') }}</q-badge>
                  <span class="text-caption text-muted">{{ formatTime(row.queuedUtc) }}</span>
                </div>
                <div class="text-caption">{{ triggerText(row) }} · {{ size(row.databaseBytes) }} · {{ attachmentsText(row) }} · {{ duration(row) }}</div>
                <div v-if="row.errorCode" class="text-caption text-negative break-all">{{ errorText(row) }}</div>
              </q-card-section>
            </q-card>
          </div>
        </template>
      </q-table>
      <div v-if="moreFailed" class="text-negative q-mt-xs" data-testid="backup-more-failed">{{ t('ops.loadFailed') }}</div>
      <div v-if="cursor !== null" class="q-mt-sm">
        <q-btn flat no-caps color="primary" :label="t('ops.loadMore')" :loading="loadingMore" data-testid="backup-load-more" @click="loadMore" />
      </div>
    </div>

    <q-dialog :model-value="openId !== null" :maximized="$q.screen.lt.sm" :position="$q.screen.lt.sm ? 'standard' : 'right'" full-height @update:model-value="(v) => { if (!v) openId = null; }">
      <q-card class="backup-detail column no-wrap" data-testid="backup-detail">
        <q-card-section class="row no-wrap items-start q-gutter-sm">
          <div class="col text-subtitle1">{{ t('ops.backup.detail.title') }}</div>
          <q-btn flat round dense icon="close" :aria-label="t('designer.close')" data-testid="backup-detail-close" @click="openId = null" />
        </q-card-section>
        <q-separator />
        <q-card-section v-if="detail.isError.value" class="text-negative">{{ t('ops.loadFailed') }}</q-card-section>
        <q-card-section v-else-if="d" class="col scroll">
          <q-list dense bordered separator>
            <q-item v-for="[k, v] in fields" :key="k">
              <q-item-section>
                <q-item-label caption>{{ k }}</q-item-label>
                <q-item-label class="break-all">{{ v }}</q-item-label>
              </q-item-section>
            </q-item>
          </q-list>
        </q-card-section>
        <q-card-section v-else class="text-muted">{{ t('designer.loading') }}</q-card-section>
      </q-card>
    </q-dialog>
  </div>
</template>

<style scoped>
.backup-tab { max-width: 960px; }
.break-all { word-break: break-all; }
.status-grid { display: grid; grid-template-columns: 1fr; }
@media (min-width: 600px) { .status-grid { grid-template-columns: 1fr 1fr; } }
.backup-detail { width: 460px; max-width: 100vw; }
</style>
