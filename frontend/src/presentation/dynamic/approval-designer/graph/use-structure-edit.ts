import { computed, ref } from 'vue';
import { addNode, canConnect, connect, insertOnEdge, relayout, removeEdge, removeNode, type GraphState, type Position } from 'src/domain/dynamic/approval-graph-edit';
import { FLOW_SELECTION, INSERT_KINDS, PALETTE_KINDS, addable, addedNode, type AddKind, type AddLimits } from './designer-graph';
import { editOptions, type GraphDesigner } from './use-graph-designer';

// 設計頁的結構編輯（fg3e，§10.3）：新增、插入、連線、刪除、整理排版。每個動作是一步復原（走 designer.edit），唯讀與手機一律不動。
// 只接 designer 與「現在能不能改結構」，不碰 Vue Flow；座標換算由畫布提供。

export function useStructureEdit(designer: GraphDesigner, opts: { structure: () => boolean; limits: () => AddLimits }) {
  const can = () => opts.structure() && designer.canEdit.value;
  const def = () => designer.state.value?.definition;
  const options = () => editOptions(designer.server.value.reserved);

  const allowed = computed(() => {
    const d = def();
    return d ? addable(d, opts.limits(), Object.keys(designer.server.value.publishedApprovers).length) : null;
  });
  const items = (kinds: AddKind[]) => kinds.map((kind) => ({ kind, disabled: !allowed.value?.[kind] }));
  const paletteItems = computed(() => items(PALETTE_KINDS));
  const insertItems = computed(() => items(INSERT_KINDS));

  /** 改圖並選取新出現的節點（屬性面板跟著切過去）。 */
  function editAndSelect(fn: (s: GraphState) => GraphState) {
    const before = def();
    designer.edit(fn);
    const after = def();
    const id = before && after && after !== before ? addedNode(before, after) : null;
    if (id) designer.select({ kind: 'node', id });
  }

  /** 從節點面板放下：落在線上而且是能插在線上的型別＝插入（U49），否則放在那個位置（U63）。 */
  function place(kind: AddKind, at: Position, edgeId: string | null) {
    if (!can() || !allowed.value?.[kind]) return;
    if (edgeId && INSERT_KINDS.includes(kind)) editAndSelect((s) => insertOnEdge(s, edgeId, kind, options()));
    else editAndSelect((s) => addNode(s, kind, at, options()));
  }
  /** 線上「＋」選了型別。 */
  function insert(edgeId: string, kind: AddKind) {
    if (!can() || !INSERT_KINDS.includes(kind) || !allowed.value?.[kind]) return;
    editAndSelect((s) => insertOnEdge(s, edgeId, kind, options()));
  }
  /** 拉線當下的本地判斷（J68）。 */
  const connectable = (from: string, to: string) => { const d = def(); return !!d && canConnect(d, from, to) === null; };
  function link(from: string, to: string) {
    if (!can()) return;
    designer.edit((s) => connect(s, from, to, options()));
  }

  /** 選取的東西能不能刪：節點（開始除外）或線。 */
  const removable = computed(() => {
    const d = def();
    const sel = designer.selection.value;
    if (!d || !can()) return false;
    if (sel.kind === 'node') { const n = d.nodes.find((x) => x.id === sel.id); return !!n && n.kind !== 'start'; }
    if (sel.kind === 'edge') return d.edges.some((e) => e.id === sel.id);
    return false;
  });
  /** 刪分支節點、而且有配對的匯合時，先問要不要一併刪（§10.3）；這裡放等著回答的分支節點代號。 */
  const askFork = ref<string | null>(null);
  function remove() {
    if (!removable.value) return;
    const sel = designer.selection.value;
    if (sel.kind === 'edge') { designer.edit((s) => removeEdge(s, sel.id)); designer.select(FLOW_SELECTION); return; }
    if (sel.kind !== 'node') return;
    const d = def()!;
    if (d.nodes.find((n) => n.id === sel.id)?.kind === 'fork' && d.nodes.some((n) => n.kind === 'join' && n.forkId === sel.id)) { askFork.value = sel.id; return; }
    designer.edit((s) => removeNode(s, sel.id));
    designer.select(FLOW_SELECTION);
  }
  /** 回答分支的詢問：`null`＝取消。 */
  function answerFork(withJoin: boolean | null) {
    const id = askFork.value;
    askFork.value = null;
    if (id === null || withJoin === null || !can()) return;
    designer.edit((s) => removeNode(s, id, { withJoin }));
    designer.select(FLOW_SELECTION);
  }

  /** 整理排版（J73）：一步可復原。 */
  function tidy() { if (can()) designer.edit(relayout); }

  return { paletteItems, insertItems, place, insert, connectable, link, removable, remove, askFork, answerFork, tidy };
}
