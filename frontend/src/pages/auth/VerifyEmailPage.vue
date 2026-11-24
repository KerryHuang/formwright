<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { dynamicApi } from 'src/services/api/dynamic-api';
import { useIdentityStore } from 'src/stores/use-identity-store';

const { t } = useI18n();
const identity = useIdentityStore();
/** 'verifying'：送出中；'ok'：已驗證；'invalid'：連結失效或用過；'failed'：連不上伺服器等。 */
const state = ref<'verifying' | 'ok' | 'invalid' | 'failed'>('verifying');

onMounted(() => {
  // 權杖在 # 後面（不進伺服器 log 與 Referer）；讀到就從網址列清掉，重新整理或分享畫面都不會帶著它
  const token = new URLSearchParams(window.location.hash.slice(1)).get('t') ?? '';
  if (window.location.hash) window.history.replaceState(window.history.state, '', window.location.pathname);
  if (!token) { state.value = 'invalid'; return; }
  // 直接呼叫、不經 vue-query：權杖不留在 mutation cache（au1a 審查次要；單次使用、只送一次，不需要重試或快取）
  dynamicApi.verifyEmail(token).then(
    () => { state.value = 'ok'; },
    (e) => { state.value = (e as { status?: number }).status === 400 ? 'invalid' : 'failed'; },
  );
});
</script>

<template>
  <div class="row justify-center q-pa-md">
    <q-card style="width: 400px; max-width: 100%">
      <q-card-section class="text-h6">{{ t('accountSecurity.verifyTitle') }}</q-card-section>
      <q-card-section>
        <div v-if="state === 'verifying'" class="row items-center q-gutter-sm"><q-spinner /><span>{{ t('accountSecurity.verifying') }}</span></div>
        <div v-else-if="state === 'ok'" class="text-positive" data-testid="verify-ok">{{ t('accountSecurity.verifyOk') }}</div>
        <div v-else-if="state === 'invalid'" class="text-negative" data-testid="verify-invalid">{{ t('accountSecurity.verifyInvalid') }}</div>
        <div v-else class="text-negative" data-testid="verify-failed">{{ t('accountSecurity.verifyFailed') }}</div>
      </q-card-section>
      <q-card-actions>
        <q-btn
          flat no-caps color="primary" :label="identity.userId ? t('auth.backToApp') : t('accountSecurity.toLogin')"
          :to="identity.userId ? '/' : '/login'" data-testid="verify-back"
        />
      </q-card-actions>
    </q-card>
  </div>
</template>
