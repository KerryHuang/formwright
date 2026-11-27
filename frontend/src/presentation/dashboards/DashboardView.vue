<script setup lang="ts">
import { Notify, useQuasar } from 'quasar';
import { computed, ref, toRef, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import {
  useDashboard, useRestoreDashboard, useSaveDashboardLayout,
} from 'src/application/dynamic/queries/dashboards-queries';
import {
  addCard, applyPositions, cardQuerySignature, FILTER_PRESETS, removeCard, sameLayout, updateCard,
} from 'src/domain/dynamic/dashboard-layout';
import type { CardOptions, DashboardLayout } from 'src/domain/dynamic/dashboard-types';
import { compactI18n, resolveI18n, sameI18n } from 'src/domain/dynamic/i18n-text';
import type { ReportColumn, ReportViz } from 'src/domain/dynamic/report-types';
import type { I18nText } from 'src/domain/dynamic/types';
import I18nTextInput from 'src/presentation/dynamic/designer/I18nTextInput.vue';
import AddCardDialog from './AddCardDialog.vue';
import CardOptionsDialog from './CardOptionsDialog.vue';
import DashboardCardView from './DashboardCardView.vue';
import DashboardGrid from './DashboardGrid.vue';

/**
 * 一張儀表板：標題列、暫時的日期篩選、個人版提示、卡片版面。拖放與卡片異動都立刻存檔
 * （指派版由後端另存個人版，自己的儀表板直接存）；日期篩選只在畫面上，不存、也不算調整。
 * allowEdit 為 false 時（首頁）只能看，不能改版面。自己的儀表板在編輯狀態可「重新命名」（對話框改各語系，名稱多語設計 Q2）。
 * linkable 的名稱是父元件依目前語系解析好的字串。
 */
const props = defineProps<{ dashboardId: number; allowEdit: boolean; linkable: { id: number; name: string }[] }>();
const { t, locale } = useI18n();
const $q = useQuasar();
const narrow = computed(() => $q.screen.lt.sm);

const dash = useDashboard(toRef(props, 'dashboardId'));
const save = useSaveDashboardLayout();
const restore = useRestoreDashboard();
const detail = computed(() => dash.data.value);

const layout = ref<DashboardLayout>({ cards: [] });
let inFlight = 0;
// 儲存進行中不要被較舊的回應蓋掉本機的版面
watch(detail, (d) => { if (d && inFlight === 0) layout.value = d.layout; }, { immediate: true });
watch(() => props.dashboardId, () => { editing.value = false; filterKey.value = null; });

const editing = ref(false);
const saveError = ref(false);
let chain: Promise<unknown> = Promise.resolve();
// 卡片查詢由後端照存好的版面執行：新增、或改了查詢內容的卡片，存好之前不查（新卡片會 404，改過的會拿到舊設定的結果）。
// 保證範圍只到同一個已掛載的 view：存檔中切走再回來，新的 view 不知道舊的存檔鏈，會照當時已存（或快取）的版面查。
const unsaved = ref(new Set<string>());
function persist(next: DashboardLayout, name?: I18nText) {
  // 送出當下畫面上的版本：第一次另存個人版時伺服器拿它當基準，管理員在這之後的更新才會顯示「已更新」
  const loadedVersion = detail.value?.version;
  const before = new Map(layout.value.cards.map((c) => [c.id, cardQuerySignature(c)]));
  unsaved.value = new Set([...unsaved.value, ...next.cards.filter((c) => before.get(c.id) !== cardQuerySignature(c)).map((c) => c.id)]);
  layout.value = next;
  saveError.value = false;
  inFlight++;
  chain = chain
    .then(() => save.mutateAsync({ id: props.dashboardId, layout: next, name, version: loadedVersion }))
    .catch(() => { saveError.value = true; })
    .finally(() => {
      inFlight--;
      if (inFlight === 0 && detail.value) layout.value = saveError.value ? detail.value.layout : layout.value;
      if (inFlight === 0) unsaved.value = new Set();
    });
}

const filterKey = ref<string | null>(null);
const filterRange = computed(() => {
  const p = FILTER_PRESETS.find((x) => x.key === filterKey.value);
  return p ? { kind: p.kind, n: p.n ?? null } : null;
});
const filterOptions = computed(() => FILTER_PRESETS.map((p) => ({ value: p.key, label: t(`dashboards.filter.${p.key}`) })));

const isOwn = computed(() => detail.value?.kind === 'mine');
const displayName = computed(() => resolveI18n(detail.value?.name, locale.value));
const renameOpen = ref(false);
const nameDraft = ref<I18nText>({});
const nameOriginal = ref<I18nText>({});
const nameInput = ref<{ validate(): Promise<boolean> } | null>(null);
function openRename() {
  // original 要是獨立的副本（I18nTextInput 用它判斷「沒改就不檢查新增的必填語系」）
  nameOriginal.value = { ...(detail.value?.name ?? {}) };
  nameDraft.value = { ...nameOriginal.value };
  renameOpen.value = true;
}
async function submitRename() {
  if (!(await nameInput.value?.validate())) return;
  renameOpen.value = false;
  const name = compactI18n(nameDraft.value) ?? {};
  if (!sameI18n(name, detail.value?.name)) persist(layout.value, name);
}

function onMove(positions: { i: string; x: number; y: number; w: number; h: number }[]) {
  const next = applyPositions(layout.value, positions);
  if (!sameLayout(next, layout.value)) persist(next);
}

const addOpen = ref(false);
function onAdd(reportId: number, viz: ReportViz) {
  addOpen.value = false;
  persist(addCard(layout.value, reportId, viz));
}

const optionsCardId = ref<string | null>(null);
const optionsColumns = ref<ReportColumn[]>([]);
const optionsCard = computed(() => layout.value.cards.find((c) => c.id === optionsCardId.value) ?? null);
function openOptions(cardId: string, columns: ReportColumn[]) { optionsColumns.value = columns; optionsCardId.value = cardId; }
function onOptionsSave(change: { title: I18nText | null; viz: ReportViz; options: CardOptions }) {
  const id = optionsCardId.value!;
  optionsCardId.value = null;
  persist(updateCard(layout.value, id, change));
}

const confirmRestore = ref(false);
function doRestore() {
  restore.mutate(props.dashboardId, {
    onSuccess: () => { confirmRestore.value = false; Notify.create({ type: 'positive', message: t('dashboards.restored') }); },
    onError: () => { confirmRestore.value = false; Notify.create({ type: 'negative', message: t('dashboards.restoreFailed') }); },
  });
}
const linkVisible = (id: number | null | undefined) => id != null && props.linkable.some((d) => d.id === id);
</script>

<template>
  <div data-testid="dashboard-view">
    <div v-if="dash.isError.value" class="text-negative" data-testid="dashboard-error">{{ t('dashboards.notFound') }}</div>
    <div v-else-if="dash.isPending.value" role="status" aria-busy="true" :aria-label="t('designer.loading')" data-testid="loading-skeleton">
      <q-skeleton type="rect" height="32px" width="40%" class="q-mb-sm" />
      <div class="row q-col-gutter-sm">
        <div v-for="n in 4" :key="n" class="col-12 col-sm-6"><q-skeleton type="rect" height="160px" /></div>
      </div>
    </div>
    <template v-else-if="detail">
      <div class="row items-center q-gutter-sm q-mb-sm">
        <div class="text-h6" data-testid="dashboard-name">{{ displayName }}</div>
        <q-btn v-if="editing && isOwn" flat dense no-caps icon="drive_file_rename_outline" :label="t('dashboards.rename')" data-testid="dashboard-rename" @click="openRename" />
        <q-badge v-if="detail.adjusted" color="primary" data-testid="dashboard-adjusted">{{ t('dashboards.adjusted') }}</q-badge>
        <q-space />
        <q-select
          v-model="filterKey" :options="filterOptions" emit-value map-options clearable dense outlined style="min-width: 180px"
          :label="t('dashboards.filter.label')" :hint="filterKey ? t('dashboards.filter.hint') : undefined" data-testid="dashboard-filter"
        />
        <template v-if="allowEdit">
          <q-btn v-if="editing" outline no-caps icon="add" :label="t('dashboards.card.add')" data-testid="dashboard-add-card" @click="addOpen = true" />
          <q-btn
            :outline="!editing" :unelevated="editing" :color="editing ? 'primary' : undefined" no-caps :icon="editing ? 'check' : 'edit'"
            :label="editing ? t('dashboards.doneEditing') : t('dashboards.edit')" data-testid="dashboard-edit-toggle" @click="editing = !editing"
          />
        </template>
        <span v-if="save.isPending.value" class="text-caption text-muted" data-testid="dashboard-saving">{{ t('dashboards.saving') }}</span>
        <span v-if="saveError" class="text-caption text-negative" data-testid="dashboard-save-error">{{ t('dashboards.saveFailed') }}</span>
      </div>

      <q-banner v-if="detail.hasUpdate" rounded dense class="q-mb-sm bg-info-soft" data-testid="dashboard-update-banner">
        <span class="text-weight-bold">{{ t('dashboards.updated') }}</span>
        <span class="q-ml-sm">{{ t('dashboards.updatedHint') }}</span>
        <template #action>
          <q-btn flat no-caps :label="t('dashboards.restore')" data-testid="dashboard-restore" @click="confirmRestore = true" />
        </template>
      </q-banner>

      <div v-if="!layout.cards.length" class="text-muted q-pa-md" data-testid="dashboard-empty">
        {{ allowEdit ? t('dashboards.emptyEditable') : t('dashboards.empty') }}
      </div>
      <DashboardGrid v-else :cards="layout.cards" :editable="editing && !narrow" :narrow="narrow" @change="onMove">
        <template #card="{ card }">
          <DashboardCardView
            :dashboard-id="dashboardId" :card="card" :report-name="detail.reportNames[String(card.reportId)]" :filter-range="filterRange"
            :editing="editing" :link-visible="linkVisible(card.options.linkDashboardId)" :unsaved="unsaved.has(card.id)"
            @settings="openOptions" @remove="(id: string) => persist(removeCard(layout, id))"
          />
        </template>
      </DashboardGrid>
    </template>

    <AddCardDialog :open="addOpen" @close="addOpen = false" @add="onAdd" />
    <CardOptionsDialog
      :card="optionsCard" :columns="optionsColumns" :report-name="optionsCard ? resolveI18n(detail?.reportNames[String(optionsCard.reportId)], locale) : undefined"
      :dashboards="linkable.filter((d) => d.id !== dashboardId)" @close="optionsCardId = null" @save="onOptionsSave"
    />
    <q-dialog v-model="renameOpen">
      <q-card style="width: 420px; max-width: 95vw" data-testid="dashboard-rename-dialog">
        <q-card-section class="text-h6">{{ t('dashboards.rename') }}</q-card-section>
        <q-card-section>
          <I18nTextInput ref="nameInput" v-model="nameDraft" :original="nameOriginal" :maxlength="100" :label="t('dashboards.name')" />
        </q-card-section>
        <q-card-actions align="right">
          <q-btn flat :label="t('reports.cancel')" data-testid="rename-cancel" @click="renameOpen = false" />
          <q-btn color="primary" :label="t('reports.save')" data-testid="rename-save" @click="submitRename" />
        </q-card-actions>
      </q-card>
    </q-dialog>
    <q-dialog v-model="confirmRestore">
      <q-card style="width: 420px; max-width: 95vw">
        <q-card-section class="text-h6">{{ t('dashboards.restore') }}</q-card-section>
        <q-card-section>{{ t('dashboards.confirmRestore') }}</q-card-section>
        <q-card-actions align="right">
          <q-btn flat :label="t('reports.cancel')" data-testid="restore-cancel" @click="confirmRestore = false" />
          <q-btn color="primary" :label="t('dashboards.restore')" :loading="restore.isPending.value" data-testid="restore-confirm" @click="doRestore" />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </div>
</template>
