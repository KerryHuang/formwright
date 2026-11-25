import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { createPinia } from 'pinia';
import * as quasar from 'quasar';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { ApprovalFlow, GraphDefinition } from 'src/domain/dynamic/approval-flow-types';
import { mapGraphIssues } from 'src/domain/dynamic/approval-graph-errors';
import type { DesignerField, ModuleDetail } from 'src/domain/dynamic/types';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import NotifyNodePanel from './NotifyNodePanel.vue';
import { useGraphDesigner } from './use-graph-designer';

vi.mock('src/services/api/dynamic-api', () => ({ dynamicApi: { getUsers: vi.fn(), getGroups: vi.fn(), getEffectivePolicies: vi.fn(), getModulePolicies: vi.fn() } }));
const api = vi.mocked(dynamicApi);

const field = (id: number, type: DesignerField['type'], label: string, status: DesignerField['status'] = 'Active'): DesignerField => ({
  id, key: `f_${id}`, type, label: { 'zh-TW': label }, blockId: 1, sequence: id, status,
  config: { required: false, readOnly: false, showInList: false, isFilterable: false, isSortable: false }, options: [],
});
const mod: ModuleDetail = {
  id: 1, key: 'doc', programCode: 'P', name: { 'zh-TW': '單據' }, status: 'Active', version: 1,
  blocks: [{ id: 1, label: { 'zh-TW': 'b' }, sequence: 1, collapsed: false, columns: 2, fields: [field(5, 'text', '備註'), field(7, 'decimal', '總額'), field(8, 'reference', '供應商'), field(9, 'text', '停用欄', 'Inactive')] }],
};
const def = (recipients: GraphDefinition['nodes'][number]['recipients'] = [{ kind: 'initiator' }]): GraphDefinition => ({
  shape: 'graph', stages: [],
  nodes: [{ id: 's', kind: 'start' }, { id: 'n', kind: 'notify', name: { 'zh-TW': '通知 1' }, recipients, template: { title: { 'zh-TW': '{title}' }, body: {} } }, { id: 'e', kind: 'end' }],
  edges: [{ id: 'e1', from: 's', to: 'n' }, { id: 'e2', from: 'n', to: 'e' }],
});
const flow = (d: GraphDefinition, published = true): ApprovalFlow => ({
  key: 'po', name: { 'zh-TW': '採購' }, toOptionId: 12, priority: 1, active: true, used: false, pendingCount: 0, moduleKey: 'doc',
  ...(published ? { version: 1, versionId: 3, definition: d, graph: d } : {}), draft: published ? undefined : { definition: d } as never,
} as ApprovalFlow);

function mountPanel(d: GraphDefinition, opts: { canEdit?: boolean; maxLevel?: number; published?: boolean } = {}) {
  api.getUsers.mockResolvedValue([{ userId: 'u1', displayName: '王小明' }] as never);
  api.getGroups.mockResolvedValue([{ id: 1, name: { 'zh-TW': '人事群組' } }] as never);
  api.getEffectivePolicies.mockResolvedValue({ values: opts.maxLevel ? { 'approval.maxManagerLevel': opts.maxLevel } : {} } as never);
  api.getModulePolicies.mockResolvedValue([]);
  const designer = useGraphDesigner(() => opts.canEdit ?? true);
  designer.load(flow(d, opts.published ?? true));
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const w = mount(NotifyNodePanel, {
    props: { designer, node: designer.state.value!.definition.nodes[1], module: mod },
    attachTo: document.body,
    global: { plugins: [createPinia(), [quasar.Quasar, { components: quasar }], i18n, [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]] },
  });
  const node = () => designer.state.value!.definition.nodes[1];
  return { w, designer, node, refresh: async () => { await w.setProps({ node: node() }); await flushPromises(); } };
}

const sel = (w: VueWrapper, id: string) => w.findAllComponents(quasar.QSelect).find((c) => c.find(`[data-testid="${id}"]`).exists())!;

beforeEach(() => vi.clearAllMocks());

