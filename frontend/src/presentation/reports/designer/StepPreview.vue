<script setup lang="ts">
import { useI18n } from 'vue-i18n';
import { computed, ref } from 'vue';
import type { ReportResult, ReportViz } from 'src/domain/dynamic/report-types';
import type { I18nText } from 'src/domain/dynamic/types';
import I18nTextInput from 'src/presentation/dynamic/designer/I18nTextInput.vue';
import ReportResultView from '../ReportResultView.vue';
import type { DesignerState } from './designer-state';

/** originalName：修改既有報表時傳載入的名稱（沒改名就不檢查新增的必填語系）；新增時不傳。 */
const props = defineProps<{
  state: DesignerState; result: ReportResult | null; loading: boolean; errors: string[]; saveErrors: string[]; canSave: boolean; saving: boolean; originalName?: I18nText;
}>();
const emit = defineEmits<{ save: []; refresh: [] }>();
const { t } = useI18n();
const VIZS: ReportViz[] = ['table', 'indicator', 'line', 'bar', 'donut'];
// 綁 original 的要是獨立副本，不能和 v-model 的 state.name 同一個物件；跟著 originalName 換（報表重新載入時不用舊值）
const original = computed(() => (props.originalName ? { ...props.originalName } : undefined));
const nameInput = ref<{ validate(): Promise<boolean> } | null>(null);
async function save() {
  if (await nameInput.value?.validate()) emit('save');
}
</script>

<template>
  <div class="column q-gutter-y-md" data-testid="designer-step-4">
    <I18nTextInput ref="nameInput" v-model="state.name" :original="original" :maxlength="100" :label="t('reports.designer.name')" data-testid="report-name-input" />
    <div class="row items-center q-gutter-x-sm">
      <span>{{ t('reports.designer.viz') }}</span>
      <q-btn
        v-for="v in VIZS" :key="v" dense no-caps :outline="state.viz !== v" :unelevated="state.viz === v" :color="state.viz === v ? 'primary' : undefined"
        :label="t(`reports.designer.vizName.${v}`)" :data-testid="`viz-${v}`" @click="state.viz = v"
      />
      <q-space />
      <q-btn flat dense no-caps color="primary" icon="refresh" :label="t('reports.designer.refresh')" data-testid="preview-refresh" @click="emit('refresh')" />
    </div>
    <q-inner-loading :showing="loading" />
    <div v-if="errors.length" class="text-negative" data-testid="preview-errors">
      <div v-for="e in errors" :key="e">{{ e }}</div>
    </div>
    <ReportResultView v-else-if="result" :result="result" :viz="state.viz" />
    <div v-if="saveErrors.length" class="text-negative" data-testid="save-errors">
      <div v-for="e in saveErrors" :key="e">{{ e }}</div>
    </div>
    <div class="row">
      <q-btn color="primary" no-caps :label="t('reports.save')" :disable="!canSave" :loading="saving" data-testid="designer-save" @click="save" />
    </div>
  </div>
</template>
