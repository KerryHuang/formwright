<script setup lang="ts">
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useGroups, useUsers } from 'src/application/dynamic/queries/permissions-queries';
import type { NotifyRecipientDef, NotifyRecipientKind } from 'src/domain/dynamic/approval-flow-types';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import { levelOptions } from '../approver-source';

// 通知節點的收件人清單：每列一個種類加對應參數。使用者、群組的查詢與主管層級的選項沿用簽核人來源編輯器（permissions-queries、levelOptions）。
const props = defineProps<{
  modelValue: NotifyRecipientDef[];
  /** 政策的主管層級上限；讀不到時（undefined）層級改成數字輸入，由後端擋。 */
  maxManagerLevel?: number;
  /** 已發布版本這個節點用過的最大主管層數：超過政策上限但不比它大的層數仍可選（改嚴只管之後）。 */
  savedLevel?: number;
  error?: string;
  disable?: boolean;
}>();
const emit = defineEmits<{ 'update:modelValue': [value: NotifyRecipientDef[]] }>();
const { t, locale } = useI18n();

const KINDS: NotifyRecipientKind[] = ['initiator', 'initiatorManager', 'users', 'group'];
const kindOptions = KINDS.map((value) => ({ value, label: t(`approval.designer.graph.notify.kind.${value}`) }));

const users = useUsers({ enabled: () => props.modelValue.some((r) => r.kind === 'users') });
const groups = useGroups({ enabled: () => props.modelValue.some((r) => r.kind === 'group') });
type Opt = { value: string | number; label: string };
function withMissing(base: Opt[], selected: (string | number)[], ready: boolean): Opt[] {
  const known = new Set(base.map((o) => o.value));
  return [...base, ...selected.filter((v) => !known.has(v)).map((v) => ({ value: v, label: ready ? t('policies.recipients.missingItem', { item: v }) : String(v) }))];
}
const userBase = computed<Opt[]>(() => (users.data.value ?? []).map((u) => ({ value: u.userId, label: t('policies.recipients.userLabel', { name: u.displayName || u.userId, id: u.userId }) })));
const groupBase = computed<Opt[]>(() => (groups.data.value ?? []).map((g) => ({ value: g.id, label: resolveI18n(g.name, locale.value) })));
const userOptions = (sel: string[] | null | undefined) => withMissing(userBase.value, sel ?? [], users.isSuccess.value);
const groupOptions = (sel: number[] | null | undefined) => withMissing(groupBase.value, sel ?? [], groups.isSuccess.value);
const levels = computed(() => levelOptions(props.maxManagerLevel, props.savedLevel)
  .map((o) => ({ value: o.value, label: t(o.legacy ? 'approval.designer.source.levelOptionLegacy' : 'approval.designer.source.levelOption', { n: o.value }) })));

const filter = ref('');
const shown = (opts: Opt[]) => { const q = filter.value.trim().toLowerCase(); return q ? opts.filter((o) => o.label.toLowerCase().includes(q)) : opts; };
function onFilter(v: string, update: (fn: () => void) => void) { update(() => { filter.value = v; }); }

const patch = (i: number, r: NotifyRecipientDef) => emit('update:modelValue', props.modelValue.map((x, j) => (j === i ? r : x)));
const setKind = (i: number, kind: NotifyRecipientKind) => {
  if (kind === props.modelValue[i].kind) return;
  patch(i, kind === 'users' ? { kind, userIds: [] } : kind === 'group' ? { kind, groupIds: [] } : kind === 'initiatorManager' ? { kind, level: 1 } : { kind });
};
const add = () => emit('update:modelValue', [...props.modelValue, { kind: 'initiator' }]);
const remove = (i: number) => emit('update:modelValue', props.modelValue.filter((_, j) => j !== i));
const num = (v: string | number | null) => (v === null || v === '' ? null : Number(v));
</script>

<template>
  <div class="column no-wrap q-gutter-y-sm" data-testid="notify-recipients">
    <div class="text-subtitle2">{{ t('approval.designer.graph.notify.recipients') }}</div>
    <div v-for="(r, i) in modelValue" :key="i" class="column no-wrap q-gutter-y-xs" :data-testid="`notify-recipient-${i}`">
      <div class="row no-wrap items-center q-gutter-x-xs">
        <q-select
          class="col" :model-value="r.kind" :options="kindOptions" :label="t('approval.designer.graph.notify.recipientKind')" emit-value map-options dense outlined
          :disable="disable" :data-testid="`notify-kind-${i}`" @update:model-value="(v: NotifyRecipientKind) => setKind(i, v)"
        />
        <q-btn
          flat dense round icon="delete" :aria-label="t('approval.designer.graph.notify.removeRecipient')" :disable="disable"
          :data-testid="`notify-remove-${i}`" @click="remove(i)"
        />
      </div>
      <q-select
        v-if="r.kind === 'users'" :model-value="r.userIds ?? []" :options="shown(userOptions(r.userIds))" :label="t('approval.designer.source.users')"
        multiple use-chips use-input emit-value map-options dense outlined :disable="disable" :data-testid="`notify-users-${i}`" @filter="onFilter"
        @update:model-value="(v: string[]) => patch(i, { kind: 'users', userIds: v })"
      />
      <q-select
        v-else-if="r.kind === 'group'" :model-value="r.groupIds ?? []" :options="shown(groupOptions(r.groupIds))" :label="t('approval.designer.source.groups')"
        multiple use-chips use-input emit-value map-options dense outlined :disable="disable" :data-testid="`notify-groups-${i}`" @filter="onFilter"
        @update:model-value="(v: number[]) => patch(i, { kind: 'group', groupIds: v })"
      />
      <template v-else-if="r.kind === 'initiatorManager'">
        <q-select
          v-if="levels.length" :model-value="r.level ?? 1" :options="levels" :label="t('approval.designer.source.level')" emit-value map-options dense outlined
          :disable="disable" :data-testid="`notify-level-${i}`" @update:model-value="(v: number) => patch(i, { kind: 'initiatorManager', level: v })"
        />
        <q-input
          v-else :model-value="r.level ?? ''" type="number" min="1" :label="t('approval.designer.source.level')" dense outlined :disable="disable"
          :data-testid="`notify-level-${i}`" @update:model-value="(v) => patch(i, { kind: 'initiatorManager', level: num(v) })"
        />
      </template>
      <q-separator v-if="i < modelValue.length - 1" class="q-mt-xs" />
    </div>
    <div v-if="!modelValue.length" class="text-muted" data-testid="notify-no-recipients">{{ t('approval.designer.graph.notify.noRecipients') }}</div>
    <div v-if="error" class="text-caption text-negative" data-testid="notify-recipients-error">{{ error }}</div>
    <div>
      <q-btn flat dense no-caps color="primary" icon="add" :label="t('approval.designer.graph.notify.addRecipient')" :disable="disable" data-testid="notify-add" @click="add" />
    </div>
  </div>
</template>
