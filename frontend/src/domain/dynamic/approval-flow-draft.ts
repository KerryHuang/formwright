import type { ApprovalFlow, ApproverSource } from './approval-flow-types';

/** One person at most can ever come out of this source, so the stage mode does not matter (saved as `any`). */
export function isSingleApprover(s: ApproverSource): boolean {
  switch (s.source) {
    case 'manager':
    case 'userField': return true;
    case 'users': return (s.userIds?.length ?? 0) <= 1;
    case 'initiatorPick': return s.max === 1;
    default: return false;
  }
}

/** Last in the target state (max + 1), 1 when it has none; `exceptKey` leaves out the flow being edited. */
export function nextPriority(flows: ApprovalFlow[], toOptionId: number, exceptKey?: string): number {
  const mine = flows.filter((f) => f.toOptionId === toOptionId && f.key !== exceptKey);
  return mine.length ? Math.max(...mine.map((f) => f.priority)) + 1 : 1;
}
