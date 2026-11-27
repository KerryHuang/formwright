<script setup lang="ts">
import { computed, toRef } from 'vue';
import { useI18n } from 'vue-i18n';
import { useRecordConversions } from 'src/application/dynamic/queries/conversions-queries';
import { resolveI18n } from 'src/domain/dynamic/i18n-text';
import type { RecordConversion } from 'src/domain/dynamic/types';

// The convert buttons live in the page's fixed header bar (they open the dialog from there); this block keeps what does not fit a bar:
// why a conversion is unavailable, and the documents already converted out.
const props = defineProps<{ recordId: number; closed?: boolean }>();
const { t, locale } = useI18n();
const query = useRecordConversions(toRef(props, 'recordId'));
// A closed app is already announced by the page banner; repeating it per conversion only adds noise.
const shown = computed(() => (query.data.value ?? []).filter((c) => (!c.allowed && !props.closed) || c.targets.length));
const nameOf = (c: RecordConversion) => resolveI18n(c.name, locale.value);
const targetNameOf = (c: RecordConversion) => resolveI18n(c.targetName, locale.value);
</script>

<template>
  <div v-if="shown.length" class="q-mb-md q-gutter-y-sm" data-testid="record-conversions">
    <div v-for="c in shown" :key="c.key" :data-testid="`conversion-${c.key}`">
      <div v-if="!c.allowed && !closed" class="text-muted" :data-testid="`convert-reason-${c.key}`">{{ nameOf(c) }}：{{ c.reason }}</div>
      <div v-if="c.targets.length" class="q-mt-xs" :data-testid="`convert-targets-${c.key}`">
        <span class="text-caption text-muted">{{ t('docflow.convert.targets', { name: targetNameOf(c) }) }}：</span>
        <template v-for="(target, i) in c.targets" :key="target.id">
          <span v-if="i">、</span>
          <router-link :to="`/dynamic/${target.moduleKey}/${target.id}`">{{ target.title }}</router-link>
        </template>
      </div>
    </div>
  </div>
</template>
