import { useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/vue-query';
import { computed, type Ref } from 'vue';
import { dynamicApi, type OpenAppBody, type UpgradeApplyBody, type UpgradeBody } from 'src/services/api/dynamic-api';

/** Opening/closing changes module statuses, so menu, module list and every cached schema (readOnly) go stale. */
const invalidateAll = (qc: QueryClient) => Promise.all(
  ['apps', 'app-packages', 'menu', 'modules', 'schema'].map((k) => qc.invalidateQueries({ queryKey: [k] })));

export function useAppPackages() {
  return useQuery({ queryKey: ['app-packages'], queryFn: dynamicApi.getAppPackages });
}

/** Upgrade preview; a 409 means up to date / no candidate / downgrade. */
/** body 是畫面上改的群組對應（§10.2 第 3 點）；改了就重新預覽。 */
export function useAppUpgradePreview(key: string, body?: Ref<UpgradeBody>) {
  return useQuery({
    queryKey: computed(() => ['app-upgrade', key, body?.value ?? {}]),
    queryFn: () => dynamicApi.previewAppUpgrade(key, body?.value ?? {}),
    retry: false,
  });
}

export function useApplyAppUpgrade(key: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: UpgradeApplyBody) => dynamicApi.applyAppUpgrade(key, body),
    // 套用會改模組、欄位與選單，同開啟一樣全部重抓
    onSuccess: () => invalidateAll(qc),
  });
}

export function useApps() {
  return useQuery({ queryKey: ['apps'], queryFn: dynamicApi.getApps });
}

export function useImportAppPackage() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (json: string) => dynamicApi.importAppPackage(json),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['app-packages'] }),
  });
}

export function usePreviewOpenApp() {
  return useMutation({ mutationFn: (v: { id: string; body: OpenAppBody }) => dynamicApi.previewOpenApp(v.id, v.body) });
}

export function useOpenApp() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (v: { id: string; body: OpenAppBody }) => dynamicApi.openApp(v.id, v.body),
    onSuccess: () => invalidateAll(qc),
  });
}

export function useCloseApp() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (v: { key: string; cascade: boolean }) => dynamicApi.closeApp(v.key, v.cascade),
    onSuccess: () => invalidateAll(qc),
  });
}

export function useReopenApp() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (key: string) => dynamicApi.reopenApp(key),
    onSuccess: () => invalidateAll(qc),
  });
}

export function useDeleteApp() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (v: { key: string; purged: number }) => dynamicApi.deleteApp(v.key, v.purged),
    onSuccess: () => invalidateAll(qc),
  });
}
