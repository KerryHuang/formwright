<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRoute, useRouter } from 'vue-router';
import { useRecordApproval } from 'src/application/dynamic/queries/approval-queries';
import { useRecordConversions } from 'src/application/dynamic/queries/conversions-queries';
import { useDeleteRecord, useRecord } from 'src/application/dynamic/queries/records-queries';
import { useModuleSchema, useRecordScopedSchema } from 'src/application/dynamic/queries/schema-queries';
import { recordRecentRecord } from 'src/application/dynamic/recent-records';
import { hasApprovalActions } from 'src/presentation/dynamic/approval/approval-actions';
import ApprovalHistoryOnly from 'src/presentation/dynamic/approval/ApprovalHistoryOnly.vue';
import ApprovalPanel from 'src/presentation/dynamic/approval/ApprovalPanel.vue';
import ApprovalBadge from 'src/presentation/dynamic/approval/ApprovalBadge.vue';
import SubmitApprovalDialog from 'src/presentation/dynamic/approval/SubmitApprovalDialog.vue';
import PrintPreviewDialog from 'src/presentation/dynamic/print/PrintPreviewDialog.vue';
import { usePrintAction } from 'src/presentation/dynamic/print/use-print-action';
import ConvertDialog from 'src/presentation/dynamic/conversions/ConvertDialog.vue';
import RecordConversions from 'src/presentation/dynamic/conversions/RecordConversions.vue';
import RecordEffects from 'src/presentation/dynamic/events/RecordEffects.vue';
import RecordLedger from 'src/presentation/dynamic/ledger/RecordLedger.vue';
import RecordHeaderBar, { type HeaderAction } from 'src/presentation/dynamic/components/RecordHeaderBar.vue';
import DynamicDetail from 'src/presentation/dynamic/components/DynamicDetail.vue';
import DetailTables from 'src/presentation/dynamic/details/DetailTables.vue';
import RecordUnavailable from 'src/presentation/dynamic/components/RecordUnavailable.vue';
import StatusChip from 'src/presentation/dynamic/components/StatusChip.vue';
import { statusChipOf } from 'src/presentation/dynamic/status-chip';
import { ruleStateOf } from 'src/presentation/dynamic/rule-state';
import { unavailableView, type UnavailableKind } from 'src/presentation/dynamic/unavailable';
import DeleteBlockedDialog from 'src/presentation/dynamic/related/DeleteBlockedDialog.vue';
import RecordHistory from 'src/presentation/dynamic/history/RecordHistory.vue';
import RecordTasks from 'src/presentation/dynamic/tasks/RecordTasks.vue';
import RelatedTabs from 'src/presentation/dynamic/related/RelatedTabs.vue';
import type { SubmitApprovalResult } from 'src/domain/dynamic/approval-types';
import type { DeleteBlocked, RecordConversion } from 'src/domain/dynamic/types';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import { canAdvance, isReadOnlyState } from 'src/domain/dynamic/workflow';
import { isStale } from 'src/presentation/dynamic/designer/api-error';
import { newIdempotencyKey } from 'src/services/api/idempotency';
import { isForbidden, type ApiError } from 'src/services/api/http';
import { useIdentityStore } from 'src/stores/use-identity-store';

const { t, locale } = useI18n();
const $q = useQuasar();
const route = useRoute();
const router = useRouter();
const moduleKey = computed(() => String(route.params.moduleKey));
const recordId = computed(() => Number(route.params.id));

// Opened from the approval center (?via=approval): the user may hold only the temporary read right, so ask for the record-scoped schema
// right away instead of sending a plain request that would be refused. Without the hint (notification links etc.) the plain schema
// goes out in parallel with the record, and only a refusal falls back to the record-scoped one.
const viaApproval = computed(() => route.query.via === 'approval');
const schemaQuery = useModuleSchema(moduleKey, computed(() => !viaApproval.value));
const viaGrant = computed(() => viaApproval.value || isForbidden(schemaQuery.error.value));
const grantSchemaQuery = useRecordScopedSchema(moduleKey, recordId, viaGrant);
const schema = computed(() => schemaQuery.data.value ?? grantSchemaQuery.data.value);
const moduleName = computed(() => resolveI18n(schema.value?.name, locale.value));
// While the plain schema is refused (or skipped), the record-scoped request decides what went wrong.
const schemaError = computed(() => (viaGrant.value ? grantSchemaQuery.error.value : schemaQuery.error.value));
const recordQuery = useRecord(recordId);
const del = useDeleteRecord();
const identity = useIdentityStore();
const confirming = ref(false);
const blocked = ref<DeleteBlocked | null>(null);
// Kept across a failed attempt so a resend takes effect once; replaced after a success or a 422 (key used for other content).
let deleteKey = newIdempotencyKey();

