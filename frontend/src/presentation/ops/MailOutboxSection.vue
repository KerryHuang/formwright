<script setup lang="ts">
import type { QTableColumn } from 'quasar';
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useMailOutbox } from 'src/application/dynamic/queries/account-security-queries';
import type { MailOutboxItem } from 'src/domain/dynamic/types';
import { formatTime } from 'src/domain/ops/ops-format';

// 帳號信寄送紀錄（帳號安全設計 §6.6、§12）：新到舊、每頁 50 筆；Demo 站是收件匣，可以看整封信
const { t, te } = useI18n();
const page = ref(1);
const outbox = useMailOutbox(page);
const data = computed(() => outbox.data.value);
const pages = computed(() => (data.value ? Math.max(1, Math.ceil(data.value.total / data.value.pageSize)) : 1));
const label = (prefix: string, v: string | null | undefined) => (!v ? '' : te(`${prefix}.${v}`) ? t(`${prefix}.${v}`) : v);

// 這一頁沒有任何一列有說明（例如全部已寄出）時不顯示「說明」欄，免得整欄空白（au1a 走查 G3）
const anyReason = computed(() => (data.value?.items ?? []).some((r) => !!r.lastError));
const columns = computed<QTableColumn<MailOutboxItem>[]>(() => [
  { name: 'createdUtc', label: t('mailOutbox.time'), align: 'left', field: (r) => formatTime(r.createdUtc) },
  { name: 'kind', label: t('mailOutbox.kind'), align: 'left', field: (r) => label('mailOutbox.kinds', r.kind) },
  { name: 'toAddress', label: t('mailOutbox.to'), align: 'left', field: (r) => `${r.toAddress}${r.userId ? `（${r.userId}）` : ''}` },
  { name: 'status', label: t('mailOutbox.status'), align: 'left', field: (r) => label('mailOutbox.statuses', r.status) },
  ...(anyReason.value
    ? [{ name: 'lastError', label: t('mailOutbox.reason'), align: 'left', field: (r) => label('mailOutbox.reasons', r.lastError) } as QTableColumn<MailOutboxItem>]
    : []),
  { name: 'actions', label: '', align: 'right', field: 'id' },
]);

const viewing = ref<MailOutboxItem | null>(null);
</script>

<template>
  <div class="column q-gutter-y-sm" data-testid="mail-outbox">
    <div class="text-subtitle1">{{ t('mailOutbox.title') }}</div>
    <div class="text-caption text-muted">{{ data?.demoInbox ? t('mailOutbox.demoHint') : t('mailOutbox.hint') }}</div>
    <q-banner v-if="data?.publicUrlMissing" dense class="bg-warning-soft" data-testid="mail-outbox-no-url">{{ t('mailOutbox.publicUrlMissing') }}</q-banner>
    <div v-if="outbox.isError.value && !data" class="text-negative">{{ t('ops.loadFailed') }}</div>
    <q-table
      v-else flat bordered row-key="id" :rows="data?.items ?? []" :columns="columns" :loading="outbox.isFetching.value"
      :rows-per-page-options="[0]" hide-pagination :no-data-label="t('mailOutbox.empty')" :grid="$q.screen.lt.sm"
    >
      <template #body-cell-toAddress="p">
        <q-td :props="p" style="overflow-wrap: anywhere; white-space: normal">{{ p.value }}</q-td>
      </template>
      <template #body-cell-actions="p">
        <q-td :props="p">
          <q-btn
            v-if="p.row.previewBody" flat dense no-caps color="primary" :label="t('mailOutbox.view')" :data-testid="`mail-outbox-view-${p.row.id}`"
            @click="viewing = p.row"
          />
        </q-td>
      </template>
      <template #item="{ row, cols }">
        <div class="col-12 q-py-xs">
          <q-card flat bordered :data-testid="`mail-outbox-card-${row.id}`">
            <q-list dense>
              <q-item v-for="c in cols.filter((x: { name: string; value: unknown }) => x.name !== 'actions' && x.value)" :key="c.name">
                <q-item-section>
                  <q-item-label caption>{{ c.label }}</q-item-label>
                  <q-item-label style="overflow-wrap: anywhere">{{ c.value }}</q-item-label>
                </q-item-section>
              </q-item>
            </q-list>
            <q-card-actions v-if="row.previewBody" align="right">
              <q-btn flat dense no-caps color="primary" :label="t('mailOutbox.view')" :data-testid="`mail-outbox-view-${row.id}`" @click="viewing = row" />
            </q-card-actions>
          </q-card>
        </div>
      </template>
    </q-table>
    <div v-if="pages > 1" class="row justify-center">
      <q-pagination v-model="page" :max="pages" :max-pages="5" direction-links boundary-links data-testid="mail-outbox-pages" />
    </div>

    <q-dialog :model-value="viewing !== null" @update:model-value="viewing = null">
      <q-card style="width: 560px; max-width: 95vw">
        <q-card-section class="text-h6">{{ label('mailOutbox.kinds', viewing?.kind) }}</q-card-section>
        <q-card-section class="q-pt-none">
          <div class="text-caption text-muted q-mb-sm" style="overflow-wrap: anywhere">{{ viewing?.toAddress }}</div>
          <!-- 信件內文是純文字；連結用 linkify 不做，Demo 收件匣的相對連結直接點得開 -->
          <div style="white-space: pre-wrap; overflow-wrap: anywhere" data-testid="mail-outbox-body">
            <template v-for="(line, i) in (viewing?.previewBody ?? '').split('\n')" :key="i">
              <a v-if="line.startsWith('/') || line.startsWith('http')" :href="line">{{ line }}</a><span v-else>{{ line }}</span>{{ '\n' }}
            </template>
          </div>
        </q-card-section>
        <q-card-actions align="right">
          <q-btn flat no-caps :label="t('mailOutbox.close')" @click="viewing = null" />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </div>
</template>
