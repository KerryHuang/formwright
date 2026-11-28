<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRoute, useRouter } from 'vue-router';
import { useAuthOptions, useDemoLogin, useLogin, useTwoFactorLogin } from 'src/application/dynamic/queries/auth-queries';
import { BRAND_NAME } from 'src/domain/brand';
import { joinList } from 'src/i18n/translate';
import { isTwoFactorChallenge, type AuthUser, type LoginResult, type TwoFactorChallenge } from 'src/domain/dynamic/types';
import BrandMark from 'src/presentation/brand/BrandMark.vue';
import LocaleSwitcher from 'src/presentation/locale/LocaleSwitcher.vue';
import { safeRedirect } from 'src/router/auth-guard';
import type { ApiError } from 'src/services/api/http';
import { deviceLabel } from 'src/services/ops/error-reporter';

const { t } = useI18n();
const $q = useQuasar();
const route = useRoute();
const router = useRouter();
const options = useAuthOptions();
const login = useLogin();
const demoLogin = useDemoLogin();
const secondStep = useTwoFactorLogin();

const userId = ref('');
const password = ref('');
const error = ref('');

// 第二步（帳號安全設計 §8.3）：密碼正確後換成驗證碼畫面；憑證在 HttpOnly cookie，前端只記「在第二步」與 Demo 提示
const challenge = ref<TwoFactorChallenge | null>(null);
const useRecovery = ref(false);
const code = ref('');
const recoveryCode = ref('');
// 記住這台裝置（§8.5）：天數是政策 auth.twoFactorRememberDays 的生效值，0 時不顯示；預設不勾
const remember = ref(false);
const rememberDays = computed(() => options.data.value?.rememberDeviceDays ?? 0);

const enter = (u: AuthUser) => {
  // 用復原碼登入：提示剩幾組（§8.4）
  if (typeof u.recoveryCodesLeft === 'number') $q.notify({ type: 'warning', message: t('twoFactor.recoveryUsed', { count: u.recoveryCodesLeft }) });
  const redirect = route.query.redirect;
  void router.replace(u.mustChangePassword ? '/change-password' : safeRedirect(typeof redirect === 'string' ? redirect : undefined));
};
const done = (r: LoginResult) => {
  if (isTwoFactorChallenge(r)) {
    challenge.value = r;
    useRecovery.value = false;
    code.value = '';
    recoveryCode.value = '';
    password.value = '';
    remember.value = false;
    return;
  }
  enter(r);
};
// 只顯示已依語系產生的訊息：後端字串表的 401（登入失敗）、503（系統忙碌）與前端的網路錯誤；其餘（如 500 的英文標題）一律「登入失敗」
const fail = (e: unknown) => {
  const err = e as ApiError;
  const localized = err.status === 401 || err.status === 503 || err.code === 'network';
  error.value = (localized && err.message) || t('auth.loginFailed');
};

// 第二步的 400（驗證碼錯、已失效）與 429（暫停）訊息後端已依語系產生；400 的訊息在 errors.code、不在 message（走查 G1）。
// 憑證失效（逾時、錯太多次）回到第一步重新輸入密碼
const failSecond = (e: unknown) => {
  const err = e as ApiError;
  const localized = err.status === 429 || err.status === 503 || err.code === 'network';
  const message = err.status === 400 ? Object.values(err.errors ?? {}).flat()[0] ?? err.message : localized ? err.message : undefined;
  error.value = message || t('auth.loginFailed');
  if (err.code === 'twoFactorExpired') challenge.value = null;
};
const submitSecond = () => {
  error.value = '';
  const proof = useRecovery.value ? { recoveryCode: recoveryCode.value.trim() } : { code: code.value.trim() };
  const device = remember.value && rememberDays.value > 0 ? { rememberDevice: true, deviceLabel: deviceLabel() } : {};
  secondStep.send({}, { ...proof, ...device }, { onSuccess: enter, onError: failSecond });
};
const back = () => {
  challenge.value = null;
  error.value = '';
};

const submit = () => {
  error.value = '';
  login.send({ userId: userId.value }, { password: password.value }, { onSuccess: done, onError: fail });
};
const pickDemo = (id: string) => {
  error.value = '';
  demoLogin.mutate(id, { onSuccess: done, onError: fail });
};
</script>

