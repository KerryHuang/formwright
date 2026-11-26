<script setup lang="ts">
import { Notify } from 'quasar';
import { computed, ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRoute, useRouter } from 'vue-router';
import {
  useCreateDashboard, useDashboards, useDeleteDashboard,
} from 'src/application/dynamic/queries/dashboards-queries';
import { pickDashboard, readLastDashboard, writeLastDashboard } from 'src/application/dynamic/last-dashboard';
import { compactI18n, resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { I18nText } from 'src/domain/dynamic/types';
import ForbiddenNotice from 'src/components/framework/ForbiddenNotice.vue';
import I18nTextInput from 'src/presentation/dynamic/designer/I18nTextInput.vue';
import DashboardSideList from 'src/presentation/dashboards/DashboardSideList.vue';
import DashboardView from 'src/presentation/dashboards/DashboardView.vue';
import { isForbidden } from 'src/services/api/http';
import { useIdentityStore } from 'src/stores/use-identity-store';

const { t, locale } = useI18n();
const route = useRoute();
const router = useRouter();
const identity = useIdentityStore();
const list = useDashboards();
const forbidden = computed(() => isForbidden(list.error.value));

const all = computed(() => [...(list.data.value?.assigned ?? []), ...(list.data.value?.mine ?? [])]);
// 卡片「點擊後前往的儀表板」選單用：名稱依目前語系解析
const linkable = computed(() => all.value.map((d) => ({ id: d.id, name: resolveI18n(d.name, locale.value) })));
const routeId = computed(() => (route.params.id ? Number(route.params.id) : null));
// 網址沒帶 id（或帶的看不到）時開上次看的，否則第一張；清單載入前不決定
const currentId = computed(() => {
  if (!list.isSuccess.value) return null;
  const ids = all.value.map((d) => d.id);
  if (routeId.value !== null && ids.includes(routeId.value)) return routeId.value;
  return pickDashboard(ids, readLastDashboard(identity.userId));
});
watch(currentId, (id) => { if (id !== null) writeLastDashboard(identity.userId, id); }, { immediate: true });
watch([currentId, routeId], ([id, r]) => { if (id !== null && id !== r) router.replace(`/dashboards/${id}`); });

const select = (id: number) => router.push(`/dashboards/${id}`);

const createOpen = ref(false);
const newName = ref<I18nText>({});
const newNameInput = ref<{ validate(): Promise<boolean> } | null>(null);
const create = useCreateDashboard();
async function doCreate() {
  if (!(await newNameInput.value?.validate())) return;
  create.mutate({ name: compactI18n(newName.value) ?? {}, layout: { cards: [] } }, {
    onSuccess: (d) => { createOpen.value = false; newName.value = {}; router.push(`/dashboards/${d.id}`); },
    onError: () => Notify.create({ type: 'negative', message: t('dashboards.saveFailed') }),
  });
}

const removeId = ref<number | null>(null);
const remove = useDeleteDashboard();
function doRemove() {
  remove.mutate(removeId.value!, {
    onSuccess: () => { removeId.value = null; router.replace('/dashboards'); },
    onError: () => { removeId.value = null; Notify.create({ type: 'negative', message: t('dashboards.saveFailed') }); },
  });
}
</script>

<template>
  <q-page padding>
    <ForbiddenNotice v-if="forbidden" />
    <div v-else-if="list.isError.value" class="text-negative" data-testid="dashboards-error">
      {{ t('reports.loadFailed') }}
      <q-btn flat dense color="primary" :label="t('reports.retry')" @click="list.refetch()" />
    </div>
    <template v-else-if="list.data.value">
      <DashboardSideList
        class="q-mb-md" :list="list.data.value" :current-id="currentId" @select="select" @create="createOpen = true" @remove="removeId = $event"
      />
      <DashboardView v-if="currentId !== null" :key="currentId" :dashboard-id="currentId" allow-edit :linkable="linkable" />
      <div v-else class="text-muted" data-testid="dashboards-none">{{ t('dashboards.none') }}</div>
    </template>

    <q-dialog v-model="createOpen">
      <q-card style="width: 420px; max-width: 95vw">
        <q-card-section class="text-h6">{{ t('dashboards.create') }}</q-card-section>
        <q-card-section>
          <I18nTextInput ref="newNameInput" v-model="newName" :maxlength="100" :label="t('dashboards.name')" data-testid="dashboard-new-name" @keyup.enter="doCreate" />
        </q-card-section>
        <q-card-actions align="right">
          <q-btn flat :label="t('reports.cancel')" @click="createOpen = false" />
          <q-btn color="primary" :label="t('reports.save')" :loading="create.isPending.value" data-testid="dashboard-new-confirm" @click="doCreate" />
        </q-card-actions>
      </q-card>
    </q-dialog>
    <q-dialog :model-value="removeId !== null" @update:model-value="removeId = null">
      <q-card style="width: 420px; max-width: 95vw">
        <q-card-section class="text-h6">{{ t('dashboards.delete') }}</q-card-section>
        <q-card-section>{{ t('dashboards.confirmDelete') }}</q-card-section>
        <q-card-actions align="right">
          <q-btn flat :label="t('reports.cancel')" @click="removeId = null" />
          <q-btn color="negative" :label="t('dashboards.delete')" :loading="remove.isPending.value" data-testid="dashboard-delete-confirm" @click="doRemove" />
        </q-card-actions>
      </q-card>
    </q-dialog>
  </q-page>
</template>
