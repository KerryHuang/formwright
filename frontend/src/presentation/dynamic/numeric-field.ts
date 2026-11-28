import type { FieldType, SchemaField } from 'src/domain/dynamic/types';

const NUMERIC = new Set<FieldType>(['integer', 'decimal', 'currency', 'percent']);

/** 數字欄（表格靠右、等寬數字）：數值型別，以及結果是數字或金額的公式與加總。 */
export function isNumericField(f: SchemaField): boolean {
  if (f.type === 'formula' || f.type === 'rollup') return f.config.resultType === 'number' || f.config.resultType === 'currency';
  return NUMERIC.has(f.type);
}