const status = (e: unknown) => (e as ApiError | null)?.status;
const schemaMissing = computed(() => status(schemaError.value) === 404);
const recordMissing = computed(() => status(recordQuery.error.value) === 404
  || (!!recordQuery.data.value && recordQuery.data.value.moduleKey !== moduleKey.value));
// The record is out of reach (the temporary right ended with the task, or it never was there): the approval history may still be.
const recordGone = computed(() => [403, 404].includes(status(recordQuery.error.value) ?? 0));
// A failed refetch keeps the last good data: only a successful answer may be shown as history (else the stale timeline outlives the task).
const historyOnly = computed(() => recordGone.value && approvalQuery.isSuccess.value
  && !!(approvalQuery.data.value?.current || approvalQuery.data.value?.previous.length));
const historyPending = computed(() => recordGone.value && approvalQuery.isPending.value);
const forbidden = computed(() => isForbidden(schemaError.value) || isForbidden(recordQuery.error.value));
const failed = computed(() => !forbidden.value && !schemaMissing.value && !recordMissing.value
  && (!!schemaError.value || !!recordQuery.error.value));

// Why the page cannot show the record (the order below is the display priority); undefined = nothing is wrong.
const problem = computed<UnavailableKind | undefined>(() => {
  if (forbidden.value) return 'forbidden';
  if (schemaMissing.value) return 'moduleMissing';
  if (recordMissing.value) return 'recordMissing';
  return failed.value ? 'failed' : undefined;
});
// Without the module name (schema pending or failed) the view uses generic wording and goes home; it follows the schema when it answers.
const unavailable = computed(() => problem.value
  && unavailableView(problem.value, { t, moduleKey: moduleKey.value, moduleName: moduleName.value, schemaRead: !!schema.value?.permissions }));
function retry() {
  if (schemaError.value) void (viaGrant.value ? grantSchemaQuery : schemaQuery).refetch();
  if (recordQuery.error.value) void recordQuery.refetch();
}

// A read-only workflow state locks the record: no delete, and edit only while the user can still move the status.
// A record an event has already posted is locked the same way (only the status may change: that is the cancel flow).
const workflow = computed(() => schema.value?.workflow);
const currentState = computed(() => (workflow.value ? recordQuery.data.value?.values[workflow.value.statusFieldKey] : undefined));
const frozen = computed(() => !!recordQuery.data.value?.frozenByEffects);
const stateLocked = computed(() => isReadOnlyState(workflow.value, currentState.value) || frozen.value);
// While an approval is running the record is read-only (the server answers 409 to PUT and DELETE); withdraw first.
const approval = computed(() => recordQuery.data.value?.approval ?? null);
const approvalPending = computed(() => approval.value?.status === 'pending');
const grant = computed(() => !!recordQuery.data.value?.approvalGrant);
const canEdit = computed(() => !grant.value && !!schema.value?.permissions.edit && !approvalPending.value && (!stateLocked.value || canAdvance(workflow.value, currentState.value)));
const canDelete = computed(() => !grant.value && !!schema.value?.permissions.delete && !stateLocked.value && !approvalPending.value);

// No summary = the module has no approval for this record: no badge, no request.
const canSubmit = computed(() => !grant.value && !!approval.value?.canSubmit && !schema.value?.readOnly);
// The block shows whenever the record has an approval summary (also after the last stage); the submit buttons need canSubmit.
const approvalQuery = useRecordApproval(recordId, () => !!approval.value || recordGone.value);
const statusField = computed(() => schema.value?.blocks.flatMap((b) => b.fields).find((f) => f.key === workflow.value?.statusFieldKey));
const stateLabel = (optionId: number) => resolveI18n(statusField.value?.options.find((o) => o.value === `opt_${optionId}`)?.label, locale.value);
// The status chip next to the title, so the state stays in sight while scrolling; a rule-hidden status field shows none.
const statusChip = computed(() => {
  const s = schema.value;
  const values = recordQuery.data.value?.values;
  const field = statusField.value;
  if (!s || !values || !field || ruleStateOf(s, values, values).hidden.has(field.id)) return undefined;
  return statusChipOf(s, values, locale.value);
});
const submitTo = ref<number | null>(null);
function onSubmitted(outcome: SubmitApprovalResult['outcome']) {
  const state = submitTo.value === null ? '' : stateLabel(submitTo.value);
  $q.notify({ type: 'positive', message: t(`approval.submit.outcome.${outcome}`, { state }) });
}
// On a phone the approval action bar is fixed to the bottom: leave room so the last block is not covered.
// Then the record's own actions do not open a second bottom bar; they go to a More menu in the top bar instead.
const phone = computed(() => $q.screen.lt.sm);
const approvalBar = computed(() => phone.value && !schema.value?.readOnly && hasApprovalActions(approvalQuery.data.value?.current ?? null, identity.userId));
const submitTargets = computed(() => (canSubmit.value ? approvalQuery.data.value?.submitTargets ?? [] : []).filter((to) => stateLabel(to)));

