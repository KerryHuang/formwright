import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { describe, expect, it } from 'vitest';
import EmptyState from './EmptyState.vue';

const mountState = (props: { message: string; hint?: string }, slots: Record<string, string> = {}) =>
  mount(EmptyState, { props, slots, global: { plugins: [[quasar.Quasar, { components: quasar }]] } });

describe('EmptyState', () => {
  it('shows the reason, the hint and the action; the icon is neutral and hidden from screen readers', () => {
    const w = mountState({ message: '原因', hint: '例子' }, { action: '<button>動作</button>' });
    const root = w.get('[data-testid="empty-state"]');
    expect(root.text()).toContain('原因');
    expect(root.text()).toContain('例子');
    expect(root.find('button').text()).toBe('動作');
    expect(root.get('.q-icon').attributes('aria-hidden')).toBe('true');
    expect(root.text()).not.toContain('warning');
  });

  it('leaves out the hint and the action when not given', () => {
    const w = mountState({ message: '原因' });
    expect(w.find('[data-testid="empty-state-hint"]').exists()).toBe(false);
    expect(w.find('[data-testid="empty-state-action"]').exists()).toBe(false);
  });
});
