<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';

/**
 * 清單工具列的一顆按鈕加它的面板（篩選、排序、欄位）：600 以上開下拉面板，手機開全螢幕對話框。
 * 面板內的改動即時生效，關掉不還原。count 大於 0 時接在按鈕文字後面。
 */
const props = defineProps<{ label: string; icon: string; testid: string; count?: number }>();
const { t } = useI18n();
const $q = useQuasar();
const phone = computed(() => !$q.screen.gt.xs);
const open = ref(false);
</script>

<template>
  <q-btn
    outline no-caps :dense="phone" :icon="icon" :label="props.count ? `${label} ${props.count}` : label" :data-testid="testid"
    :aria-haspopup="phone ? 'dialog' : 'menu'" @click="phone && (open = true)"
  >
    <q-menu v-if="!phone">
      <div class="toolbar-panel q-pa-sm"><slot /></div>
    </q-menu>
    <q-dialog v-else v-model="open" maximized>
      <q-card class="column no-wrap">
        <q-card-section class="row items-center no-wrap q-py-sm">
          <div class="text-h6 col">{{ label }}</div>
          <q-btn flat no-caps color="primary" :label="t('dynamic.toolbar.done')" :data-testid="`${testid}-done`" @click="open = false" />
        </q-card-section>
        <q-separator />
        <q-card-section class="col scroll q-pa-sm"><slot /></q-card-section>
      </q-card>
    </q-dialog>
  </q-btn>
</template>

<style scoped>
.toolbar-panel { width: min(520px, 90vw); }
</style>
