import { describe, expect, it } from 'vitest';
import type { SchemaField } from 'src/domain/dynamic/types';
import { appendPlaceholder, placeholderOptions } from './notify-placeholders';

const t = (k: string, p?: Record<string, unknown>) => (p ? `${k}:${p.name}` : k);
const field = (id: number, label: string): SchemaField => ({ id, key: `f_${id}`, type: 'text', label: { 'zh-TW': label }, config: {} as never, options: [] });

describe('notify placeholders', () => {
  it('lists the four built-ins and then each given (active header) field as {f:id}', () => {
    const o = placeholderOptions([field(7, '總額'), field(9, '供應商')], 'zh-TW', t);
    expect(o.map((x) => x.value)).toEqual(['{title}', '{flow}', '{initiator}', '{node}', '{f:7}', '{f:9}']);
    expect(o[4].label).toBe('approval.designer.graph.notify.ph.field:總額');
  });

  it('with no fields only the built-ins are offered', () => {
    expect(placeholderOptions([], 'zh-TW', t)).toHaveLength(4);
  });

  it('appends the token to every locale that has text plus the default (required) locales, leaving others untouched', () => {
    expect(appendPlaceholder({ 'en-US': 'Hi ' }, '{title}', ['zh-TW'])).toEqual({ 'en-US': 'Hi {title}', 'zh-TW': '{title}' });
    expect(appendPlaceholder({}, '{f:7}', ['zh-TW'])).toEqual({ 'zh-TW': '{f:7}' });
  });
});
