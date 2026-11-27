import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import * as quasar from 'quasar';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { h } from 'vue';
import { createI18n } from 'vue-i18n';
import { createMemoryHistory, createRouter } from 'vue-router';
import type { ModuleSchema, SchemaField, SchemaPermissions, SchemaWorkflow } from 'src/domain/dynamic/types';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import { dynamicApi } from 'src/services/api/dynamic-api';
import FormPage from './FormPage.vue';

vi.mock('src/services/api/dynamic-api', () => ({
  dynamicApi: { getModuleSchema: vi.fn(), getRecord: vi.fn(), createRecord: vi.fn(), updateRecord: vi.fn(), light: vi.fn() },
}));
const api = vi.mocked(dynamicApi);

const field = (id: number, label: string, required = false): SchemaField => ({
  id, key: `f_${id}`, type: 'text', label: { 'zh-TW': label }, options: [],
  config: { required, readOnly: false, showInList: true, isFilterable: true, isSortable: true },
});
const all: SchemaPermissions = { read: true, add: true, edit: true, delete: true, printExport: true, pricing: true };
// A hand-made module outside any app pack: the bar must work from schema and permissions alone.
const schema = (fields: SchemaField[] = [field(1, '標題', true), field(2, '甲')], extra: Partial<ModuleSchema> = {}): ModuleSchema => ({
  key: 'm', programCode: 'DYN_M', name: { 'zh-TW': '自建模組' }, titleFieldKey: 'f_1', version: 1, rules: [], permissions: all,
  blocks: [{ id: 1, label: { 'zh-TW': '區' }, collapsed: false, columns: 2, fields }], ...extra,
});
const record = { id: 5, moduleKey: 'm', title: 'r', values: { f_1: 'r', f_2: 'x' }, ownerUserId: 'a', createdUserId: 'a', createdTime: '', modifiedUserId: 'a', modifiedTime: '', rowVersion: 'v', references: {}, hostKey: null };

async function mountPage(s: ModuleSchema, path: string, width = 1366, rec: unknown = record) {
  window.innerWidth = width;
  setActivePinia(createPinia());
  api.getModuleSchema.mockResolvedValue(s);
  api.getRecord.mockResolvedValue(rec as never);
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/dynamic/:moduleKey/new', component: FormPage }, { path: '/dynamic/:moduleKey/:id/edit', component: FormPage }, { path: '/dynamic/:moduleKey/:id', component: { render: () => null } }, { path: '/dynamic/:moduleKey', component: { render: () => null } }],
  });
  await router.push(path);
  const i18n = createI18n({ legacy: false, locale: 'zh-TW', messages: { 'zh-TW': zhTW } });
  const Host = { render: () => h(quasar.QLayout, null, () => h(quasar.QPageContainer, null, () => h(FormPage))) };
  const w = mount(Host, {
    attachTo: document.body,
    global: {
      plugins: [[quasar.Quasar, { components: quasar, directives: quasar, plugins: { Notify: quasar.Notify } }], router, i18n,
        [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }]],
    },
  });
  window.dispatchEvent(new Event('resize'));
  await new Promise((r) => setTimeout(r, 150));
  await flushPromises();
  return { w, router };
}

const save = (w: ReturnType<typeof mount>) => w.find('[data-testid="form-save"]');

