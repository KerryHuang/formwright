import { describe, expect, it } from 'vitest';
import { countConditions, operatorNeedsValue, pruneCondition, serializeFilter } from './condition';
import type { ConditionNode } from './types';

describe('condition', () => {
  it('operatorNeedsValue maps every operator to its value shape', () => {
    expect(operatorNeedsValue('isEmpty')).toBe('none');
    expect(operatorNeedsValue('isNotEmpty')).toBe('none');
    expect(operatorNeedsValue('beforeToday')).toBe('none');
    expect(operatorNeedsValue('between')).toBe('pair');
    expect(operatorNeedsValue('in')).toBe('list');
    expect(operatorNeedsValue('notIn')).toBe('list');
    expect(operatorNeedsValue('withinNextDays')).toBe('days');
    expect(operatorNeedsValue('withinLastDays')).toBe('days');
    for (const op of ['eq', 'ne', 'gt', 'gte', 'lt', 'lte', 'contains', 'startsWith'] as const) {
      expect(operatorNeedsValue(op)).toBe('single');
    }
  });

  it('pruneCondition removes incomplete rows and empty groups', () => {
    const tree: ConditionNode = {
      all: [
        { field: 3, op: 'eq', value: 'x' },
        { field: 0, op: 'eq', value: 'x' },
        { field: 4, op: 'eq' },
        { field: 4, op: 'eq', value: '' },
        { field: 5, op: 'in', value: [] },
        { field: 5, op: 'between', value: [1, null] },
        { field: 5, op: 'between', value: [1, 2] },
        { field: 6, op: 'isEmpty' },
        { field: 7, op: 'withinNextDays', value: 0 },
        { field: 7, op: 'withinLastDays' },
        { any: [{ field: 0, op: 'eq', value: 1 }, { field: 8, op: 'eq' }] },
        { any: [{ field: 9, op: 'ne', value: false }] },
      ],
    };
    expect(pruneCondition(tree)).toEqual({
      all: [
        { field: 3, op: 'eq', value: 'x' },
        { field: 5, op: 'between', value: [1, 2] },
        { field: 6, op: 'isEmpty' },
        { field: 7, op: 'withinNextDays', value: 0 },
        { any: [{ field: 9, op: 'ne', value: false }] },
      ],
    });
    expect(pruneCondition({ all: [{ field: 0, op: 'eq', value: 1 }] })).toBeUndefined();
    expect(pruneCondition({ all: [{ field: 7, op: 'beforeToday' }] })).toEqual({ all: [{ field: 7, op: 'beforeToday' }] });
    expect(pruneCondition(undefined)).toBeUndefined();
  });

  it('serializeFilter returns undefined for empty', () => {
    expect(serializeFilter(undefined)).toBeUndefined();
    expect(serializeFilter({ all: [{ field: 0, op: 'eq', value: 1 }] })).toBeUndefined();
    expect(serializeFilter({ all: [{ field: 3, op: 'eq', value: 'a' }, { field: 0, op: 'eq' }] }))
      .toBe('{"all":[{"field":3,"op":"eq","value":"a"}]}');
  });
});

describe('countConditions', () => {
  it('只數填好的條件，巢狀群組算裡面的葉節點', () => {
    expect(countConditions(undefined)).toBe(0);
    expect(countConditions({ all: [{ field: 1, op: 'eq', value: '' }] })).toBe(0);
    expect(countConditions({ field: 1, op: 'isEmpty' })).toBe(1);
    expect(countConditions({ all: [{ field: 1, op: 'eq', value: 'x' }, { any: [{ field: 2, op: 'eq', value: 'y' }, { field: 3, op: 'eq', value: 'z' }] }, { field: 4, op: 'eq' }] })).toBe(3);
  });
});
