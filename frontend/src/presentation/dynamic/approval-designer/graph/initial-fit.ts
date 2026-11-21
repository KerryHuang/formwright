// 手機首屏 fitView 的縮放下限：預設 0.6（節點還讀得清楚），但聚焦的節點在 0.6 下比畫布寬時會被左右切掉，
// 這時降到剛好放得下（左右各留 EDGE_MARGIN），不低於畫布的全域下限。
const PREFERRED_MIN_ZOOM = 0.6;
const GLOBAL_MIN_ZOOM = 0.2;
const EDGE_MARGIN = 8;

export function initialMinZoom(nodes: { position: { x: number } }[], nodeWidth: number, containerWidth: number): number {
  if (!nodes.length || containerWidth <= 0) return PREFERRED_MIN_ZOOM;
  const xs = nodes.map((n) => n.position.x);
  const span = Math.max(...xs) - Math.min(...xs) + nodeWidth;
  const fit = (containerWidth - 2 * EDGE_MARGIN) / span;
  return Math.max(GLOBAL_MIN_ZOOM, Math.min(PREFERRED_MIN_ZOOM, fit));
}
