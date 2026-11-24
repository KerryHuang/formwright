<script setup lang="ts">
import { useQuasar } from 'quasar';
import { computed, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import { renderSVG } from 'uqr';
import {
  useDisableTwoFactor, useEnableTwoFactor, useRegenerateRecoveryCodes, useStartTwoFactorSetup,
} from 'src/application/dynamic/queries/account-security-queries';
import type { TwoFactorSetup, TwoFactorStatus } from 'src/domain/dynamic/types';
import { leftoverMessages } from 'src/presentation/dynamic/designer/api-error';
import type { ApiError } from 'src/services/api/http';
import { joinList } from 'src/i18n/translate';

// 帳號安全頁的雙因素卡（帳號安全設計 §8.2、§8.4、§8.9）：啟用、重新綁定、關閉、重新產生復原碼都在同一個對話框分步驟走。
// 金鑰與復原碼明文只存在這個元件的 ref，關閉對話框就清掉；密碼、驗證碼用 secret mutation 綁當次提交。
const props = defineProps<{ status: TwoFactorStatus }>();
const { t } = useI18n();
const $q = useQuasar();

type Mode = 'enable' | 'rebind' | 'disable' | 'regenerate';
type Step = 'proof' | 'scan' | 'codes';
const open = ref(false);
const mode = ref<Mode>('enable');
const step = ref<Step>('proof');
const password = ref('');
const code = ref('');
const useRecovery = ref(false);
const recoveryCode = ref('');
const setup = ref<TwoFactorSetup | null>(null);
const codes = ref<string[]>([]);
const error = ref('');
const copyState = ref<'idle' | 'copied' | 'failed'>('idle');

const start = useStartTwoFactorSetup();
const enable = useEnableTwoFactor();
const disable = useDisableTwoFactor();
const regenerate = useRegenerateRecoveryCodes();
const busy = computed(() => start.isPending.value || enable.isPending.value || disable.isPending.value || regenerate.isPending.value);

const canBind = computed(() => !props.status.blocked && props.status.hasPassword);
const low = computed(() => props.status.enabled && props.status.recoveryCodesLeft <= props.status.remindAtOrBelow);
// QR 碼固定白底黑點、外圍留白（SVG 自帶背景；掃描器要高對比，不跟主題變色）
const qr = computed(() => setup.value ? `data:image/svg+xml;utf8,${encodeURIComponent(renderSVG(setup.value.otpAuthUri, { border: 2 }))}` : '');
const destructive = computed(() => mode.value === 'disable');
const proofNeedsCode = computed(() => mode.value !== 'enable');
const allowRecovery = computed(() => mode.value === 'rebind' || mode.value === 'disable');
const title = computed(() => ({
  enable: t('twoFactor.enable'), rebind: t('twoFactor.rebind'), disable: t('twoFactor.disable'), regenerate: t('twoFactor.regenerate'),
})[mode.value]);
const proofHint = computed(() => ({
  enable: '', rebind: t('twoFactor.proofHint'), disable: t('twoFactor.disableHint') + ' ' + t('twoFactor.proofHint'), regenerate: t('twoFactor.regenerateHint'),
})[mode.value]);

function clearSecrets() {
  password.value = '';
  code.value = '';
  recoveryCode.value = '';
  setup.value = null;
  codes.value = [];
  // 回應裡的金鑰與復原碼也留在 mutation 狀態：頁面開著時 gcTime: 0 不會回收，要自己清（審查 S3）
  start.reset();
  enable.reset();
  regenerate.reset();
}

function openDialog(m: Mode) {
  clearSecrets();
  mode.value = m;
  step.value = 'proof';
  error.value = '';
  copyState.value = 'idle';
  // 金鑰解不開時驗證碼一定不對，重新綁定預設改用復原碼
  useRecovery.value = m === 'rebind' && props.status.unreadable;
  open.value = true;
}

function close() {
  clearSecrets();
  open.value = false;
}

const fail = (e: unknown) => { error.value = joinList(leftoverMessages(e as unknown as ApiError, [])) || t('twoFactor.failed'); };

function proof() {
  if (!proofNeedsCode.value) return { password: password.value };
  return useRecovery.value && allowRecovery.value
    ? { password: password.value, recoveryCode: recoveryCode.value.trim() }
    : { password: password.value, code: code.value.trim() };
}

function submitProof() {
  error.value = '';
  if (mode.value === 'disable') {
    disable.send({}, proof(), {
      onSuccess: () => { close(); $q.notify({ type: 'positive', message: t('twoFactor.disabled') }); },
      onError: fail,
    });
    return;
  }
  if (mode.value === 'regenerate') {
    regenerate.send({}, { password: password.value, code: code.value.trim() }, {
      onSuccess: (r) => { password.value = ''; code.value = ''; codes.value = r.recoveryCodes; step.value = 'codes'; },
      onError: fail,
    });
    return;
  }
  start.send({}, proof(), {
    onSuccess: (r) => { password.value = ''; code.value = ''; recoveryCode.value = ''; setup.value = r; step.value = 'scan'; },
    onError: fail,
  });
}

function submitCode() {
  error.value = '';
  enable.send({}, { code: code.value.trim() }, {
    onSuccess: (r) => {
      setup.value = null;
      code.value = '';
      codes.value = r.recoveryCodes;
      step.value = 'codes';
      $q.notify({ type: 'positive', message: t('twoFactor.enabled') });
    },
    onError: fail,
  });
}

async function copyCodes() {
  try {
    await navigator.clipboard.writeText(codes.value.join('\n'));
    copyState.value = 'copied';
  } catch { copyState.value = 'failed'; }
}

/** 已顯示復原碼時 Esc 或點背景不關，要按「我已保存」。 */
function onToggle(v: boolean) {
  if (!v && step.value !== 'codes' && !busy.value) close();
}
</script>

<template>
  <q-card flat bordered data-testid="security-2fa-card">
    <q-card-section>
      <div class="row items-center q-gutter-sm">
        <div class="text-subtitle1">{{ t('twoFactor.title') }}</div>
        <q-badge
          :color="status.enabled ? 'positive' : 'muted'" :label="status.enabled ? t('twoFactor.statusOn') : t('twoFactor.statusOff')"
          data-testid="security-2fa-status"
        />
      </div>
      <div class="text-caption text-muted">{{ t('twoFactor.hint') }}</div>
    </q-card-section>
    <q-card-section class="q-pt-none q-gutter-sm">
      <div v-if="status.enabled" :class="low ? 'text-negative' : ''" data-testid="security-2fa-left">
        {{ t('twoFactor.recoveryLeft', { count: status.recoveryCodesLeft }) }}<span v-if="low">　{{ t('twoFactor.recoveryLow') }}</span>
      </div>
      <q-banner v-if="status.unreadable" dense class="bg-negative-soft" data-testid="security-2fa-unreadable">{{ t('twoFactor.unreadable') }}</q-banner>
      <q-banner v-if="status.blocked === 'keysNotPersisted'" dense class="bg-info-soft" data-testid="security-2fa-keys">{{ t('twoFactor.keysNotPersisted') }}</q-banner>
      <q-banner v-else-if="status.blocked === 'demoSite'" dense class="bg-info-soft" data-testid="security-2fa-demo">{{ t('twoFactor.demoSite') }}</q-banner>
      <div v-else-if="!status.hasPassword" class="text-muted" data-testid="security-2fa-no-password">{{ t('twoFactor.noPassword') }}</div>
    </q-card-section>
    <q-card-actions v-if="canBind" align="right" class="q-gutter-xs">
      <q-btn v-if="!status.enabled" flat no-caps color="primary" :label="t('twoFactor.enable')" data-testid="security-2fa-enable" @click="openDialog('enable')" />
      <template v-else>
        <q-btn
          v-if="!status.unreadable" flat no-caps color="primary" :label="t('twoFactor.regenerate')" data-testid="security-2fa-regenerate"
          @click="openDialog('regenerate')"
        />
        <q-btn flat no-caps color="primary" :label="t('twoFactor.rebind')" data-testid="security-2fa-rebind" @click="openDialog('rebind')" />
        <q-btn flat no-caps color="negative" :label="t('twoFactor.disable')" data-testid="security-2fa-disable" @click="openDialog('disable')" />
      </template>
    </q-card-actions>
  </q-card>

  <q-dialog :model-value="open" no-backdrop-dismiss :maximized="$q.screen.lt.sm" @update:model-value="onToggle" @hide="clearSecrets">
    <q-card :style="$q.screen.lt.sm ? undefined : 'width: 460px; max-width: 95vw'" data-testid="security-2fa-dialog">
      <q-card-section class="text-h6">{{ step === 'codes' ? t('twoFactor.codesTitle') : step === 'scan' ? t('twoFactor.setupTitle') : title }}</q-card-section>

      <q-form v-if="step === 'proof'" @submit.prevent="submitProof">
        <q-card-section class="q-gutter-sm">
          <div v-if="proofHint" class="text-caption">{{ proofHint }}</div>
          <q-input
            v-model="password" type="password" autocomplete="current-password" :label="t('auth.currentPassword')" dense outlined
            data-testid="security-2fa-password"
          />
          <template v-if="proofNeedsCode">
            <q-input
              v-if="!useRecovery || !allowRecovery" v-model="code" inputmode="numeric" autocomplete="one-time-code" maxlength="6"
              :label="t('twoFactor.code')" dense outlined data-testid="security-2fa-code"
            />
            <q-input v-else v-model="recoveryCode" autocomplete="off" :label="t('twoFactor.recoveryCode')" dense outlined data-testid="security-2fa-recovery" />
            <q-btn
              v-if="allowRecovery" flat dense no-caps size="sm" color="primary" :label="useRecovery ? t('twoFactor.useCode') : t('twoFactor.useRecovery')"
              data-testid="security-2fa-toggle" @click="useRecovery = !useRecovery"
            />
          </template>
          <div v-if="error" class="text-negative" data-testid="security-2fa-error">{{ error }}</div>
        </q-card-section>
        <q-card-actions align="right">
          <q-btn flat no-caps :label="t('users.cancel')" :disable="busy" @click="close" />
          <q-btn
            type="submit" no-caps :color="destructive ? 'negative' : 'primary'" :label="destructive ? t('twoFactor.disable') : t('twoFactor.next')"
            :loading="busy" data-testid="security-2fa-submit"
          />
        </q-card-actions>
      </q-form>

      <q-form v-else-if="step === 'scan' && setup" @submit.prevent="submitCode">
        <q-card-section class="q-gutter-sm">
          <div class="text-caption">{{ t('twoFactor.setupScan') }}</div>
          <div class="row justify-center">
            <img :src="qr" :alt="t('twoFactor.qrAlt')" class="tf-qr" data-testid="security-2fa-qr">
          </div>
          <div class="text-caption text-muted">{{ t('twoFactor.secret') }}</div>
          <code class="tf-code" data-testid="security-2fa-secret">{{ setup.secret }}</code>
          <div class="text-caption">{{ t('twoFactor.setupConfirm', { minutes: setup.pendingMinutes }) }}</div>
          <q-input
            v-model="code" inputmode="numeric" autocomplete="one-time-code" maxlength="6" :label="t('twoFactor.code')" autofocus dense outlined
            data-testid="security-2fa-confirm-code"
          />
          <div v-if="error" class="text-negative" data-testid="security-2fa-error">{{ error }}</div>
        </q-card-section>
        <q-card-actions align="right">
          <q-btn flat no-caps :label="t('users.cancel')" :disable="busy" @click="close" />
          <q-btn type="submit" color="primary" no-caps :label="t('twoFactor.finish')" :loading="busy" data-testid="security-2fa-finish" />
        </q-card-actions>
      </q-form>

      <template v-else-if="step === 'codes'">
        <q-card-section class="q-gutter-sm">
          <q-banner dense class="bg-negative-soft" data-testid="security-2fa-codes-warn">{{ t('twoFactor.codesWarn') }}</q-banner>
          <div class="tf-codes" data-testid="security-2fa-codes">
            <code v-for="c in codes" :key="c" class="tf-code">{{ c }}</code>
          </div>
          <div v-if="copyState === 'failed'" class="text-negative" data-testid="security-2fa-copy-failed">{{ t('twoFactor.copyFailed') }}</div>
        </q-card-section>
        <q-card-actions align="right">
          <q-btn
            flat no-caps icon="content_copy" :label="copyState === 'copied' ? t('twoFactor.copied') : t('twoFactor.copy')" data-testid="security-2fa-copy"
            @click="copyCodes"
          />
          <q-btn color="primary" no-caps :label="t('twoFactor.codesDone')" data-testid="security-2fa-codes-done" @click="close" />
        </q-card-actions>
      </template>
    </q-card>
  </q-dialog>
</template>

<style scoped>
.tf-qr { width: 200px; height: 200px; max-width: 100%; border-radius: var(--t-radius); }
.tf-codes { display: grid; grid-template-columns: repeat(auto-fill, minmax(170px, 1fr)); gap: 6px; }
.tf-code {
  display: block;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  overflow-wrap: anywhere;
  padding: 6px 8px;
  border: 1px solid var(--t-border);
  border-radius: var(--t-radius);
}
</style>
