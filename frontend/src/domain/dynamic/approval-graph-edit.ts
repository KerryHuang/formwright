import type { ApprovalEdgeDef, ApprovalNodeDef, ApprovalNodeKind, GraphDefinition } from './approval-flow-types';
import { COL_GAP, LABELED_COL_GAP, NODE_H, NODE_W, layoutLeftToRight } from './approval-graph-layout';
import type { I18nText } from './types';

// 流程圖設計器的編輯純函式（J71、J72）：每個函式吃 GraphState、回新的 GraphState（沒有改動時回原物件）。
// 不碰 UI、不碰 i18n 檔：預設名稱由呼叫端以 EditOptions.names 傳入。

export interface Position { x: number; y: number }
export interface GraphState {
  definition: GraphDefinition;
  positions: Record<string, Position>;
}

export interface EditOptions {
  /** 已發布版本用過的節點代號：不重用，免得新節點被後端（G13 以代號對應）當成同一個舊節點。 */
  reserved?: string[];
  names?: {
    /** 新簽核節點的預設名稱（U51「簽核 n」），n 是代號的流水號。 */
    approval?: (n: number) => I18nText;
    /** 插在線上的條件節點，條件線的標籤（U49「條件 n」）。 */
    conditionLabel?: (n: number) => I18nText;
  };
  /** 新通知節點的預設（U-b）：名稱「通知 n」與標題 `{title}`（各語系同一段文字）；收件人固定一項「申請人」、內文空白。 */
  notifyDefaults?: (n: number) => { name: I18nText; title: I18nText };
}

export type ConnectRefusal = 'missing' | 'selfLoop' | 'intoStart' | 'outOfEnd' | 'duplicate' | 'outLimit';

const CODE = /^[a-z][a-z0-9-]{0,39}$/;
const PREFIX: Record<ApprovalNodeKind, string> = { start: 'start', approval: 'approval', condition: 'cond', fork: 'fork', join: 'join', end: 'end', notify: 'notify' };
/** 只能有一條出線的型別（§6.2）。 */
const SINGLE_OUT = new Set<ApprovalNodeKind>(['start', 'approval', 'notify', 'join']);
/** 從畫面收到的值先深複製再放進狀態：它可能是 Vue 的 reactive 代理，也可能是編輯器之後還會原地修改的物件（復原堆疊不能共用）。 */
export const deepCopy = <T>(v: T): T => (v === undefined ? v : (JSON.parse(JSON.stringify(v)) as T));
const clone = (s: GraphState): GraphState => structuredClone(s);

function allocator(def: GraphDefinition, reserved: string[] = []) {
  // 匯合的 forkId 也算已用：分支單獨刪掉後留下的匯合仍指著舊代號，新分支用同一個代號就會被它配走
  const nodes = new Set([...def.nodes.map((n) => n.id), ...def.nodes.flatMap((n) => (n.forkId ? [n.forkId] : [])), ...reserved]);
  const edges = new Set(def.edges.map((e) => e.id));
  const next = (taken: Set<string>, prefix: string) => {
    let n = 1;
    while (taken.has(`${prefix}-${n}`)) n++;
    taken.add(`${prefix}-${n}`);
    return { id: `${prefix}-${n}`, n };
  };
  return { node: (kind: ApprovalNodeKind) => next(nodes, PREFIX[kind]), edge: () => next(edges, 'e').id };
}

/** 新節點的代號：依型別 `approval-n`、`cond-n`…，取最小未用的 n。 */
export const nextCode = (kind: ApprovalNodeKind, def: GraphDefinition, reserved: string[] = []): string => allocator(def, reserved).node(kind).id;

type Alloc = ReturnType<typeof allocator>;

function newApproval(a: Alloc, opts: EditOptions): ApprovalNodeDef {
  const { id, n } = a.node('approval');
  return {
    id, kind: 'approval', name: opts.names?.approval?.(n) ?? {}, approvers: { source: 'manager', level: 1 },
    mode: 'any', editableFields: [], onEmpty: 'fallback',
  };
}

function newNotify(a: Alloc, opts: EditOptions): ApprovalNodeDef {
  const { id, n } = a.node('notify');
  const d = opts.notifyDefaults?.(n);
  return { id, kind: 'notify', name: d?.name ?? {}, recipients: [{ kind: 'initiator' }], template: { title: d?.title ?? {}, body: {} } };
}

