<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRoute, useRouter } from 'vue-router';
import { useQuasar } from 'quasar';
import { useQuery, useQueryClient } from '@tanstack/vue-query';
import { useAppUpgradePreview, useApplyAppUpgrade, useApps } from 'src/application/dynamic/queries/apps-queries';
import ForbiddenNotice from 'src/components/framework/ForbiddenNotice.vue';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import {
  applyErrorLines, CATEGORY_ORDER, confirmCounts, countByChip, DEFAULT_CATEGORIES, defaultSelection, filterItems, formatValue, groupItems, isLong,
  moduleNames, NOT_APPLICABLE, toggleItem,
} from 'src/presentation/dynamic/upgrade/upgrade-view';
import type { I18nText } from 'src/domain/dynamic/types';
import { dynamicApi, type UpgradeBody, type UpgradeGroup, type UpgradeGroupChoice, type UpgradeItem } from 'src/services/api/dynamic-api';
import { isForbidden, type ApiError } from 'src/services/api/http';

// 更新頁（套件更新設計 §10.2）：列出差異、勾選（相依連勾）、群組對應、套用；能不能勾、預設勾不勾都照後端回應
const { t, te, locale } = useI18n();
const $q = useQuasar();
const route = useRoute();
const router = useRouter();
const key = String(route.params.key);
const body = ref<UpgradeBody>({});
const preview = useAppUpgradePreview(key, body);
const applying = useApplyAppUpgrade(key);
const adminGroups = useQuery({ queryKey: ['admin-groups'], queryFn: dynamicApi.getAdminGroups });
const apps = useApps();
const qc = useQueryClient();
// 重新預覽失敗（例如另一個分頁已套用、回 409 已是最新）時不留舊清單與套用鈕：react-query 失敗時仍保留上一次的 data（走查）
const data = computed(() => (preview.error.value ? undefined : preview.data.value));
const error = computed(() => preview.error.value as ApiError | null);
const forbidden = computed(() => isForbidden(error.value));
const name = (n: Parameters<typeof resolveI18n>[0]) => resolveI18n(n, locale.value);

const chips = ref<string[]>([...DEFAULT_CATEGORIES]);
const allItems = computed(() => [...(data.value?.items ?? []), ...(data.value?.masters.flatMap((m) => m.items) ?? [])]);
const chipCounts = computed(() => countByChip(allItems.value));
const chipList = computed(() => [...CATEGORY_ORDER, NOT_APPLICABLE].filter((c) => chipCounts.value[c]));
const toggle = (c: string) => { chips.value = chips.value.includes(c) ? chips.value.filter((x) => x !== c) : [...chips.value, c]; };
const sections = computed(() => {
  if (!data.value) return [];
  return [
    {
      key: data.value.app, title: null as string | null, hasBaseline: true, pendingGroups: [] as UpgradeGroup[],
      modules: moduleNames(apps.data.value ?? [], data.value.app, data.value.items), groups: groupItems(filterItems(data.value.items, chips.value)),
    },
    ...data.value.masters.map((m) => ({
      key: `master:${m.id}`,
      title: t('apps.upgrade.master', { name: name(m.name), from: m.fromVersion, to: m.toVersion }),
      hasBaseline: m.hasBaseline,
      pendingGroups: m.groups,
      modules: moduleNames(apps.data.value ?? [], m.id, m.items),
      groups: groupItems(filterItems(m.items, chips.value)),
    })),
  ];
});


const labelOf = (i: UpgradeItem) => (i.label ? name(i.label) : i.id.slice(i.id.indexOf(':') + 1));
const kindOf = (i: UpgradeItem) => t(`apps.upgrade.section.${i.section}`);
const groupTitle = (g: { key: string; module: string | null }, modules: Map<string, I18nText>) => {
  if (g.module === null) return t(`apps.upgrade.group.${g.key.slice(1)}`);
  const label = modules.get(g.module);
  return t('apps.upgrade.moduleGroup', { module: label ? name(label) : g.module });
};
const propName = (n: string) => {
  const k = n.startsWith('config.') ? `apps.upgrade.prop.${n.slice(7)}` : `apps.upgrade.prop.${n}`;
  return te(k) ? t(k) : n;
};
const dependsLabel = (ids: string[]) => ids.map((id) => {
  const hit = allItems.value.find((i) => i.id === id);
  return hit ? labelOf(hit) : id;
}).join('、');
const effectLabel = (effect: string) => (te(`apps.upgrade.effect.${effect}`) ? t(`apps.upgrade.effect.${effect}`) : effect);
const reasonLabel = (reason: string, n?: number | null, detail?: string | null) =>
  te(`apps.upgrade.reason.${reason}`) ? t(`apps.upgrade.reason.${reason}`, { n: n ?? 0, detail: detail ?? '' }) : reason;

