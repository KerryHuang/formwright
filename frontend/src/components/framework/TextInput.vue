<script setup lang="ts">
defineProps<{ modelValue: string | null | undefined; label: string; error?: string; readonly?: boolean; required?: boolean; hint?: string }>();
defineEmits<{ 'update:modelValue': [value: string | null] }>();
</script>

<template>
  <q-input
    :model-value="modelValue ?? ''" :label="required ? `${label} *` : label" :error="!!error" :error-message="error"
    :readonly="readonly" :hint="hint" outlined dense
    @update:model-value="(v) => $emit('update:modelValue', v === '' || v == null ? null : String(v))"
  />
</template>
