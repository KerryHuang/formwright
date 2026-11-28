<script setup lang="ts">
import { useField } from 'vee-validate';
import { computed } from 'vue';
import { useI18n } from 'vue-i18n';
import type { FieldType, ReferenceInfo, SchemaField } from 'src/domain/dynamic/types';
import { fieldTypeRegistry } from 'src/presentation/dynamic/field-type-registry';

// readonly / required / allowedOptions come from the rule state; without them the field's own config applies.
const props = defineProps<{ field: SchemaField; readonly?: boolean; required?: boolean; allowedOptions?: Set<string>; reference?: ReferenceInfo }>();
const { locale, t } = useI18n();
/** Types nobody types into: the hint says where the value comes from (detail grid cells go without it). */
const COMPUTED_HINTS = new Set<FieldType>(['autonumber', 'formula', 'rollup']);
const { value, errorMessage, handleChange } = useField<unknown>(() => props.field.key);

const bound = computed(() => {
  const base = fieldTypeRegistry[props.field.type].props(props.field, locale.value);
  const options = base.options as { value: string }[] | undefined;
  return {
    ...base,
    ...(props.readonly !== undefined && { readonly: props.readonly }),
    ...(props.required !== undefined && { required: props.required }),
    ...(props.field.type === 'reference' && { reference: props.reference }),
    ...(COMPUTED_HINTS.has(props.field.type) && { hint: t(`dynamic.computedHint.${props.field.type}`) }),
    ...(options && props.allowedOptions && { options: options.filter((o) => props.allowedOptions!.has(o.value)) }),
  };
});
</script>

<template>
  <component
    :is="fieldTypeRegistry[field.type].component"
    v-bind="bound"
    :model-value="value"
    :error="errorMessage"
    @update:model-value="handleChange"
  />
</template>
