import { useMutation, useQuery, useQueryClient } from '@tanstack/vue-query';
import { isTwoFactorChallenge, type AuthUser, type LoginResult } from 'src/domain/dynamic/types';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { i18n } from 'src/i18n/instance';
import { takeStartupAuthOptions } from 'src/i18n/startup';
import { endPreview } from 'src/i18n/ui-messages';
import { useIdentityStore } from 'src/stores/use-identity-store';
import { resetSessionSettings } from '../session-settings';
import { SECRET_MUTATION, useSecretMutation } from './secret-mutation';

/**
 * Polls while the server is generating demo accounts so they appear without a reload. 第一次掛載重用啟動那次的 auth/options
 * （首屏只打一次，i4b 計畫判斷 12），之後的輪詢與重取都是新請求。
 */
export function useAuthOptions() {
  return useQuery({
    queryKey: ['auth-options'],
    queryFn: () => takeStartupAuthOptions() ?? dynamicApi.getAuthOptions(),
    refetchInterval: (q) => (q.state.data?.demoGenerating ? 2000 : false),
  });
}

/** A new session must not see the previous user's cached data. */
function useStartSession() {
  const qc = useQueryClient();
  const identity = useIdentityStore();
  return async (u: AuthUser) => {
    qc.clear();
    identity.setSession(u.userId, u.mustChangePassword);
    // 登入回應沒有 isDemoAccount；導頁前先從 /me 補齊，守衛才擋得住 Demo 帳號進改密碼頁。失敗時留 false，下次載入由守衛補。
    try {
      identity.setDemoAccount((await dynamicApi.getMe()).isDemoAccount === true);
    } catch { /* keep false */ }
  };
}

/** 要第二步時（帳號安全設計 §8.3）還沒有 session，不開始；由呼叫端顯示第二步畫面。 */
function useStartUnlessChallenged() {
  const start = useStartSession();
  return async (r: LoginResult) => { if (!isTwoFactorChallenge(r)) await start(r); };
}

/** 密碼用 send 綁定當次提交，不進 mutation variables（見 useSecretMutation）。 */
export function useLogin() {
  const start = useStartUnlessChallenged();
  return useSecretMutation((take: (b: { userId: string }) => { password: string }) =>
    useMutation({ ...SECRET_MUTATION, mutationFn: (b: { userId: string }) => dynamicApi.login({ ...b, ...take(b) }), onSuccess: start }));
}

export function useDemoLogin() {
  const start = useStartUnlessChallenged();
  return useMutation({ mutationFn: (userId: string) => dynamicApi.demoLogin(userId), onSuccess: start });
}

/** 登入第二步：驗證碼或復原碼同樣綁當次提交。 */
export function useTwoFactorLogin() {
  const start = useStartSession();
  return useSecretMutation((take: (v: object) => { code?: string; recoveryCode?: string }) =>
    useMutation({ ...SECRET_MUTATION, mutationFn: (v: object) => dynamicApi.twoFactorLogin(take(v)), onSuccess: start }));
}

/**
 * Clears the local session only after the server confirmed the logout. On a failure (network down) the cookie is still valid,
 * and a reload would return to the same account, so the caller keeps the page and shows an error.
 */
export function useLogout() {
  const qc = useQueryClient();
  const identity = useIdentityStore();
  return useMutation({
    mutationFn: () => dynamicApi.logout(),
    onSuccess: () => {
      qc.clear();
      identity.clear();
      resetSessionSettings();
      endPreview(i18n);
    },
  });
}

/** 同 useLogin：新舊密碼用 send 綁定當次提交（variables 傳新建的 {}）。 */
export function useChangePassword() {
  const identity = useIdentityStore();
  return useSecretMutation((take: (v: object) => { oldPassword: string; newPassword: string }) =>
    useMutation({
      ...SECRET_MUTATION,
      mutationFn: (v: object) => dynamicApi.changePassword(take(v)),
      onSuccess: (u) => identity.setSession(u.userId, u.mustChangePassword),
    }));
}
