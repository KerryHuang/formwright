import { describe, expect, it } from 'vitest';
import { initialMinZoom } from './initial-fit';

const n = (x: number) => ({ position: { x, y: 0 } });

describe('initialMinZoom', () => {
  it('keeps 0.6 when the focused nodes fit the container at 0.6', () => {
    expect(initialMinZoom([n(0), n(200)], 168, 390)).toBe(0.6); // 寬 368 × 0.6 = 221
    expect(initialMinZoom([n(0), n(200)], 168, 1366)).toBe(0.6);
  });
  it('drops below 0.6 on a phone so the nodes are not cut at the edges', () => {
    const z = initialMinZoom([n(0), n(240), n(480)], 168, 390); // 寬 648：0.6 會是 389 + 邊距 > 390
    expect(z).toBeLessThan(0.6);
    expect(648 * z + 2 * 8).toBeLessThanOrEqual(390.0001);
  });
  it('never goes below the global minimum zoom 0.2 and ignores empty input', () => {
    expect(initialMinZoom([n(0), n(5000)], 168, 390)).toBe(0.2);
    expect(initialMinZoom([], 168, 390)).toBe(0.6);
    expect(initialMinZoom([n(0)], 168, 0)).toBe(0.6); // 尚未量到寬度
  });
});