describe('NotifyNodePanel (fg5d)', () => {
  it('shows name, recipients and template; body is optional (no * on it)', async () => {
    const { w } = mountPanel(def());
    await flushPromises();
    expect(w.find('[data-testid="notify-panel"]').exists()).toBe(true);
    expect(w.find('[data-testid="notify-kind-0"]').exists()).toBe(true);
    expect(w.get('[data-testid="notify-title"] [data-testid="i18n-zh-TW"]').text()).toContain('*');
    expect(w.get('[data-testid="notify-body"] [data-testid="i18n-zh-TW"]').text()).not.toContain('*');
    expect(w.find('[data-testid="notify-body"] textarea').exists()).toBe(true);
    w.unmount();
  });

  it('add and remove recipient rows edit the working copy', async () => {
    const { w, node, refresh } = mountPanel(def());
    await flushPromises();
    await w.get('[data-testid="notify-add"]').trigger('click');
    expect(node().recipients).toEqual([{ kind: 'initiator' }, { kind: 'initiator' }]);
    await refresh();
    await w.get('[data-testid="notify-remove-0"]').trigger('click');
    expect(node().recipients).toEqual([{ kind: 'initiator' }]);
    w.unmount();
  });

  it('insert menu lists built-ins and only the active header fields (inactive field absent) and appends the token', async () => {
    const { w, node, refresh } = mountPanel(def());
    await flushPromises();
    const picker = sel(w, 'notify-insert-title');
    const values = (picker.props('options') as { value: string }[]).map((o) => o.value);
    expect(values).toEqual(['{title}', '{flow}', '{initiator}', '{node}', '{f:5}', '{f:7}', '{f:8}']);
    picker.vm.$emit('update:modelValue', '{f:7}');
    await flushPromises();
    expect(node().template!.title).toEqual({ 'zh-TW': '{title}{f:7}' });
    await refresh();
    sel(w, 'notify-insert-body').vm.$emit('update:modelValue', '{flow}');
    await flushPromises();
    expect(node().template!.body).toEqual({ 'zh-TW': '{flow}' });
    w.unmount();
  });

  it('server errors land on the right field: recipients, template.title, template.body, name', async () => {
    const { w, designer } = mountPanel(def());
    designer.issues.value = mapGraphIssues({
      'definition.nodes.n.recipients': ['收件人錯'], 'definition.nodes.n.template.title': ['標題錯'], 'definition.nodes.n.template.body': ['內文錯'], 'definition.nodes.n.name': ['名稱錯'],
    }, [], designer.state.value!.definition);
    await flushPromises();
    expect(w.get('[data-testid="notify-recipients-error"]').text()).toBe('收件人錯');
    expect(w.get('[data-testid="notify-title"]').text()).toContain('標題錯');
    expect(w.get('[data-testid="notify-body"]').text()).toContain('內文錯');
    expect(w.text()).toContain('名稱錯');
    w.unmount();
  });

  it('read-only (no edit right): controls are disabled', async () => {
    const { w } = mountPanel(def(), { canEdit: false });
    await flushPromises();
    expect(w.get('[data-testid="notify-add"]').attributes('disabled')).toBeDefined();
    expect(w.get('[data-testid="notify-remove-0"]').attributes('disabled')).toBeDefined();
    w.unmount();
  });

  it('tightening only governs what comes after: a published level above the policy max stays selectable (marked), others above are not', async () => {
    const { w } = mountPanel(def([{ kind: 'initiatorManager', level: 4 }]), { maxLevel: 2 });
    await flushPromises();
    const levels = (sel(w, 'notify-level-0').props('options') as { value: number; label: string }[]);
    expect(levels.map((o) => o.value)).toEqual([1, 2, 3, 4]);
    expect(levels[3].label).toContain('沿用舊值');
    w.unmount();
  });

  it('a draft-only flow (nothing published) offers just 1..max', async () => {
    const { w } = mountPanel(def([{ kind: 'initiatorManager', level: 1 }]), { maxLevel: 2, published: false });
    await flushPromises();
    expect((sel(w, 'notify-level-0').props('options') as { value: number }[]).map((o) => o.value)).toEqual([1, 2]);
    w.unmount();
  });
});
