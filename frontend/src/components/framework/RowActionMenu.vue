<script setup lang="ts">
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';

/**
 * 把列上的動作收進「⋮」選單。刪除、停用這類危險動作放最後並標 negative（第一個危險動作前面自動加分隔線）；
 * 確認對話框由呼叫端的 run 開。
 */
export interface RowAction {
  label: string;
  run: () => void;
  disable?: boolean;
  negative?: boolean;
  testid?: string;
}
const props = defineProps<{ actions: RowAction[]; testid?: string; ariaLabel?: string; icon?: string }>();
const { t } = useI18n();
const separatorAt = computed(() => props.actions.findIndex((a) => a.negative));
</script>

<template>
  <q-btn flat round dense :icon="icon ?? 'more_vert'" :aria-label="ariaLabel ?? t('common.moreActions')" :data-testid="testid" @click.stop>
    <q-menu>
      <q-list dense>
        <template v-for="(a, i) in actions" :key="i">
          <q-separator v-if="i > 0 && i === separatorAt" />
          <q-item v-close-popup clickable :disable="a.disable" :class="{ 'text-negative': a.negative }" :data-testid="a.testid" @click="a.run()">
            <q-item-section>{{ a.label }}</q-item-section>
          </q-item>
        </template>
      </q-list>
    </q-menu>
  </q-btn>
</template>
