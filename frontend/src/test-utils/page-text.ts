import type { VueWrapper } from '@vue/test-utils';

/** 整頁文字扣掉歷程卡片：歷程的空狀態說明提到「新增、修改、刪除」，會讓「沒有刪除鈕」這類整頁斷言誤判。 */
export function textWithoutHistory(w: Pick<VueWrapper, 'text' | 'find'>): string {
  const history = w.find('[data-testid="record-history"]');
  return history.exists() ? w.text().replace(history.text(), '') : w.text();
}
