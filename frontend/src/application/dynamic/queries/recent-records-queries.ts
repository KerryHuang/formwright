import { useQueries } from '@tanstack/vue-query';
import { computed, type Ref } from 'vue';
import type { RecentRecord } from 'src/application/dynamic/recent-records';
import type { DynamicRecord } from 'src/domain/dynamic/types';
import { dynamicApi } from 'src/services/api/dynamic-api';

/**
 * 逐筆讀最近開過的單據（平行）。只回這次掛載後成功取回的結果：權限或資料範圍可能剛被收回，
 * 所以不吃明細頁的快取（獨立的快取鍵、每次掛載重抓），重新驗權中、暫停（離線）、失敗、模組鍵對不上的都略過，不顯示錯誤。
 */
export function useRecentRecordDetails(recent: Ref<RecentRecord[]>) {
  const results = useQueries({
    queries: computed(() => recent.value.map((r) => ({
      queryKey: ['recent-record', r.moduleKey, r.recordId],
      queryFn: () => dynamicApi.getRecord(r.recordId),
      retry: false,
      staleTime: 0,
      refetchOnMount: 'always' as const,
    }))),
  });
  return computed<DynamicRecord[]>(() => recent.value.flatMap((r, i) => {
    const q = results.value[i];
    const fresh = !!q && q.isFetchedAfterMount && q.isSuccess && q.fetchStatus === 'idle';
    return fresh && q.data?.moduleKey === r.moduleKey ? [q.data] : [];
  }));
}
