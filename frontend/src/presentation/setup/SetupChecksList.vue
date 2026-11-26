<script setup lang="ts">
import { useI18n } from 'vue-i18n';
import type { SetupCheckItem } from 'src/services/api/setup-api';

defineProps<{ items: SetupCheckItem[] }>();
const { t, te } = useI18n({ useScope: 'global' });

const title = (key: string) => (te(`setup.check.${key}`) ? t(`setup.check.${key}`) : key);
// 後端只回代碼；沒有對應句子的新代碼退回原碼，不丟翻譯鍵
const problem = (i: SetupCheckItem) => {
  if (!i.problem) return '';
  const key = `setup.problem.${i.problem}`;
  return te(key) ? t(key, i.params ?? {}) : i.problem;
};
</script>

<template>
  <q-list separator data-testid="setup-checks">
    <q-item v-for="i in items" :key="i.key" :data-testid="`setup-check-${i.key}`" :class="i.ok ? 'setup-check-ok' : 'setup-check-red'">
      <q-item-section avatar>
        <q-icon :name="i.ok ? 'check_circle' : 'cancel'" :color="i.ok ? 'positive' : 'negative'" aria-hidden="true" />
      </q-item-section>
      <q-item-section>
        <q-item-label>{{ title(i.key) }}</q-item-label>
        <q-item-label v-if="i.path" caption class="setup-path">{{ i.path }}</q-item-label>
        <q-item-label v-if="!i.ok && i.problem" class="text-negative">{{ problem(i) }}</q-item-label>
        <q-item-label v-if="!i.ok && i.params?.with" caption class="setup-path">{{ i.params.with }}</q-item-label>
      </q-item-section>
    </q-item>
  </q-list>
</template>

<style scoped>
.setup-path {
  word-break: break-all;
}
</style>