/** 分支、配對匯合、兩條各含一個新簽核節點的分支（U49、U63）；回傳新節點、新線與兩端。 */
function forkBlock(a: Alloc, opts: EditOptions) {
  const fork: ApprovalNodeDef = { id: a.node('fork').id, kind: 'fork' };
  const join: ApprovalNodeDef = { id: a.node('join').id, kind: 'join', forkId: fork.id, mode: 'all' };
  const branches = [newApproval(a, opts), newApproval(a, opts)];
  const edges: ApprovalEdgeDef[] = [];
  for (const b of branches) edges.push({ id: a.edge(), from: fork.id, to: b.id }, { id: a.edge(), from: b.id, to: join.id });
  return { fork, join, branches, edges, nodes: [fork, ...branches, join] };
}

/** 顯示文字的線（有標籤，或條件線顯示條件摘要）：起點右側的欄距要加寬（U59）。 */
function labeledEdges(def: GraphDefinition): Set<string> {
  const kind = new Map(def.nodes.map((n) => [n.id, n.kind]));
  return new Set(def.edges.filter((e) => (!!e.label && Object.keys(e.label).length > 0) || (kind.get(e.from) === 'condition' && !e.default)).map((e) => e.id));
}

/**
 * 插入後騰位置（#109）：只動新節點（`added`）與插入處下游（從 `to` 走得到的節點），上游與無關節點不動。
 * 1. 新節點照整理排版（只排 `from`、新節點、`to` 這一小段）的相對位置放在 `from` 右側一個欄距；
 *    高度對齊原本那條線：`from` 分岔時是終點的高度，否則是起點的高度。
 * 2. 依拓撲順序只往右推：前一個節點動過時，原本在它右邊的下一個節點保持至少一個欄距；與其他節點重疊就推到它右邊。
 */
function makeRoom(s: GraphState, from: string, to: string, added: ApprovalNodeDef[]): void {
  const { nodes, edges } = s.definition;
  const isNew = new Set(added.map((n) => n.id));
  const entry = edges.find((e) => e.from === from && isNew.has(e.to))!.to;
  const labeled = labeledEdges(s.definition);
  const gap = (id: string) => (edges.some((e) => e.from === id && labeled.has(e.id)) ? LABELED_COL_GAP : COL_GAP);
  const at = (id: string): Position => s.positions[id] ?? { x: 0, y: 0 };

  const part = new Set([from, to, ...isNew]);
  const ideal = relayout({
    definition: { ...s.definition, nodes: nodes.filter((n) => part.has(n.id)), edges: edges.filter((e) => isNew.has(e.from) || isNew.has(e.to)) },
    positions: {},
  }).positions;
  const anchor = { x: at(from).x + NODE_W + gap(from), y: edges.filter((e) => e.from === from).length > 1 ? at(to).y : at(from).y };
  for (const id of isNew) s.positions[id] = { x: anchor.x + ideal[id].x - ideal[entry].x, y: anchor.y + ideal[id].y - ideal[entry].y };

  const walk = (start: string, next: (e: ApprovalEdgeDef) => [string, string]) => {
    const seen = new Set([start]);
    const queue = [start];
    for (let i = 0; i < queue.length; i++) {
      for (const e of edges) {
        const [a, b] = next(e);
        if (a === queue[i] && !seen.has(b) && !isNew.has(b)) { seen.add(b); queue.push(b); }
      }
    }
    return seen;
  };
  // 編輯中的圖可能有環：走得回起點的節點算上游，不動
  const upstream = walk(from, (e) => [e.to, e.from]);
  const movable = new Set([...isNew, ...[...walk(to, (e) => [e.from, e.to])].filter((id) => !upstream.has(id))]);
  const order = topoOrder({
    ...s.definition,
    nodes: [nodes.find((n) => n.id === entry)!, ...nodes.filter((n) => n.id !== entry && movable.has(n.id))],
    edges: edges.filter((e) => movable.has(e.from) && movable.has(e.to)),
  });

  const before = new Map(order.map((id) => [id, at(id)]));
  const moved = new Set(isNew);
  const placed = nodes.map((n) => n.id).filter((id) => !movable.has(id));
  for (const id of order) {
    const p = { ...at(id) };
    for (const e of edges) {
      if (e.to !== id || !moved.has(e.from)) continue;
      if (!isNew.has(e.from) && before.get(id)!.x <= before.get(e.from)!.x) continue; // 手排成往回的線照舊
      p.x = Math.max(p.x, at(e.from).x + NODE_W + gap(e.from));
    }
    const hit = () => placed.find((o) => Math.abs(at(o).x - p.x) < NODE_W && Math.abs(at(o).y - p.y) < NODE_H);
    for (let o = hit(); o !== undefined; o = hit()) p.x = at(o).x + NODE_W + COL_GAP;
    if (p.x !== at(id).x) { moved.add(id); s.positions[id] = p; }
    placed.push(id);
  }
}