// 勾選：預覽載入（含改群組對應後重新預覽）時回到後端的預設勾選
const selected = ref<Set<string>>(new Set());
watch(() => data.value?.token, () => { selected.value = defaultSelection(data.value?.items ?? []); }, { immediate: true });
const canTick = (i: UpgradeItem) => !!data.value?.canApply && i.applicable && (data.value?.items ?? []).includes(i);
function tick(i: UpgradeItem, on: boolean) {
  const r = toggleItem(data.value?.items ?? [], selected.value, i.id, on);
  selected.value = r.selected;
  if (r.together.length) $q.notify({ message: t(on ? 'apps.upgrade.together' : 'apps.upgrade.togetherOff', { items: dependsLabel(r.together) }) });
}

// 群組對應（§10.2 第 3 點）：只列後端給的代號（新代號、失效的、略過過而新物件用得到的），改了就帶 groupMap 重新預覽
const groupOptions = computed(() => [
  ...(adminGroups.data.value ?? []).map((g) => ({ label: `${t('apps.upgrade.mapTo')}：${name(g.nameI18n)}`, value: `map:${g.id}` })),
  { label: t('apps.upgrade.groupChoice.create'), value: 'create' },
  { label: t('apps.upgrade.groupChoice.skip'), value: 'skip' },
]);
const choiceValue = (c: { mode: string; groupId?: number | null }) => (c.mode === 'map' ? `map:${c.groupId}` : c.mode);
function choose(code: string, value: string) {
  const choice: UpgradeGroupChoice = value.startsWith('map:') ? { mode: 'map', groupId: Number(value.slice(4)) } : { mode: value as 'create' | 'skip' };
  body.value = { ...body.value, groupMap: { ...(body.value.groupMap ?? {}), [code]: choice } };
}

const confirming = ref(false);
const summary = computed(() => confirmCounts(data.value?.items ?? [], selected.value));
// 套用回 400 的逐項原因（§8.3）：項目識別換成名稱；重新預覽後清掉
const applyErrors = ref<{ label: string; messages: string[] }[]>([]);
watch(() => data.value?.token, () => { applyErrors.value = []; });
async function apply() {
  if (!data.value) return;
  applyErrors.value = [];
  try {
    const result = await applying.mutateAsync({ ...body.value, token: data.value.token, items: [...selected.value] });
    confirming.value = false;
    $q.notify({
      type: 'positive', timeout: 0, closeBtn: true, message: t('apps.upgrade.applied', { version: data.value.toVersion }),
      caption: result.warnings?.join('；') || undefined,
    });
    await router.push('/system/apps');
  } catch (e) {
    confirming.value = false;
    const err = e as ApiError;
    // 409 依代碼分辨（審查 M5）：token 不符才重新預覽；擋下的用附帶的預覽重畫；其他（例如設計器指令擋下）只顯示訊息、不自動重試
    if (err.status === 409 && err.code === 'upgradeStale') {
      $q.notify({ type: 'warning', message: t('apps.upgrade.stale') });
      await preview.refetch();
    } else if (err.status === 409 && err.code === 'upgradeUnavailable') {
      // 另一個分頁已套用等：已沒有可套用的新版，重新預覽讓頁面收起舊清單、顯示原因
      $q.notify({ type: 'warning', message: err.message || t('apps.upgrade.unavailable') });
      await preview.refetch();
    } else if (err.status === 409 && err.upgradePreview) {
      qc.setQueryData(['app-upgrade', key, body.value], err.upgradePreview);
      $q.notify({ type: 'warning', message: t('apps.upgrade.blocked') });
    } else if (err.status === 400 && err.errors && Object.keys(err.errors).length) {
      applyErrors.value = applyErrorLines(err.errors, allItems.value, labelOf);
      $q.notify({ type: 'negative', message: t('apps.upgrade.applyFailed') });
    } else $q.notify({ type: 'negative', message: err.message || t('apps.upgrade.applyFailed') });
  }
}

