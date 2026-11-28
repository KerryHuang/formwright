import type { ModuleSchema } from 'src/domain/dynamic/types';
import { fieldTypeRegistry } from 'src/presentation/dynamic/field-type-registry';

/** 狀態章的內容：只有模組狀態流程的狀態欄有；沒有值就沒有章。顏色取自該值的選項設定。 */
export function statusChipOf(schema: ModuleSchema | undefined, values: Record<string, unknown>, locale: string) {
  const key = schema?.workflow?.statusFieldKey;
  const field = key ? schema!.blocks.flatMap((b) => b.fields).find((f) => f.key === key) : undefined;
  const value = field && values[field.key];
  if (!field || value === undefined || value === null || value === '') return undefined;
  return {
    label: fieldTypeRegistry[field.type].format(value, field, locale),
    color: field.options.find((o) => o.value === value)?.color,
  };
}
