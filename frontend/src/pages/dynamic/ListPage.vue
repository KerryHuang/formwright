<script setup lang="ts">
// POC uses a plain q-table; when merged into the main system, swap it for the framework's DataTable.
import { date, Notify, useQuasar, type QTableColumn } from 'quasar';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRoute, useRouter } from 'vue-router';
import { usePagedRecords, type RecordsState } from 'src/application/dynamic/queries/records-queries';
import { useModuleSchema } from 'src/application/dynamic/queries/schema-queries';
import {
  pickDefaultView, useCreateView, useDeleteView, useUpdateView, useViews,
} from 'src/application/dynamic/queries/views-queries';
import { countConditions, pruneCondition, serializeFilter } from 'src/domain/dynamic/condition';
import type { ApprovalListFilter } from 'src/domain/dynamic/approval-types';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { ConditionNode, DynamicRecord, FieldType, I18nText, ListView, PagedResponse, ViewSort } from 'src/domain/dynamic/types';
import { referenceView } from 'src/presentation/dynamic/reference-display';
import { isNumericField } from 'src/presentation/dynamic/numeric-field';
import { statusChipOf } from 'src/presentation/dynamic/status-chip';
import StatusChip from 'src/presentation/dynamic/components/StatusChip.vue';
import { fieldTypeRegistry } from 'src/presentation/dynamic/field-type-registry';
import ColumnChooser from 'src/presentation/dynamic/list/ColumnChooser.vue';
import ExportMenu from 'src/presentation/dynamic/list/ExportMenu.vue';
import FilterPanel from 'src/presentation/dynamic/list/FilterPanel.vue';
import ImportDialog from 'src/presentation/dynamic/list/ImportDialog.vue';
import SortEditor from 'src/presentation/dynamic/list/SortEditor.vue';
import ToolbarPanel from 'src/presentation/dynamic/list/ToolbarPanel.vue';
import ViewMenu from 'src/presentation/dynamic/list/ViewMenu.vue';
import EmptyState from 'src/components/framework/EmptyState.vue';
import ForbiddenNotice from 'src/components/framework/ForbiddenNotice.vue';
import { isForbidden, type ApiError } from 'src/services/api/http';

const { t, locale } = useI18n();
const $q = useQuasar();
const route = useRoute();
const router = useRouter();
const moduleKey = computed(() => String(route.params.moduleKey));
const size = 20;

const page = ref(1);
const filter = ref<ConditionNode>();
const sort = ref<ViewSort[]>([]);
const q = ref('');
/** System filter (approval design §13.4.1); not part of a saved view. Undefined = all. */
const approval = ref<ApprovalListFilter>();
/** null = the schema's ShowInList fields. */
const columnIds = ref<number[] | null>(null);
const currentViewId = ref<number | null>(null);
/** Module the current state was initialised for; the records query only runs when it matches the route. */
const initializedFor = ref<string | null>(null);
const initialized = computed(() => initializedFor.value === moduleKey.value);
/** Last successful page, shown while the query is in error (placeholderData only covers pending). */
const lastGood = ref<PagedResponse<DynamicRecord>>();

const schemaQuery = useModuleSchema(moduleKey);
const viewsQuery = useViews(moduleKey);
const schema = computed(() => schemaQuery.data.value);
const views = computed(() => viewsQuery.data.value ?? []);
const currentView = computed(() => views.value.find((v) => v.id === currentViewId.value));

const state = computed<RecordsState>(() => ({
  page: page.value, size, filter: pruneCondition(filter.value), sort: sort.value, q: q.value.trim() || undefined, approval: approval.value,
}));
/** 空清單的原因分兩種：有篩選、關鍵字或簽核切換時是「沒有符合」，否則是還沒有資料。 */
const filtered = computed(() => !!(state.value.filter || state.value.q || state.value.approval));
const exportQuery = computed(() => ({
  filter: serializeFilter(filter.value), sort: sort.value, q: q.value.trim() || undefined, approval: approval.value,
}));
const importing = ref(false);
// 擴充模組的記錄掛在既有實體上，後端不支援匯入（決策 6）
const canImport = computed(() => !!schema.value?.permissions.add && schema.value.kind !== 'extension');
const recordsQuery = usePagedRecords(moduleKey, state, initialized);

