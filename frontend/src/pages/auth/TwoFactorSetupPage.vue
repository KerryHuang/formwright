<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query';
import { ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRouter } from 'vue-router';
import { useLogout } from 'src/application/dynamic/queries/auth-queries';
import type { TwoFactorStatus } from 'src/domain/dynamic/types';
import TwoFactorCard from 'src/presentation/account/TwoFactorCard.vue';
import { useIdentityStore } from 'src/stores/use-identity-store';

// 強制綁定頁（帳號安全設計 §8.7、§12）：寬限已過、還沒啟用時守衛只放行這一頁；後端只放行綁定兩步、/auth/* 與 GET /me，
// 所以不讀帳號安全頁的 API，直接用「未啟用、在強制範圍內」的狀態開綁定對話框。只有登出能離開。
const { t } = useI18n();
const router = useRouter();
const qc = useQueryClient();
const identity = useIdentityStore();
const logoutMutation = useLogout();
const error = ref('');

const status: TwoFactorStatus = {
  enabled: false, recoveryCodesLeft: 0, unreadable: false, blocked: null, remindAtOrBelow: 0, hasPassword: true, required: true,
};

/** 綁定完成、按「我已保存」後解除，回首頁（守衛那時才載入營運時區等登入後設定）。 */
function done() {
  identity.setEnrollmentRequired(false);
  qc.clear();
  void router.replace('/');
}

const logout = () => {
  error.value = '';
  logoutMutation.mutate(undefined, {
    onSuccess: () => { void router.replace('/login'); },
    onError: () => { error.value = t('auth.logoutFailed'); },
  });
};
</script>

<template>
  <div class="row justify-center q-pa-md">
    <div style="width: 460px; max-width: 100%" class="q-gutter-md">
      <div class="text-h6" data-testid="tf-setup-title">{{ t('twoFactor.forcedTitle') }}</div>
      <div class="text-body2" data-testid="tf-setup-hint">{{ t('twoFactor.forcedHint') }}</div>
      <TwoFactorCard :status="status" @enabled="done" />
      <div v-if="error" class="text-negative" data-testid="tf-setup-error">{{ error }}</div>
      <div class="row justify-end">
        <q-btn flat no-caps :label="t('auth.logout')" :loading="logoutMutation.isPending.value" data-testid="tf-setup-logout" @click="logout" />
      </div>
    </div>
  </div>
</template>