/** U49：在線 A→B 上插入節點，線上原有的標籤與條件留在前一段（A→新節點）；新節點與下游往右騰出位置（#109）。 */
export function insertOnEdge(state: GraphState, edgeId: string, kind: ApprovalNodeKind, opts: EditOptions = {}): GraphState {
  if (kind !== 'approval' && kind !== 'condition' && kind !== 'fork' && kind !== 'notify') return state;
  const s = clone(state);
  const edge = s.definition.edges.find((e) => e.id === edgeId);
  if (!edge) return state;
  const oldTo = edge.to;
  const a = allocator(s.definition, opts.reserved);
  let added: ApprovalNodeDef[];
  if (kind === 'approval' || kind === 'notify') {
    const n = kind === 'approval' ? newApproval(a, opts) : newNotify(a, opts);
    edge.to = n.id;
    s.definition.edges.push({ id: a.edge(), from: n.id, to: oldTo });
    added = [n];
  } else if (kind === 'fork') {
    const b = forkBlock(a, opts);
    edge.to = b.fork.id;
    s.definition.edges.push(...b.edges, { id: a.edge(), from: b.join.id, to: oldTo });
    added = b.nodes;
  } else {
    const c: ApprovalNodeDef = { id: a.node('condition').id, kind: 'condition' };
    const x = newApproval(a, opts);
    edge.to = c.id;
    s.definition.edges.push(
      { id: a.edge(), from: c.id, to: oldTo, default: true },
      { id: a.edge(), from: c.id, to: x.id, order: 1, ...(opts.names?.conditionLabel ? { label: opts.names.conditionLabel(1) } : {}) },
      { id: a.edge(), from: x.id, to: oldTo },
    );
    added = [c, x];
  }
  s.definition.nodes.push(...added);
  makeRoom(s, edge.from, oldTo, added);
  return s;
}

/** U63：從面板放到空白處；分支帶配對匯合與兩條分支（不接外面的線），匯合與開始不提供。面板列簽核、條件、通知、分支、結束；通知節點帶 U-b 預設值。 */
export function addNode(state: GraphState, kind: ApprovalNodeKind, at: Position, opts: EditOptions = {}): GraphState {
  if (kind === 'join' || kind === 'start') return state;
  const s = clone(state);
  const a = allocator(s.definition, opts.reserved);
  if (kind === 'fork') {
    const b = forkBlock(a, opts);
    s.definition.nodes.push(...b.nodes);
    s.definition.edges.push(...b.edges);
    const at4 = [at, { x: at.x + 224, y: at.y - 50 }, { x: at.x + 224, y: at.y + 50 }, { x: at.x + 448, y: at.y }];
    b.nodes.forEach((n, i) => { s.positions[n.id] = at4[i]; });
    return s;
  }
  const n: ApprovalNodeDef = kind === 'approval' ? newApproval(a, opts) : kind === 'notify' ? newNotify(a, opts) : { id: a.node(kind).id, kind };
  s.definition.nodes.push(n);
  s.positions[n.id] = { ...at };
  return s;
}

/** 拉線當下的本地判斷（J68）；可以連回 null，不行回原因代碼。 */
export function canConnect(def: GraphDefinition, from: string, to: string): ConnectRefusal | null {
  const a = def.nodes.find((n) => n.id === from);
  const b = def.nodes.find((n) => n.id === to);
  if (!a || !b) return 'missing';
  if (from === to) return 'selfLoop';
  if (b.kind === 'start') return 'intoStart';
  if (a.kind === 'end') return 'outOfEnd';
  if (def.edges.some((e) => e.from === from && e.to === to)) return 'duplicate';
  if (SINGLE_OUT.has(a.kind) && def.edges.some((e) => e.from === from)) return 'outLimit';
  return null;
}

/** 條件節點的出線：條件線依 order，預設線固定最後。 */
export function conditionOutEdges(def: GraphDefinition, id: string): ApprovalEdgeDef[] {
  return def.edges.filter((e) => e.from === id).sort((x, y) => Number(!!x.default) - Number(!!y.default) || (x.order ?? 0) - (y.order ?? 0));
}

/**
 * 連線。從條件節點拉出的線：節點還沒有預設線時，這一條成為預設線，否則是下一個 order 的條件線（條件待填），
 * 標籤與 insertOnEdge 一致（U49「條件 n」，由 `opts.names.conditionLabel` 提供）。
 */
