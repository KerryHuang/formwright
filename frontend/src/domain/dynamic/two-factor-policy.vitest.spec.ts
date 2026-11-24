import { describe, expect, it } from 'vitest';
import { adminsFixed, operatorMustEnrollAfterSave } from './two-factor-policy';

const optional = { 'auth.twoFactorMode': 'optional', 'auth.twoFactorRequireAdmins': false, 'auth.twoFactorGroups': [], 'auth.twoFactorGraceDays': 0 };
const admin = { isSystemAdmin: true, twoFactor: { enabled: false, required: false } };

describe('強制雙因素的政策頁判斷（帳號安全設計 §8.7）', () => {
  it('強制模式固定包含系統管理員，選用模式不固定', () => {
    expect(adminsFixed('all')).toBe(true);
    expect(adminsFixed('groups')).toBe(true);
    expect(adminsFixed('optional')).toBe(false);
  });

  it('還沒啟用的管理員把模式改成強制、寬限 0 → 提示', () => {
    expect(operatorMustEnrollAfterSave('auth.twoFactorMode', 'all', 'optional', optional, admin)).toBe(true);
    expect(operatorMustEnrollAfterSave('auth.twoFactorMode', 'groups', 'optional', optional, admin)).toBe(true);
    expect(operatorMustEnrollAfterSave('auth.twoFactorRequireAdmins', true, true, optional, admin)).toBe(true);
    // 回到出廠值（選用時強制系統管理員＝是）
    expect(operatorMustEnrollAfterSave('auth.twoFactorRequireAdmins', null, true, optional, admin)).toBe(true);
    // 寬限從 7 改成 0，模式已是強制但自己還沒被判定（required=false 只會在暫停時出現）也照提示
    expect(operatorMustEnrollAfterSave('auth.twoFactorGraceDays', 0, 0, { ...optional, 'auth.twoFactorMode': 'all', 'auth.twoFactorGraceDays': 7 }, admin)).toBe(true);
  });

  it('不提示：有寬限、已啟用、已在範圍內、不是範圍政策、存成選用', () => {
    expect(operatorMustEnrollAfterSave('auth.twoFactorMode', 'all', 'optional', { ...optional, 'auth.twoFactorGraceDays': 7 }, admin)).toBe(false);
    expect(operatorMustEnrollAfterSave('auth.twoFactorMode', 'all', 'optional', optional, { isSystemAdmin: true, twoFactor: { enabled: true, required: false } })).toBe(false);
    expect(operatorMustEnrollAfterSave('auth.twoFactorMode', 'all', 'optional', optional, { isSystemAdmin: true, twoFactor: { enabled: false, required: true } })).toBe(false);
    expect(operatorMustEnrollAfterSave('auth.passwordMinLength', 12, 8, optional, admin)).toBe(false);
    expect(operatorMustEnrollAfterSave('auth.twoFactorMode', 'optional', 'optional', optional, admin)).toBe(false);
    expect(operatorMustEnrollAfterSave('auth.twoFactorMode', 'all', 'optional', optional, { isSystemAdmin: true, twoFactor: null })).toBe(false);
  });
});
