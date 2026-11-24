<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { useAccountSecurity, useResendMyEmail, useSetMyEmail } from 'src/application/dynamic/queries/account-security-queries';
import { leftoverMessages } from 'src/presentation/dynamic/designer/api-error';
import type { ApiError } from 'src/services/api/http';
import { joinList } from 'src/i18n/translate';
import TwoFactorCard from 'src/presentation/account/TwoFactorCard.vue';

const { t } = useI18n();
const $q = useQuasar();
const security = useAccountSecurity();
const data = computed(() => security.data.value);

// 變更 Email 對話框：要重新輸入目前密碼（後端走登入限流）
const editing = ref(false);
const email = ref('');
const password = ref('');
const error = ref('');
const setEmail = useSetMyEmail();
function openEdit() {
  email.value = data.value?.email ?? '';
  password.value = '';
  error.value = '';
  editing.value = true;
}
function submit() {
  error.value = '';
  setEmail.send({ email: email.value.trim() || null }, { password: password.value }, {
    onSuccess: (r) => {
      editing.value = false;
      $q.notify({ type: 'positive', message: r.email ? t('accountSecurity.emailSaved') : t('accountSecurity.emailCleared') });
    },
    onError: (e) => { error.value = joinList(leftoverMessages(e as unknown as ApiError, [])) || t('accountSecurity.saveFailed'); },
  });
}

const resend = useResendMyEmail();
function resendMail() {
  resend.mutate(undefined, {
    onSuccess: () => $q.notify({ type: 'positive', message: t('accountSecurity.resent') }),
    onError: (e) => $q.notify({ type: 'negative', message: joinList(leftoverMessages(e as unknown as ApiError, [])) || t('accountSecurity.saveFailed') }),
  });
}
</script>

<template>
  <q-page padding>
    <div class="text-h6 q-mb-md">{{ t('accountSecurity.title') }}</div>
    <div v-if="security.isError.value && !data" class="text-negative" data-testid="security-load-error">{{ t('accountSecurity.loadFailed') }}</div>
    <div v-else-if="data" class="row q-col-gutter-md">
      <div class="col-12 col-md-6">
        <q-card flat bordered data-testid="security-email-card">
          <q-card-section>
            <div class="text-subtitle1">{{ t('accountSecurity.emailTitle') }}</div>
            <div class="text-caption text-muted">{{ t('accountSecurity.emailHint') }}</div>
          </q-card-section>
          <q-card-section class="q-pt-none">
            <div v-if="data.email" class="row items-center q-gutter-sm" style="overflow-wrap: anywhere">
              <span data-testid="security-email">{{ data.email }}</span>
              <q-badge
                :color="data.emailVerified ? 'positive' : 'warning'" :label="data.emailVerified ? t('accountSecurity.verified') : t('accountSecurity.unverified')"
                data-testid="security-email-status"
              />
            </div>
            <div v-else class="text-muted" data-testid="security-email-none">{{ t('accountSecurity.noEmail') }}</div>
            <div v-if="data.email && !data.emailVerified" class="text-caption q-mt-sm">
              {{ data.mailAvailable ? t('accountSecurity.checkInbox') : t('accountSecurity.mailUnavailable') }}
            </div>
          </q-card-section>
          <q-card-actions align="right">
            <q-btn
              v-if="data.email && !data.emailVerified && data.mailAvailable" flat no-caps color="primary" :label="t('accountSecurity.resend')"
              :loading="resend.isPending.value" data-testid="security-resend" @click="resendMail"
            />
            <q-btn
              v-if="data.canChangeEmail" flat no-caps color="primary" :label="t('accountSecurity.changeEmail')" data-testid="security-email-edit"
              @click="openEdit"
            />
          </q-card-actions>
        </q-card>
      </div>
      <div class="col-12 col-md-6">
        <q-card flat bordered data-testid="security-password-card">
          <q-card-section>
            <div class="text-subtitle1">{{ t('accountSecurity.passwordTitle') }}</div>
            <div class="text-caption text-muted">{{ t('accountSecurity.passwordHint') }}</div>
          </q-card-section>
          <q-card-actions align="right">
            <q-btn flat no-caps color="primary" :label="t('auth.changePassword')" to="/change-password" data-testid="security-password-link" />
          </q-card-actions>
        </q-card>
      </div>
      <div v-if="data.twoFactor" class="col-12 col-md-6">
        <TwoFactorCard :status="data.twoFactor" />
      </div>
    </div>

    <q-dialog v-model="editing" :persistent="setEmail.isPending.value" @hide="password = ''">
      <q-card style="width: 420px; max-width: 95vw">
        <q-card-section class="text-h6">{{ t('accountSecurity.changeEmail') }}</q-card-section>
        <q-form @submit.prevent="submit">
          <q-card-section class="q-gutter-sm">
            <q-input v-model="email" type="email" :label="t('accountSecurity.emailTitle')" :hint="t('accountSecurity.emailClearHint')" dense outlined data-testid="security-email-input" />
            <q-input
              v-model="password" type="password" autocomplete="current-password" :label="t('auth.currentPassword')" dense outlined
              data-testid="security-password-input"
            />
            <div v-if="error" class="text-negative" data-testid="security-email-error">{{ error }}</div>
          </q-card-section>
          <q-card-actions align="right">
            <q-btn flat no-caps :label="t('users.cancel')" :disable="setEmail.isPending.value" @click="editing = false" />
            <q-btn type="submit" color="primary" no-caps :label="t('users.save')" :loading="setEmail.isPending.value" data-testid="security-email-save" />
          </q-card-actions>
        </q-form>
      </q-card>
    </q-dialog>
  </q-page>
</template>
