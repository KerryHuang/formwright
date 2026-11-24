import { describe, expect, it } from 'vitest';
import type { ApprovalFlow } from './approval-flow-types';
import { nextPriority } from './approval-flow-draft';

const flow = (key: string, to: number, priority: number): ApprovalFlow => ({
  key, name: { 'zh-TW': key }, toOptionId: to, priority, active: true, moduleKey: 'm', version: 1, used: false, pendingCount: 0,
});

describe('approval flow draft', () => {
  it('nextPriority is max+1 in the target, 1 when empty, ignores the flow itself', () => {
    const flows = [flow('a', 12, 1), flow('b', 12, 4), flow('c', 13, 9)];
    expect(nextPriority(flows, 12)).toBe(5);
    expect(nextPriority(flows, 14)).toBe(1);
    expect(nextPriority(flows, 12, 'b')).toBe(2);
    expect(nextPriority([flow('a', 12, 1)], 12, 'a')).toBe(1);
  });
});