const allFields = computed(() => schema.value?.blocks.flatMap((b) => b.fields) ?? []);
const defaultColumns = computed(() =>
  allFields.value.filter((f) => f.config.showInList && f.key !== schema.value?.titleFieldKey).map((f) => f.id));
const effectiveColumns = computed(() => {
  const ids = (columnIds.value ?? []).filter((id) => allFields.value.some((f) => f.id === id && f.key !== schema.value?.titleFieldKey));
  return ids.length ? columnIds.value! : defaultColumns.value;
});

function applyView(v?: ListView) {
  currentViewId.value = v?.id ?? null;
  filter.value = v?.filter;
  sort.value = v?.sort ?? [];
  columnIds.value = v ? v.columns : null;
  page.value = 1;
}
function resetAll() {
  applyView();
  q.value = '';
  approval.value = undefined;
  initializedFor.value = null;
  lastGood.value = undefined;
}
watch(moduleKey, resetAll);
// Only initialise from data fetched after mount: cached views/schema may predate a designer change (stripped filters).
const fresh = computed(() => viewsQuery.isFetchedAfterMount.value && !viewsQuery.isFetching.value && !schemaQuery.isFetching.value);
watch([schema, fresh, moduleKey], () => {
  if (initialized.value || schema.value?.key !== moduleKey.value || !fresh.value) return;
  applyView(pickDefaultView(views.value));
  initializedFor.value = moduleKey.value;
}, { immediate: true });

const signature = (f: ConditionNode | undefined, s: ViewSort[], c: number[]) => JSON.stringify([pruneCondition(f) ?? null, s, c]);
const modified = computed(() => {
  const v = currentView.value;
  return !!v && signature(filter.value, sort.value, effectiveColumns.value) !== signature(v.filter, v.sort, v.columns);
});

watch(() => recordsQuery.data.value, (d) => {
  if (d && !recordsQuery.isPlaceholderData.value) lastGood.value = d;
});
const pageData = computed(() => recordsQuery.data.value ?? lastGood.value);

const notFound = computed(() => (schemaQuery.error.value as ApiError | null)?.status === 404);
const recordsError = computed(() => recordsQuery.error.value as ApiError | null);
const forbidden = computed(() => isForbidden(schemaQuery.error.value) || isForbidden(recordsError.value));
const failed = computed(() => !notFound.value && !forbidden.value
  && (!!schemaQuery.error.value || (!!recordsError.value && recordsError.value.status !== 400)));

function notifyApiError(e: unknown) {
  const err = e as ApiError | null;
  if (err?.status !== 400) return;
  const message = Object.values(err.errors ?? {}).flat().find((m) => !!m);
  if (message) Notify.create({ type: 'negative', message });
}
watch(recordsError, notifyApiError);
/** View writes: 403 = shared views need designer Edit (or reference fields the editor cannot see). */
function notifyViewError(e: unknown) {
  if (isForbidden(e)) Notify.create({ type: 'negative', message: t('dynamic.forbidden') });
  else notifyApiError(e);
}

/** 編號與數值欄加 cell-code：指令台風格用等寬字對齊，其他風格不受影響。 */
const CODE_TYPES = new Set<FieldType>(['autonumber', 'integer', 'decimal', 'currency', 'percent', 'rollup', 'formula']);

const columns = computed<QTableColumn<DynamicRecord>[]>(() => {
  const s = schema.value;
  if (!s) return [];
  const title = s.titleFieldKey ? allFields.value.find((f) => f.key === s.titleFieldKey) : undefined;
  const listed = effectiveColumns.value
    .map((id) => allFields.value.find((f) => f.id === id))
    .filter((f): f is NonNullable<typeof f> => !!f && f.key !== s.titleFieldKey);
  return [
    ...(s.kind === 'extension' ? [{
      name: 'title', label: t('designer.extension.hostEntity'), align: 'left' as const, sortable: false,
      field: (r: DynamicRecord) => r.title,
    }] : []),
    ...(s.titleFieldKey ? [{
      name: title?.key ?? 'title', label: s.hasTitleTemplate ? t('dynamic.titleColumn') : resolveI18n(title?.label, locale.value),
      align: title && isNumericField(title) ? 'right' as const : 'left' as const, sortable: title?.config.isSortable,
      field: (r: DynamicRecord) => r.title, classes: title && CODE_TYPES.has(title.type) ? 'cell-code' : undefined,
    }] : []),
    ...listed.map<QTableColumn<DynamicRecord>>((f) => ({
      name: f.key, label: resolveI18n(f.label, locale.value), align: isNumericField(f) ? 'right' : 'left', sortable: f.config.isSortable,
      classes: CODE_TYPES.has(f.type) ? 'cell-code' : undefined,
      field: (r) => (f.type === 'reference' && referenceView(r, f, t)?.text) || fieldTypeRegistry[f.type].format(r.values[f.key], f, locale.value),
    })),
    {
      name: 'modifiedTime', label: t('dynamic.modifiedTime'), align: 'left',
      field: (r) => date.formatDate(new Date(r.modifiedTime), 'YYYY-MM-DD HH:mm'),
    },
  ];
});

