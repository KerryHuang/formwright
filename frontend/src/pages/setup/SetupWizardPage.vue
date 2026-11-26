<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue';
import { useI18n } from 'vue-i18n';
import BrandMark from 'src/presentation/brand/BrandMark.vue';
import LocaleSwitcher from 'src/presentation/locale/LocaleSwitcher.vue';
import SetupChecksList from 'src/presentation/setup/SetupChecksList.vue';
import { setupApi, SetupValidationError, type SetupCheckItem, type SetupCompleteBody, type SetupDraft, type SetupFieldError } from 'src/services/api/setup-api';

const POLL_MS = 2000;
const TIMEOUT_MS = 120_000;

const { t, te } = useI18n({ useScope: 'global' });

type Step = 'code' | 'checks' | 'database' | 'admin' | 'company' | 'backup' | 'done';
const step = ref<Step>('code');
// 設定碼只放元件狀態，不寫任何儲存空間
const code = ref('');
const codeInvalid = ref(false);
const unreachable = ref(false);
const busy = ref(false);

const checks = ref<{ items: SetupCheckItem[]; ok: boolean } | null>(null);
const dbChecks = ref<SetupCheckItem[] | null>(null);
const backupChecks = ref<SetupCheckItem[] | null>(null);

const provider = ref<'sqlite' | 'postgres'>('sqlite');
const connectionString = ref('');
const showConnection = ref(false);
const advancedOpen = ref(false);
const adminPassword = ref('');
const adminConfirm = ref('');
const mismatch = ref(false);
const siteName = ref('');
const backupRoot = ref('');
const fieldErrors = ref<Record<string, SetupFieldError[]>>({});

const restarting = ref(false);
const timedOut = ref(false);
let timer: ReturnType<typeof setTimeout> | undefined;
onBeforeUnmount(() => clearTimeout(timer));

const stepOrder: Step[] = ['code', 'checks', 'database', 'admin', 'company', 'backup', 'done'];
const doneSteps = (s: Step) => stepOrder.indexOf(step.value) > stepOrder.indexOf(s);

const draft = (withBackup: boolean): SetupDraft => ({
  provider: provider.value,
  ...(provider.value === 'postgres' ? { connectionString: connectionString.value } : {}),
  ...(withBackup ? { backupRoot: backupRoot.value } : {}),
});

async function guarded(work: () => Promise<void>) {
  busy.value = true;
  unreachable.value = false;
  try {
    await work();
  } catch (e) {
    const err = e as { status?: number; code?: string } | null;
    if (err?.status === 403 && err.code === 'setupCodeInvalid') {
      // 設定碼失效（例如另一個分頁已完成精靈）：任何請求都回到第一步
      codeInvalid.value = true;
      step.value = 'code';
    } else {
      unreachable.value = true;
    }
  } finally {
    busy.value = false;
  }
}

async function loadChecks() {
  const r = await setupApi.runChecks(code.value);
  checks.value = r;
  // 預設備份位置取後端回的值，前端不寫路徑（TD-02）
  if (!backupRoot.value) backupRoot.value = r.items.find((i) => i.key === 'backupDirectory')?.path ?? '';
}

const submitCode = () => guarded(async () => {
  if (!(await setupApi.verify(code.value))) {
    codeInvalid.value = true;
    return;
  }
  codeInvalid.value = false;
  await loadChecks();
  step.value = 'checks';
});

const recheck = () => guarded(loadChecks);

const nextFromDatabase = () => guarded(async () => {
  if (provider.value === 'sqlite') { step.value = 'admin'; return; }
  const r = await setupApi.runChecks(code.value, draft(false));
  dbChecks.value = r.items;
  if (r.ok) step.value = 'admin';
});

const testDatabase = () => guarded(async () => {
  dbChecks.value = (await setupApi.runChecks(code.value, draft(false))).items;
});

function nextFromAdmin() {
  mismatch.value = adminPassword.value !== adminConfirm.value;
  if (mismatch.value) return;
  delete fieldErrors.value.adminPassword;
  step.value = 'company';
}

function nextFromCompany() {
  delete fieldErrors.value.siteName;
  step.value = 'backup';
}

const nextFromBackup = () => guarded(async () => {
  const r = await setupApi.runChecks(code.value, draft(true));
  backupChecks.value = r.items;
  if (r.ok) step.value = 'done';
});

/** 依紅燈項目退回所屬步驟：重新檢查要帶那一步填的值，不能一律退回環境檢查（那裡的重新檢查不帶草稿）。 */
function backToFailedStep(items: SetupCheckItem[]) {
  const postgres = provider.value === 'postgres';
  const stepOf = (i: SetupCheckItem): Step =>
    i.key === 'backupDirectory' ? 'backup' : postgres && ['database', 'pgDump', 'existingSqliteData'].includes(i.key) ? 'database' : 'checks';
  const reds = items.filter((i) => !i.ok).map(stepOf);
  const target = stepOrder.find((s) => reds.includes(s)) ?? 'checks';
  if (target === 'checks') checks.value = { items, ok: false };
  else if (target === 'database') dbChecks.value = items;
  else backupChecks.value = items;
  step.value = target;
}

