<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import DateSelect from 'src/components/framework/DateSelect.vue';
import type { ApprovalInboxFilter, ApprovalInstanceStatus } from 'src/domain/dynamic/approval-types';
import { INBOX_STATUSES, type ModuleOption } from './inbox-filter';

const props = defineProps<{ modelValue: ApprovalInboxFilter; showStatus: boolean; modules: ModuleOption[] }>();
const emit = defineEmits<{ 'update:modelValue': [value: ApprovalInboxFilter] }>();
const { t } = useI18n();

const statusOptions = computed(() => INBOX_STATUSES.map((s) => ({ value: s, label: t(`approval.status.${s}`) })));
const set = (patch: Partial<ApprovalInboxFilter>) => emit('update:modelValue', { ...props.modelValue, ...patch });
// 日期欄是逐字輸入的：打完整一天（或清空）才往外送，打到一半的值不套用
const DAY = /^\d{4}-\d{2}-\d{2}$/;
const setDay = (key: 'from' | 'to', v: string | null) => {
  if (v === null || DAY.test(v)) set({ [key]: v ?? undefined });
};
</script>

<template>
  <div class="row q-col-gutter-sm">
    <div class="col-12 col-sm-6 col-md-3">
      <q-select
        :model-value="modelValue.module ?? null" :options="modules" emit-value map-options clearable outlined dense
        :label="t('approval.center.filters.module')" data-testid="approval-filter-module"
        @update:model-value="(v: string | null) => set({ module: v ?? undefined })"
      />
    </div>
    <div v-if="showStatus" class="col-12 col-sm-6 col-md-3">
      <q-select
        :model-value="modelValue.status ?? null" :options="statusOptions" emit-value map-options clearable outlined dense
        :label="t('approval.center.filters.status')" data-testid="approval-filter-status"
        @update:model-value="(v: ApprovalInstanceStatus | null) => set({ status: v ?? undefined })"
      />
    </div>
    <div class="col-12 col-sm-6 col-md-3">
      <DateSelect
        :model-value="modelValue.from ?? null" :label="t('approval.center.filters.from')" clearable data-testid="approval-filter-from"
        @update:model-value="(v) => setDay('from', v)"
      />
    </div>
    <div class="col-12 col-sm-6 col-md-3">
      <DateSelect
        :model-value="modelValue.to ?? null" :label="t('approval.center.filters.to')" clearable data-testid="approval-filter-to"
        @update:model-value="(v) => setDay('to', v)"
      />
    </div>
  </div>
</template>