/** 手機卡片：標題列放記錄標題與流程狀態，其餘欄以欄名／值排在下面（空值不顯示）。 */
type CardColumn = { name: string; label: string; value: unknown; classes?: unknown };
const cardColumns = (cols: CardColumn[]) => {
  const s = schema.value;
  const inHeader = new Set(['title', s?.titleFieldKey, s?.workflow?.statusFieldKey]);
  return cols.filter((c) => !inHeader.has(c.name) && c.value !== '');
};
/** 狀態欄不在目前顯示的欄裡也要出現在卡片上，所以直接從記錄值取。 */
const statusChip = (row: DynamicRecord) => statusChipOf(schema.value, row.values, locale.value);

/** q-table shows a sort arrow only when exactly one column is sorted. */
const tableSort = computed(() => {
  const only = sort.value.length === 1 ? sort.value[0]! : undefined;
  const f = only && allFields.value.find((x) => x.id === only.field);
  return f ? { sortBy: f.key, descending: only.order === 'desc' } : { sortBy: null, descending: false };
});
const pagination = computed(() => ({
  page: page.value, rowsPerPage: size, rowsNumber: pageData.value?.totalCount ?? 0, ...tableSort.value,
}));

/** Header click replaces the sort with that single column; paging only moves the page. Idempotent, so safe for both events. */
function onPagination(p: { page?: number; sortBy?: string | null; descending?: boolean }) {
  const sortBy = p.sortBy ?? null;
  const descending = !!p.descending;
  if (sortBy !== tableSort.value.sortBy || (sortBy !== null && descending !== tableSort.value.descending)) {
    const f = allFields.value.find((x) => x.key === sortBy);
    sort.value = f ? [{ field: f.id, order: descending ? 'desc' : 'asc' }] : [];
    page.value = 1;
  } else {
    page.value = p.page ?? page.value;
  }
}

function setFilter(v: ConditionNode | undefined) { filter.value = v; page.value = 1; }
function setSort(v: ViewSort[]) { sort.value = v; page.value = 1; }
function setQ(v: string | number | null) { q.value = v == null ? '' : String(v); page.value = 1; }
const approvalOptions = computed(() => (['all', 'pending', 'none'] as const).map((value) => ({ value, label: t(`dynamic.approvalFilter.${value}`) })));
function setApproval(v: 'all' | ApprovalListFilter) { approval.value = v === 'all' ? undefined : v; page.value = 1; }
// 切換列隨 schema 消失（最後一條需簽核轉移被關掉）時，看不到也解不掉的條件要一起清掉，清單與匯出才不會帶著它
watch(() => !!schema.value?.approvalEnabled, (enabled) => {
  if (!enabled && approval.value) { approval.value = undefined; page.value = 1; }
});

const createView = useCreateView();
const updateView = useUpdateView();
const deleteView = useDeleteView();
const currentBody = (isDefault: boolean) => ({
  columns: effectiveColumns.value, filter: pruneCondition(filter.value) ?? null, sort: sort.value, isDefault,
});

