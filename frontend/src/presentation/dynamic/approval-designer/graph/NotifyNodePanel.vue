<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import SelectNoOption from 'src/components/framework/SelectNoOption.vue';
import type { ApprovalNodeDef, NotifyRecipientDef } from 'src/domain/dynamic/approval-flow-types';
import { setNode } from 'src/domain/dynamic/approval-graph-edit';
import type { I18nText, ModuleDetail } from 'src/domain/dynamic/types';
import { requiredTextLocales } from 'src/i18n/locale-settings';
import I18nTextInput from 'src/presentation/dynamic/designer/I18nTextInput.vue';
import { toSchemaFields } from 'src/presentation/dynamic/designer/schema-fields';
import { useApprovalLimits } from '../use-approval-limits';
import { fieldErrors } from './designer-graph';
import { appendPlaceholder, placeholderOptions } from './notify-placeholders';
import NotifyRecipientsEditor from './NotifyRecipientsEditor.vue';
import type { GraphDesigner } from './use-graph-designer';

// 通知節點屬性：名稱、收件人、範本標題與內文（內文選填）；「插入」把佔位符接在文字結尾。
const props = defineProps<{ designer: GraphDesigner; node: ApprovalNodeDef; module: ModuleDetail }>();
const { t, locale } = useI18n();
const { limits } = useApprovalLimits(() => props.module.key);
const errs = computed(() => fieldErrors(props.designer.issues.value?.errors.nodes[props.node.id]));
// fieldErrors 只取欄位第一段：template.title／template.body 都歸在 template，這裡直接依完整欄位名分開
const issueOf = (field: string) => props.designer.issues.value?.errors.nodes[props.node.id]?.find((i) => i.field === field)?.message;
const options = computed(() => placeholderOptions(toSchemaFields(props.module), locale.value, t));
const key = (part: string) => `node:${props.node.id}:${part}`;

const title = computed<I18nText>(() => props.node.template?.title ?? {});
const body = computed<I18nText>(() => props.node.template?.body ?? {});
const patchTemplate = (p: { title?: I18nText; body?: I18nText }, part: string) =>
  props.designer.edit((s) => setNode(s, props.node.id, { template: { title: title.value, body: body.value, ...p } }), key(part));
const setName = (name: I18nText) => props.designer.edit((s) => setNode(s, props.node.id, { name }), key('name'));
const setRecipients = (recipients: NotifyRecipientDef[]) => props.designer.edit((s) => setNode(s, props.node.id, { recipients }), key('recipients'));
const insert = (part: 'title' | 'body', token: string | null) => {
  if (!token) return;
  patchTemplate({ [part]: appendPlaceholder(part === 'title' ? title.value : body.value, token, requiredTextLocales()) }, part);
};
</script>

<template>
  <div class="column no-wrap q-gutter-y-md" data-testid="notify-panel">
    <div class="text-caption text-muted">{{ t('approval.designer.graph.notify.hint') }}</div>
    <I18nTextInput :model-value="node.name ?? {}" :label="t('approval.designer.graph.nodeName')" :error="errs.name" @update:model-value="setName" />
    <NotifyRecipientsEditor
      :model-value="node.recipients ?? []" :max-manager-level="limits.maxManagerLevel" :saved-level="designer.server.value.publishedNotifyLevels[node.id] || undefined"
      :error="errs.recipients" :disable="!designer.canEdit.value" @update:model-value="setRecipients"
    />
    <div class="text-subtitle2">{{ t('approval.designer.graph.notify.template') }}</div>
    <template v-for="part in (['title', 'body'] as const)" :key="part">
      <I18nTextInput
        :model-value="part === 'title' ? title : body" :label="t(`approval.designer.graph.notify.${part}`)" :optional="part === 'body'" :multiline="part === 'body'"
        :error="issueOf(`template.${part}`)" :data-testid="`notify-${part}`" @update:model-value="(v) => patchTemplate({ [part]: v }, part)"
      />
      <q-select
        :model-value="null" :options="options" :label="t('approval.designer.graph.notify.insert')" emit-value map-options dense outlined :disable="!designer.canEdit.value"
        :data-testid="`notify-insert-${part}`" @update:model-value="(v: string | null) => insert(part, v)"
      >
        <template #no-option><SelectNoOption /></template>
      </q-select>
    </template>
  </div>
</template>