// The convert and print buttons only trigger: their dialogs live here, outside the (phone) menu that unmounts when it closes.
// Asked for only where the content below is shown (same conditions as the template chain), and never for a temporary-read grant.
const contentShown = computed(() => !historyPending.value && !historyOnly.value && !forbidden.value && !schemaMissing.value && !recordMissing.value && !failed.value
  && !!schema.value && !!recordQuery.data.value);
// 首頁的「最近開過的單據」：單據內容顯示出來才記（刪掉、沒權限、模組對不上的不記）
watch(() => (contentShown.value ? recordQuery.data.value?.id : undefined), (id) => {
  if (id !== undefined) recordRecentRecord(identity.userId, moduleKey.value, id);
}, { immediate: true });
const conversionsQuery = useRecordConversions(recordId, computed(() => contentShown.value && !grant.value));
const convertKey = ref<string | null>(null);
const convertOpen = computed(() => (conversionsQuery.data.value ?? []).find((c) => c.key === convertKey.value));
const convertTargetName = (c: RecordConversion) => resolveI18n(c.targetName, locale.value);
const print = usePrintAction(moduleKey, recordId);

const headerActions = computed<HeaderAction[]>(() => {
  const list: HeaderAction[] = submitTargets.value.map((to) => ({
    key: `submit-${to}`, label: t('approval.submitTo', { state: stateLabel(to) }), icon: 'rule', color: 'primary',
    testid: `submit-approval-${to}`, run: () => { submitTo.value = to; },
  }));
  if (!grant.value) {
    for (const c of (conversionsQuery.data.value ?? []).filter((x) => x.allowed)) {
      list.push({
        key: `convert-${c.key}`, label: t('docflow.convert.button', { name: convertTargetName(c) }), icon: 'swap_horiz', outline: true, color: 'primary',
        testid: `convert-open-${c.key}`, run: () => { convertKey.value = c.key; },
      });
    }
  }
  if (print.items.value.length) {
    list.push({
      key: 'print', label: t('print.print'), icon: 'print', loading: print.busy.value, testid: 'print-menu',
      children: print.items.value.map((o) => ({
        key: o.key, label: o.name, menuLabel: t('print.printNamed', { name: o.name }), testid: `print-${o.key}`, disable: print.busy.value, run: () => void print.show(o.key, o.name),
      })),
    });
  }
  if (canEdit.value) list.push({ key: 'edit', label: t('dynamic.edit'), color: 'primary', to: `/dynamic/${moduleKey.value}/${recordId.value}/edit`, bottom: true });
  if (canDelete.value) list.push({ key: 'delete', label: t('dynamic.delete'), color: 'negative', outline: true, negative: true, run: () => { confirming.value = true; } });
  // Phone: one main button on the bottom bar: Edit, else the first submit target.
  const main = list.find((a) => a.key === 'edit') ?? list.find((a) => a.key.startsWith('submit-'));
  if (main) main.bottom = true;
  return list;
});
const bottomBar = computed(() => phone.value && !approvalBar.value && headerActions.value.length > 0);
const barPad = computed(() => approvalBar.value || bottomBar.value);

async function onDelete() {
  try {
    await del.mutateAsync({ moduleKey: moduleKey.value, id: recordId.value, rowVersion: recordQuery.data.value!.rowVersion, idempotencyKey: deleteKey });
    deleteKey = newIdempotencyKey();
    await router.push(`/dynamic/${moduleKey.value}`);
  } catch (e) {
    const err = e as ApiError;
    if (err.status === 422) deleteKey = newIdempotencyKey();
    if (err.status === 409 && err.references) {
      blocked.value = { message: err.message ?? '', references: err.references, total: err.total ?? err.references.length };
    } else if (err.status === 409 && err.message && !isStale(err)) {
      $q.notify({ type: 'warning', message: err.message });
    } else if (err.status === 409) {
      $q.notify({
        type: 'warning', message: t('dynamic.stale'),
        actions: [{ label: t('dynamic.reload'), color: 'white', handler: () => void recordQuery.refetch() }],
      });
    } else $q.notify({ type: 'negative', message: err.message ?? String(err.status) });
  } finally {
    confirming.value = false;
  }
}
</script>