const FIELD_STEP: Record<string, Step> = { provider: 'database', connectionString: 'database', adminPassword: 'admin', siteName: 'company', backupRoot: 'backup' };

function poll(ticks = 0) {
  timer = setTimeout(async () => {
    try {
      if ((await setupApi.getStatus()) === 'notInSetup' && (await setupApi.isReady())) {
        // 整頁重新載入：一般模式從頭開機，auth/options 與語系設定才會重取（啟動狀態不會殘留設定模式的失敗結果）
        window.location.replace('/login');
        return;
      }
    } catch {
      // 服務重啟中、連不上：繼續等
    }
    if ((ticks + 1) * POLL_MS >= TIMEOUT_MS) {
      timedOut.value = true;
      return;
    }
    poll(ticks + 1);
  }, POLL_MS);
}

const complete = () => guarded(async () => {
  const body: SetupCompleteBody = { ...draft(true), adminPassword: adminPassword.value, siteName: siteName.value };
  try {
    await setupApi.complete(code.value, body);
  } catch (e) {
    if (e instanceof SetupValidationError) {
      if (e.checks) {
        backToFailedStep(e.checks);
        return;
      }
      fieldErrors.value = e.errors ?? {};
      const first = stepOrder.find((s) => Object.keys(fieldErrors.value).some((f) => FIELD_STEP[f] === s));
      if (first) step.value = first;
      return;
    }
    throw e;
  }
  restarting.value = true;
  poll();
});

const errorText = (e: SetupFieldError) => (te(`setup.error.${e.code}`) ? t(`setup.error.${e.code}`, e.params ?? {}) : e.code);
const errorsOf = (...fields: string[]) => fields.flatMap((f) => fieldErrors.value[f] ?? []);

const reload = () => window.location.reload();
const checksOk = computed(() => checks.value?.ok === true);
</script>