const downloading = ref(false);
async function download() {
  downloading.value = true;
  try {
    const { blob, fileName } = await dynamicApi.exportApp(key);
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  } catch (e) {
    $q.notify({ type: 'negative', message: (e as ApiError).message });
  } finally {
    downloading.value = false;
  }
}

const expanded = ref<Record<string, boolean>>({});
const viewing = ref<{ title: string; value: string } | null>(null);
</script>

<template>
  <q-page padding class="upgrade-page">
    <ForbiddenNotice v-if="forbidden" />
    <template v-else>
      <div class="row items-center q-gutter-sm q-mb-md">
        <q-btn flat dense icon="arrow_back" :to="'/system/apps'" :aria-label="t('apps.upgrade.back')" data-testid="upgrade-back" />
        <div class="text-h6 col" data-testid="upgrade-title">
          <template v-if="data">{{ t('apps.upgrade.title', { name: name(data.name), from: data.fromVersion, to: data.toVersion }) }}</template>
          <template v-else>{{ t('apps.title') }}</template>
        </div>
        <q-btn v-if="data" outline no-caps icon="download" :label="t('apps.upgrade.download')" :loading="downloading"
          data-testid="upgrade-download" @click="download" />
      </div>

      <q-inner-loading :showing="preview.isLoading.value" />
      <q-banner v-if="error && !forbidden" rounded class="bg-info-soft q-mb-md" data-testid="upgrade-unavailable">
        {{ error.message || t('apps.upgrade.unavailable') }}
      </q-banner>

      <template v-if="data">
        <div class="text-caption text-muted q-mb-md" data-testid="upgrade-apply-hint">{{ t('apps.upgrade.applyHint') }}</div>
        <q-banner v-if="!data.hasBaseline" rounded class="bg-warning-soft q-mb-md" data-testid="upgrade-no-baseline">{{ t('apps.upgrade.noBaseline') }}</q-banner>
        <q-banner v-if="data.blockers.length" rounded class="bg-negative-soft q-mb-md" data-testid="upgrade-blockers">
          <div class="text-weight-medium">{{ t('apps.upgrade.blocked') }}</div>
          <div v-for="(b, i) in data.blockers" :key="i">{{ t(`apps.upgrade.blocker.${b.code}`, { detail: b.detail ?? '' }) }}</div>
        </q-banner>
        <q-banner v-if="applyErrors.length" rounded class="bg-negative-soft q-mb-md" data-testid="upgrade-apply-errors">
          <div class="text-weight-medium">{{ t('apps.upgrade.applyFailed') }}</div>
          <div v-for="(line, k) in applyErrors" :key="k">{{ line.label }}：{{ line.messages.join('；') }}</div>
        </q-banner>
        <div v-for="w in data.warnings" :key="w" class="text-caption text-muted q-mb-xs">{{ w }}</div>

        <div v-if="data.groups.length" class="q-mb-md" data-testid="upgrade-groups">
          <div class="text-subtitle2">{{ t('apps.upgrade.groupsTitle') }}</div>
          <div v-for="g in data.groups" :key="g.code" class="row items-center q-col-gutter-sm q-mb-xs upgrade-group" data-testid="upgrade-group">
            <div class="col-12 col-sm-5 text-body2">
              {{ name(g.name) }} <span class="text-muted">({{ g.code }})</span>：{{ t(`apps.upgrade.groupReason.${g.reason}`) }}
            </div>
            <q-select class="col-12 col-sm-7" dense outlined emit-value map-options :options="groupOptions" :model-value="choiceValue(g.choice)"
              :aria-label="g.code" data-testid="upgrade-group-choice" @update:model-value="(v: string) => choose(g.code, v)" />
          </div>
        </div>

        <div class="row q-gutter-xs q-mb-md" data-testid="upgrade-chips">
          <q-chip v-for="c in chipList" :key="c" clickable dense :outline="!chips.includes(c)" color="primary" :data-testid="`upgrade-chip-${c}`" @click="toggle(c)">
            {{ t(`apps.upgrade.category.${c}`) }} {{ chipCounts[c] }}
          </q-chip>
        </div>
        <div class="text-caption text-muted q-mb-sm">{{ t('apps.upgrade.customerAdded', { n: data.counts.customerAdded ?? 0 }) }}</div>

        <template v-for="s in sections" :key="s.key">
          <div v-if="s.title" class="text-subtitle1 q-mt-lg" data-testid="upgrade-master">{{ s.title }}</div>
          <q-banner v-if="!s.hasBaseline" rounded class="bg-warning-soft q-my-sm" data-testid="upgrade-master-no-baseline">{{ t('apps.upgrade.noBaseline') }}</q-banner>
          <div v-if="s.pendingGroups.length" class="q-my-sm" data-testid="upgrade-master-groups">
            <div class="text-subtitle2">{{ t('apps.upgrade.groupsTitle') }}</div>
            <div v-for="g in s.pendingGroups" :key="g.code" class="text-body2">
              {{ name(g.name) }} <span class="text-muted">({{ g.code }})</span>：{{ t(`apps.upgrade.groupReason.${g.reason}`) }}・{{ t(`apps.upgrade.groupChoice.${g.choice.mode}`) }}
            </div>
          </div>
          <div v-if="!s.groups.length" class="text-muted q-my-sm">{{ t('apps.upgrade.nothing') }}</div>
          <q-list v-for="g in s.groups" :key="g.key" bordered separator class="q-mb-md">
            <q-item-label header>{{ groupTitle(g, s.modules) }}</q-item-label>
            <q-item v-for="i in g.items" :key="i.id" class="column items-stretch upgrade-item" data-testid="upgrade-item">
              <div class="row items-start no-wrap upgrade-row">
                <q-checkbox :model-value="selected.has(i.id)" :disable="!canTick(i)" dense :aria-label="labelOf(i)"
                  data-testid="upgrade-tick" @update:model-value="(v: boolean) => tick(i, v)" />
                <div class="col upgrade-name">
                  <div class="row items-center q-gutter-xs">
                    <span class="text-weight-medium">{{ labelOf(i) }}</span>
                    <span class="text-caption text-muted">{{ kindOf(i) }}</span>
                    <q-badge outline color="primary" :data-testid="`upgrade-category-${i.category}`">{{ t(`apps.upgrade.category.${i.category}`) }}</q-badge>
                  </div>
                  <div v-if="i.reason" class="text-caption text-negative">{{ reasonLabel(i.reason, i.affectedRecords, i.detail) }}</div>
                  <div v-for="(m, k) in i.impacts ?? []" :key="`impact-${k}`" class="text-caption text-muted" data-testid="upgrade-impact">
                    {{ te(`apps.upgrade.impact.${m.code}`) ? t(`apps.upgrade.impact.${m.code}`, { n: m.count ?? 0, detail: m.detail ?? '' }) : m.code }}
                  </div>
                  <div v-if="i.dependsOn.length" class="text-caption text-muted">{{ t('apps.upgrade.dependsOn', { items: dependsLabel(i.dependsOn) }) }}</div>
                  <div v-if="i.attachedPermissions.length" class="row q-gutter-xs q-mt-xs" data-testid="upgrade-attached">
                    <q-badge v-for="(p, k) in i.attachedPermissions" :key="k" :color="p.grants ? 'warning' : 'secondary'" :outline="!p.grants">
                      {{ p.groupName }}：{{ effectLabel(p.effect) }}<template v-if="p.grants">（{{ t('apps.upgrade.grants') }}）</template>
                    </q-badge>
                  </div>
                </div>
                <q-btn v-if="i.properties.length" flat dense round :icon="expanded[i.id] ? 'expand_less' : 'expand_more'"
                  :aria-label="t('apps.upgrade.details')" data-testid="upgrade-expand" @click="expanded[i.id] = !expanded[i.id]" />
              </div>
              <div v-if="expanded[i.id]" class="upgrade-props q-mt-sm">
                <div class="upgrade-prop upgrade-prop--head">
                  <span>{{ t('apps.upgrade.property') }}</span><span>{{ t('apps.upgrade.base') }}</span>
                  <span>{{ t('apps.upgrade.new') }}</span><span>{{ t('apps.upgrade.current') }}</span>
                </div>
                <div v-for="(p, k) in i.properties" :key="k" class="upgrade-prop" data-testid="upgrade-prop">
                  <span class="text-weight-medium">{{ propName(p.name) }}<template v-if="p.locale"> · {{ p.locale }}</template></span>
                  <span v-for="col in (['base', 'new', 'current'] as const)" :key="col" :data-label="t(`apps.upgrade.${col}`)">
                    <template v-if="col === 'current' && p.result === 'newOnly'">—</template>
                    <template v-else-if="isLong(p[col])">
                      {{ t('apps.upgrade.longValue') }}
                      <q-btn flat dense no-caps size="sm" :label="t('apps.upgrade.view')"
                        @click="viewing = { title: propName(p.name), value: formatValue(p[col]) }" />
                    </template>
                    <template v-else>{{ formatValue(p[col]) || '—' }}</template>
                  </span>
                </div>
              </div>
            </q-item>
          </q-list>
        </template>
      </template>

      <div v-if="data" class="upgrade-bar row items-center q-gutter-sm" data-testid="upgrade-bar">
        <span class="col" data-testid="upgrade-selected">{{ t('apps.upgrade.selected', { n: selected.size }) }}</span>
        <q-btn color="primary" unelevated no-caps :label="t('apps.upgrade.apply')" :disable="!data.canApply || selected.size === 0"
          data-testid="upgrade-apply" @click="confirming = true" />
      </div>

      <q-dialog v-model="confirming">
        <q-card class="upgrade-dialog" data-testid="upgrade-confirm">
          <q-card-section class="text-subtitle1">{{ t('apps.upgrade.confirmTitle') }}</q-card-section>
          <q-card-section class="q-pt-none">
            <div v-for="(n, c) in summary.byCategory" :key="c">{{ t(`apps.upgrade.category.${c}`) }}：{{ n }}</div>
            <div v-if="summary.overwrite" class="text-negative q-mt-sm" data-testid="upgrade-confirm-overwrite">
              {{ t('apps.upgrade.confirmOverwrite', { n: summary.overwrite }) }}
            </div>
            <div class="text-caption text-muted q-mt-sm">{{ t('apps.upgrade.applyHint') }}</div>
          </q-card-section>
          <q-card-actions align="right">
            <q-btn flat no-caps :label="t('apps.upgrade.close')" v-close-popup />
            <q-btn color="primary" unelevated no-caps :label="t('apps.upgrade.apply')" :loading="applying.isPending.value"
              data-testid="upgrade-confirm-ok" @click="apply" />
          </q-card-actions>
        </q-card>
      </q-dialog>

      <q-dialog :model-value="!!viewing" @update:model-value="viewing = null">
        <q-card class="upgrade-dialog">
          <q-card-section class="text-subtitle1">{{ viewing?.title }}</q-card-section>
          <q-card-section><pre class="upgrade-pre">{{ viewing?.value }}</pre></q-card-section>
          <q-card-actions align="right"><q-btn flat no-caps :label="t('apps.upgrade.close')" v-close-popup /></q-card-actions>
        </q-card>
      </q-dialog>
    </template>
  </q-page>
