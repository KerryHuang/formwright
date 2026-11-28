import { resolveI18n } from './i18n-text';
import type { I18nText } from './types';

/** 比對範圍：啟用、停用、關閉的模組；草稿與明細模組不算。 */
const COMPARED = new Set(['Active', 'Inactive', 'Closed']);

/** 新名稱的繁中值或目前語系值和另一個模組相同時，回那個模組的名稱（目前語系）；沒有撞名回 undefined。 */
export function duplicateModuleName(
  modules: { name: I18nText; status: string; detailOf?: string }[], name: I18nText, locale: string,
): string | undefined {
  const same = (other: I18nText) => ['zh-TW', locale].some((l) => {
    const mine = name[l]?.trim();
    return !!mine && other[l]?.trim() === mine;
  });
  const found = modules.find((m) => !m.detailOf && COMPARED.has(m.status) && same(m.name));
  return found && resolveI18n(found.name, locale);
}