function saveAs(v: { name: I18nText; shared: boolean; isDefault: boolean }) {
  createView.mutate({ moduleKey: moduleKey.value, body: { ...v, ...currentBody(v.isDefault) } }, {
    onSuccess: (created) => { currentViewId.value = created.id; },
    onError: notifyViewError,
  });
}
function updateCurrent() {
  const v = currentView.value;
  if (v) updateView.mutate({ moduleKey: moduleKey.value, id: v.id, body: { name: v.name, ...currentBody(v.isDefault) } }, { onError: notifyViewError });
}
function setDefault() {
  const v = currentView.value;
  if (!v) return;
  const body = { name: v.name, columns: v.columns, filter: v.filter ?? null, sort: v.sort, isDefault: true };
  updateView.mutate({ moduleKey: moduleKey.value, id: v.id, body }, { onError: notifyViewError });
}
function removeCurrent() {
  const v = currentView.value;
  if (!v) return;
  deleteView.mutate({ moduleKey: moduleKey.value, id: v.id }, {
    onSuccess: () => applyView(),
    onError: notifyViewError,
  });
}

/** 點整列進明細：給 q-table 的列 class（hover、手指、手機箭頭） */
const rowClass = () => 'row-clickable row-chevron';
const openRecord = (row: DynamicRecord) => router.push(`/dynamic/${moduleKey.value}/${row.id}`);
</script>

