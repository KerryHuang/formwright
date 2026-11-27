<script lang="ts">
export interface HeaderActionChild {
  key: string;
  label: string;
  /** Text in the phone menu (defaults to label). */
  menuLabel?: string;
  run: () => void;
  testid?: string;
}

/** One entry of the bar. Which actions exist is decided by the page (schema, permission, API), never here. */
export interface HeaderAction {
  key: string;
  label: string;
  menuLabel?: string;
  icon?: string;
  color?: string;
  outline?: boolean;
  flat?: boolean;
  to?: string;
  run?: () => void;
  /** Id of the form this button submits (native `form` attribute), so validation stays in the form. */
  submitForm?: string;
  loading?: boolean;
  /** Phone menu: shown red. */
  negative?: boolean;
  /** Phone: a button on the bottom bar instead of an item in the menu. */
  bottom?: boolean;
  testid?: string;
  /** A dropdown of sub-actions (e.g. print templates); on the phone each child is a menu item. */
  children?: HeaderActionChild[];
}
</script>

<script setup lang="ts">
import { QPageSticky, useQuasar } from 'quasar';
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRouter } from 'vue-router';
import RowActionMenu, { type RowAction } from 'src/components/framework/RowActionMenu.vue';

const props = defineProps<{
  backLabel: string;
  backTo: string;
  backAriaLabel: string;
  title?: string;
  actions: HeaderAction[];
  /** Phone: another fixed bottom bar is on screen, so every action goes into one menu in the top bar. */
  menuOnly?: boolean;
  backTestid?: string;
  titleTestid?: string;
}>();
const { t } = useI18n();
const $q = useQuasar();
const router = useRouter();
const phone = computed(() => $q.screen.lt.sm);

const bottomActions = computed(() => (phone.value && !props.menuOnly ? props.actions.filter((a) => a.bottom) : []));
const menuSource = computed(() => (props.menuOnly ? props.actions : props.actions.filter((a) => !a.bottom)));
const menu = computed<RowAction[]>(() => menuSource.value.flatMap((a): RowAction[] => (a.children
  ? a.children.map((c) => ({ label: c.menuLabel ?? c.label, run: c.run, testid: c.testid }))
  : [{ label: a.menuLabel ?? a.label, run: a.run ?? (() => { if (a.to) void router.push(a.to); }), negative: a.negative, testid: a.testid }])));
const showBottom = computed(() => phone.value && !props.menuOnly && (bottomActions.value.length > 0 || menu.value.length > 0));
const barTitle = computed(() => props.title || (phone.value ? props.backLabel : undefined));

function btnProps(a: HeaderAction) {
  return {
    color: a.color, outline: a.outline, flat: a.flat, icon: a.icon, label: a.label, loading: a.loading, noCaps: false,
    'data-testid': a.testid,
    ...(a.to ? { to: a.to } : {}),
    ...(a.submitForm ? { type: 'submit', form: a.submitForm } : {}),
  };
}
</script>

<template>
  <div class="record-header-bar" data-testid="record-header-bar">
    <div class="record-header-main">
      <q-btn
        flat dense no-caps icon="arrow_back" :label="phone ? undefined : backLabel" :round="phone" :aria-label="backAriaLabel"
        :to="backTo" :data-testid="backTestid ?? 'record-back'"
      />
      <template v-if="barTitle">
        <span v-if="!phone" class="record-header-sep" aria-hidden="true">/</span>
        <span class="text-h6 record-header-title" :data-testid="titleTestid ?? 'record-title'">{{ barTitle }}</span>
      </template>
      <slot name="badge" />
    </div>
    <div v-if="phone && menuOnly && menu.length" class="record-header-actions">
      <RowActionMenu :actions="menu" testid="record-more" />
    </div>
    <div v-else-if="!phone && actions.length" class="record-header-actions" data-testid="record-actions">
      <template v-for="a in actions" :key="a.key">
        <q-btn-dropdown v-if="a.children" outline :icon="a.icon" :label="a.label" :loading="a.loading" :data-testid="a.testid">
          <q-list>
            <q-item v-for="c in a.children" :key="c.key" v-close-popup clickable :data-testid="c.testid" @click="c.run()">
              <q-item-section>{{ c.label }}</q-item-section>
            </q-item>
          </q-list>
        </q-btn-dropdown>
        <q-btn v-else v-bind="btnProps(a)" @click="a.run?.()" />
      </template>
    </div>
  </div>
  <QPageSticky v-if="showBottom" position="bottom" expand class="record-bottom-sticky" style="margin: 0">
    <div class="row items-center justify-between no-wrap full-width record-bottom-bar" data-testid="record-bottom-bar">
      <q-btn v-for="a in bottomActions" :key="a.key" v-bind="btnProps(a)" @click="a.run?.()" />
      <RowActionMenu v-if="menu.length" :actions="menu" :aria-label="t('common.moreActions')" testid="record-more" />
    </div>
  </QPageSticky>
</template>

<style scoped>
.record-header-bar {
  position: sticky;
  top: var(--app-header-h, 0px);
  z-index: 90;
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  margin: 0 -16px 16px;
  padding: 8px 16px;
  background: var(--t-panel);
  border-bottom: 1px solid var(--t-border);
}
.record-header-main {
  display: flex;
  flex: 1 1 auto;
  align-items: center;
  gap: 8px;
  min-width: 0;
}
.record-header-sep { color: var(--t-muted); }
.record-header-title { overflow-wrap: anywhere; }
.record-header-actions {
  display: flex;
  flex: 0 0 auto;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  margin-left: auto;
}
/* Above the blocks that follow in the page (they are positioned too), below the header, drawers and dialogs. */
.record-bottom-sticky { z-index: 100; }
.record-bottom-bar {
  gap: 8px;
  padding: 8px 8px calc(8px + env(safe-area-inset-bottom));
  background: var(--t-panel);
  border-top: 1px solid var(--t-border);
}
</style>
