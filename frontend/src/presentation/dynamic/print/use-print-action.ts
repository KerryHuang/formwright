import { useQuasar } from 'quasar';
import { computed, toValue, ref, type MaybeRefOrGetter } from 'vue';
import { useI18n } from 'vue-i18n';
import { usePrintTemplates } from 'src/application/dynamic/queries/print-queries';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import { dynamicApi } from 'src/services/api/dynamic-api';
import type { ApiError } from 'src/services/api/http';

/**
 * The print trigger without a button: the page lists `items` as a button or as menu entries, calls `show`, and renders
 * one `PrintPreviewDialog` bound to `open`/`html`/`title`, so the dialog never lives inside a menu that unmounts on close.
 */
export function usePrintAction(moduleKey: MaybeRefOrGetter<string>, recordId: MaybeRefOrGetter<number>) {
  const { t, locale } = useI18n();
  const $q = useQuasar();
  const templates = usePrintTemplates(moduleKey, recordId);
  const items = computed(() => (templates.data.value ?? []).map((o) => ({ key: o.key, name: resolveI18n(o.name, locale.value) })));

  const html = ref('');
  const title = ref('');
  const open = ref(false);
  const busy = ref(false);

  async function show(key: string, name: string) {
    busy.value = true;
    try {
      html.value = await dynamicApi.printRecord(toValue(recordId), key, locale.value);
      title.value = name;
      open.value = true;
    } catch (e) {
      $q.notify({ type: 'negative', message: (e as ApiError).message || t('print.failed') });
    } finally {
      busy.value = false;
    }
  }

  return { items, html, title, open, busy, show };
}
