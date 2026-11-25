import type { I18nText, Locale, SchemaField } from 'src/domain/dynamic/types';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { Translate } from 'src/i18n/translate';

// 通知範本的「插入佔位符」選單與插入動作（純函式）。語法以後端 ApprovalNotifyTemplate 為準：{title} {flow} {initiator} {node} {f:欄位Id}。

export interface PlaceholderOption { value: string; label: string }

const BUILTIN = ['title', 'flow', 'initiator', 'node'] as const;

/** 內建四項＋本模組表頭啟用中的欄位（`fields` 來自 toSchemaFields，只含啟用中的表頭欄位；明細欄位不在其中，後端也不收）。 */
export function placeholderOptions(fields: SchemaField[], locale: string, t: Translate): PlaceholderOption[] {
  return [
    ...BUILTIN.map((k) => ({ value: `{${k}}`, label: t(`approval.designer.graph.notify.ph.${k}`) })),
    ...fields.map((f) => ({ value: `{f:${f.id}}`, label: t('approval.designer.graph.notify.ph.field', { name: resolveI18n(f.label, locale) || f.key }) })),
  ];
}

/**
 * 把佔位符接在文字結尾：每個「已有文字的語系」與 `defaults`（必填語系）都接一份，
 * 因為佔位符不分語言；使用者要另外調整位置，直接在輸入框改。
 */
export function appendPlaceholder(text: I18nText, token: string, defaults: Locale[]): I18nText {
  const next: I18nText = { ...text };
  for (const l of new Set([...Object.keys(text), ...defaults])) next[l] = `${text[l] ?? ''}${token}`;
  return next;
}
