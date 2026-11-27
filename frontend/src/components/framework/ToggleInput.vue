<script setup lang="ts">
// ariaLabelledby：根節點是外層 div，名稱要接到開關本身
defineProps<{ modelValue: boolean | null | undefined; label: string; error?: string; readonly?: boolean; required?: boolean; ariaLabelledby?: string }>();
defineEmits<{ 'update:modelValue': [value: boolean] }>();
</script>

<template>
  <div>
    <q-toggle
      :model-value="!!modelValue" :label="required ? `${label} *` : label" :disable="readonly" :aria-labelledby="ariaLabelledby"
      @update:model-value="(v) => $emit('update:modelValue', v)"
    />
    <div v-if="error" class="text-negative text-caption">{{ error }}</div>
  </div>
</template>
