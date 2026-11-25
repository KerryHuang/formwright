<template>
  <q-btn flat dense round icon="notifications" class="q-mr-sm" :aria-label="t('notifications.title')" data-testid="notification-bell">
    <q-badge v-if="unread > 0" color="negative" floating rounded data-testid="notification-badge">{{ unread }}</q-badge>
    <q-menu anchor="bottom right" self="top right" class="notification-menu">
      <q-list>
        <q-item>
          <q-item-section class="text-subtitle2">{{ t('notifications.title') }}</q-item-section>
          <q-item-section side>
            <q-btn
              flat dense no-caps color="primary" :label="t('notifications.readAll')" :disable="unread === 0"
              data-testid="notification-read-all" @click="markAll.mutate()"
            />
          </q-item-section>
        </q-item>
        <q-separator />
        <q-item v-if="!items.length" data-testid="notification-empty">
          <q-item-section class="text-muted">{{ t('notifications.empty') }}</q-item-section>
        </q-item>
        <q-item
          v-for="n in items" :key="n.id" v-close-popup clickable :class="{ 'text-weight-bold': !n.read }"
          :data-testid="`notification-item-${n.id}`" @click="open(n)"
        >
          <q-item-section>
            <q-item-label class="notification-text">{{ label(n) }}</q-item-label>
            <q-item-label v-if="preview(n)" caption class="notification-preview" :data-testid="`notification-preview-${n.id}`">{{ preview(n) }}</q-item-label>
            <q-item-label caption :data-testid="`notification-time-${n.id}`">{{ timeText(n) }}</q-item-label>
          </q-item-section>
        </q-item>
      </q-list>
    </q-menu>
    <NotificationDetailDialog
      v-if="detail" v-model="detailOpen" :title="detail.title" :body="detail.body" :can-open="detail.canOpen" @open="openDetailLink"
    />
  </q-btn>
</template>

<script setup lang="ts">
import { date } from 'quasar';
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRouter } from 'vue-router';
import {
  useMarkAllNotificationsRead, useMarkNotificationRead, useNotifications,
} from 'src/application/dynamic/queries/notifications-queries';
import { parseNotifyData, renderNotify } from 'src/domain/dynamic/approval-notify';
import { NOTIFICATION_KINDS, type NotificationItem } from 'src/domain/dynamic/task-types';
import NotificationDetailDialog from './NotificationDetailDialog.vue';
import { safeRedirect } from 'src/router/auth-guard';
import { incidentTitle } from 'src/domain/ops/ops-format';

const { t, te, locale } = useI18n();
const router = useRouter();
const { data } = useNotifications();
const markRead = useMarkNotificationRead();
const markAll = useMarkAllNotificationsRead();

const items = computed(() => data.value?.items ?? []);
const unread = computed(() => data.value?.unreadCount ?? 0);

/** 通知節點（approvalNotify）的標題與內文，依讀者語系從 data 組出；沒有 data 或格式不對回 null，退回 title（設計 §24）。 */
function composed(n: NotificationItem) {
  if (n.kind !== 'approvalNotify') return null;
  const data = parseNotifyData(n.data);
  if (!data) return null;
  return {
    ...renderNotify(data, locale.value, { denied: t('notifications.notify.denied'), empty: t('notifications.notify.empty'), noTitle: t('notifications.notify.noTitle') }),
    canOpen: data.read && !!n.link,
  };
}

/** 清單只顯示內文前 2 行，後面還有內容就加「…」（U-a）；過長的單行由 CSS 截斷。 */
function preview(n: NotificationItem) {
  const body = composed(n)?.body.trim();
  if (!body) return '';
  const lines = body.split('\n');
  return lines.length > PREVIEW_LINES ? `${lines.slice(0, PREVIEW_LINES).join('\n')}…` : body;
}
const PREVIEW_LINES = 2;

/**
 * "Kind label + title"; a kind this build does not know shows the title alone. Ops kinds compose the title from titleKey＋params in the
 * current locale (維運設計 §17.2.5) and fall back to the zh-TW snapshot when the server sent no titleKey.
 */
function label(n: NotificationItem) {
  const known = (NOTIFICATION_KINDS as readonly string[]).includes(n.kind);
  const title = composed(n)?.title ?? (n.titleKey ? incidentTitle(t, te, n.titleKey, n.titleParams) : n.title);
  return known ? `${t(`notifications.kind.${n.kind}`)} ${title}` : title;
}

const timeText = (n: NotificationItem) => (n.createdTime ? date.formatDate(new Date(n.createdTime), 'YYYY-MM-DD HH:mm') : '');

/** Mark as read first, then follow the link. Only in-app paths are followed (never `//host`, `/\host` or a full URL). */
async function open(n: NotificationItem) {
  const link = n.link;
  const view = composed(n);
  // 通知節點：標已讀並開詳情，由詳情的「開啟單據」前往（U-a）；沒有讀取權時沒有連結（U-h）。
  if (view) detail.value = { title: view.title, body: view.body, canOpen: view.canOpen, link };
  try {
    await markRead.mutateAsync(n.id);
  } catch {
    // Still follow the link: failing to mark it read must not make the notification unreachable.
  }
  if (view) detailOpen.value = true;
  else follow(link);
}

const detail = ref<{ title: string; body: string; canOpen: boolean; link: string } | null>(null);
const detailOpen = ref(false);
const follow = (link: string) => { if (link && safeRedirect(link) === link) void router.push(link); };
function openDetailLink() {
  detailOpen.value = false;
  if (detail.value?.canOpen) follow(detail.value.link);
}
</script>

<style scoped>
.notification-text {
  white-space: normal;
  overflow-wrap: anywhere;
}
.notification-preview {
  white-space: pre-line;
  overflow-wrap: anywhere;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}
.notification-menu {
  max-width: min(360px, 92vw);
}
</style>