<template>
  <q-page padding :style="barPad ? 'padding-bottom: calc(88px + env(safe-area-inset-bottom))' : undefined">
    <template v-if="historyPending" />
    <ApprovalHistoryOnly v-else-if="historyOnly" :approval="approvalQuery.data.value!" :record-id="recordId" :module-key="moduleKey" />
    <RecordUnavailable v-else-if="unavailable" v-bind="unavailable" :retry="problem === 'failed' ? retry : undefined" />
    <template v-else-if="schema && recordQuery.data.value">
      <RecordHeaderBar
        :back-label="moduleName" :back-to="`/dynamic/${moduleKey}`" :back-aria-label="t('dynamic.backTo', { module: moduleName })" back-testid="module-title"
        :title="schema.titleFieldKey || schema.kind === 'extension' ? recordQuery.data.value.title : undefined"
        :actions="headerActions" :menu-only="approvalBar"
      >
        <template #badge>
          <ApprovalBadge v-if="approval" :summary="approval" />
          <StatusChip v-if="statusChip" v-bind="statusChip" />
        </template>
      </RecordHeaderBar>
      <div v-if="grant" class="row items-center no-wrap q-mb-sm text-muted" data-testid="approval-grant-banner">
        <q-icon name="visibility" class="q-mr-sm" />
        <span>{{ t('approval.grantBanner') }}</span>
      </div>
      <div v-if="schema.readOnly" class="row items-center no-wrap q-mb-sm text-muted" data-testid="closed-banner">
        <q-icon name="lock" class="q-mr-sm" />
        <span>{{ t('dynamic.closedBanner') }}</span>
      </div>
      <div v-if="approvalPending" class="row items-center no-wrap q-mb-sm text-muted" data-testid="approval-pending-readonly">
        <q-icon name="lock" class="q-mr-sm" />
        <span>{{ t('approval.pendingReadOnly') }}</span>
      </div>
      <div v-if="frozen" class="row items-center no-wrap q-mb-sm text-muted" data-testid="frozen-notice">
        <q-icon name="lock" class="q-mr-sm" />
        <span>{{ t('ledger.frozen') }}</span>
      </div>
      <RecordLedger v-if="schema.ledger && !grant" :record-id="recordId" :module-key="moduleKey" />
      <RecordConversions v-if="!grant" :record-id="recordId" :closed="schema.readOnly" />
      <ApprovalPanel
        v-if="approval && approvalQuery.data.value" :approval="approvalQuery.data.value" :schema="schema" :record="recordQuery.data.value" :record-id="recordId" :module-key="moduleKey"
        :readonly="!!schema.readOnly"
      />
      <DynamicDetail :schema="schema" :record="recordQuery.data.value" />
      <DetailTables v-if="schema.details?.length" class="q-mt-md" :details="schema.details" :rows="recordQuery.data.value.details ?? {}" />
      <RecordTasks v-if="schema.tasksEnabled !== false && !grant" :record-id="recordId" :closed="!!schema.readOnly" />
      <template v-if="!grant">
        <RecordEffects :record-id="recordId" />
        <RelatedTabs :record-id="recordId" />
        <RecordHistory :record-id="recordId" />
      </template>
      <SubmitApprovalDialog
        v-if="submitTo !== null" :model-value="true" :record-id="recordId" :module-key="moduleKey" :to="submitTo" :to-label="stateLabel(submitTo)"
        :record-row-version="recordQuery.data.value.rowVersion" @update:model-value="submitTo = null" @submitted="onSubmitted"
      />
      <ConvertDialog
        v-if="convertOpen" :model-value="true" :record-id="recordId" :row-version="recordQuery.data.value.rowVersion" :conversion="convertOpen"
        @update:model-value="convertKey = null"
      />
      <PrintPreviewDialog v-if="print.items.value.length" v-model="print.open.value" :html="print.html.value" :title="print.title.value" />
      <DeleteBlockedDialog v-if="blocked" :model-value="true" :blocked="blocked" @update:model-value="blocked = null" />
      <q-dialog v-model="confirming">
        <q-card>
          <q-card-section>{{ t('dynamic.confirmDelete') }}</q-card-section>
          <q-card-actions align="right">
            <q-btn flat :label="t('dynamic.cancel')" v-close-popup />
            <q-btn color="negative" :label="t('dynamic.delete')" :loading="del.isPending.value" @click="onDelete" />
          </q-card-actions>
        </q-card>
      </q-dialog>
    </template>
  </q-page>
</template>
