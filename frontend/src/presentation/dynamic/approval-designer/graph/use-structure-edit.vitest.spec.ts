import { describe, expect, it } from 'vitest';
import type { ApprovalFlow, GraphDefinition } from 'src/domain/dynamic/approval-flow-types';
import type { AddLimits } from './designer-graph';
import { useGraphDesigner } from './use-graph-designer';
import { useStructureEdit } from './use-structure-edit';

const def = (): GraphDefinition => ({
  shape: 'graph', stages: [],
  nodes: [{ id: 's', kind: 'start' }, { id: 'a', kind: 'approval', name: { 'zh-TW': '財務' }, approvers: { source: 'manager', level: 1 }, mode: 'any' }, { id: 'e', kind: 'end' }],
  edges: [{ id: 'e1', from: 's', to: 'a' }, { id: 'e2', from: 'a', to: 'e' }],
});
const flow = (p: Partial<ApprovalFlow> = {}): ApprovalFlow => ({
  key: 'po', name: { 'zh-TW': '採購' }, toOptionId: 3, priority: 2, active: true, used: false, pendingCount: 0, moduleKey: 'm', version: 1, versionId: 11, definition: def(), graph: def(), ...p,
});

function setup(o: { canEdit?: boolean; structure?: boolean; limits?: AddLimits } = {}) {
  const d = useGraphDesigner(() => o.canEdit ?? true);
  d.load(flow());
  const s = useStructureEdit(d, { structure: () => o.structure ?? true, limits: () => o.limits ?? { ready: true, maxStages: 10, maxBranches: 5 } });
  const g = () => d.state.value!.definition;
  return { d, s, g };
}

describe('useStructureEdit', () => {
  it('a palette drop on blank canvas adds the node there and selects it; one undo step', () => {
    const { d, s, g } = setup();
    s.place('condition', { x: 400, y: 320 }, null);
    const c = g().nodes.find((n) => n.kind === 'condition')!;
    expect(d.state.value!.positions[c.id]).toEqual({ x: 400, y: 320 });
    expect(d.selection.value).toEqual({ kind: 'node', id: c.id });
    d.undo();
    expect(g().nodes).toHaveLength(3);
  });

  it('a drop on a line inserts (U49); an end node dropped on a line is placed, not inserted', () => {
    const { s, g } = setup();
    s.place('approval', { x: 0, y: 0 }, 'e2');
    const n = g().nodes.find((x) => x.id === 'approval-1')!;
    expect(n.name?.['zh-TW']).toBeDefined();
    expect(g().edges.find((e) => e.id === 'e2')!.to).toBe('approval-1');
    s.place('end', { x: 8, y: 8 }, 'e1');
    expect(g().edges.find((e) => e.id === 'e1')!.to).toBe('a');
    expect(g().nodes.filter((x) => x.kind === 'end')).toHaveLength(2);
  });

  it('the plus inserts a fork with its join and two branches and selects the fork', () => {
    const { d, s, g } = setup();
    s.insert('e2', 'fork');
    expect(g().nodes.map((n) => n.kind).sort()).toEqual(['approval', 'approval', 'approval', 'end', 'fork', 'join', 'start']);
    expect(d.selection.value).toEqual({ kind: 'node', id: 'fork-1' });
  });

  it('policy limits disable the kinds (J81); before the values are back only end and notify (no approval node added) are allowed', () => {
    const { s, g } = setup({ limits: { ready: true, maxStages: 2 } });
    expect(s.paletteItems.value).toEqual([
      { kind: 'approval', disabled: false, limit: false }, { kind: 'condition', disabled: false, limit: false }, { kind: 'notify', disabled: false, limit: false }, { kind: 'fork', disabled: true, limit: true }, { kind: 'end', disabled: false, limit: false },
    ]);
    s.insert('e2', 'fork');
    expect(g().nodes).toHaveLength(3);
    const early = setup({ limits: { ready: false } });
    expect(early.s.insertItems.value.filter((i) => i.kind !== 'notify').every((i) => i.disabled && !i.limit)).toBe(true); // 停用但不說已達上限（通知不帶簽核節點，不受上限）
    expect(early.s.insertItems.value.find((i) => i.kind === 'notify')!.disabled).toBe(false);
    expect(early.s.paletteItems.value.find((i) => i.kind === 'end')!.disabled).toBe(false);
  });

  it('connect follows canConnect (J68); the first line out of a fresh condition becomes its default line (#35, unchanged)', () => {
    const { s, g } = setup();
    expect(s.connectable('a', 's')).toBe(false); // 連進開始
    expect(s.connectable('a', 'e')).toBe(false); // 重複
    s.place('condition', { x: 0, y: 0 }, null);
    expect(s.connectable('cond-1', 'e')).toBe(true);
    s.link('cond-1', 'e');
    expect(g().edges.find((e) => e.from === 'cond-1')).toMatchObject({ to: 'e', default: true });
    s.link('e', 'a'); // 不合法：不動
    expect(g().edges.filter((e) => e.from === 'e')).toHaveLength(0);
  });

  it('delete: the start node cannot be deleted; an approval node bridges its neighbours (U50); a line is removed alone', () => {
    const { d, s, g } = setup();
    d.select({ kind: 'node', id: 's' });
    expect(s.removable.value).toBe(false);
    d.select({ kind: 'node', id: 'a' });
    s.remove();
    expect(g().nodes.map((n) => n.id)).toEqual(['s', 'e']);
    expect(g().edges).toEqual([{ id: 'e1', from: 's', to: 'e' }]);
    expect(d.selection.value).toEqual({ kind: 'flow' });
    d.select({ kind: 'edge', id: 'e1' });
    s.remove();
    expect(g().edges).toEqual([]);
  });

  it('deleting a fork asks first: with its join removes the block, fork only keeps the rest, cancel does nothing', () => {
    const { d, s, g } = setup();
    s.insert('e2', 'fork');
    d.select({ kind: 'node', id: 'fork-1' });
    s.remove();
    expect(s.askFork.value).toBe('fork-1');
    s.answerFork(null);
    expect(g().nodes).toHaveLength(7);
    s.remove();
    s.answerFork(true);
    expect(g().nodes.map((n) => n.id)).toEqual(['s', 'a', 'e']);
    d.undo();
    d.select({ kind: 'node', id: 'fork-1' });
    s.remove();
    s.answerFork(false);
    expect(g().nodes.some((n) => n.id === 'join-1')).toBe(true);
    expect(g().nodes.some((n) => n.id === 'fork-1')).toBe(false);
  });

  it('tidy lays everything out again as one undo step', () => {
    const { d, s } = setup();
    d.move('a', { x: 999, y: 999 });
    s.tidy();
    expect(d.state.value!.positions.a).not.toEqual({ x: 999, y: 999 });
    d.undo();
    expect(d.state.value!.positions.a).toEqual({ x: 999, y: 999 });
  });

  it('phone or read-only: nothing changes the structure', () => {
    for (const o of [{ structure: false }, { canEdit: false }]) {
      const { d, s, g } = setup(o);
      const before = JSON.stringify(d.state.value);
      s.place('approval', { x: 0, y: 0 }, null);
      s.insert('e2', 'condition');
      s.link('a', 's');
      s.tidy();
      d.select({ kind: 'node', id: 'a' });
      expect(s.removable.value).toBe(false);
      s.remove();
      expect(JSON.stringify(d.state.value)).toBe(before);
      expect(g()).toBeTruthy();
    }
  });
});
