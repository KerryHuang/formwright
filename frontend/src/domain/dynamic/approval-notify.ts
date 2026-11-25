import { resolveI18n } from './i18n-text';
import type { I18nText } from './types';

/** 佔位符的值（fg5 計畫 T1）：三型擇一；`s` 只有 denied／empty，文字由前端 i18n 給。 */
export interface NotifyValue { t?: string; i18n?: I18nText; s?: string }

/** approvalNotify 通知的 `data`（契約見 backend ApprovalNotifyData；案例 shared/approval-notify-cases.json）。 */
export interface ApprovalNotifyData {
  v: number;
  node: string;
  /** false：收件人沒有這張單的讀取權——沒有連結、欄位與申請人一律無權限。 */
  read: boolean;
  template: { title: I18nText; body?: I18nText };
  values: Record<string, NotifyValue>;
}

/** 組字時的系統字（i18n 提供）：無權限、空值、標題被遮蔽的替代字。 */
export interface NotifyTexts { denied: string; empty: string; noTitle: string }

const MAX_LONG = 9223372036854775807n;
const isObj = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v);

/** 後端已解析並重組成白名單；這裡只確認形狀，不對就回 null（鈴鐺退回 title）。 */
export function parseNotifyData(raw: unknown): ApprovalNotifyData | null {
  if (!isObj(raw) || raw.v !== 1 || typeof raw.read !== 'boolean') return null;
  if (!isObj(raw.template) || !isObj(raw.template.title) || !isObj(raw.values)) return null;
  return raw as unknown as ApprovalNotifyData;
}

/** 與後端 ApprovalNotifyTemplate.Parse 同語意：只認 {title}{flow}{initiator}{node}{f:數字}，其他（含單獨大括號）照字面。 */
function tokens(text: string): { text?: string; key?: string; field?: boolean }[] {
  const out: { text?: string; key?: string; field?: boolean }[] = [];
  let lit = '';
  const flush = () => { if (lit) out.push({ text: lit }); lit = ''; };
  let i = 0;
  while (i < text.length) {
    if (text[i] === '{') {
      let j = i + 1;
      while (j < text.length && text[j] !== '{' && text[j] !== '}') j++;
      if (j < text.length && text[j] === '}') {
        const name = text.slice(i + 1, j);
        if (['title', 'flow', 'initiator', 'node'].includes(name)) { flush(); out.push({ key: name }); i = j + 1; continue; }
        // 與後端一樣把數字解成數值再組鍵（{f:021} 與 {f:21} 同一欄）；超過 long 範圍的數字照字面（後端 TryParse 失敗）。
        if (/^f:[0-9]+$/.test(name) && BigInt(name.slice(2)) <= MAX_LONG) { flush(); out.push({ key: `f:${BigInt(name.slice(2))}`, field: true }); i = j + 1; continue; }
        lit += text.slice(i, j + 1);
        i = j + 1;
        continue;
      }
    }
    lit += text[i];
    i++;
  }
  flush();
  return out;
}

function valueText(key: string, value: NotifyValue | undefined, locale: string, texts: NotifyTexts): string {
  if (!value) return texts.denied;
  const text = value.t ?? (value.i18n ? resolveI18n(value.i18n, locale) : undefined);
  if (text === undefined) return value.s === 'empty' ? texts.empty : texts.denied;
  if (text.length > 0) return text;
  return key === 'title' ? texts.noTitle : texts.empty;
}

function fill(template: string, data: ApprovalNotifyData, locale: string, texts: NotifyTexts): string {
  return tokens(template).map((p) => {
    if (p.key === undefined) return p.text;
    const value = p.field && !data.read ? undefined : data.values[p.key];
    return valueText(p.key, value, locale, texts);
  }).join('');
}

/** 依讀者語系組標題與內文（純函式）；缺值、`read:false` 的欄位一律無權限（fail closed）。內文省略回空字串。 */
export function renderNotify(data: ApprovalNotifyData, locale: string, texts: NotifyTexts): { title: string; body: string } {
  return {
    title: fill(resolveI18n(data.template.title, locale), data, locale, texts),
    body: data.template.body ? fill(resolveI18n(data.template.body, locale), data, locale, texts) : '',
  };
}