<template>
  <div class="row justify-center q-pa-md">
    <q-card style="width: 360px; max-width: 100%">
      <q-card-section>
        <div data-testid="login-brand">
          <div class="row items-center no-wrap">
            <BrandMark class="q-mr-sm" style="font-size: 28px" />
            <h1 class="text-h6 col q-my-none">{{ BRAND_NAME }}</h1>
            <LocaleSwitcher />
          </div>
          <div class="text-caption text-muted q-mt-xs">{{ t('auth.tagline') }}</div>
        </div>
        <h2 class="text-subtitle2 q-mt-md q-mb-none">{{ t('auth.loginTitle') }}</h2>
      </q-card-section>
      <q-form v-if="challenge" @submit.prevent="submitSecond">
        <q-card-section class="q-gutter-sm" data-testid="login-second-step">
          <div class="text-subtitle2">{{ t('twoFactor.loginTitle') }}</div>
          <div class="text-caption">{{ useRecovery ? t('twoFactor.loginRecoveryHint') : t('twoFactor.loginCodeHint') }}</div>
          <q-banner v-if="challenge.demoHint" dense class="bg-info-soft" data-testid="login-demo-hint">
            <div>{{ t('twoFactor.demoHint', { code: challenge.demoHint.code }) }}</div>
            <div v-if="challenge.demoHint.recoveryCode" class="text-caption">{{ t('twoFactor.demoRecoveryHint', { code: challenge.demoHint.recoveryCode }) }}</div>
          </q-banner>
          <q-input
            v-if="!useRecovery" v-model="code" :label="t('twoFactor.code')" inputmode="numeric" autocomplete="one-time-code" maxlength="6"
            autofocus dense outlined data-testid="login-code"
          />
          <q-input
            v-else v-model="recoveryCode" :label="t('twoFactor.recoveryCode')" autocomplete="off" autofocus dense outlined
            data-testid="login-recovery"
          />
          <q-checkbox
            v-if="rememberDays > 0" v-model="remember" dense :label="t('twoFactor.rememberDevice', { days: rememberDays })"
            data-testid="login-remember"
          />
          <div v-if="error" class="text-negative" data-testid="login-error">{{ error }}</div>
          <q-btn type="submit" color="primary" class="full-width" :label="t('twoFactor.verify')" :loading="secondStep.isPending.value" data-testid="login-second-submit" />
          <div class="row justify-between">
            <q-btn flat dense no-caps size="sm" color="primary" :label="t('auth.backToLogin')" data-testid="login-back" @click="back" />
            <q-btn
              flat dense no-caps size="sm" color="primary" :label="useRecovery ? t('twoFactor.useCode') : t('twoFactor.useRecovery')"
              data-testid="login-toggle-recovery" @click="useRecovery = !useRecovery; error = ''"
            />
          </div>
        </q-card-section>
      </q-form>
      <q-form v-else @submit.prevent="submit">
        <q-card-section class="q-gutter-sm">
          <q-input v-model="userId" :label="t('auth.userId')" autocomplete="username" autofocus dense outlined data-testid="login-user" />
          <q-input v-model="password" :label="t('auth.password')" type="password" autocomplete="current-password" dense outlined data-testid="login-password" />
          <div v-if="error" class="text-negative" data-testid="login-error">{{ error }}</div>
          <q-btn type="submit" color="primary" class="full-width" :label="t('auth.login')" :loading="login.isPending.value" data-testid="login-submit" />
          <!-- 忘記密碼（帳號安全設計 §7.4）：不可用時照樣顯示，點進去只寫請聯絡管理員 -->
          <div class="text-right">
            <router-link to="/forgot-password" class="text-caption text-primary" data-testid="login-forgot">{{ t('auth.forgotLink') }}</router-link>
          </div>
        </q-card-section>
      </q-form>
      <template v-if="options.data.value?.demoLoginEnabled && !challenge">
        <q-separator />
        <q-card-section>
          <div class="text-subtitle2 q-mb-sm">{{ t('auth.demoTitle') }}</div>
          <div v-if="options.data.value.demoGenerating" class="text-caption" data-testid="demo-generating">{{ t('auth.demoGenerating') }}</div>
          <div class="row q-col-gutter-sm" data-testid="demo-accounts">
            <div v-for="a in options.data.value.demoAccounts" :key="a.userId" class="col-12 col-sm-6">
              <q-btn outline no-caps align="left" class="full-width full-height" :data-testid="`demo-account-${a.userId}`" @click="pickDemo(a.userId)">
                <div class="full-width text-left demo-account-text">
                  <div>{{ a.displayName }}</div>
                  <div v-if="a.groups.length" class="text-caption text-muted" :data-testid="`demo-account-groups-${a.userId}`">{{ joinList(a.groups) }}</div>
                </div>
              </q-btn>
            </div>
          </div>
        </q-card-section>
      </template>
    </q-card>
  </div>
</template>

<style scoped>
.demo-account-text { overflow-wrap: anywhere; line-height: 1.4; }
</style>
