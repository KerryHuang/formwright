<script setup lang="ts" generic="T">
import { ref, watch } from 'vue';
import { useI18n } from 'vue-i18n';

// POC thin wrapper; replaced by the framework component of the same name when merged.
const props = defineProps<{
  columns: { name: string; label: string }[]; rows: T[]; rowKey: (row: T) => string; readonly?: boolean;
  /** 手機卡片用：有錯誤的列一律展開，錯誤才不會藏在收合的卡片裡。 */
  hasError?: (row: T) => boolean;
  /** 手機卡片用：這一列不顯示的欄位，連欄名一起略過（表格模式照舊留空格）。 */
  isCellHidden?: (row: T, name: string) => boolean;
}>();
const emit = defineEmits<{ add: [] }>();
defineSlots<Record<`cell-${string}`, (p: { row: T; index: number }) => unknown> & {
  'row-actions'?: (p: { row: T; index: number }) => unknown;
  /** 手機寬度可編輯時，收合卡片上的值摘要。 */
  summary?: (p: { row: T; index: number }) => unknown;
}>();
const { t } = useI18n();

/** 手機卡片展開中的列（rowKey）。 */
const opened = ref(new Set<string>());
const isOpen = (row: T) => props.readonly || !!props.hasError?.(row) || opened.value.has(props.rowKey(row));
function toggle(row: T) {
  if (props.hasError?.(row)) return;
  const key = props.rowKey(row);
  if (!opened.value.delete(key)) opened.value.add(key);
}
// 有過錯誤的列記成展開：錯誤隨編輯消失時卡片不會在輸入中途收起來
watch(
  () => props.rows.filter((r) => props.hasError?.(r)).map(props.rowKey),
  (keys) => keys.forEach((k) => opened.value.add(k)),
  { immediate: true },
);
// 只有按「新增一列」帶進來的列自動展開；重新載入的列維持收合
let adding = false;
function add() {
  adding = true;
  emit('add');
}
watch(() => props.rows.map(props.rowKey), (keys, before) => {
  if (!adding) return;
  adding = false;
  keys.filter((k) => !before.includes(k)).forEach((k) => opened.value.add(k));
});
</script>

<template>
  <div>
    <template v-if="$q.screen.lt.sm">
      <q-card v-for="(row, index) in rows" :key="rowKey(row)" flat bordered class="q-mb-sm" data-testid="row-card">
        <div
          v-if="!readonly" class="row items-center no-wrap q-pa-sm cursor-pointer card-clickable" role="button" tabindex="0"
          :aria-expanded="isOpen(row)" data-testid="row-card-toggle" @click="toggle(row)"
          @keydown.enter.self.prevent="toggle(row)" @keydown.space.self.prevent="toggle(row)"
        >
          <div class="text-muted q-mr-sm">#{{ index + 1 }}</div>
          <div class="col" style="overflow-wrap: anywhere" data-testid="row-card-summary">
            <slot v-if="!isOpen(row)" name="summary" :row="row" :index="index" />
          </div>
          <q-icon :name="isOpen(row) ? 'expand_less' : 'expand_more'" size="sm" aria-hidden="true" />
        </div>
        <!-- v-show：收合時保留編輯元件的狀態（例如參照欄位剛挑到的標題） -->
        <div v-show="isOpen(row)" class="q-pa-sm" data-testid="row-card-body">
          <template v-for="c in columns" :key="c.name">
            <div v-if="!isCellHidden?.(row, c.name)" class="q-mb-sm" role="group" :aria-label="c.label">
              <div class="text-caption text-muted">{{ c.label }}</div>
              <div style="overflow-wrap: anywhere"><slot :name="`cell-${c.name}`" :row="row" :index="index" /></div>
            </div>
          </template>
          <div v-if="$slots['row-actions']"><slot name="row-actions" :row="row" :index="index" /></div>
        </div>
      </q-card>
      <div v-if="!rows.length" class="text-muted q-pa-sm">{{ t('dynamic.noData') }}</div>
    </template>
    <q-markup-table v-else flat bordered dense separator="cell" wrap-cells>
      <thead>
        <tr>
          <th v-for="c in columns" :key="c.name" class="text-left">{{ c.label }}</th>
          <th v-if="$slots['row-actions']" />
        </tr>
      </thead>
      <tbody>
        <tr v-for="(row, index) in rows" :key="rowKey(row)">
          <td v-for="c in columns" :key="c.name"><slot :name="`cell-${c.name}`" :row="row" :index="index" /></td>
          <td v-if="$slots['row-actions']"><slot name="row-actions" :row="row" :index="index" /></td>
        </tr>
        <tr v-if="!rows.length">
          <td :colspan="columns.length + 1" class="text-muted">{{ t('dynamic.noData') }}</td>
        </tr>
      </tbody>
    </q-markup-table>
    <q-btn v-if="!readonly" flat dense color="primary" icon="add" :label="t('dynamic.details.addRow')" data-testid="add-row" class="q-mt-xs" @click="add" />
  </div>
</template>
