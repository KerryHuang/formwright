import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import { toValue, type MaybeRef } from 'vue';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { SECRET_MUTATION, useSecretMutation } from './secret-mutation';

/** 「帳號安全」頁（帳號安全設計 §12）。 */
export const useAccountSecurity = () => useQuery({ queryKey: ['account-security'], queryFn: dynamicApi.getAccountSecurity });

/** 改自己的 Email：密碼用 send 綁定當次提交，不進 mutation variables（見 useSecretMutation）。 */
export function useSetMyEmail() {
  const qc = useQueryClient();
  return useSecretMutation((take: (v: { email: string | null }) => { password: string }) =>
    useMutation({
      ...SECRET_MUTATION,
      mutationFn: (v: { email: string | null }) => dynamicApi.setMyEmail({ ...v, ...take(v) }),
      onSuccess: () => qc.invalidateQueries({ queryKey: ['account-security'] }),
    }));
}

export function useResendMyEmail() {
  return useMutation({ mutationFn: () => dynamicApi.resendMyEmail() });
}

/** 管理員設定／清空別人的 Email、重寄驗證信；清單與寄送紀錄跟著重取。 */
export function useSetUserEmail() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (v: { id: string; email: string | null }) => dynamicApi.setUserEmail(v.id, v.email),
    onSuccess: () => Promise.all([qc.invalidateQueries({ queryKey: ['admin-users'] }), qc.invalidateQueries({ queryKey: ['mail-outbox'] })]),
  });
}

export function useResendUserEmail() {
  const qc = useQueryClient();
  return useMutation({ mutationFn: (id: string) => dynamicApi.resendUserEmail(id), onSuccess: () => qc.invalidateQueries({ queryKey: ['mail-outbox'] }) });
}

/** 管理員寄重設密碼連結（帳號安全設計 §7.5）：寄送紀錄跟著重取。 */
export function useSendResetLink() {
  const qc = useQueryClient();
  return useMutation({ mutationFn: (id: string) => dynamicApi.sendResetLink(id), onSuccess: () => qc.invalidateQueries({ queryKey: ['mail-outbox'] }) });
}

export const useMailOutbox = (page: MaybeRef<number>) =>
  useQuery({ queryKey: ['mail-outbox', page], queryFn: () => dynamicApi.getMailOutbox(toValue(page)) });

// ── 雙因素（帳號安全設計 §8.2、§8.9）：密碼、驗證碼、復原碼都用 send 綁當次提交，不進 mutation variables ──

type Proof = { password: string; code?: string; recoveryCode?: string };

/** 開始綁定或重新綁定：回金鑰（只這一次）。 */
export function useStartTwoFactorSetup() {
  return useSecretMutation((take: (v: object) => Proof) =>
    useMutation({ ...SECRET_MUTATION, gcTime: 0, mutationFn: (v: object) => dynamicApi.startTwoFactorSetup(take(v)) }));
}

/** 完成綁定：回 10 組復原碼（只這一次）；其他裝置登出、本頁不受影響。 */
export function useEnableTwoFactor() {
  const qc = useQueryClient();
  return useSecretMutation((take: (v: object) => { code: string }) =>
    useMutation({
      ...SECRET_MUTATION,
      gcTime: 0,
      mutationFn: (v: object) => dynamicApi.enableTwoFactor(take(v).code),
      onSuccess: () => Promise.all([qc.invalidateQueries({ queryKey: ['account-security'] }), qc.invalidateQueries({ queryKey: ['me'] })]),
    }));
}

export function useDisableTwoFactor() {
  const qc = useQueryClient();
  return useSecretMutation((take: (v: object) => Proof) =>
    useMutation({
      ...SECRET_MUTATION,
      mutationFn: (v: object) => dynamicApi.disableTwoFactor(take(v)),
      onSuccess: () => Promise.all([qc.invalidateQueries({ queryKey: ['account-security'] }), qc.invalidateQueries({ queryKey: ['me'] })]),
    }));
}

/** 重新產生復原碼（只接受驗證碼，§8.4）。 */
export function useRegenerateRecoveryCodes() {
  const qc = useQueryClient();
  return useSecretMutation((take: (v: object) => { password: string; code: string }) =>
    useMutation({
      ...SECRET_MUTATION,
      gcTime: 0,
      mutationFn: (v: object) => dynamicApi.regenerateRecoveryCodes(take(v)),
      onSuccess: () => qc.invalidateQueries({ queryKey: ['account-security'] }),
    }));
}

/** 管理員重設別人的雙因素（§8.8）：清單與寄送紀錄跟著重取。 */
export function useResetUserTwoFactor() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => dynamicApi.resetUserTwoFactor(id),
    onSuccess: () => Promise.all([qc.invalidateQueries({ queryKey: ['admin-users'] }), qc.invalidateQueries({ queryKey: ['mail-outbox'] })]),
  });
}
