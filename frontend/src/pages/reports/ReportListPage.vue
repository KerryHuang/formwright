<script setup lang="ts">
import type { QTableColumn } from 'quasar';
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRouter } from 'vue-router';
import { useReports } from 'src/application/dynamic/queries/reports-queries';
import ForbiddenNotice from 'src/components/framework/ForbiddenNotice.vue';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { ReportItem } from 'src/domain/dynamic/report-types';
import { isForbidden } from 'src/services/api/http';

const { t, locale } = useI18n();
const router = useRouter();
const reports = useReports();
const forbidden = computed(() => isForbidden(reports.error.value));
// 內建報表在上方自成一區（看板設計 §8.3），「我的報表」只列其餘（自己的與分享來的）
const builtIns = computed(() => (reports.data.value ?? []).filter((r) => r.builtIn));
const others = computed(() => (reports.data.value ?? []).filter((r) => !r.builtIn));
const columns = computed<QTableColumn<ReportItem>[]>(() => [
  { name: 'name', label: t('reports.name'), align: 'left', field: (r) => resolveI18n(r.name, locale.value) },
  { name: 'owner', label: t('reports.owner'), align: 'left', field: (r) => (r.isOwner ? t('reports.mine') : r.ownerName) },
]);
</script>

<template>
  <q-page padding>
    <ForbiddenNotice v-if="forbidden" />
    <template v-else>
      <div v-if="reports.isError.value" class="text-negative" data-testid="reports-error">
        {{ t('reports.loadFailed') }}
        <q-btn flat dense color="primary" :label="t('reports.retry')" data-testid="reports-retry" @click="reports.refetch()" />
      </div>
      <section v-else-if="builtIns.length" class="q-mb-lg" data-testid="builtin-reports">
        <div class="text-h6 q-mb-md">{{ t('reports.builtIn.section') }}</div>
        <q-list bordered separator>
          <q-item v-for="r in builtIns" :key="r.id" clickable :data-testid="`builtin-report-${r.id}`" @click="router.push(`/reports/${r.id}`)">
            <q-item-section>
              <q-item-label>{{ resolveI18n(r.name, locale) }}</q-item-label>
              <q-item-label v-if="r.description" caption lines="2">{{ resolveI18n(r.description, locale) }}</q-item-label>
            </q-item-section>
            <q-item-section side top><q-badge color="muted" :label="t('reports.builtIn.owner')" /></q-item-section>
          </q-item>
        </q-list>
      </section>
      <div class="row items-center q-mb-md">
        <div class="text-h6">{{ t('reports.myReports') }}</div>
        <q-space />
        <q-btn color="primary" icon="add" :label="t('reports.create')" to="/reports/new" data-testid="report-create" />
      </div>
      <q-table
        v-if="!reports.isError.value" flat bordered row-key="id" :rows="others" :columns="columns" :loading="reports.isFetching.value"
        :rows-per-page-options="[0]" hide-pagination :no-data-label="t('reports.empty')"
      >
        <template #body="{ row, cols }">
          <q-tr class="row-clickable row-chevron" :data-testid="`report-row-${row.id}`" @click="router.push(`/reports/${row.id}`)">
            <q-td v-for="c in cols" :key="c.name" :align="c.align">
              {{ c.value }}
              <q-badge v-if="c.name === 'name' && row.isOwner && row.sharedGroupIds.length" class="q-ml-sm" color="muted" :label="t('reports.shared')" />
            </q-td>
          </q-tr>
        </template>
      </q-table>
    </template>
  </q-page>
</template>
