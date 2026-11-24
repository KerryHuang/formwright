import type { PolicyValue } from './policy-types';

/** 強制雙因素的四項政策（帳號安全設計 §8.7）。 */
export const TWO_FACTOR_MODE = 'auth.twoFactorMode';
export const TWO_FACTOR_GROUPS = 'auth.twoFactorGroups';
export const TWO_FACTOR_REQUIRE_ADMINS = 'auth.twoFactorRequireAdmins';
export const TWO_FACTOR_GRACE_DAYS = 'auth.twoFactorGraceDays';

const SCOPE_CODES = [TWO_FACTOR_MODE, TWO_FACTOR_GROUPS, TWO_FACTOR_REQUIRE_ADMINS, TWO_FACTOR_GRACE_DAYS];

/** 強制模式（all、groups）下系統管理員固定在範圍內，「選用時強制系統管理員」不能改。 */
export const adminsFixed = (mode: PolicyValue | undefined) => mode !== undefined && mode !== 'optional';

/**
 * 存這一項之後，操作者（系統管理員）是不是要立刻綁定（§8.7 存檔前提示）：自己還沒啟用、目前不在範圍內，
 * 存檔後在範圍內（強制模式一律包含系統管理員；選用模式看「選用時強制系統管理員」），且寬限天數是 0。
 * effective 是各政策目前的生效值；value 是要存的值（null＝回到出廠值 factory）。
 */
export function operatorMustEnrollAfterSave(
  code: string, value: PolicyValue | null, factory: PolicyValue, effective: Record<string, PolicyValue>,
  me: { isSystemAdmin: boolean; twoFactor?: { enabled: boolean; required: boolean } | null },
): boolean {
  if (!SCOPE_CODES.includes(code) || !me.isSystemAdmin || !me.twoFactor || me.twoFactor.enabled || me.twoFactor.required) return false;
  const next = { ...effective, [code]: value ?? factory };
  const inScope = adminsFixed(next[TWO_FACTOR_MODE]) || next[TWO_FACTOR_REQUIRE_ADMINS] === true;
  return inScope && Number(next[TWO_FACTOR_GRACE_DAYS] ?? 0) === 0;
}