<template>
  <q-page padding>
    <ForbiddenNotice v-if="forbidden" />
    <div v-else-if="notFound" class="text-negative">{{ t('dynamic.moduleNotFound') }}</div>
    <div v-else-if="failed" class="text-negative">{{ t('dynamic.loadFailed') }}</div>
    <template v-else-if="schema">
      <!-- 標題、檢視、搜尋、篩選／排序／欄位與新增／匯入／匯出併成一列；手機上檢視與三顆按鈕一列、關鍵字獨佔一列 -->
      <div class="row items-center q-mb-sm list-toolbar" data-testid="list-toolbar">
        <div class="text-h6 list-toolbar__title">{{ resolveI18n(schema.name, locale) }}</div>
        <ViewMenu
          class="list-toolbar__view" :views="views" :current="currentView" :modified="modified"
          @select="(id) => applyView(views.find((v) => v.id === id))"
          @save-as="saveAs" @update="updateCurrent" @remove="removeCurrent" @set-default="setDefault"
        />
        <q-input
          :model-value="q" :placeholder="t('dynamic.view.search')" :aria-label="t('dynamic.view.search')" maxlength="100" debounce="300" clearable outlined dense
          class="list-toolbar__keyword" data-testid="keyword" @update:model-value="setQ"
        >
          <template #prepend><q-icon name="search" /></template>
        </q-input>
        <ToolbarPanel icon="filter_list" testid="toolbar-filter" :label="t('dynamic.filter.title')" :count="countConditions(filter)">
          <FilterPanel :schema="schema" :model-value="filter" expanded @update:model-value="setFilter" />
        </ToolbarPanel>
        <ToolbarPanel icon="sort" testid="toolbar-sort" :label="t('dynamic.sort.title')" :count="sort.length">
          <SortEditor :schema="schema" :model-value="sort" @update:model-value="setSort" />
        </ToolbarPanel>
        <ToolbarPanel icon="view_column" testid="toolbar-columns" :label="t('dynamic.columns.button')">
          <ColumnChooser :schema="schema" :model-value="effectiveColumns" @update:model-value="(v) => (columnIds = v)" />
        </ToolbarPanel>
        <q-space class="list-toolbar__space" />
        <div class="row items-center list-toolbar__actions">
          <q-btn v-if="schema.permissions.add" color="primary" icon="add" data-testid="new-btn" :label="t('dynamic.new')" :to="`/dynamic/${moduleKey}/new`" />
          <q-btn v-if="canImport" outline icon="upload" data-testid="import-btn" :label="t('dynamic.import.title')" @click="importing = true" />
          <ExportMenu :schema="schema" :query="exportQuery" />
        </div>
      </div>
      <div v-if="schema.readOnly" class="row items-center no-wrap q-mb-sm text-muted" data-testid="closed-banner">
        <q-icon name="lock" class="q-mr-sm" />
        <span>{{ t('dynamic.closedBanner') }}</span>
      </div>
      <div v-if="currentView?.staleFieldIds.length" class="text-warning q-mb-sm" data-testid="stale-notice">
        {{ t('dynamic.view.stale', { n: currentView.staleFieldIds.length }) }}
      </div>
      <div v-if="schema.approvalEnabled" class="row items-center q-mb-sm" style="gap: 8px" data-testid="approval-filter">
        <span class="text-muted">{{ t('dynamic.approvalFilter.label') }}</span>
        <q-btn-toggle
          :model-value="approval ?? 'all'" :options="approvalOptions" :dense="!$q.screen.lt.sm" no-caps unelevated toggle-color="primary"
          :aria-label="t('dynamic.approvalFilter.label')" role="group" data-testid="approval-toggle"
          @update:model-value="(v) => setApproval(v as 'all' | ApprovalListFilter)"
        />
      </div>
      <q-table
        flat bordered row-key="id" :rows="pageData?.items ?? []" :columns="columns" :grid="$q.screen.lt.sm"
        :loading="recordsQuery.isFetching.value" :pagination="pagination" :rows-per-page-options="[20]"
        :no-data-label="t('dynamic.noData')" :table-row-class-fn="rowClass"
        @request="(p) => onPagination(p.pagination)"
        @update:pagination="onPagination"
        @row-click="(_e, row) => openRecord(row)"
      >
        <template v-if="schema.workflow" #[`body-cell-${schema.workflow.statusFieldKey}`]="cell">
          <q-td :props="cell"><StatusChip v-if="statusChip(cell.row)" v-bind="statusChip(cell.row)!" /></q-td>
        </template>
        <template #no-data="{ message }">
          <!-- 只有目前條件的查詢成功回來才說沒有資料；載入中、失敗（含 400）、暫時沿用前次結果時照舊顯示表格的文字 -->
          <EmptyState
            v-if="recordsQuery.isSuccess.value && !recordsQuery.isPlaceholderData.value && !recordsQuery.isFetching.value"
            :message="filtered ? t('dynamic.list.emptyFiltered') : t('dynamic.list.emptyAll', { module: resolveI18n(schema.name, locale) })"
            :hint="filtered ? t('dynamic.list.emptyFilteredHint') : undefined"
          >
            <template v-if="!filtered && schema.permissions.add" #action>
              <q-btn color="primary" no-caps icon="add" data-testid="empty-new-btn" :label="t('dynamic.new')" :to="`/dynamic/${moduleKey}/new`" />
            </template>
          </EmptyState>
          <div v-else class="full-width">{{ message }}</div>
        </template>
        <template #item="{ row, cols }">
          <div class="col-12 q-py-xs">
            <q-card
              flat bordered class="cursor-pointer card-clickable" tabindex="0" role="link" :aria-label="row.title" :data-testid="`record-card-${row.id}`"
              @click="openRecord(row)" @keydown.enter.self.prevent="openRecord(row)" @keydown.space.self.prevent="openRecord(row)"
            >
              <q-card-section class="row items-start no-wrap q-gutter-x-sm q-py-sm" data-testid="record-card-header">
                <div class="col text-subtitle2" style="min-width: 0; overflow-wrap: anywhere" data-testid="record-card-title">{{ row.title }}</div>
                <!-- 狀態文字可能很長：限寬、可縮小、可斷行，不把卡片撐出畫面 -->
                <div v-if="statusChip(row)" style="max-width: 50%; min-width: 0; overflow-wrap: anywhere" data-testid="record-card-status">
                  <StatusChip v-bind="statusChip(row)!" />
                </div>
              </q-card-section>
              <q-list dense data-testid="record-card-fields">
                <q-item v-for="c in cardColumns(cols)" :key="c.name">
                  <q-item-section>
                    <q-item-label caption>{{ c.label }}</q-item-label>
                    <q-item-label :class="{ 'cell-code': c.classes === 'cell-code' }" style="overflow-wrap: anywhere">{{ c.value }}</q-item-label>
                  </q-item-section>
                </q-item>
              </q-list>
            </q-card>
          </div>
        </template>
      </q-table>
      <ImportDialog v-if="canImport" v-model="importing" :schema="schema" />
    </template>
  </q-page>
</template>

<style scoped>
.list-toolbar { gap: 8px; }
.list-toolbar__keyword { min-width: 14rem; }
.list-toolbar__actions { gap: 8px; }
/* 手機（<600）：標題一列；檢視＋篩選／排序／欄位一列；關鍵字獨佔一列；其餘動作排在後面 */
@media (max-width: 599.98px) {
  .list-toolbar__title { flex: 0 0 100%; order: -1; }
  .list-toolbar__view { flex: 1 1 0; }
  .list-toolbar__keyword { flex: 0 0 100%; order: 1; }
  .list-toolbar__space { display: none; }
  .list-toolbar__actions { order: 2; }
}
</style>
