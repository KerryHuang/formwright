<script setup lang="ts">
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useApprovalInbox } from 'src/application/dynamic/queries/approval-queries';
import { useMenu } from 'src/application/dynamic/queries/modules-queries';
import { pickDashboard, readLastDashboard, writeLastDashboard } from 'src/application/dynamic/last-dashboard';
import { useDashboards } from 'src/application/dynamic/queries/dashboards-queries';
import { useMe } from 'src/application/dynamic/queries/permissions-queries';
import { useRecentRecordDetails } from 'src/application/dynamic/queries/recent-records-queries';
import { readRecentRecords, RECENT_SHOWN } from 'src/application/dynamic/recent-records';
import { appNameHints } from 'src/domain/dynamic/menu-nav';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import ForbiddenNotice from 'src/components/framework/ForbiddenNotice.vue';
import DashboardView from 'src/presentation/dashboards/DashboardView.vue';
import TaskSummaryCard from 'src/presentation/tasks/TaskSummaryCard.vue';
import { useIdentityStore } from 'src/stores/use-identity-store';

const { t, locale } = useI18n();
const identity = useIdentityStore();
const me = useMe();
const menu = useMenu();

// 有儀表板就顯示在最近單據下方（上次看的，沒有就第一張）；沒有或載入失敗時不顯示這一區
const dashboards = useDashboards();
// 名稱依目前語系解析成字串（下拉選單與卡片連結選單用）
const dashList = computed(() => [...(dashboards.data.value?.assigned ?? []), ...(dashboards.data.value?.mine ?? [])]
  .map((d) => ({ id: d.id, name: resolveI18n(d.name, locale.value) })));
const picked = ref<number | null>(null);
const dashId = computed(() => {
  const ids = dashList.value.map((d) => d.id);
  if (picked.value !== null && ids.includes(picked.value)) return picked.value;
  return pickDashboard(ids, readLastDashboard(identity.userId));
});
function pickDash(id: number) { picked.value = id; writeLastDashboard(identity.userId, id); }

// 待我簽核的總筆數；沒有權限或查詢失敗時整張卡不顯示
const approvals = useApprovalInbox('todo', {}, 1);

// 最近開過的單據：只列選單裡還在的模組（已停用或被收回權限的不查也不顯示）；storage 不可用時是空陣列
const menuByKey = computed(() => new Map((menu.data.value ?? []).map((m) => [m.key, m])));
const hints = computed(() => appNameHints(menu.data.value ?? [], locale.value));
const recentRefs = computed(() => readRecentRecords(identity.userId).filter((r) => menuByKey.value.has(r.moduleKey)).slice(0, RECENT_SHOWN));
const recentRecords = useRecentRecordDetails(recentRefs);
const recent = computed(() => recentRecords.value.map((r) => {
  const moduleName = resolveI18n(menuByKey.value.get(r.moduleKey)?.name, locale.value);
  return { key: `${r.moduleKey}/${r.id}`, to: `/dynamic/${r.moduleKey}/${r.id}`, title: r.title || `#${r.id}`, moduleName, appName: hints.value.get(r.moduleKey) };
}));
</script>

<template>
  <q-page padding>
    <!-- 姓名讀回來之前留白但保留高度，不閃字也不跳版 -->
    <div class="text-h5 q-mb-lg home-title" data-testid="home-welcome">{{ me.data.value ? t('home.welcome', { name: me.data.value.displayName }) : '' }}</div>

    <div v-if="menu.isSuccess.value && !menu.data.value?.length" class="q-mb-lg text-muted" data-testid="home-empty">
      <template v-if="me.data.value?.designer.add">
        {{ t('home.noModulesDesigner') }}
        <q-btn flat dense no-caps color="primary" icon="design_services" :label="t('home.noModulesDesignerLink')" to="/system/dynamic-modules" data-testid="home-empty-designer-link" />
      </template>
      <template v-else>{{ t('home.noModules') }}</template>
    </div>

    <TaskSummaryCard />

    <section v-if="approvals.isSuccess.value" class="q-mb-lg">
      <div class="row q-col-gutter-sm">
        <div class="col-12 col-sm-6 col-md-4 col-lg-3">
          <q-card flat bordered>
            <q-item clickable to="/approvals" data-testid="home-approvals">
              <q-item-section avatar><q-icon name="approval" /></q-item-section>
              <q-item-section>{{ t('home.approvalsTodo') }}</q-item-section>
              <q-item-section side class="text-h6">{{ approvals.data.value?.totalCount }}</q-item-section>
            </q-item>
          </q-card>
        </div>
      </div>
    </section>

    <ForbiddenNotice v-if="menu.isError.value" />

    <section v-if="recent.length" class="q-mb-lg" data-testid="home-recent">
      <div class="text-subtitle1 q-mb-sm">{{ t('home.recent') }}</div>
      <q-list bordered separator class="rounded-borders">
        <q-item v-for="r in recent" :key="r.key" clickable :to="r.to" data-testid="home-recent-item">
          <q-item-section>
            <q-item-label class="home-recent-title">{{ r.title }}</q-item-label>
            <q-item-label caption>{{ r.moduleName }}<span v-if="r.appName" class="q-ml-sm">{{ r.appName }}</span></q-item-label>
          </q-item-section>
        </q-item>
      </q-list>
    </section>

    <section v-if="dashId !== null" class="q-mb-lg" data-testid="home-dashboard">
      <q-select
        v-if="dashList.length > 1" :model-value="dashId" :options="dashList" option-value="id" option-label="name" emit-value map-options
        dense outlined class="q-mb-sm" style="max-width: 320px" :label="t('dashboards.title')" data-testid="home-dashboard-select"
        @update:model-value="pickDash"
      />
      <DashboardView :key="dashId" :dashboard-id="dashId" :allow-edit="false" :linkable="dashList" />
      <q-btn flat no-caps dense color="primary" icon="open_in_new" :label="t('dashboards.openFull')" :to="`/dashboards/${dashId}`" data-testid="home-dashboard-open" />
    </section>
  </q-page>
</template>

<style scoped>
.home-title { min-height: 1.6em; overflow-wrap: anywhere; }
.home-recent-title { overflow-wrap: anywhere; }
</style>
