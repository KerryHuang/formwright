import type { Translate } from 'src/i18n/translate';

export type UnavailableKind = 'recordMissing' | 'moduleMissing' | 'forbidden' | 'addForbidden' | 'editForbidden' | 'failed';

/** 單據詳情與表單頁打不開時，RecordUnavailable 要顯示的文字與返回目的地。 */
export function unavailableView(kind: UnavailableKind, p: {
  t: Translate; moduleKey: string; moduleName: string;
  /** schema 讀得到（有權限資訊）：返回鈕回模組列表，否則回首頁。 */
  schemaRead: boolean;
}) {
  const known = !!p.moduleName;
  const text = (name: string) => p.t(`dynamic.unavailable.${name}`, { module: p.moduleName || p.t('dynamic.unavailable.fallbackModule') });
  const toList = kind !== 'moduleMissing' && p.schemaRead && known;
  const title = {
    recordMissing: 'recordMissingTitle',
    moduleMissing: 'moduleMissingTitle',
    forbidden: known ? 'forbiddenTitle' : 'forbiddenTitleUnknown',
    addForbidden: 'addForbiddenTitle',
    editForbidden: 'editForbiddenTitle',
    failed: known ? 'failedTitle' : 'failedTitleUnknown',
  }[kind];
  const reason = {
    recordMissing: 'recordMissingReason',
    moduleMissing: 'moduleMissingReason',
    forbidden: 'forbiddenReason',
    addForbidden: 'forbiddenReason',
    editForbidden: 'forbiddenReason',
    failed: 'failedReason',
  }[kind];
  return {
    title: text(title),
    reason: text(reason),
    backLabel: text(toList ? 'backToList' : 'backHome'),
    backTo: toList ? `/dynamic/${p.moduleKey}` : '/',
  };
}
