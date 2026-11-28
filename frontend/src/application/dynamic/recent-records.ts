/** 多存一些：模組被停用或收回權限的紀錄在首頁會被濾掉，不該讓清單縮水。首頁只顯示前 5 筆。 */
export const RECENT_STORED = 10;
export const RECENT_SHOWN = 5;
const storageKey = (userId: string) => `formwright:recent-records:${userId}`;

/** 只存鍵值、不存標題：共用瀏覽器不留別人看過的單據標題，顯示時才向 API 取。 */
export interface RecentRecord {
  moduleKey: string;
  recordId: number;
}

const isRecent = (v: unknown): v is RecentRecord => {
  const r = v as Partial<RecentRecord> | null;
  return typeof r?.moduleKey === 'string' && !!r.moduleKey && Number.isInteger(r.recordId);
};

/** Records the user opened lately, newest first. Storage may be blocked or corrupt: then there is simply no history. */
export function readRecentRecords(userId: string): RecentRecord[] {
  if (!userId) return [];
  try {
    const v: unknown = JSON.parse(localStorage.getItem(storageKey(userId)) ?? '[]');
    return Array.isArray(v)
      ? v.filter(isRecent).map(({ moduleKey, recordId }) => ({ moduleKey, recordId })).slice(0, RECENT_STORED)
      : [];
  } catch {
    return [];
  }
}

export function recordRecentRecord(userId: string, moduleKey: string, recordId: number) {
  const entry = { moduleKey, recordId };
  if (!userId || !isRecent(entry)) return;
  try {
    const next = [entry, ...readRecentRecords(userId).filter((r) => r.moduleKey !== moduleKey || r.recordId !== recordId)].slice(0, RECENT_STORED);
    localStorage.setItem(storageKey(userId), JSON.stringify(next));
  } catch {
    // 無痕或被封鎖時略過：最近開過只是便利功能
  }
}
