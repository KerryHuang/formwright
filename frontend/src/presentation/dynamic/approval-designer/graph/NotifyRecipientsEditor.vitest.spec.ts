import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { NotifyRecipientDef } from 'src/domain/dynamic/approval-flow-types';
import enUS from 'shared/i18n/en-US/ui.json';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import NotifyRecipientsEditor from './NotifyRecipientsEditor.vue';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { getUsers: vi.fn(), getGroups: vi.fn() } }));
const api = vi.mocked(dynamicApi);

let groupList = [{ id: 1, name: { 'zh-TW': '人事群組' } }];
const i18nOf = () => createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW, 'en-US': enUS } });

function mountEditor(modelValue: NotifyRecipientDef[], extra: Record<string, unknown> = {}, i18n = i18nOf()) {
  api.getUsers.mockResolvedValue([{ userId: 'u1', displayName: '王小明' }] as never);
  api.getGroups.mockResolvedValue(groupList as never);
  return mount(NotifyRecipientsEditor, {
    props: { modelValue, ...extra },
    global: { plugins: [createPinia(), [quasar.Quasar, { components: quasar }], i18n, [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]] },
  });
}
const sel = (w: VueWrapper, id: string) => w.findAllComponents(quasar.QSelect).find((c) => c.find(`[data-testid="${id}"]`).exists())!;
const last = (w: ReturnType<typeof mountEditor>) => w.emitted('update:modelValue')!.at(-1)![0];


beforeEach(() => { vi.clearAllMocks(); groupList = [{ id: 1, name: { 'zh-TW': '人事群組' } }]; });

describe('NotifyRecipientsEditor', () => {
  it('lists the four kinds; initiator has no parameter', async () => {
    const w = mountEditor([{ kind: 'initiator' }]);
    await flushPromises();
    const kinds = (sel(w, 'notify-kind-0').props('options') as { value: string }[]).map((o) => o.value);
    expect(kinds).toEqual(['initiator', 'initiatorManager', 'users', 'group']);
    expect(w.find('[data-testid="notify-users-0"]').exists()).toBe(false);
    expect(w.find('[data-testid="notify-groups-0"]').exists()).toBe(false);
    expect(w.find('[data-testid="notify-level-0"]').exists()).toBe(false);
  });

  it('changing the kind resets to that kind\'s own empty parameter (no leftovers from the old kind)', async () => {
    const w = mountEditor([{ kind: 'users', userIds: ['u1'] }]);
    await flushPromises();
    const kind = sel(w, 'notify-kind-0');
    kind.vm.$emit('update:modelValue', 'group');
    expect(last(w)).toEqual([{ kind: 'group', groupIds: [] }]);
    kind.vm.$emit('update:modelValue', 'initiatorManager');
    expect(last(w)).toEqual([{ kind: 'initiatorManager', level: 1 }]);
    kind.vm.$emit('update:modelValue', 'initiator');
    expect(last(w)).toEqual([{ kind: 'initiator' }]);
  });

  it('users and groups multi-selects patch their own parameter; an id no longer in the list is marked once the list has loaded', async () => {
    const w = mountEditor([{ kind: 'users', userIds: ['u1', 'gone'] }, { kind: 'group', groupIds: [1] }]);
    await flushPromises();
    const users = sel(w, 'notify-users-0');
    expect((users.props('options') as { value: string; label: string }[]).find((o) => o.value === 'gone')!.label).toContain('gone');
    users.vm.$emit('update:modelValue', ['u1']);
    expect(last(w)).toEqual([{ kind: 'users', userIds: ['u1'] }, { kind: 'group', groupIds: [1] }]);
    sel(w, 'notify-groups-1').vm.$emit('update:modelValue', [1, 2]);
    expect(last(w)).toEqual([{ kind: 'users', userIds: ['u1', 'gone'] }, { kind: 'group', groupIds: [1, 2] }]);
  });

  it('without a policy value the manager level is a number input (the backend still checks)', async () => {
    const w = mountEditor([{ kind: 'initiatorManager', level: 2 }]);
    await flushPromises();
    expect(w.get('input[data-testid="notify-level-0"]').attributes('type')).toBe('number');
  });

  it('shows the empty hint and the error text', async () => {
    const w = mountEditor([], { error: '至少一位' });
    await flushPromises();
    expect(w.find('[data-testid="notify-no-recipients"]').exists()).toBe(true);
    expect(w.get('[data-testid="notify-recipients-error"]').text()).toBe('至少一位');
  });

  it('G4: kind labels follow the UI locale while the editor is open', async () => {
    const i18n = i18nOf();
    const w = mountEditor([{ kind: 'initiator' }], {}, i18n);
    await flushPromises();
    const label = () => (sel(w, 'notify-kind-0').props('options') as { label: string }[])[0].label;
    expect(label()).toBe('申請人');
    i18n.global.locale.value = 'en-US';
    await flushPromises();
    expect(label()).toBe('Requester');
  });

  it('G4: the search text of one row does not filter the options of another row', async () => {
    groupList = [{ id: 1, name: { 'zh-TW': '人事群組' } }, { id: 2, name: { 'zh-TW': '財務群組' } }];
    const w = mountEditor([{ kind: 'group', groupIds: [] }, { kind: 'group', groupIds: [] }]);
    await flushPromises();
    const options = (i: number) => (sel(w, `notify-groups-${i}`).props('options') as { value: number }[]).map((o) => o.value);
    sel(w, 'notify-groups-0').vm.$emit('filter', '人事', (fn: () => void) => fn());
    await flushPromises();
    expect(options(0)).toEqual([1]);
    expect(options(1)).toEqual([1, 2]);
  });
});