export function connect(state: GraphState, from: string, to: string, opts: EditOptions = {}): GraphState {
  if (canConnect(state.definition, from, to)) return state;
  const s = clone(state);
  const a = allocator(s.definition);
  const e: ApprovalEdgeDef = { id: a.edge(), from, to };
  if (s.definition.nodes.find((n) => n.id === from)!.kind === 'condition') {
    const outs = s.definition.edges.filter((x) => x.from === from);
    if (outs.some((x) => x.default)) {
      e.order = Math.max(0, ...outs.map((x) => x.order ?? 0)) + 1;
      if (opts.names?.conditionLabel) e.label = opts.names.conditionLabel(e.order);
    } else e.default = true;
  }
  s.definition.edges.push(e);
  return s;
}

/**
 * U50：刪節點。簽核、通知節點恰一進一出時，前一個節點直接連到後一個節點（線的標籤與條件留在前一段），接回會重複或自己連自己時不接；
 * 條件、分支、匯合、結束不自動接。`withJoin`：刪分支時一併刪配對的匯合與之間的節點（兩者之間走得通的節點，不含區塊外的）。開始節點不能刪。
 */
export function removeNode(state: GraphState, id: string, opts: { withJoin?: boolean } = {}): GraphState {
  const node = state.definition.nodes.find((n) => n.id === id);
  if (!node || node.kind === 'start') return state;
  const s = clone(state);
  const gone = new Set([id]);
  if (node.kind === 'fork' && opts.withJoin) {
    const join = s.definition.nodes.find((n) => n.kind === 'join' && n.forkId === id);
    if (join) {
      gone.add(join.id);
      // 「之間」＝從分支往後走得到（到匯合為止）、而且從匯合往回走得到（到分支為止）的節點；編輯到一半沒收進匯合的分支會走到區塊外，不能刪
      const reach = (start: string, stop: string, next: (e: ApprovalEdgeDef) => [string, string]) => {
        const seen = new Set([start]);
        const queue = [start];
        for (let i = 0; i < queue.length; i++) {
          if (queue[i] === stop) continue;
          for (const e of s.definition.edges) {
            const [from, to] = next(e);
            if (from === queue[i] && !seen.has(to)) { seen.add(to); queue.push(to); }
          }
        }
        return seen;
      };
      const after = reach(id, join.id, (e) => [e.from, e.to]);
      const before = reach(join.id, id, (e) => [e.to, e.from]);
      for (const n of after) if (before.has(n)) gone.add(n);
    }
  }
  if (node.kind === 'approval' || node.kind === 'notify') {
    const ins = s.definition.edges.filter((e) => e.to === id);
    const outs = s.definition.edges.filter((e) => e.from === id);
    if (ins.length === 1 && outs.length === 1) {
      const [i] = ins;
      const target = outs[0].to;
      if (i.from !== target && !s.definition.edges.some((e) => e.from === i.from && e.to === target)) {
        i.to = target;
        s.definition.edges = s.definition.edges.filter((e) => e.id !== outs[0].id);
      }
    }
  }
  s.definition.edges = s.definition.edges.filter((e) => !gone.has(e.from) && !gone.has(e.to));
  s.definition.nodes = s.definition.nodes.filter((n) => !gone.has(n.id));
  for (const g of gone) delete s.positions[g];
  return s;
}

export function removeEdge(state: GraphState, edgeId: string): GraphState {
  if (!state.definition.edges.some((e) => e.id === edgeId)) return state;
  const s = clone(state);
  s.definition.edges = s.definition.edges.filter((e) => e.id !== edgeId);
  return s;
}

export function moveNode(state: GraphState, id: string, to: Position): GraphState {
  if (!state.definition.nodes.some((n) => n.id === id)) return state;
  return { ...state, positions: { ...state.positions, [id]: { ...to } } };
}

/** 合併欄位；值為 undefined 的欄位刪掉（讓後端的 null 省略規則成立）。 */
function patched<T extends object>(target: T, patch: Partial<T> | Partial<Omit<T, 'id'>>): T {
  const out = { ...target } as Record<string, unknown>;
  for (const [k, v] of Object.entries(patch)) out[k] = deepCopy(v);
  for (const k of Object.keys(out)) if (out[k] === undefined) delete out[k];
  return out as T;
}

export function setNode(state: GraphState, id: string, patch: Partial<Omit<ApprovalNodeDef, 'id'>>): GraphState {
  const i = state.definition.nodes.findIndex((n) => n.id === id);
  if (i < 0) return state;
  const s = clone(state);
  s.definition.nodes[i] = patched<ApprovalNodeDef>(s.definition.nodes[i], patch);
  return s;
}