</template>

<style scoped>
/* 列與列之間留白：全域把 q-item 的上下內距壓得很小，第二列以後會貼著分隔線（走查 W4） */
.upgrade-item { padding-top: 0.5rem; padding-bottom: 0.5rem; }
/* 底部固定列：已選數量與套用（§10.2 第 7 點）；頁面底部留白讓最後一項不被擋住 */
.upgrade-page { padding-bottom: 5rem; }
.upgrade-bar { position: sticky; bottom: 0; z-index: 2; background: var(--t-panel); border-top: 1px solid var(--t-border); padding: 0.5rem 0; }
.upgrade-row { gap: 0.5rem; }
.upgrade-name { min-width: 0; overflow-wrap: anywhere; }
.upgrade-prop { display: grid; grid-template-columns: minmax(8rem, 1fr) repeat(3, minmax(0, 2fr)); gap: 0.5rem; padding: 0.25rem 0; }
.upgrade-prop > span { min-width: 0; overflow-wrap: anywhere; }
.upgrade-prop--head { font-size: 0.75rem; opacity: 0.7; }
.upgrade-dialog { width: min(90vw, 48rem); }
.upgrade-pre { white-space: pre-wrap; overflow-wrap: anywhere; margin: 0; }
/* 820 以下：原值／新版／目前三列堆疊（§10.3） */
@media (max-width: 1023px) {
  .upgrade-prop { grid-template-columns: 1fr; gap: 0.125rem; }
  .upgrade-prop--head { display: none; }
  .upgrade-prop > span[data-label]::before { content: attr(data-label) '：'; opacity: 0.7; }
}
</style>