describe('FormPage sticky header', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    document.body.innerHTML = '';
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  });
  afterEach(() => { window.innerWidth = 1024; });

  it('wide: back, title, Cancel and Save are in the fixed bar, and the form has no Save button of its own', async () => {
    const { w } = await mountPage(schema(), '/dynamic/m/5/edit');
    const bar = w.find('[data-testid="record-header-bar"]');
    expect(bar.find('[data-testid="record-back"]').text()).toContain('自建模組');
    expect(bar.find('[data-testid="module-title"]').text()).toBe(zhTW.dynamic.editOf.replace('{module}', '自建模組'));
    expect(bar.find('[data-testid="form-cancel"]').text()).toBe(zhTW.dynamic.cancel);
    expect(save(w).text()).toBe(zhTW.dynamic.save);
    expect(w.find('form button[type="submit"]').exists()).toBe(false);
    expect(w.findAll('button').filter((b) => b.text() === zhTW.dynamic.save)).toHaveLength(1);
  });

  it('Save in the bar submits the form: values go to the API and the page returns to the record', async () => {
    api.updateRecord.mockResolvedValue({ ...record, id: 5 } as never);
    const { w, router } = await mountPage(schema(), '/dynamic/m/5/edit');
    await w.find('input').setValue('新標題');
    await save(w).trigger('click');
    await vi.waitFor(() => expect(api.updateRecord).toHaveBeenCalledWith(5, expect.objectContaining({ f_1: '新標題' }), 'v', undefined, expect.any(String)));
    await vi.waitFor(() => expect(router.currentRoute.value.path).toBe('/dynamic/m/5'));
  });

  it('Save in the bar still validates: required empty shows the error and nothing is sent', async () => {
    const { w, router } = await mountPage(schema(), '/dynamic/m/new');
    await save(w).trigger('click');
    await new Promise((r) => setTimeout(r, 100));
    await flushPromises();
    expect(api.createRecord).not.toHaveBeenCalled();
    expect(router.currentRoute.value.path).toBe('/dynamic/m/new');
    expect(w.find('form').text()).toMatch(/必填|required/i);
  });

  it('Cancel goes back to the list when adding and to the record when editing', async () => {
    const add = await mountPage(schema(), '/dynamic/m/new');
    await add.w.find('[data-testid="form-cancel"]').trigger('click');
    await flushPromises();
    expect(add.router.currentRoute.value.path).toBe('/dynamic/m');
    expect(add.w.find('[data-testid="record-back"]').attributes('href')).toBe('/dynamic/m');
    const edit = await mountPage(schema(), '/dynamic/m/5/edit');
    expect(edit.w.find('[data-testid="record-back"]').attributes('href')).toBe('/dynamic/m/5');
    await edit.w.find('[data-testid="form-cancel"]').trigger('click');
    await flushPromises();
    expect(edit.router.currentRoute.value.path).toBe('/dynamic/m/5');
  });

  it('a pending approval leaves only the way back: no Save', async () => {
    const pending = { ...record, approval: { status: 'pending', stageName: null, stageSeq: 1, canSubmit: false } };
    const { w } = await mountPage(schema(), '/dynamic/m/5/edit', 1366, pending);
    expect(w.find('[data-testid="record-back"]').exists()).toBe(true);
    expect(save(w).exists()).toBe(false);
  });

  it('a record the form cannot save (read-only workflow state) shows no Save', async () => {
    const workflow: SchemaWorkflow = { statusFieldKey: 'f_2', initialState: 'opt_1', states: [{ value: 'opt_1', readOnly: true }], transitions: [] };
    const { w } = await mountPage(schema([field(1, '標題'), field(2, '甲')], { workflow }), '/dynamic/m/5/edit', 1366, { ...record, values: { f_1: 'r', f_2: 'opt_1' } });
    expect(save(w).exists()).toBe(false);
  });

  it('phone: top bar has the back icon and the title; bottom bar has Cancel and Save and no More', async () => {
    const { w } = await mountPage(schema(), '/dynamic/m/new', 390);
    expect(w.find('[data-testid="record-back"]').text()).not.toContain('自建模組');
    expect(w.find('[data-testid="module-title"]').text()).toBe(zhTW.dynamic.newOf.replace('{module}', '自建模組'));
    const bottom = w.find('[data-testid="record-bottom-bar"]');
    expect(bottom.find('[data-testid="form-cancel"]').exists()).toBe(true);
    expect(bottom.find('[data-testid="form-save"]').exists()).toBe(true);
    expect(w.find('[data-testid="record-more"]').exists()).toBe(false);
    expect(w.find('.q-page').attributes('style')).toContain('padding-bottom: calc(88px + env(safe-area-inset-bottom))');
  });
});
