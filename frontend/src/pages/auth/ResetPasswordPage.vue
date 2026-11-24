<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, onMounted, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRouter } from 'vue-router';
import { useAuthOptions } from 'src/application/dynamic/queries/auth-queries';
import { leftoverMessages } from 'src/presentation/dynamic/designer/api-error';
import { dynamicApi } from 'src/services/api/dynamic-api';
import type { ApiError } from 'src/services/api/http';
import { joinList } from 'src/i18n/translate';

// 用連結重設密碼（帳號安全設計 §7.1 第 4–6 點）：先檢查連結（不消耗），有效才顯示表單；成功不自動登入，回登入頁
const { t } = useI18n();
const $q = useQuasar();
const router = useRouter();
const options = useAuthOptions();

/** 'checking'：檢查連結中；'form'：連結有效；'invalid'：失效或用過；'failed'：連不上伺服器等。 */
const state = ref<'checking' | 'form' | 'invalid' | 'failed'>('checking');
// 權杖只留在元件變數：直接呼叫 API、不經 vue-query，不進 mutation／query cache（同 /verify-email）
let token = '';
const newPassword = ref('');
const confirm = ref('');
const error = ref('');
const sending = ref(false);

// 最短長度讀回之前不顯示數字（前端畫面通則）
const newPasswordLabel = computed(() => {
  const min = options.data.value?.passwordMinLength;
  return min === undefined ? t('auth.newPasswordBare') : t('auth.newPassword', { min });
});

const invalid = (e: unknown) => (e as ApiError).status === 400 && (e as ApiError).code === 'resetLinkInvalid';

onMounted(() => {
  // 權杖在 # 後面（不進伺服器 log 與 Referer）；讀到就從網址列清掉
  token = new URLSearchParams(window.location.hash.slice(1)).get('t') ?? '';
  if (window.location.hash) window.history.replaceState(window.history.state, '', window.location.pathname);
  if (!token) { state.value = 'invalid'; return; }
  dynamicApi.checkResetLink(token).then(
    () => { state.value = 'form'; },
    (e) => { state.value = invalid(e) ? 'invalid' : 'failed'; },
  );
});

const submit = () => {
  error.value = '';
  const min = options.data.value?.passwordMinLength;
  // 前端只在「送出一定會被後端擋」時先擋；最短長度讀 auth/options，讀到之前交給後端判斷
  if (min !== undefined && newPassword.value.length < min) { error.value = t('auth.passwordTooShort', { min }); return; }
  if (newPassword.value !== confirm.value) { error.value = t('auth.passwordMismatch'); return; }
  sending.value = true;
  dynamicApi.resetPasswordByLink(token, newPassword.value).then(
    () => {
      token = '';
      $q.notify({ type: 'positive', message: t('auth.resetDone') });
      void router.replace('/login');
    },
    (e) => {
      if (invalid(e)) { state.value = 'invalid'; return; }
      error.value = joinList(leftoverMessages(e as ApiError, [])) || t('auth.resetFailed');
    },
  ).finally(() => { sending.value = false; });
};
</script>

<template>
  <div class="row justify-center q-pa-md">
    <q-card style="width: 400px; max-width: 100%">
      <q-card-section class="text-h6">{{ t('auth.resetTitle') }}</q-card-section>
      <q-card-section v-if="state === 'checking'" class="row items-center q-gutter-sm">
        <q-spinner /><span>{{ t('auth.resetChecking') }}</span>
      </q-card-section>
      <q-card-section v-else-if="state === 'invalid'" class="text-negative" data-testid="reset-invalid">{{ t('auth.resetInvalid') }}</q-card-section>
      <q-card-section v-else-if="state === 'failed'" class="text-negative" data-testid="reset-failed">{{ t('auth.resetFailed') }}</q-card-section>
      <q-form v-else @submit.prevent="submit">
        <q-card-section class="q-gutter-sm">
          <q-input
            v-model="newPassword" :label="newPasswordLabel" type="password"
            autocomplete="new-password" autofocus dense outlined data-testid="reset-new"
          />
          <q-input v-model="confirm" :label="t('auth.confirmPassword')" type="password" autocomplete="new-password" dense outlined data-testid="reset-confirm" />
          <div v-if="error" class="text-negative" data-testid="reset-error">{{ error }}</div>
          <q-btn type="submit" color="primary" class="full-width" :label="t('auth.resetSubmit')" :loading="sending" data-testid="reset-submit" />
        </q-card-section>
      </q-form>
      <q-card-actions>
        <q-btn v-if="state === 'invalid'" flat no-caps color="primary" :label="t('auth.resetAgain')" to="/forgot-password" data-testid="reset-again" />
        <q-space />
        <q-btn flat no-caps :label="t('auth.backToLogin')" to="/login" data-testid="reset-back" />
      </q-card-actions>
    </q-card>
  </div>
</template>