export function setEdge(state: GraphState, id: string, patch: Partial<Omit<ApprovalEdgeDef, 'id'>>): GraphState {
  const i = state.definition.edges.findIndex((e) => e.id === id);
  if (i < 0) return state;
  const s = clone(state);
  s.definition.edges[i] = patched<ApprovalEdgeDef>(s.definition.edges[i], patch);
  return s;
}

/** 條件線上移或下移一格（對調 order）；預設線與已在邊界的線不動。 */
export function reorderConditionEdge(state: GraphState, edgeId: string, delta: -1 | 1): GraphState {
  const e = state.definition.edges.find((x) => x.id === edgeId);
  if (!e || e.default || e.order == null) return state;
  const siblings = conditionOutEdges(state.definition, e.from).filter((x) => !x.default);
  const j = siblings.findIndex((x) => x.id === edgeId) + delta;
  if (j < 0 || j >= siblings.length) return state;
  const other = siblings[j];
  const s = clone(state);
  const mine = s.definition.edges.find((x) => x.id === edgeId)!;
  const theirs = s.definition.edges.find((x) => x.id === other.id)!;
  [mine.order, theirs.order] = [theirs.order, mine.order];
  return s;
}

/** 改節點代號：線的端點、匯合的 `forkId`、座標一併改；新代號不合法或已被用就不動。 */
export function renameCode(state: GraphState, oldId: string, newId: string): GraphState {
  if (oldId === newId || !CODE.test(newId) || state.definition.nodes.some((n) => n.id === newId)) return state;
  if (!state.definition.nodes.some((n) => n.id === oldId)) return state;
  const s = clone(state);
  for (const n of s.definition.nodes) {
    if (n.id === oldId) n.id = newId;
    if (n.forkId === oldId) n.forkId = newId;
  }
  for (const e of s.definition.edges) {
    if (e.from === oldId) e.from = newId;
    if (e.to === oldId) e.to = newId;
  }
  if (s.positions[oldId]) { s.positions[newId] = s.positions[oldId]; delete s.positions[oldId]; }
  return s;
}

/** 整理排版（J73）：重算全部座標；有標籤的線（含條件線，它們顯示條件摘要）起點右側的欄距加寬（U59）。 */
export function relayout(state: GraphState): GraphState {
  const r = layoutLeftToRight(
    state.definition.nodes.map((n) => ({ id: n.id, kind: n.kind })),
    state.definition.edges.map((e) => ({ id: e.id, from: e.from, to: e.to })),
    { labeledEdgeIds: labeledEdges(state.definition) },
  );
  const positions = Object.fromEntries(Object.entries(r.positions).map(([id, p]) => [id, { x: p.x, y: p.y }]));
  const same = Object.keys(positions).length === Object.keys(state.positions).length
    && Object.entries(positions).every(([id, p]) => state.positions[id]?.x === p.x && state.positions[id]?.y === p.y);
  return same ? state : { ...state, positions };
}

/** 節點清單用：從開始節點起的拓撲順序（同一層依節點在定義裡的順序），走不到的節點排最後。 */
export function topoOrder(def: GraphDefinition): string[] {
  const start = def.nodes.find((n) => n.kind === 'start') ?? def.nodes[0];
  if (!start) return [];
  const rank = new Map(def.nodes.map((n, i) => [n.id, i]));
  const indeg = new Map(def.nodes.map((n) => [n.id, 0]));
  const reach = new Set([start.id]);
  const q = [start.id];
  for (let i = 0; i < q.length; i++) for (const e of def.edges) if (e.from === q[i] && !reach.has(e.to)) { reach.add(e.to); q.push(e.to); }
  for (const e of def.edges) if (reach.has(e.from) && reach.has(e.to)) indeg.set(e.to, indeg.get(e.to)! + 1);
  const ready = [...reach].filter((id) => indeg.get(id) === 0).sort((a, b) => rank.get(a)! - rank.get(b)!);
  const out: string[] = [];
  while (ready.length) {
    const id = ready.shift()!;
    out.push(id);
    for (const e of def.edges) {
      if (e.from !== id || !reach.has(e.to)) continue;
      indeg.set(e.to, indeg.get(e.to)! - 1);
      if (indeg.get(e.to) === 0) { ready.push(e.to); ready.sort((a, b) => rank.get(a)! - rank.get(b)!); }
    }
  }
  const seen = new Set(out);
  return [...out, ...def.nodes.map((n) => n.id).filter((id) => !seen.has(id))];
}
