import { useQueries } from '@tanstack/vue-query';
import { computed, type Ref } from 'vue';
import type { RecentRecord } from 'src/application/dynamic/recent-records';
import type { DynamicRecord } from 'src/domain/dynamic/types';
import { dynamicApi } from 'src/services/api/dynamic-api';

/**
 * 逐筆讀最近開過的單據（平行，和明細頁共用快取鍵）。讀不到的（刪掉了、沒有權限、模組鍵對不上）直接略過，不顯示錯誤；
 * 快取裡留著舊資料但這次讀取失敗的也略過。
 */
export function useRecentRecordDetails(recent: Ref<RecentRecord[]>) {
  const results = useQueries({
    queries: computed(() => recent.value.map((r) => ({
      queryKey: ['record', r.recordId],
      queryFn: () => dynamicApi.getRecord(r.recordId),
      retry: false,
    }))),
  });
  return computed<DynamicRecord[]>(() => recent.value.flatMap((r, i) => {
    const q = results.value[i];
    return q && !q.isError && q.data?.moduleKey === r.moduleKey ? [q.data] : [];
  }));
}
