import { describe, expect, it } from 'vitest';
import type { FieldType, SchemaField } from 'src/domain/dynamic/types';
import { isNumericField } from './numeric-field';

const field = (type: FieldType, resultType?: 'number' | 'currency' | 'date'): SchemaField => ({
  id: 1, key: 'f', type, label: {}, options: [],
  config: { required: false, readOnly: false, showInList: true, isFilterable: true, isSortable: true, ...(resultType && { resultType }) },
});

describe('isNumericField', () => {
  it.each(['integer', 'decimal', 'currency', 'percent'] as const)('%s 是數字欄', (type) => {
    expect(isNumericField(field(type))).toBe(true);
  });

  it.each(['formula', 'rollup'] as const)('%s 依 resultType：number、currency 算數字欄，date 與沒設不算', (type) => {
    expect(isNumericField(field(type, 'number'))).toBe(true);
    expect(isNumericField(field(type, 'currency'))).toBe(true);
    expect(isNumericField(field(type, 'date'))).toBe(false);
    expect(isNumericField(field(type))).toBe(false);
  });

  it.each(['text', 'date', 'datetime', 'time', 'enum', 'autonumber', 'reference', 'boolean', 'phone'] as const)('%s 不是數字欄', (type) => {
    expect(isNumericField(field(type))).toBe(false);
  });
});