<template>
  <div class="row justify-center q-pa-md">
    <q-card class="setup-card">
      <q-card-section class="row items-center no-wrap">
        <BrandMark class="q-mr-sm" style="font-size: 28px" />
        <div class="text-h6 col">{{ t('setup.title') }}</div>
        <LocaleSwitcher />
      </q-card-section>

      <q-stepper v-model="step" flat vertical animated>
        <q-step name="code" :title="t('setup.code.label')" icon="vpn_key" :done="doneSteps('code')">
          <div data-testid="setup-code" class="q-gutter-sm">
            <div class="text-caption">{{ t('setup.code.hint') }}</div>
            <q-input
              v-model="code" :label="t('setup.code.label')" autocomplete="off" autocapitalize="characters" spellcheck="false" dense outlined
              data-testid="setup-code-input" @keyup.enter="submitCode"
            />
            <div v-if="codeInvalid" class="text-negative" data-testid="setup-code-invalid">{{ t('setup.code.invalid') }}</div>
          </div>
          <q-stepper-navigation>
            <q-btn color="primary" :label="t('setup.code.submit')" :loading="busy" data-testid="setup-code-submit" @click="submitCode" />
          </q-stepper-navigation>
        </q-step>

        <q-step name="checks" :title="t('setup.step.checks')" icon="fact_check" :done="doneSteps('checks')">
          <div v-if="checks" data-testid="setup-step-checks">
            <SetupChecksList :items="checks.items" />
          </div>
          <q-stepper-navigation class="row q-gutter-sm no-wrap">
            <q-btn color="primary" :label="t('setup.next')" :disable="!checksOk" data-testid="setup-next" @click="step = 'database'" />
            <q-btn flat :label="t('setup.recheck')" :loading="busy" data-testid="setup-recheck" @click="recheck" />
          </q-stepper-navigation>
        </q-step>

        <q-step name="database" :title="t('setup.step.database')" icon="storage" :done="doneSteps('database')">
          <div data-testid="setup-step-database" class="q-gutter-sm">
            <q-radio v-model="provider" val="sqlite" :label="t('setup.database.sqlite')" data-testid="setup-provider-sqlite" />
            <div class="text-caption">{{ t('setup.database.sqliteHint') }}</div>
            <q-expansion-item v-model="advancedOpen" :label="t('setup.database.advanced')" dense>
              <template #header>
                <q-item-section data-testid="setup-advanced">{{ t('setup.database.advanced') }}</q-item-section>
              </template>
              <div v-if="advancedOpen || provider === 'postgres'" class="q-pa-sm q-gutter-sm">
                <q-radio v-model="provider" val="postgres" :label="t('setup.database.postgres')" data-testid="setup-provider-postgres" />
                <template v-if="provider === 'postgres'">
                  <q-input
                    v-model="connectionString" :label="t('setup.database.connectionString')" :type="showConnection ? 'text' : 'password'"
                    autocomplete="off" dense outlined data-testid="setup-connection"
                  >
                    <template #append>
                      <q-btn
                        flat round dense type="button" :icon="showConnection ? 'visibility_off' : 'visibility'"
                        :aria-label="showConnection ? t('setup.database.hide') : t('setup.database.show')" data-testid="setup-connection-toggle"
                        @click="showConnection = !showConnection"
                      />
                    </template>
                  </q-input>
                  <q-btn flat :label="t('setup.database.test')" :loading="busy" data-testid="setup-db-test" @click="testDatabase" />
                </template>
              </div>
            </q-expansion-item>
            <SetupChecksList v-if="dbChecks && provider === 'postgres'" :items="dbChecks" />
            <div v-for="e in errorsOf('provider', 'connectionString')" :key="e.code" class="text-negative" data-testid="setup-field-error">{{ errorText(e) }}</div>
          </div>
          <q-stepper-navigation>
            <q-btn
              color="primary" :label="t('setup.next')" :loading="busy" :disable="provider === 'postgres' && !connectionString"
              data-testid="setup-next" @click="nextFromDatabase"
            />
          </q-stepper-navigation>
        </q-step>

        <q-step name="admin" :title="t('setup.step.admin')" icon="admin_panel_settings" :done="doneSteps('admin')">
          <div data-testid="setup-step-admin" class="q-gutter-sm">
            <q-input
              v-model="adminPassword" :label="t('setup.admin.password')" type="password" autocomplete="new-password" dense outlined
              data-testid="setup-password"
            />
            <q-input
              v-model="adminConfirm" :label="t('setup.admin.confirm')" type="password" autocomplete="new-password" dense outlined
              data-testid="setup-password-confirm"
            />
            <div v-if="mismatch" class="text-negative" data-testid="setup-admin-mismatch">{{ t('setup.admin.mismatch') }}</div>
            <div v-for="e in errorsOf('adminPassword')" :key="e.code" class="text-negative" data-testid="setup-field-error">{{ errorText(e) }}</div>
          </div>
          <q-stepper-navigation class="row q-gutter-sm no-wrap">
            <q-btn color="primary" :label="t('setup.next')" :disable="!adminPassword" data-testid="setup-next" @click="nextFromAdmin" />
            <q-btn flat :label="t('setup.back')" data-testid="setup-back" @click="step = 'database'" />
          </q-stepper-navigation>
        </q-step>

        <q-step name="company" :title="t('setup.step.company')" icon="business" :done="doneSteps('company')">
          <div data-testid="setup-step-company" class="q-gutter-sm">
            <q-input v-model="siteName" :label="t('setup.company.label')" :hint="t('setup.company.hint')" dense outlined data-testid="setup-site-name" />
            <div v-for="e in errorsOf('siteName')" :key="e.code" class="text-negative" data-testid="setup-field-error">{{ errorText(e) }}</div>
          </div>
          <q-stepper-navigation class="row q-gutter-sm no-wrap">
            <q-btn color="primary" :label="t('setup.next')" :disable="!siteName.trim()" data-testid="setup-next" @click="nextFromCompany" />
            <q-btn flat :label="t('setup.back')" data-testid="setup-back" @click="step = 'admin'" />
          </q-stepper-navigation>
        </q-step>

        <q-step name="backup" :title="t('setup.step.backup')" icon="backup" :done="doneSteps('backup')">
          <div data-testid="setup-step-backup" class="q-gutter-sm">
            <q-input v-model="backupRoot" :label="t('setup.backup.label')" :hint="t('setup.backup.hint')" dense outlined data-testid="setup-backup-root" />
            <SetupChecksList v-if="backupChecks && !backupChecks.every((i) => i.ok)" :items="backupChecks" />
          </div>
          <q-stepper-navigation class="row q-gutter-sm no-wrap">
            <q-btn color="primary" :label="t('setup.next')" :loading="busy" :disable="!backupRoot" data-testid="setup-next" @click="nextFromBackup" />
            <q-btn flat :label="t('setup.back')" data-testid="setup-back" @click="step = 'company'" />
          </q-stepper-navigation>
        </q-step>

        <q-step name="done" :title="t('setup.step.done')" icon="check_circle">
          <div data-testid="setup-step-done" class="q-gutter-sm">
            <div>{{ t('setup.done.twoFactor') }}</div>
            <div v-if="restarting && !timedOut" class="row items-center no-wrap" data-testid="setup-restarting">
              <q-spinner class="q-mr-sm" />
              <span>{{ t('setup.done.restarting') }}</span>
            </div>
            <div v-if="timedOut" class="text-negative" data-testid="setup-timeout">{{ t('setup.done.timeout') }}</div>
          </div>
          <q-stepper-navigation class="row q-gutter-sm no-wrap">
            <q-btn v-if="!restarting" color="primary" :label="t('setup.done.submit')" :loading="busy" data-testid="setup-complete" @click="complete" />
            <q-btn v-if="timedOut" color="primary" :label="t('setup.done.reload')" data-testid="setup-reload" @click="reload" />
            <q-btn v-if="!restarting" flat :label="t('setup.back')" @click="step = 'backup'" />
          </q-stepper-navigation>
        </q-step>
      </q-stepper>

      <q-card-section v-if="unreachable" class="text-negative" data-testid="setup-error">{{ t('setup.error.unreachable') }}</q-card-section>
    </q-card>
  </div>
</template>

<style scoped>
.setup-card {
  max-width: 720px;
  width: 100%;
}
</style>
