import { mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import { createI18n } from 'vue-i18n';
import type { ApprovalActionView } from 'src/domain/dynamic/approval-types';
import enUS from 'shared/i18n/en-US/ui.json';
import zhTW from 'shared/i18n/zh-TW/ui.json';
import ApprovalActionLog from './ApprovalActionLog.vue';

const action = (p: Partial<ApprovalActionView>): ApprovalActionView => ({
  action: 'transfer', actorUserId: 'a', actorName: '進銷存主管', onBehalfOfUserId: null, targetUserIds: [], comment: null, data: null, createdTime: '2026-11-14T01:00:00', ...p,
});
const render = (actions: ApprovalActionView[], locale = 'zh-TW') => mount(ApprovalActionLog, {
  props: { actions, schema: null },
  global: { plugins: [createI18n({ legacy: false, locale, messages: { 'zh-TW': zhTW, 'en-US': enUS } })] },
});

describe('ApprovalActionLog', () => {
  it('shows the target names of a transfer and a reassign, next to the comment', () => {
    const w = render([
      action({ action: 'transfer', targetUserIds: ['mgr'], targetNames: ['業務主管'], comment: '出差中' }),
      action({ action: 'reassign', actorName: '系統管理員', targetUserIds: ['fin1'], targetNames: ['財務一'] }),
    ]);
    const logs = w.findAll('[data-testid="approval-log"] > li');
    expect(logs[0]!.find('[data-testid="log-targets"]').text()).toBe('→ 業務主管');
    expect(logs[0]!.find('[data-testid="log-comment"]').text()).toBe('出差中');
    expect(logs[1]!.find('[data-testid="log-targets"]').text()).toBe('→ 財務一');
    expect(logs[1]!.find('[data-testid="log-comment"]').exists()).toBe(false);
  });

  it('shows the add-sign users of an add-sign entry', () => {
    const w = render([action({ action: 'add-sign', targetUserIds: ['s1', 's2'], targetNames: ['業務一', '業務二'], comment: '請確認數量', data: { mode: 'any' } })]);
    const log = w.find('[data-testid="approval-log"] > li');
    expect(log.find('[data-testid="log-targets"]').text()).toBe('→ 業務一、業務二');
    expect(log.find('[data-testid="log-comment"]').text()).toBe('請確認數量');
  });

  it('falls back to the user ids without names, and shows no target for other actions', () => {
    const w = render([action({ action: 'transfer', targetUserIds: ['mgr'] }), action({ action: 'approve', targetUserIds: ['x'], comment: '好' })]);
    expect(w.findAll('[data-testid="log-targets"]').map((e) => e.text())).toEqual(['→ mgr']);
  });

  it('joins several targets with the locale list separator', () => {
    const w = render([action({ action: 'transfer', targetUserIds: ['a', 'b'], targetNames: ['Ann', 'Bob'] })], 'en-US');
    expect(w.find('[data-testid="log-targets"]').text()).toBe('→ Ann, Bob');
  });

  it('ActionLog_BranchCancelled_UsesLocaleNotComment: words the cancellation from data.name in the reader language, never the stored zh-TW comment', () => {
    const a = action({ action: 'branch-cancelled', actorName: 'System', comment: '分支「財務」已完成，其他分支自動取消', data: { name: { 'zh-TW': '財務', 'en-US': 'Finance' } } });
    const en = render([a], 'en-US');
    expect(en.find('[data-testid="approval-log"] > li').text()).toContain('Other branches cancelled');
    expect(en.find('[data-testid="log-branch-cancelled-note"]').text()).toBe('Branch "Finance" finished; the other branches were cancelled');
    expect(en.find('[data-testid="log-comment"]').exists()).toBe(false);
    expect(en.text()).not.toContain('分支「財務」');
    const zh = render([a]);
    expect(zh.find('[data-testid="log-branch-cancelled-note"]').text()).toBe('分支「財務」已完成，其他分支自動取消');
    expect(zh.find('[data-testid="log-comment"]').exists()).toBe(false);
  });

  it('ActionLog_BranchCancelled_NoName_Unnamed: without data.name it says the unnamed branch finished', () => {
    const zh = render([action({ action: 'branch-cancelled', comment: '分支「（未命名分支）」已完成，其他分支自動取消', data: { forkId: 'f' } })]);
    expect(zh.find('[data-testid="log-branch-cancelled-note"]').text()).toBe('分支「未命名分支」已完成，其他分支自動取消');
    const en = render([action({ action: 'branch-cancelled', data: null })], 'en-US');
    expect(en.find('[data-testid="log-branch-cancelled-note"]').text()).toBe('Branch "Unnamed branch" finished; the other branches were cancelled');
  });

  it('ActionLog_BranchCancelled_NameFallsBackToZhTw: a name without the reader language uses the zh-TW text', () => {
    const w = render([action({ action: 'branch-cancelled', data: { name: { 'zh-TW': '財務' } } })], 'en-US');
    expect(w.find('[data-testid="log-branch-cancelled-note"]').text()).toBe('Branch "財務" finished; the other branches were cancelled');
  });

  it('ActionLog_SystemActor_Translated: the engine user id "system" reads as the locale word, real users keep their display name', () => {
    const sys = action({ action: 'branch-cancelled', actorUserId: 'system', actorName: 'system' });
    const user = action({ action: 'approve', actorUserId: 'fin1', actorName: '財務一' });
    const zh = render([sys, user]).findAll('[data-testid="approval-log"] > li');
    expect(zh[0]!.text()).toContain('· 系統 ·');
    expect(zh[0]!.text()).not.toContain('system');
    expect(zh[1]!.text()).toContain('· 財務一 ·');
    expect(render([sys], 'en-US').find('[data-testid="approval-log"] > li').text()).toContain('· System ·');
  });

  describe('notify', () => {
    const notify = (data: unknown, comment: string | null = '已通知 3 人') => action({ action: 'notify', actorUserId: 'system', actorName: '', comment, data });

    it('依 data.count 用讀者語系組「已通知 n 人」，不顯示伺服器的 comment', () => {
      const w = render([notify({ count: 3, total: 3, truncated: false, none: false })]);
      expect(w.get('[data-testid="log-notify-note"]').text()).toBe('已通知 3 人');
      expect(w.find('[data-testid="log-comment"]').exists()).toBe(false);
      expect(render([notify({ count: 3, total: 3, truncated: false, none: false })], 'en-US').get('[data-testid="log-notify-note"]').text()).toBe('Notified 3 people');
    });

    it('沒有收件人：沒有收件人，未發送', () => {
      expect(render([notify({ count: 0, total: 0, truncated: false, none: true }, '沒有收件人')]).get('[data-testid="log-notify-note"]').text()).toBe('沒有收件人，未發送');
    });

    it('超過上限：以 count 為準，說明只通知前 count 人', () => {
      const text = render([notify({ count: 200, total: 350, truncated: true, none: false })]).get('[data-testid="log-notify-note"]').text();
      expect(text).toBe('收件人共 350 人，只通知前 200 人');
    });

    it('data 缺或格式不對：不顯示 comment 也不壞', () => {
      const w = render([notify(null), notify({ count: 'x' })]);
      expect(w.findAll('[data-testid="log-comment"]').length).toBe(0);
      expect(w.findAll('[data-testid="log-notify-note"]').length).toBe(0);
    });
  });
});
