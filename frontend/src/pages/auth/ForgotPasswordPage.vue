<script setup lang="ts">
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useAuthOptions } from 'src/application/dynamic/queries/auth-queries';
import { dynamicApi } from 'src/services/api/dynamic-api';

// 忘記密碼（帳號安全設計 §7.1、§12）：回應一律相同，畫面也只有同一句結果，不透露帳號是否存在
const { t } = useI18n();
const options = useAuthOptions();
const contactAdmin = computed(() => options.data.value?.passwordReset === 'contactAdmin');

const userId = ref('');
const sending = ref(false);
const sentMinutes = ref<number | null>(null);
const error = ref('');

const submit = () => {
  if (!userId.value.trim()) return;
  error.value = '';
  sending.value = true;
  dynamicApi.forgotPassword(userId.value).then(
    (r) => { sentMinutes.value = r.linkMinutes; },
    () => { error.value = t('auth.forgotFailed'); },
  ).finally(() => { sending.value = false; });
};
</script>

<template>
  <div class="row justify-center q-pa-md">
    <q-card style="width: 400px; max-width: 100%">
      <q-card-section class="text-h6">{{ t('auth.forgotTitle') }}</q-card-section>
      <q-card-section v-if="options.isPending.value" class="row justify-center"><q-spinner /></q-card-section>
      <q-card-section v-else-if="contactAdmin" data-testid="forgot-contact-admin">{{ t('auth.forgotContactAdmin') }}</q-card-section>
      <q-card-section v-else-if="sentMinutes !== null" class="text-positive" data-testid="forgot-sent">
        {{ t('auth.forgotSent', { minutes: sentMinutes }) }}
      </q-card-section>
      <q-form v-else @submit.prevent="submit">
        <q-card-section class="q-gutter-sm">
          <div class="text-caption">{{ t('auth.forgotHint') }}</div>
          <div v-if="options.data.value?.demoLoginEnabled" class="text-caption text-warning" data-testid="forgot-demo-hint">
            {{ t('auth.forgotDemoHint') }}
          </div>
          <q-input v-model="userId" :label="t('auth.userId')" autocomplete="username" autofocus dense outlined data-testid="forgot-user" />
          <div v-if="error" class="text-negative" data-testid="forgot-error">{{ error }}</div>
          <q-btn
            type="submit" color="primary" class="full-width" :label="t('auth.forgotSubmit')" :loading="sending" :disable="!userId.trim()"
            data-testid="forgot-submit"
          />
        </q-card-section>
      </q-form>
      <q-card-actions>
        <q-btn flat no-caps color="primary" :label="t('auth.backToLogin')" to="/login" data-testid="forgot-back" />
      </q-card-actions>
    </q-card>
  </div>
</template>
