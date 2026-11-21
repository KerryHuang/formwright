<script setup lang="ts">
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRoute } from 'vue-router';
import { useAppUpgradePreview } from 'src/application/dynamic/queries/apps-queries';
import ForbiddenNotice from 'src/components/framework/ForbiddenNotice.vue';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import {
  CATEGORY_ORDER, countByChip, DEFAULT_CATEGORIES, filterItems, formatValue, groupItems, isLong, NOT_APPLICABLE,
} from 'src/presentation/dynamic/upgrade/upgrade-view';
import type { UpgradeItem } from 'src/services/api/dynamic-api';
import { isForbidden, type ApiError } from 'src/services/api/http';

// 唯讀更新頁（套件更新設計 §10.2，pkg1b）：列出差異與預設勾選，套用在 pkg1c 才上線，所以勾選框不能改、沒有套用鈕
const { t, te, locale } = useI18n();
const route = useRoute();
const key = String(route.params.key);
const preview = useAppUpgradePreview(key);
const data = computed(() => preview.data.value);
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
    { key: data.value.app, title: null as string | null, groups: groupItems(filterItems(data.value.items, chips.value)) },
    ...data.value.masters.map((m) => ({
      key: `master:${m.id}`,
      title: t('apps.upgrade.master', { name: name(m.name), from: m.fromVersion, to: m.toVersion }),
      groups: groupItems(filterItems(m.items, chips.value)),
    })),
  ];
});

const labelOf = (i: UpgradeItem) => (i.label ? name(i.label) : i.id.slice(i.id.indexOf(':') + 1));
const kindOf = (i: UpgradeItem) => t(`apps.upgrade.section.${i.section}`);
const groupTitle = (g: { key: string; module: string | null }) =>
  g.module !== null ? t('apps.upgrade.moduleGroup', { module: g.module }) : t(`apps.upgrade.group.${g.key.slice(1)}`);
const propName = (n: string) => {
  const k = n.startsWith('config.') ? `apps.upgrade.prop.${n.slice(7)}` : `apps.upgrade.prop.${n}`;
  return te(k) ? t(k) : n;
};
const dependsLabel = (ids: string[]) => ids.map((id) => {
  const hit = allItems.value.find((i) => i.id === id);
  return hit ? labelOf(hit) : id;
}).join('、');
const effectLabel = (effect: string) => (te(`apps.upgrade.effect.${effect}`) ? t(`apps.upgrade.effect.${effect}`) : effect);
const reasonLabel = (reason: string) => (te(`apps.upgrade.reason.${reason}`) ? t(`apps.upgrade.reason.${reason}`) : reason);

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
      </div>

      <q-inner-loading :showing="preview.isLoading.value" />
      <q-banner v-if="error && !forbidden" rounded class="bg-info-soft q-mb-md" data-testid="upgrade-unavailable">
        {{ error.message || t('apps.upgrade.unavailable') }}
      </q-banner>

      <template v-if="data">
        <q-banner rounded class="bg-info-soft q-mb-md" data-testid="upgrade-readonly">{{ t('apps.upgrade.readOnlyHint') }}</q-banner>
        <q-banner v-if="!data.hasBaseline" rounded class="bg-warning-soft q-mb-md" data-testid="upgrade-no-baseline">{{ t('apps.upgrade.noBaseline') }}</q-banner>
        <q-banner v-if="data.blockers.length" rounded class="bg-negative-soft q-mb-md" data-testid="upgrade-blockers">
          <div class="text-weight-medium">{{ t('apps.upgrade.blocked') }}</div>
          <div v-for="(b, i) in data.blockers" :key="i">{{ t(`apps.upgrade.blocker.${b.code}`, { detail: b.detail ?? '' }) }}</div>
        </q-banner>
        <div v-for="w in data.warnings" :key="w" class="text-caption text-muted q-mb-xs">{{ w }}</div>

        <div v-if="data.groups.length" class="q-mb-md" data-testid="upgrade-groups">
          <div class="text-subtitle2">{{ t('apps.upgrade.groupsTitle') }}</div>
          <div v-for="g in data.groups" :key="g.code" class="text-body2">
            {{ name(g.name) }} <span class="text-muted">({{ g.code }})</span>：{{ t(`apps.upgrade.groupReason.${g.reason}`) }}・{{ t(`apps.upgrade.groupChoice.${g.choice.mode}`) }}
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
          <div v-if="!s.groups.length" class="text-muted q-my-sm">{{ t('apps.upgrade.nothing') }}</div>
          <q-list v-for="g in s.groups" :key="g.key" bordered separator class="q-mb-md">
            <q-item-label header>{{ groupTitle(g) }}</q-item-label>
            <q-item v-for="i in g.items" :key="i.id" class="column items-stretch" data-testid="upgrade-item">
              <div class="row items-start no-wrap q-gutter-sm">
                <q-checkbox :model-value="i.applicable && i.defaultSelected" disable dense :aria-label="labelOf(i)" />
                <div class="col upgrade-name">
                  <div class="row items-center q-gutter-xs">
                    <span class="text-weight-medium">{{ labelOf(i) }}</span>
                    <span class="text-caption text-muted">{{ kindOf(i) }}</span>
                    <q-badge outline color="primary" :data-testid="`upgrade-category-${i.category}`">{{ t(`apps.upgrade.category.${i.category}`) }}</q-badge>
                  </div>
                  <div v-if="i.reason" class="text-caption text-negative">{{ reasonLabel(i.reason) }}</div>
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
